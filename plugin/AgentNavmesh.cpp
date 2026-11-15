// Constraint: navmesh is read from the cell's own forms and driven through the editor's menu commands.

#include <windows.h>

#include <cctype>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include <EditorAPI/N/NavMesh.h>
#include <EditorAPI/T/TESForm.h>
#include <EditorAPI/T/TESObjectCELL.h>

#include "AgentEditorShared.h"
#include "AgentNavmesh.h"
#include "AgentNavmeshCheck.h"
#include "AgentNavmeshRead.h"

namespace ckagent
{
	namespace
	{
		using namespace editor;
		using EditorCell = CKPE::Fallout4::EditorAPI::Forms::TESObjectCELL;
		using EditorForm = CKPE::Fallout4::EditorAPI::Forms::TESForm;
		using EditorNavMesh = CKPE::Fallout4::EditorAPI::Forms::NavMesh;

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

		// Constraint: match captions ignoring case, '&' and spacing, because builds spell them differently.
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

		struct NavCommand
		{
			std::string path;
			std::string caption;
			int id = 0;
			bool enabled = false;
			bool checked = false;
		};

		// A caption must be one protocol-safe line.
		std::string CaptionOf(const wchar_t* raw)
		{
			std::string text = StripAmp(U8(raw));
			const std::size_t tab = text.find('\t');
			if (tab != std::string::npos) text.resize(tab);
			while (!text.empty() && text.back() == ' ') text.pop_back();
			return text;
		}

		// Constraint: resolve by caption, because the CK generates menu ids per build.
		void CollectNavCommands(HMENU menu, std::vector<std::string> trail, std::vector<NavCommand>& out, int depth)
		{
			if (depth < 0) return;

			const int count = GetMenuItemCount(menu);
			for (int i = 0; i < count; ++i)
			{
				MENUITEMINFOW mi{};
				mi.cbSize = sizeof mi;
				mi.fMask = MIIM_ID | MIIM_SUBMENU | MIIM_STATE | MIIM_FTYPE;
				if (!GetMenuItemInfoW(menu, i, TRUE, &mi)) continue;
				if (mi.fType & MFT_SEPARATOR) continue;

				wchar_t raw[256] = {};
				GetMenuStringW(menu, i, raw, 255, MF_BYPOSITION);
				const std::string caption = CaptionOf(raw);
				if (caption.empty()) continue;

				trail.push_back(caption);

				if (mi.hSubMenu)
				{
					CollectNavCommands(mi.hSubMenu, trail, out, depth - 1);
					trail.pop_back();
					continue;
				}

				if (!mi.wID)
				{
					trail.pop_back();
					continue;
				}

				// Constraint: the whole path is matched, so a same-named leaf under another menu is not taken.
				std::string path;
				for (const auto& part : trail)
					path += (path.empty() ? "" : "/") + part;

				if (Flatten(path).find("navmesh") != std::string::npos)
				{
					NavCommand found;
					found.path = path;
					found.caption = caption;
					found.id = static_cast<int>(mi.wID);
					found.enabled = !(mi.fState & MFS_GRAYED);
					found.checked = (mi.fState & MFS_CHECKED) != 0;
					out.push_back(found);
				}

				trail.pop_back();
			}
		}

		bool ReadNavCommands(std::vector<NavCommand>& out, std::string& errorCode, std::string& errorMessage)
		{
			HWND frame = MainFrame();
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

			CollectNavCommands(menu, {}, out, 3);
			return true;
		}

		std::string CommandJson(const NavCommand& command)
		{
			std::string s = "{\"path\":" + Quote(command.path) + ",\"caption\":" + Quote(command.caption) +
				",\"id\":" + std::to_string(command.id);
			s += ",\"enabled\":";
			s += command.enabled ? "true" : "false";
			if (command.checked) s += ",\"checked\":true";
			s += "}";
			return s;
		}

		std::string CommandsJson(const std::vector<NavCommand>& commands)
		{
			std::string s = "{\"count\":" + std::to_string(commands.size()) + ",\"commands\":[";
			bool first = true;
			for (const auto& command : commands)
			{
				if (!first) s += ",";
				first = false;
				s += CommandJson(command);
			}
			s += "]}";
			return s;
		}

		// Constraint: an unresolved caption must report what the live menu offers.
		std::string CaptionsOf(const std::vector<NavCommand>& commands)
		{
			std::string out;
			for (const auto& command : commands)
				out += (out.empty() ? "\"" : ", \"") + command.caption + "\"";
			return out.empty() ? "none" : out;
		}

		bool FindCommandByCaption(const std::vector<NavCommand>& commands, const std::string& caption, NavCommand& out)
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

		// Constraint: refuse a disabled item before it is posted.
		bool PostNavCommand(const NavCommand& command, HWND frame,
			std::string& payload, std::string& errorCode, std::string& errorMessage)
		{
			if (!command.enabled)
			{
				errorCode = "command_disabled";
				errorMessage = "the editor greys out '" + command.caption + "' (" + command.path +
					"), so it would not run: open a cell with a navmesh first";
				return false;
			}

			if (!PostMessageW(frame, WM_COMMAND, MAKEWPARAM(static_cast<WORD>(command.id), 0), 0))
			{
				errorCode = "post_failed";
				errorMessage = "PostMessage to the editor window failed for " + command.path;
				return false;
			}

			payload = "{\"posted\":true,\"command\":" + Quote(command.path) +
				",\"caption\":" + Quote(command.caption) + ",\"id\":" + std::to_string(command.id) + "}";
			return true;
		}

		// Constraint: the generation command only opens a dialog, so the run has to be waited for, not assumed from the post.
		struct GenerationDialog
		{
			bool opened = false;
			bool accepted = false;
			bool closed = false;
			HWND window = nullptr;
			std::string title;
			int waitedMs = 0;
		};

		// Constraint: a hidden dialog still owns the frame, so liveness is the window, not its visibility.
		bool DialogStillUp(HWND dialog)
		{
			return IsWindow(dialog) != FALSE;
		}

		std::string DialogJson(const GenerationDialog& dialog)
		{
			return "{\"title\":" + Quote(dialog.title) +
				",\"opened\":" + (dialog.opened ? "true" : "false") +
				",\"accepted\":" + (dialog.accepted ? "true" : "false") +
				",\"closed\":" + (dialog.closed ? "true" : "false") +
				",\"waitedMs\":" + std::to_string(dialog.waitedMs) + "}";
		}

		// Constraint: a generation dialog can be hidden yet still disable the frame, so visibility is not the filter.
		HWND FindGenerationDialog(HWND frame, std::string& title)
		{
			HWND fallback = nullptr;
			std::string fallbackTitle;

			for (HWND window : TopWindows())
			{
				if (GetWindow(window, GW_OWNER) != frame) continue;

				const std::string text = U8(WinText(window));
				if (text.find("Generation") == std::string::npos) continue;

				// Constraint: a visible dialog wins, so an older hidden one cannot be answered instead.
				if (IsWindowVisible(window))
				{
					title = text;
					return window;
				}

				if (!fallback) { fallback = window; fallbackTitle = text; }
			}

			if (fallback) title = fallbackTitle;
			return fallback;
		}

		// Constraint: the pipe handler runs on the editor thread, so it must never sleep or the dialog cannot open.
		void ProbeGenerationDialog(HWND frame, GenerationDialog& out)
		{
			HWND dialog = FindGenerationDialog(frame, out.title);
			if (!dialog) return;

			out.window = dialog;
			out.opened = true;
			out.closed = !DialogStillUp(dialog);

			// Constraint: one press is enough, because a second one would start the generation again.
			if (out.accepted) return;

			HWND button = FindButtonByText(dialog, "OK");
			if (!button) return;

			PostMessage(button, BM_CLICK, 0, 0);
			out.accepted = true;
		}

		bool RunNamedCommand(const std::string& caption, const std::string& tool, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			std::vector<NavCommand> commands;
			if (!ReadNavCommands(commands, errorCode, errorMessage))
				return false;

			if (commands.empty())
			{
				errorCode = "no_navmesh_commands";
				errorMessage = "the editor's menu bar carries no navmesh command on this build: " + tool +
					" has nothing to run";
				return false;
			}

			NavCommand command;
			if (!FindCommandByCaption(commands, caption, command))
			{
				errorCode = "no_navmesh_command";
				errorMessage = "no navmesh command matches '" + caption + "'; this build offers " + CaptionsOf(commands);
				return false;
			}

			const HWND frame = MainFrame();
			if (!frame)
			{
				errorCode = "no_frame";
				errorMessage = "the editor has no window with a menu bar yet";
				return false;
			}

			return PostNavCommand(command, frame, payload, errorCode, errorMessage);
		}

		// Constraint: a mutating verb must see an explicit confirm token before it touches cell data.
		bool Confirmed(const std::string& args)
		{
			const std::string text = Lower(args);
			const std::size_t at = text.rfind(' ');
			return (at == std::string::npos ? text : text.substr(at + 1)) == "confirm";
		}

		std::string FirstWord(const std::string& args)
		{
			const std::string text = Lower(args);
			const std::size_t at = text.find(' ');
			return at == std::string::npos ? text : text.substr(0, at);
		}

		std::string WithoutConfirm(const std::string& args)
		{
			const std::size_t at = args.rfind(' ');
			return at == std::string::npos ? std::string() : args.substr(0, at);
		}

		// Constraint: only a caption that exists in this build may be resolved, so the map is explicit.
		const char* GenerationCaption(const std::string& kind)
		{
			if (kind.empty() || kind == "recast") return "Recast-Based Generation";
			if (kind == "havok") return "Havok-Based Generation";
			if (kind == "advanced") return "Advanced";
			return nullptr;
		}

		bool RunGenerate(const std::string& kind, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			const char* caption = GenerationCaption(kind);
			if (!caption)
			{
				errorCode = "invalid_argument";
				errorMessage = "kind must be recast, havok or advanced";
				return false;
			}

			std::vector<NavCommand> commands;
			if (!ReadNavCommands(commands, errorCode, errorMessage))
				return false;

			NavCommand command;
			if (!FindCommandByCaption(commands, caption, command))
			{
				errorCode = "no_navmesh_command";
				errorMessage = std::string("no navmesh command matches '") + caption + "'; this build offers " + CaptionsOf(commands);
				return false;
			}

			const HWND frame = MainFrame();
			if (!frame)
			{
				errorCode = "no_frame";
				errorMessage = "the editor has no window with a menu bar yet";
				return false;
			}

			std::string posted;
			if (!PostNavCommand(command, frame, posted, errorCode, errorMessage))
				return false;

			GenerationDialog dialog;
			ProbeGenerationDialog(frame, dialog);

			// Constraint: the reply must carry the dialog outcome, so the posted object is extended rather than replaced.
			payload = posted.substr(0, posted.size() - 1) + ",\"dialog\":" + DialogJson(dialog) + "}";
			return true;
		}

		// Constraint: the sequence must post in generation, finalize, check order, so a later step sees the earlier one.
		// Constraint: every stage must be advanced by a later call, because no call may sleep on the editor thread.
		constexpr ULONGLONG kApplyAcceptMs = 20000;
		constexpr ULONGLONG kApplySettleMs = 180000;
		constexpr ULONGLONG kApplyStaleMs = 300000;

		struct ApplyRun
		{
			bool active = false;
			std::string kind;
			const void* cell = nullptr;
			int stage = 0;
			ULONGLONG stageSince = 0;
			ULONGLONG started = 0;
			std::uint32_t beforeForms = 0;
			std::uint32_t beforeVertices = 0;
			std::uint32_t beforeTriangles = 0;
			std::string steps;
			GenerationDialog dialog;
			NavCommand resolved[3];
			const char* captions[3] = { nullptr, nullptr, nullptr };
		};

		ApplyRun g_apply;

		const char* ApplyStepNames[] = { "generate", "finalize", "check" };

		void ResetApply()
		{
			g_apply = ApplyRun();
		}

		// Constraint: an abandoned run can leave a modal generation dialog up, and the frame stays disabled until it is answered.
		void DismissGenerationDialog(HWND frame, GenerationDialog& dialog, std::string& via)
		{
			HWND window = FindGenerationDialog(frame, dialog.title);
			if (!window) return;

			dialog.window = window;
			dialog.opened = true;
			dialog.closed = !DialogStillUp(window);

			// Constraint: cancel leaves the cell untouched, so it wins over accepting work nobody is waiting for anymore.
			HWND button = FindButtonByText(window, "Cancel");
			via = "cancel";

			if (!button)
			{
				button = FindButtonByText(window, "OK");
				via = "accept";
			}

			if (!button)
			{
				via = "none";
				return;
			}

			PostMessage(button, BM_CLICK, 0, 0);
			dialog.accepted = via == "accept";
		}

		// Constraint: a failed run must leave the editor usable, so its dialog is dismissed and its capture dropped.
		std::string AbandonApply(const HWND frame)
		{
			const bool dropped = g_apply.active;
			const std::string steps = g_apply.steps;
			GenerationDialog dialog = g_apply.dialog;

			std::string via = "none";
			DismissGenerationDialog(frame, dialog, via);

			navmeshcheck::Abandon();
			ResetApply();

			return std::string("{\"stage\":\"aborted\",\"pending\":false,\"done\":true,\"dropped\":") +
				(dropped ? "true" : "false") +
				",\"dismissedBy\":" + (via == "none" ? std::string("null") : Quote(via)) +
				",\"dialog\":" + DialogJson(dialog) +
				",\"steps\":[" + steps + "]}";
		}

		std::string ApplyPendingJson(const char* stage, const std::string& extra)
		{
			return std::string("{\"stage\":") + Quote(stage) +
				",\"pending\":true,\"done\":false,\"steps\":[" + g_apply.steps + "]" +
				",\"dialog\":" + DialogJson(g_apply.dialog) + extra + "}";
		}

		bool PostApplyStep(int index, const HWND frame, std::string& errorCode, std::string& errorMessage)
		{
			std::string posted;
			if (!PostNavCommand(g_apply.resolved[index], frame, posted, errorCode, errorMessage))
			{
				// Constraint: the specific refusal code must survive, so the step name only prefixes the message.
				errorCode = "step_failed";
				errorMessage = std::string("step ") + ApplyStepNames[index] + std::string(" ('") +
					g_apply.captions[index] + "') failed: " + errorMessage;
				return false;
			}

			if (!g_apply.steps.empty()) g_apply.steps += ",";
			g_apply.steps += std::string("{\"step\":") + Quote(ApplyStepNames[index]) + ",\"result\":" + posted + "}";
			return true;
		}

		// Constraint: one call advances one stage, so a run is a sequence of calls and never a sleep.
		bool RunApply(const std::string& kind, const void* cell, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			const char* generation = GenerationCaption(kind);
			if (!generation)
			{
				errorCode = "invalid_argument";
				errorMessage = "kind must be recast, havok or advanced";
				return false;
			}

			const HWND frame = MainFrame();
			if (!frame)
			{
				errorCode = "no_frame";
				errorMessage = "the editor has no window with a menu bar yet";
				return false;
			}

			// Constraint: a run belongs to one cell and kind, so another cell cannot be finalized with its plan.
			if (g_apply.active && (g_apply.cell != cell || g_apply.kind != kind))
				AbandonApply(frame);

			const ULONGLONG now = GetTickCount64();

			if (g_apply.active && now - g_apply.stageSince > kApplyStaleMs)
			{
				AbandonApply(frame);
				errorCode = "apply_stalled";
				errorMessage = "the apply sequence stopped advancing and was dropped, with any dialog it left up dismissed; call it again to start from the generation";
				return false;
			}

			if (!g_apply.active)
			{
				std::vector<NavCommand> commands;
				if (!ReadNavCommands(commands, errorCode, errorMessage))
					return false;

				g_apply.captions[0] = generation;
				g_apply.captions[1] = "Finalize Cell NavMeshes";
				g_apply.captions[2] = "Check NavMeshes";

				for (int i = 0; i < 3; ++i)
				{
					if (FindCommandByCaption(commands, g_apply.captions[i], g_apply.resolved[i])) continue;

					errorCode = "no_navmesh_command";
					errorMessage = std::string("the apply sequence needs '") + g_apply.captions[i] +
						"', which this build does not offer; it lists " + CaptionsOf(commands);
					return false;
				}

				if (!navmeshread::ReadTotals(cell, g_apply.beforeForms, g_apply.beforeVertices, g_apply.beforeTriangles))
				{
					errorCode = "no_cell";
					errorMessage = "no loaded cell is readable through the render window, so generation has nothing to rebuild and no change to measure";
					return false;
				}

				g_apply.active = true;
				g_apply.kind = kind;
				g_apply.cell = cell;
				g_apply.stage = 1;
				g_apply.stageSince = now;
				g_apply.started = now;

				if (!PostApplyStep(0, frame, errorCode, errorMessage))
				{
					AbandonApply(frame);
					return false;
				}

				ProbeGenerationDialog(frame, g_apply.dialog);
				payload = ApplyPendingJson("generate", std::string());
				return true;
			}

			const std::string before = navmeshread::ReadTotalsJson(g_apply.beforeForms, g_apply.beforeVertices, g_apply.beforeTriangles, true);
			const ULONGLONG settledMs = now - g_apply.started;
			const std::string settledJson = ",\"settledMs\":" + std::to_string(settledMs);

			if (g_apply.stage == 1)
			{
				ProbeGenerationDialog(frame, g_apply.dialog);

				if (g_apply.dialog.opened && DialogStillUp(g_apply.dialog.window))
				{
					if (!g_apply.dialog.accepted && now - g_apply.stageSince > kApplyAcceptMs)
					{
						AbandonApply(frame);
						errorCode = "generation_unanswered";
						errorMessage = "the generation dialog appeared but its OK button was not found, so nothing was generated";
						return false;
					}

					payload = ApplyPendingJson("accept", std::string());
					return true;
				}

				if (!g_apply.dialog.opened)
				{
					if (now - g_apply.stageSince > kApplyAcceptMs)
					{
						AbandonApply(frame);
						errorCode = "generation_incomplete";
						errorMessage = "the generation command was posted but no generation dialog appeared, so the editor refused or ignored it";
						return false;
					}

					payload = ApplyPendingJson("generate", std::string());
					return true;
				}

				g_apply.dialog.closed = true;
				g_apply.stage = 2;
				g_apply.stageSince = now;
				payload = ApplyPendingJson("settle", settledJson + ",\"before\":" + before);
				return true;
			}

			if (g_apply.stage == 2)
			{
				std::uint32_t forms = 0, vertices = 0, triangles = 0;
				const bool known = navmeshread::ReadTotals(cell, forms, vertices, triangles);
				const bool changed = known && (vertices != g_apply.beforeVertices || triangles != g_apply.beforeTriangles);

				if (changed)
				{
					if (!PostApplyStep(1, frame, errorCode, errorMessage))
					{
						AbandonApply(frame);
						return false;
					}

					g_apply.stage = 3;
					g_apply.stageSince = now;
					payload = ApplyPendingJson("finalize", settledJson + ",\"before\":" + before);
					return true;
				}

				if (now - g_apply.started > kApplySettleMs)
				{
					// Constraint: a sequence that moved no geometry has not applied anything, so it must not read as success.
					errorCode = "no_geometry_change";
					errorMessage = "generation changed no geometry: the cell still carries " + std::to_string(vertices) +
						" vertices and " + std::to_string(triangles) + " triangles in " + std::to_string(forms) +
						" forms, its generation dialog was " + (g_apply.dialog.accepted ? "accepted" : "left unanswered") +
						", and " + std::to_string(settledMs) + " ms of polling saw no change. Check the editor's log for a" +
						" refused check-out, then select a cell with real geometry";
					AbandonApply(frame);
					return false;
				}

				payload = ApplyPendingJson("settle", settledJson + ",\"before\":" + before);
				return true;
			}

			if (g_apply.stage == 3)
			{
				// Constraint: the capture opens before the post, so apply reads exactly the lines its own check wrote.
				navmeshcheck::BeginCapture();

				if (!PostApplyStep(2, frame, errorCode, errorMessage))
				{
					AbandonApply(frame);
					return false;
				}

				g_apply.stage = 4;
				g_apply.stageSince = now;
				payload = ApplyPendingJson("check", settledJson + ",\"before\":" + before);
				return true;
			}

			if (g_apply.stage == 4)
			{
				std::string report;
				if (!navmeshcheck::Collect(report, errorCode, errorMessage))
				{
					AbandonApply(frame);
					return false;
				}

				// Constraint: the check is still writing, so the run stays pending rather than classifying a half report.
				if (report.find("\"pending\":true") != std::string::npos)
				{
					payload = ApplyPendingJson("check", settledJson + ",\"before\":" + before);
					return true;
				}
			}

			std::uint32_t afterForms = 0, afterVertices = 0, afterTriangles = 0;
			const bool afterKnown = navmeshread::ReadTotals(cell, afterForms, afterVertices, afterTriangles);

			payload = "{\"stage\":\"done\",\"pending\":false,\"done\":true,\"steps\":[" + g_apply.steps + "],\"count\":3" +
				",\"dialog\":" + DialogJson(g_apply.dialog) +
				",\"settled\":true" + settledJson +
				",\"report\":" + navmeshcheck::LastReportJson() +
				"," + navmeshcheck::LastVerdictFields() +
				",\"before\":" + before +
				",\"after\":" + navmeshread::ReadTotalsJson(afterForms, afterVertices, afterTriangles, afterKnown) + "}";
			ResetApply();
			return true;
		}

		// Constraint: Save is the editor's own File command, so the path is walked instead of using a build id.
		bool FindSaveCommand(NavCommand& out)
		{
			HWND frame = MainFrame();
			if (!frame) return false;

			HMENU menu = GetMenu(frame);
			if (!menu) return false;

			const int count = GetMenuItemCount(menu);
			for (int i = 0; i < count; ++i)
			{
				wchar_t raw[256] = {};
				GetMenuStringW(menu, i, raw, 255, MF_BYPOSITION);
				if (Flatten(CaptionOf(raw)) != "file") continue;

				MENUITEMINFOW file{ };
				file.cbSize = sizeof file;
				file.fMask = MIIM_SUBMENU;
				if (!GetMenuItemInfoW(menu, i, TRUE, &file) || !file.hSubMenu) continue;

				const int innerCount = GetMenuItemCount(file.hSubMenu);
				for (int k = 0; k < innerCount; ++k)
				{
					wchar_t innerRaw[256] = {};
					GetMenuStringW(file.hSubMenu, k, innerRaw, 255, MF_BYPOSITION);
					if (Flatten(CaptionOf(innerRaw)) != "save") continue;

					MENUITEMINFOW item{ };
					item.cbSize = sizeof item;
					item.fMask = MIIM_ID | MIIM_STATE;
					if (!GetMenuItemInfoW(file.hSubMenu, k, TRUE, &item)) continue;

					out.path = "File/Save";
					out.caption = CaptionOf(innerRaw);
					out.id = static_cast<int>(item.wID);
					out.enabled = !(item.fState & MFS_GRAYED);
					return out.id != 0;
				}
			}

			return false;
		}

		bool RunSave(std::string& payload, std::string& errorCode, std::string& errorMessage)
		{
			NavCommand save;
			if (!FindSaveCommand(save))
			{
				errorCode = "no_save_command";
				errorMessage = "the editor's File menu carries no Save command on this build";
				return false;
			}

			const HWND frame = MainFrame();
			if (!frame)
			{
				errorCode = "no_frame";
				errorMessage = "the editor has no window with a menu bar yet";
				return false;
			}

			return PostNavCommand(save, frame, payload, errorCode, errorMessage);
		}

		// Constraint: a dry run must resolve exactly the steps apply posts, without posting any of them.
		bool RunPlan(const std::string& kind, std::string& payload, std::string& errorCode, std::string& errorMessage)
		{
			const char* generation = GenerationCaption(kind);
			if (!generation)
			{
				errorCode = "invalid_argument";
				errorMessage = "kind must be recast, havok or advanced";
				return false;
			}

			std::vector<NavCommand> commands;
			if (!ReadNavCommands(commands, errorCode, errorMessage))
				return false;

			const char* captions[] = { generation, "Finalize Cell NavMeshes", "Check NavMeshes" };
			const char* steps[] = { "generate", "finalize", "check" };

			std::string list;
			bool complete = true;
			for (int i = 0; i < 3; ++i)
			{
				NavCommand found;
				const bool resolved = FindCommandByCaption(commands, captions[i], found);
				if (!resolved || !found.enabled) complete = false;

				if (!list.empty()) list += ",";
				list += std::string("{\"step\":") + Quote(steps[i]) +
					",\"caption\":" + Quote(captions[i]) +
					",\"path\":" + Quote(resolved ? found.path : std::string()) +
					",\"id\":" + std::to_string(resolved ? found.id : 0) +
					",\"resolved\":" + (resolved ? "true" : "false") +
					",\"enabled\":" + (resolved && found.enabled ? "true" : "false") + "}";
			}

			NavCommand save;
			const bool hasSave = FindSaveCommand(save);
			if (!hasSave || !save.enabled) complete = false;

			payload = "{\"kind\":" + Quote(generation) + ",\"steps\":[" + list + "]" +
				",\"save\":{\"path\":" + Quote(hasSave ? save.path : std::string()) +
				",\"id\":" + std::to_string(hasSave ? save.id : 0) +
				",\"resolved\":" + (hasSave ? "true" : "false") +
				",\"enabled\":" + (hasSave && save.enabled ? "true" : "false") + "}" +
				",\"complete\":" + (complete ? "true" : "false") + "}";
			return true;
		}
	}

	// Constraint: the check module posts through the same resolver, so a caption is never addressed by a build id.
	bool PostNavmeshCommand(const std::string& caption, std::string& payload,
		std::string& errorCode, std::string& errorMessage)
	{
		return RunNamedCommand(caption, "nav.check", payload, errorCode, errorMessage);
	}

	bool RunNavmeshTool(const std::string& tool, const std::string& args, const void* cell,
		std::string& payload, std::string& errorCode, std::string& errorMessage)
	{
		if (tool == "nav.state")
			return navmeshread::ReadCellState(cell, payload, errorCode, errorMessage);

		if (tool == "nav.strip")
		{
			// Constraint: emptying a cell's navmesh is destructive, so it needs the same confirm token as the other writers.
			if (Lower(args) != "confirm")
			{
				errorCode = "confirmation_required";
				errorMessage = "nav.strip empties the loaded cell's navmesh geometry; repeat with confirm=true when that is intended";
				return false;
			}

			return navmeshread::StripCellGeometry(cell, payload, errorCode, errorMessage);
		}

		if (tool == "nav.prune")
		{
			// Constraint: dropping a form shortens the cell's navmesh list, so it needs the same confirm token as the writers.
			if (!Confirmed(args))
			{
				errorCode = "confirmation_required";
				errorMessage = "nav.prune drops the named navmesh forms from the loaded cell; repeat with "
					"'<ids> confirm' when that is intended";
				return false;
			}

			return navmeshread::PruneCellForms(cell, WithoutConfirm(args), payload, errorCode, errorMessage);
		}

		if (tool == "nav.commands")
		{
			std::vector<NavCommand> commands;
			if (!ReadNavCommands(commands, errorCode, errorMessage))
				return false;

			payload = CommandsJson(commands);
			return true;
		}

		if (tool == "nav.check")
			return navmeshcheck::RunCheck(payload, errorCode, errorMessage);

		if (tool == "nav.generate")
		{
			if (!Confirmed(args))
			{
				errorCode = "confirmation_required";
				errorMessage = "nav.generate replaces the loaded cell's generated navmesh; repeat the call with "
					"kind=<recast|havok|advanced> confirm when that is intended";
				return false;
			}

			return RunGenerate(FirstWord(args), payload, errorCode, errorMessage);
		}

		if (tool == "nav.apply")
		{
			// Constraint: an abandoned run must be droppable at once, without waiting out the stale window.
			if (FirstWord(args) == "abort")
			{
				const HWND frame = MainFrame();
				if (!frame)
				{
					errorCode = "no_frame";
					errorMessage = "the editor has no window with a menu bar yet";
					return false;
				}

				payload = AbandonApply(frame);
				return true;
			}

			if (!Confirmed(args))
			{
				errorCode = "confirmation_required";
				errorMessage = "nav.apply regenerates, finalises and checks the loaded cell; repeat the call with "
					"kind=<recast|havok|advanced> confirm when that is intended";
				return false;
			}

			return RunApply(FirstWord(args), cell, payload, errorCode, errorMessage);
		}

		if (tool == "nav.plan")
			return RunPlan(FirstWord(args), payload, errorCode, errorMessage);

		if (tool == "nav.accept")
		{
			// Constraint: the dialog appears on the editor's own timer after the post, so acceptance is its own call.
			const HWND frame = MainFrame();
			if (!frame)
			{
				errorCode = "no_frame";
				errorMessage = "the editor has no window with a menu bar yet";
				return false;
			}

			GenerationDialog dialog;
			ProbeGenerationDialog(frame, dialog);

			if (!dialog.opened)
			{
				errorCode = "no_dialog";
				errorMessage = "no generation dialog is up; post the generation command and call again on the next tick";
				return false;
			}

			payload = "{\"dialog\":" + DialogJson(dialog) + "}";
			return true;
		}

		if (tool == "nav.save")
		{
			if (!Confirmed(args))
			{
				errorCode = "confirmation_required";
				errorMessage = "nav.save writes the editor's open file; repeat the call with confirm when that is intended";
				return false;
			}

			return RunSave(payload, errorCode, errorMessage);
		}

		if (tool == "nav.run")
		{
			// Constraint: an arbitrary navmesh command still needs the caller's own confirmation.
			if (!Confirmed(args))
			{
				errorCode = "confirmation_required";
				errorMessage = "nav.run executes a navmesh command that may change cell data; repeat the call with "
					"'<caption> confirm' when that is intended";
				return false;
			}

			const std::string caption = WithoutConfirm(args);
			if (caption.empty())
			{
				errorCode = "invalid_argument";
				errorMessage = "nav.run needs a navmesh command caption, for example 'Audit Navmesh Report confirm'";
				return false;
			}

			return RunNamedCommand(caption, tool, payload, errorCode, errorMessage);
		}

		if (tool == "nav.finalize")
		{
			// Constraint: finalising changes cell data, so it needs an explicit confirm here too.
			if (Lower(args) != "confirm")
			{
				errorCode = "confirmation_required";
				errorMessage = "nav.finalize changes the loaded cell's navmesh data; repeat the call with "
					"confirm=true when that is intended";
				return false;
			}

			return RunNamedCommand("Finalize Cell NavMeshes", tool, payload, errorCode, errorMessage);
		}

		errorCode = "unknown_tool";
		errorMessage = tool;
		return false;
	}
}
