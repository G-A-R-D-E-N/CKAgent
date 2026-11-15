// Constraint: a reference is placed by the editor's own edit commands, so nothing here writes cell data.

#include <windows.h>

#include <cctype>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include <EditorAPI/T/TESForm.h>
#include <EditorAPI/T/TESObjectCELL.h>

#include "AgentEditorShared.h"
#include "AgentPlace.h"

namespace ckagent
{
	namespace
	{
		using namespace editor;
		using EditorCell = CKPE::Fallout4::EditorAPI::Forms::TESObjectCELL;

		// Constraint: a read must stay inside one committed region, so a stale pointer cannot fault the editor.
		bool Readable(const void* address, std::size_t bytes)
		{
			if (!address || !bytes) return false;

			MEMORY_BASIC_INFORMATION info{};
			if (!VirtualQuery(address, &info, sizeof info)) return false;
			if (info.State != MEM_COMMIT) return false;
			if (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;

			const auto start = reinterpret_cast<std::uintptr_t>(address);
			const auto region = reinterpret_cast<std::uintptr_t>(info.BaseAddress) + info.RegionSize;
			return start + bytes <= region;
		}

		// Constraint: the same item is spelled differently per build, so matching ignores case, '&' and spacing.
		std::string Flatten(const std::string& text)
		{
			std::string out;
			out.reserve(text.size());
			for (unsigned char c : text)
			{
				if (c == '&' || c == ' ' || c == '\t' || c == '-' || c == '_') continue;
				out += static_cast<char>(std::tolower(c));
			}
			return out;
		}

		std::string CaptionOf(const wchar_t* raw)
		{
			std::string text = StripAmp(U8(raw));
			const std::size_t tab = text.find('\t');
			return tab == std::string::npos ? text : text.substr(0, tab);
		}

		struct EditCommand
		{
			std::string caption;
			int id = 0;
			bool enabled = false;
		};

		std::string CommandJson(const EditCommand& command)
		{
			return "{\"caption\":" + Quote(command.caption) +
				",\"id\":" + std::to_string(command.id) +
				",\"enabled\":" + (command.enabled ? "true" : "false") + "}";
		}

		// Constraint: ids belong to one build, so every placement command is resolved from the live menu.
		bool ReadEditCommands(std::vector<EditCommand>& out, std::string& errorCode, std::string& errorMessage)
		{
			const HWND frame = MainFrame();
			if (!frame)
			{
				errorCode = "no_frame";
				errorMessage = "the editor has no window with a menu bar yet";
				return false;
			}

			HMENU menu = GetMenu(frame);
			if (!menu)
			{
				errorCode = "no_menu";
				errorMessage = "the editor's main window has no menu bar";
				return false;
			}

			const int count = GetMenuItemCount(menu);
			for (int i = 0; i < count; ++i)
			{
				wchar_t raw[256] = {};
				GetMenuStringW(menu, i, raw, 255, MF_BYPOSITION);
				if (Flatten(CaptionOf(raw)) != "edit") continue;

				MENUITEMINFOW item{};
				item.cbSize = sizeof item;
				item.fMask = MIIM_SUBMENU;
				if (!GetMenuItemInfoW(menu, i, TRUE, &item) || !item.hSubMenu) continue;

				const int inner = GetMenuItemCount(item.hSubMenu);
				for (int k = 0; k < inner; ++k)
				{
					wchar_t innerRaw[256] = {};
					GetMenuStringW(item.hSubMenu, k, innerRaw, 255, MF_BYPOSITION);

					const std::string caption = CaptionOf(innerRaw);
					const std::string key = Flatten(caption);
					if (key.find("duplicate") == std::string::npos && key.find("paste") == std::string::npos &&
						key.find("copyrender") == std::string::npos) continue;

					MENUITEMINFOW entry{};
					entry.cbSize = sizeof entry;
					entry.fMask = MIIM_ID | MIIM_STATE;
					if (!GetMenuItemInfoW(item.hSubMenu, k, TRUE, &entry)) continue;
					if (!entry.wID) continue;

					EditCommand command;
					command.caption = caption;
					command.id = static_cast<int>(entry.wID);
					command.enabled = !(entry.fState & MFS_GRAYED);
					out.push_back(command);
				}

				return true;
			}

			errorCode = "no_edit_menu";
			errorMessage = "the editor's menu bar carries no Edit menu on this build";
			return false;
		}

		bool FindByCaption(const std::vector<EditCommand>& commands, const std::string& caption, EditCommand& out)
		{
			const std::string want = Flatten(caption);
			for (const auto& command : commands)
			{
				if (Flatten(command.caption) != want) continue;
				out = command;
				return true;
			}

			return false;
		}

		// Constraint: the loaded cell is the only thing a placement can change, so its reference count is the measure.
		bool ReferenceCount(const void* cellPointer, std::uint32_t& count)
		{
			count = 0;
			if (!cellPointer || !Readable(cellPointer, 0x180)) return false;

			auto* cell = const_cast<EditorCell*>(static_cast<const EditorCell*>(cellPointer));
			count = cell->GetItemCount();
			return true;
		}
	}

	bool RunPlaceTool(const std::string& tool, const std::string& args, const void* cell,
		std::string& payload, std::string& errorCode, std::string& errorMessage)
	{
		if (tool == "place.commands")
		{
			std::vector<EditCommand> commands;
			if (!ReadEditCommands(commands, errorCode, errorMessage))
				return false;

			std::string list;
			for (const auto& command : commands)
			{
				if (!list.empty()) list += ",";
				list += CommandJson(command);
			}

			payload = "{\"count\":" + std::to_string(commands.size()) + ",\"commands\":[" + list + "]}";
			return true;
		}

		if (tool == "place.duplicate")
		{
			std::vector<EditCommand> commands;
			if (!ReadEditCommands(commands, errorCode, errorMessage))
				return false;

			EditCommand duplicate;
			if (!FindByCaption(commands, "&Duplicate", duplicate) && !FindByCaption(commands, "Duplicate", duplicate))
			{
				errorCode = "no_duplicate_command";
				errorMessage = "the Edit menu has no Duplicate item on this build";
				return false;
			}

			if (!duplicate.enabled)
			{
				errorCode = "command_disabled";
				errorMessage = "the editor greys out Duplicate, so nothing is selected in the render window: select a reference first";
				return false;
			}

			std::uint32_t before = 0;
			if (!ReferenceCount(cell, before))
			{
				errorCode = "no_cell";
				errorMessage = "no loaded cell is readable through the render window, so there is nothing to place into";
				return false;
			}

			const HWND frame = MainFrame();
			if (!frame || !PostMessageW(frame, WM_COMMAND, MAKEWPARAM(static_cast<WORD>(duplicate.id), 0), 0))
			{
				errorCode = "post_failed";
				errorMessage = "PostMessage to the editor window failed for Duplicate";
				return false;
			}

			std::uint32_t after = 0;
			int waitedMs = 0;
			for (int waited = 0; waited < 6000; waited += 250)
			{
				Sleep(250);
				waitedMs = waited;
				if (!ReferenceCount(cell, after)) break;
				if (after != before) break;
			}

			if (after == before)
			{
				errorCode = "no_change";
				errorMessage = "Duplicate was posted but the cell still holds " + std::to_string(before) +
					" references after " + std::to_string(waitedMs) + " ms, so nothing was placed: select a reference in the render window first";
				return false;
			}

			payload = "{\"placed\":true,\"command\":" + Quote(duplicate.caption) +
				",\"id\":" + std::to_string(duplicate.id) +
				",\"before\":" + std::to_string(before) +
				",\"after\":" + std::to_string(after) +
				",\"waitedMs\":" + std::to_string(waitedMs) + "}";
			return true;
		}

		errorCode = "unknown_tool";
		errorMessage = tool;
		return false;
	}
}
