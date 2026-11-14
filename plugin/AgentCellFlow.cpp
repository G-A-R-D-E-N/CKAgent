// Constraint: these flows must run on the editor thread, where the CK handles its own messages inline.

#include <windows.h>
#include <commctrl.h>

#include <string>
#include <vector>

#include "AgentCellFlow.h"
#include "AgentEditorShared.h"

namespace ckagent
{
	namespace editor
	{
		// --- composite: load a master file through File > Data (requires two calls, see ui.dataload) ---

		HWND FindDialogByTitle(const std::string& needle)
		{
			const std::string want = Lower(needle);
			for (HWND h : TopWindows())
			{
				if (!IsClass(h, L"#32770")) continue;
				if (Lower(U8(WinText(h))).find(want) == std::string::npos) continue;
				if (!IsWindowVisible(h)) ShowWindowAsync(h, SW_SHOW);
				return h;
			}
			return nullptr;
		}

		HWND FindListViewContaining(HWND root, const std::string& needle)
		{
			const std::string want = Lower(needle);
			for (HWND c : Children(root))
			{
				if (IsClass(c, L"SysListView32"))
				{
					const int count = static_cast<int>(Send(c, LVM_GETITEMCOUNT));
					for (int i = 0; i < count; ++i)
					{
						wchar_t buf[512] = {};
						LVITEMW li{};
						li.iSubItem = 0;
						li.pszText = buf;
						li.cchTextMax = 511;
						Send(c, LVM_GETITEMTEXTW, i, reinterpret_cast<LPARAM>(&li));
						if (Lower(U8(buf)).find(want) != std::string::npos)
							return c;
					}
				}
				if (HWND deeper = FindListViewContaining(c, needle))
					return deeper;
			}
			return nullptr;
		}

		int FindListViewRow(HWND list, const std::string& needle)
		{
			const std::string want = Lower(needle);
			const int count = static_cast<int>(Send(list, LVM_GETITEMCOUNT));
			for (int i = 0; i < count; ++i)
			{
				for (int c = 0; c < 4; ++c)
				{
					wchar_t buf[512] = {};
					LVITEMW li{};
					li.iSubItem = c;
					li.pszText = buf;
					li.cchTextMax = 511;
					Send(list, LVM_GETITEMTEXTW, i, reinterpret_cast<LPARAM>(&li));
					if (Lower(U8(buf)).find(want) != std::string::npos)
						return i;
				}
			}
			return -1;
		}

		// The cell list must be reachable without a handle, so the Cell View pane is found by title.
		HWND FindFirstListView(HWND root)
		{
			for (HWND c : Children(root))
			{
				if (IsClass(c, L"SysListView32")) return c;
				if (HWND deeper = FindFirstListView(c)) return deeper;
			}
			return nullptr;
		}

		HWND FindButtonByText(HWND root, const std::string& text)
		{
			const std::string want = Lower(StripAmp(text));
			for (HWND c : Children(root))
			{
				if (IsClass(c, L"Button") && Lower(StripAmp(U8(WinText(c)))).find(want) != std::string::npos)
					return c;
				if (HWND deeper = FindButtonByText(c, text))
					return deeper;
			}
			return nullptr;
		}

		// --- composite: open a cell from the Cell View (the item must exist before it is opened) ---

		// Constraint: the cell must come from the Cell View's own item list, so both its tree and
		// its list view must both be searched before anything is clicked.
		struct CellTarget
		{
			HWND control = nullptr;
			bool tree = false;
			HTREEITEM node = nullptr;
			int row = -1;
			std::string text;
		};

		bool MatchNode(HWND tree, HTREEITEM item, const std::string& want, CellTarget& out, int& budget)
		{
			for (HTREEITEM it = item; it && budget > 0;
				it = reinterpret_cast<HTREEITEM>(Send(tree, TVM_GETNEXTITEM, TVGN_NEXT, reinterpret_cast<LPARAM>(it))))
			{
				--budget;
				wchar_t buf[512] = {};
				TVITEMW tv{};
				tv.mask = TVIF_TEXT | TVIF_HANDLE | TVIF_CHILDREN;
				tv.hItem = it;
				tv.pszText = buf;
				tv.cchTextMax = 511;
				Send(tree, TVM_GETITEMW, 0, reinterpret_cast<LPARAM>(&tv));

				if (Lower(StripAmp(U8(buf))).find(want) != std::string::npos)
				{
					out.control = tree;
					out.tree = true;
					out.node = it;
					out.text = U8(buf);
					return true;
				}

				const auto child = reinterpret_cast<HTREEITEM>(Send(tree, TVM_GETNEXTITEM, TVGN_CHILD, reinterpret_cast<LPARAM>(it)));
				if (child)
				{
					// A collapsed branch hides its cells, so it must be expanded before searching inside.
					if (tv.cChildren) Send(tree, TVM_EXPAND, TVE_EXPAND, reinterpret_cast<LPARAM>(it));
					if (MatchNode(tree, child, want, out, budget))
						return true;
				}
			}
			return false;
		}

		bool FindCellInChildren(HWND root, const std::string& want, CellTarget& out, int& budget)
		{
			for (HWND c : Children(root))
			{
				if (budget <= 0) return false;
				if (IsClass(c, L"SysTreeView32"))
				{
					const auto rootItem = reinterpret_cast<HTREEITEM>(Send(c, TVM_GETNEXTITEM, TVGN_ROOT, 0));
					if (rootItem && MatchNode(c, rootItem, want, out, budget))
						return true;
				}
				if (IsClass(c, L"SysListView32"))
				{
					const int row = FindListViewRow(c, want);
					if (row >= 0)
					{
						out.control = c;
						out.tree = false;
						out.row = row;
						out.text = want;
						return true;
					}
				}
				if (FindCellInChildren(c, want, out, budget))
					return true;
			}
			return false;
		}
	}

	bool RunCellTool(const std::string& tool, const std::string& args,
		std::string& payload, std::string& errorCode, std::string& errorMessage)
	{
		using namespace editor;

		if (tool == "ui.dataload")
		{
			const std::string plugin = args;
			if (plugin.empty())
			{
				errorCode = "bad_args";
				errorMessage = "ui.dataload needs a plugin name, for example Fallout4.esm";
				return false;
			}

			// Constraint: two calls drive File > Data, because the CK runs the dialog's own modal loop
			// after the command, so this call must only open it and return.
			HWND dialog = FindDialogByTitle("Data");
			if (!dialog)
			{
				HWND frame = MainFrame();
				if (!frame)
				{
					errorCode = "no_frame";
					errorMessage = "the editor has no window with a menu bar yet";
					return false;
				}
				const int id = FindMenuCommand(GetMenu(frame), "Data");
				if (!id)
				{
					errorCode = "no_command";
					errorMessage = "the File menu has no Data item";
					return false;
				}
				PostMessageW(frame, WM_COMMAND, MAKEWPARAM(id, 0), 0);
				payload = "{\"stage\":\"opened\",\"id\":" + std::to_string(id) + "}";
				return true;
			}

			HWND list = FindListViewContaining(dialog, ".esm");
			if (!list)
			{
				payload = "{\"stage\":\"dialog\",\"plugin\":null}";
				return true;
			}

			const int row = FindListViewRow(list, plugin);
			if (row < 0)
			{
				errorCode = "no_plugin";
				errorMessage = "the Data dialog does not list " + plugin + " (is it in Data?)";
				return false;
			}

			// The checkbox is the state image, so it must be set rather than left to the click handler.
			SelectListViewRow(list, row);
			LVITEMW li{};
			li.stateMask = LVIS_STATEIMAGEMASK;
			li.state = INDEXTOSTATEIMAGEMASK(2);
			Send(list, LVM_SETITEMSTATE, row, reinterpret_cast<LPARAM>(&li));

			HWND ok = FindButtonByText(dialog, "OK");
			if (!ok) ok = FindButtonByText(dialog, "Open");
			if (!ok) ok = FindButtonByText(dialog, "&OK");
			if (ok) PostMessageW(ok, BM_CLICK, 0, 0);

			payload = "{\"stage\":\"ticked\",\"row\":" + std::to_string(row) + ",\"ok\":" + (ok ? "true" : "false") + "}";
			return true;
		}

		if (tool == "ui.cells")
		{
			// A cell list must be readable by name, so an agent can choose without guessing row indices.
			const std::string filter = Lower(StripAmp(args));
			HWND pane = FindDialogByTitle("Cell View");
			HWND list = pane ? FindFirstListView(pane) : nullptr;
			if (!list)
			{
				errorCode = "no_cell_view";
				errorMessage = "the editor has no Cell View list; load a master file first";
				return false;
			}

			const int count = static_cast<int>(Send(list, LVM_GETITEMCOUNT));
			std::string rows;
			int matched = 0;
			for (int i = 0; i < count && matched < 200; ++i)
			{
				std::string cell[3];
				for (int c = 0; c < 3; ++c)
				{
					wchar_t buf[512] = {};
					LVITEMW li{};
					li.iSubItem = c;
					li.pszText = buf;
					li.cchTextMax = 511;
					Send(list, LVM_GETITEMTEXTW, i, reinterpret_cast<LPARAM>(&li));
					cell[c] = U8(buf);
				}

				if (!filter.empty() &&
					Lower(cell[0]).find(filter) == std::string::npos &&
					Lower(cell[2]).find(filter) == std::string::npos)
					continue;

				if (!rows.empty()) rows += ",";
				rows += "{\"row\":" + std::to_string(i) + ",\"editorId\":" + Quote(cell[0]) +
					",\"formId\":" + Quote(cell[1]) + ",\"name\":" + Quote(cell[2]) + "}";
				++matched;
			}

			payload = "{\"total\":" + std::to_string(count) + ",\"matched\":" + std::to_string(matched) + ",\"rows\":[" + rows + "]}";
			return true;
		}

		if (tool == "ui.cellload")
		{
			const std::string name = args;
			int budget = 4000;
			CellTarget target;

			if (name.empty())
			{
				// Without a name the tool must report what the Cell View actually offers.
				for (HWND h : TopWindows())
				{
					if (!IsClass(h, L"#32770")) continue;
					for (HWND c : Children(h))
					{
						if (!IsClass(c, L"SysTreeView32") && !IsClass(c, L"SysListView32")) continue;
						const std::string items = ItemsJson(c, 40, 0);
						if (items.empty()) continue;
						char handle[24];
						std::snprintf(handle, sizeof handle, "%llX", HandleOf(c));
						payload += payload.empty() ? "" : ",";
						payload += "{\"window\":" + Quote(U8(WinText(h))) + ",\"control\":" + Quote(std::string("0x") + handle) +
							",\"title\":" + Quote(U8(WinText(c))) + ",\"items\":" + items + "}";
					}
				}
				if (payload.empty())
				{
					errorCode = "no_lists";
					errorMessage = "no list view or tree with items was found in the editor's panes";
					return false;
				}
				payload = "{\"candidates\":[" + payload + "]}";
				return true;
			}

			const std::string want = Lower(StripAmp(name));
			for (HWND h : TopWindows())
			{
				if (FindCellInChildren(h, want, target, budget))
					break;
			}

			if (!target.control)
			{
				errorCode = "no_cell";
				errorMessage = "no cell view item matches: " + name;
				return false;
			}

			if (target.tree)
			{
				Send(target.control, TVM_SELECTITEM, TVGN_CARET, reinterpret_cast<LPARAM>(target.node));
				Send(target.control, TVM_ENSUREVISIBLE, 0, reinterpret_cast<LPARAM>(target.node));
			}
			else
			{
				SelectListViewRow(target.control, target.row);
			}

			// Constraint: the cell opens through the Cell View's double-click handler, so the notification
			// must be sent to its parent rather than simulated as mouse input.
			NotifyParent(target.control, NM_DBLCLK, target.tree ? -1 : target.row);

			payload = "{\"cell\":" + Quote(target.text) + ",\"via\":" + Quote(target.tree ? "tree" : "list") + "}";
			return true;
		}


		errorCode = "unknown_tool";
		errorMessage = tool;
		return false;
	}
}
