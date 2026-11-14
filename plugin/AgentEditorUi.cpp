// Constraint: every call here must run on the editor thread, where SendMessage is handled inline.

#include <windows.h>
#include <commctrl.h>

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "AgentCellFlow.h"
#include "AgentEditorShared.h"
#include "AgentEditorUi.h"

namespace ckagent
{
	namespace editor
	{
		std::string U8(const std::wstring& w)
		{
			if (w.empty()) return {};
			const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
			if (n <= 0) return {};
			std::string s(static_cast<std::size_t>(n), 0);
			WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
			return s;
		}

		std::wstring W(const std::string& s)
		{
			if (s.empty()) return {};
			const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
			if (n <= 0) return {};
			std::wstring w(static_cast<std::size_t>(n), 0);
			MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
			return w;
		}

		std::string Lower(std::string s)
		{
			for (auto& c : s)
				c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
			return s;
		}

		// Menu and control captions carry accelerators, so a match must ignore '&'.
		std::string StripAmp(std::string s)
		{
			s.erase(std::remove(s.begin(), s.end(), '&'), s.end());
			return s;
		}

		// A reply must stay one line, so quotes and control characters are escaped.
		std::string JsonEsc(const std::string& s)
		{
			std::string out;
			out.reserve(s.size() + 8);
			for (unsigned char c : s)
			{
				switch (c)
				{
				case '"': out += "\\\""; break;
				case '\\': out += "\\\\"; break;
				case '\n': out += "\\n"; break;
				case '\r': out += "\\r"; break;
				case '\t': out += "\\t"; break;
				default:
					if (c < 0x20)
					{
						char buf[8];
						std::snprintf(buf, sizeof buf, "\\u%04X", c);
						out += buf;
					}
					else out += static_cast<char>(c);
				}
			}
			return out;
		}

		std::string Quote(const std::string& s) { return "\"" + JsonEsc(s) + "\""; }

		// Precondition: no call may block forever, because the editor UI thread may sit in a modal loop.
		LRESULT Send(HWND h, UINT msg, WPARAM w, LPARAM l, DWORD ms)
		{
			DWORD_PTR result = 0;
			if (!SendMessageTimeoutW(h, msg, w, l, SMTO_ABORTIFHUNG | SMTO_BLOCK, ms, &result))
				return 0;
			return static_cast<LRESULT>(result);
		}

		std::wstring ClassOf(HWND h)
		{
			wchar_t buf[128] = {};
			GetClassNameW(h, buf, 127);
			return buf;
		}

		std::wstring WinText(HWND h)
		{
			const auto len = static_cast<int>(Send(h, WM_GETTEXTLENGTH));
			if (len <= 0 || len > (1 << 20))
				return {};
			std::wstring s(static_cast<std::size_t>(len) + 1, 0);
			Send(h, WM_GETTEXT, s.size(), reinterpret_cast<LPARAM>(s.data()));
			s.resize(static_cast<std::size_t>(len));
			return s;
		}

		bool IsClass(HWND h, const wchar_t* name) { return _wcsicmp(ClassOf(h).c_str(), name) == 0; }
		unsigned long long HandleOf(HWND h) { return static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(h)); }
		HWND ToHwnd(const std::string& text) { return reinterpret_cast<HWND>(static_cast<ULONG_PTR>(std::strtoull(text.c_str(), nullptr, 0))); }

		std::vector<HWND> Children(HWND parent)
		{
			std::vector<HWND> out;
			for (HWND c = GetWindow(parent, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT))
				out.push_back(c);
			return out;
		}

		struct TopCtx { DWORD pid = 0; std::vector<HWND>* out = nullptr; };
		BOOL CALLBACK EnumTop(HWND h, LPARAM l)
		{
			auto* ctx = reinterpret_cast<TopCtx*>(l);
			DWORD pid = 0;
			GetWindowThreadProcessId(h, &pid);
			if (pid == ctx->pid)
				ctx->out->push_back(h);
			return TRUE;
		}

		std::vector<HWND> TopWindows()
		{
			std::vector<HWND> out;
			TopCtx ctx{ GetCurrentProcessId(), &out };
			EnumWindows(EnumTop, reinterpret_cast<LPARAM>(&ctx));
			return out;
		}

		// Invariant: a modal dialog can be created hidden while the editor is not foreground, leaving the
		// frame disabled with nothing to click, so such a dialog must be named in the dump.
		bool IsHiddenModal(HWND h)
		{
			if (IsWindowVisible(h) || !IsClass(h, L"#32770"))
				return false;
			HWND owner = GetWindow(h, GW_OWNER);
			return owner && IsWindowVisible(owner) && !IsWindowEnabled(owner);
		}

		std::string WindowJson(HWND h)
		{
			std::string s = "{";
			s += "\"hwnd\":" + Quote("0x" + [&] { char b[24]; std::snprintf(b, sizeof b, "%llX", HandleOf(h)); return std::string(b); }());
			s += ",\"class\":" + Quote(U8(ClassOf(h)));
			s += ",\"title\":" + Quote(U8(WinText(h)));
			s += ",\"visible\":";
			s += IsWindowVisible(h) ? "true" : "false";
			s += ",\"enabled\":";
			s += IsWindowEnabled(h) ? "true" : "false";
			if (GetMenu(h)) s += ",\"menu\":true";
			if (IsHiddenModal(h)) s += ",\"hiddenModal\":true";
			s += "}";
			return s;
		}

		// --- item lists (ordering follows each control's own row indices) ---

		std::string ListViewJson(HWND h, int max, int offset)
		{
			const int count = static_cast<int>(Send(h, LVM_GETITEMCOUNT));
			HWND header = reinterpret_cast<HWND>(Send(h, LVM_GETHEADER));
			int cols = header ? static_cast<int>(Send(header, HDM_GETITEMCOUNT)) : 1;
			if (cols < 1) cols = 1;
			if (cols > 32) cols = 32;

			std::string s = "{\"kind\":\"listview\",\"count\":" + std::to_string(count) + ",\"columns\":" + std::to_string(cols);
			s += ",\"headers\":[";
			for (int c = 0; c < cols && header; ++c)
			{
				wchar_t buf[256] = {};
				HDITEMW hi{};
				hi.mask = HDI_TEXT;
				hi.pszText = buf;
				hi.cchTextMax = 255;
				Send(header, HDM_GETITEMW, c, reinterpret_cast<LPARAM>(&hi));
				if (c) s += ",";
				s += Quote(U8(buf));
			}
			s += "],\"rows\":[";
			bool first = true;
			for (int i = offset; i < count && i < offset + max; ++i)
			{
				std::string cells;
				for (int c = 0; c < cols; ++c)
				{
					wchar_t buf[512] = {};
					LVITEMW li{};
					li.iSubItem = c;
					li.pszText = buf;
					li.cchTextMax = 511;
					Send(h, LVM_GETITEMTEXTW, i, reinterpret_cast<LPARAM>(&li));
					if (c) cells += ",";
					cells += Quote(U8(buf));
				}
				const UINT state = static_cast<UINT>(Send(h, LVM_GETITEMSTATE, i, LVIS_SELECTED | LVIS_STATEIMAGEMASK));
				const UINT image = (state & LVIS_STATEIMAGEMASK) >> 12;
				if (!first) s += ",";
				first = false;
				s += "{\"i\":" + std::to_string(i) + ",\"cells\":[" + cells + "]";
				if (state & LVIS_SELECTED) s += ",\"selected\":true";
				if (image) s += std::string(",\"checked\":") + (image == 2 ? "true" : "false");
				s += "}";
			}
			s += "]}";
			return s;
		}

		std::string TreeNodesJson(HWND h, HTREEITEM first, int& budget, int level)
		{
			std::string s = "[";
			bool firstNode = true;
			for (HTREEITEM it = first; it && budget > 0;
				it = reinterpret_cast<HTREEITEM>(Send(h, TVM_GETNEXTITEM, TVGN_NEXT, reinterpret_cast<LPARAM>(it))))
			{
				wchar_t buf[512] = {};
				TVITEMW tv{};
				tv.mask = TVIF_TEXT | TVIF_STATE | TVIF_HANDLE | TVIF_CHILDREN;
				tv.hItem = it;
				tv.pszText = buf;
				tv.cchTextMax = 511;
				tv.stateMask = TVIS_SELECTED | TVIS_EXPANDED;
				Send(h, TVM_GETITEMW, 0, reinterpret_cast<LPARAM>(&tv));
				--budget;

				char handle[24];
				std::snprintf(handle, sizeof handle, "%llX", static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(it)));

				if (!firstNode) s += ",";
				firstNode = false;
				s += "{\"handle\":" + Quote(std::string("0x") + handle) + ",\"text\":" + Quote(U8(buf));
				if (tv.state & TVIS_SELECTED) s += ",\"selected\":true";
				if (tv.state & TVIS_EXPANDED) s += ",\"expanded\":true";
				if (tv.cChildren)
				{
					HTREEITEM child = reinterpret_cast<HTREEITEM>(Send(h, TVM_GETNEXTITEM, TVGN_CHILD, reinterpret_cast<LPARAM>(it)));
					if (child && (tv.state & TVIS_EXPANDED) && level < 6 && budget > 0)
						s += ",\"children\":" + TreeNodesJson(h, child, budget, level + 1);
					else
						s += ",\"hasChildren\":true";
				}
				s += "}";
			}
			s += "]";
			return s;
		}

		std::string SimpleListJson(HWND h, bool combo, int max, int offset)
		{
			const int count = static_cast<int>(Send(h, combo ? CB_GETCOUNT : LB_GETCOUNT));
			const int sel = static_cast<int>(Send(h, combo ? CB_GETCURSEL : LB_GETCURSEL));
			std::string s = "{\"kind\":";
			s += combo ? "\"combo\"" : "\"listbox\"";
			s += ",\"count\":" + std::to_string(count) + ",\"selected\":" + std::to_string(sel) + ",\"rows\":[";
			bool first = true;
			for (int i = offset; i < count && i < offset + max; ++i)
			{
				const int len = static_cast<int>(Send(h, combo ? CB_GETLBTEXTLEN : LB_GETTEXTLEN, i));
				std::wstring text;
				if (len > 0 && len < 4096)
				{
					text.assign(static_cast<std::size_t>(len) + 1, 0);
					Send(h, combo ? CB_GETLBTEXT : LB_GETTEXT, i, reinterpret_cast<LPARAM>(text.data()));
					text.resize(static_cast<std::size_t>(len));
				}
				if (!first) s += ",";
				first = false;
				s += Quote(U8(text));
			}
			s += "]}";
			return s;
		}

		std::string ItemsJson(HWND h, int max, int offset)
		{
			if (IsClass(h, L"SysListView32")) return ListViewJson(h, max, offset);
			if (IsClass(h, L"ListBox")) return SimpleListJson(h, false, max, offset);
			if (IsClass(h, L"ComboBox") || IsClass(h, L"ComboBoxEx32")) return SimpleListJson(h, true, max, offset);
			if (IsClass(h, L"SysTreeView32"))
			{
				int budget = max;
				const auto root = reinterpret_cast<HTREEITEM>(Send(h, TVM_GETNEXTITEM, TVGN_ROOT, 0));
				return "{\"kind\":\"tree\",\"count\":" + std::to_string(static_cast<int>(Send(h, TVM_GETCOUNT))) +
					",\"nodes\":" + TreeNodesJson(h, root, budget, 0) + "}";
			}
			if (IsClass(h, L"SysTabControl32"))
			{
				const int count = static_cast<int>(Send(h, TCM_GETITEMCOUNT));
				std::string s = "{\"kind\":\"tab\",\"count\":" + std::to_string(count) + ",\"rows\":[";
				for (int i = 0; i < count && i < max; ++i)
				{
					wchar_t buf[256] = {};
					TCITEMW ti{};
					ti.mask = TCIF_TEXT;
					ti.pszText = buf;
					ti.cchTextMax = 255;
					Send(h, TCM_GETITEMW, i, reinterpret_cast<LPARAM>(&ti));
					if (i) s += ",";
					s += Quote(U8(buf));
				}
				s += "]}";
				return s;
			}
			return {};
		}

		std::string ControlJson(HWND h, int depth, int maxItems);

		std::string ControlsJson(HWND parent, int depth, int maxItems)
		{
			std::string s = "[";
			bool first = true;
			for (HWND c : Children(parent))
			{
				if (!first) s += ",";
				first = false;
				s += ControlJson(c, depth, maxItems);
			}
			s += "]";
			return s;
		}

		std::string ControlJson(HWND h, int depth, int maxItems)
		{
			std::string s = "{\"hwnd\":" + Quote("0x" + [&] { char b[24]; std::snprintf(b, sizeof b, "%llX", HandleOf(h)); return std::string(b); }());
			s += ",\"id\":" + std::to_string(GetDlgCtrlID(h));
			s += ",\"class\":" + Quote(U8(ClassOf(h)));
			s += ",\"text\":" + Quote(U8(WinText(h)));
			s += ",\"visible\":";
			s += IsWindowVisible(h) ? "true" : "false";
			s += ",\"enabled\":";
			s += IsWindowEnabled(h) ? "true" : "false";

			const std::string items = ItemsJson(h, maxItems, 0);
			if (!items.empty() && depth >= 0)
				s += ",\"items\":" + items;

			if (depth > 0)
			{
				const std::string kids = ControlsJson(h, depth - 1, maxItems);
				if (kids.size() > 2)
					s += ",\"children\":" + kids;
			}
			s += "}";
			return s;
		}

		// The frame must be found by what it owns: the editor's window class name is not stable.
		HWND MainFrame()
		{
			for (HWND h : TopWindows())
				if (IsWindowVisible(h) && GetMenu(h) && IsWindowEnabled(h))
					return h;
			for (HWND h : TopWindows())
				if (IsWindowVisible(h) && GetMenu(h))
					return h;
			return nullptr;
		}

		// Invariant: a hidden modal dialog is why a disabled frame has no visible dialog to click.
		std::string WindowsJson()
		{
			std::string s = "[";
			bool first = true;
			for (HWND h : TopWindows())
			{
				if (!first) s += ",";
				first = false;
				s += WindowJson(h);
			}
			s += "]";
			return s;
		}

		// A dialog must be described well enough to decide about it: owner, state and its buttons.
		std::string ModalJson(HWND h)
		{
			std::string buttons;
			int shown = 0;
			for (HWND c : Children(h))
			{
				if (!IsClass(c, L"Button")) continue;
				const std::string caption = U8(WinText(c));
				if (caption.empty()) continue;
				if (shown++) buttons += ",";
				buttons += Quote(caption);
				if (shown >= 6) break;
			}

			HWND owner = GetWindow(h, GW_OWNER);
			char handle[24];
			char ownerHandle[24];
			std::snprintf(handle, sizeof handle, "%llX", HandleOf(h));
			std::snprintf(ownerHandle, sizeof ownerHandle, "%llX", HandleOf(owner));

			std::string s = "{\"hwnd\":" + Quote(std::string("0x") + handle);
			s += ",\"title\":" + Quote(U8(WinText(h)));
			s += ",\"owner\":" + Quote(std::string("0x") + ownerHandle);
			s += ",\"visible\":";
			s += IsWindowVisible(h) ? "true" : "false";
			s += ",\"enabled\":";
			s += IsWindowEnabled(h) ? "true" : "false";
			s += ",\"ownerEnabled\":";
			s += (owner && IsWindowEnabled(owner)) ? "true" : "false";
			s += ",\"blocking\":";
			s += IsHiddenModal(h) ? "true" : "false";
			s += ",\"buttons\":[" + buttons + "]}";
			return s;
		}

		// Constraint: the payload must be built from Quote, because an escaped-quote literal here does not compile.
		std::string DismissedJson(const std::string& by, const std::string& handle)
		{
			return std::string("{") + Quote("dismissed") + ":1," + Quote("by") + ":" + Quote(by) + "," +
				Quote("hwnd") + ":" + Quote(handle) + "}";
		}

		std::string ModalsJson()
		{
			std::string out = "[";
			bool first = true;
			for (HWND h : TopWindows())
			{
				if (!IsClass(h, L"#32770")) continue;
				if (!first) out += ",";
				first = false;
				out += ModalJson(h);
			}
			out += "]";
			return out;
		}

		std::string HiddenModalTitle()
		{
			for (HWND h : TopWindows())
				if (IsHiddenModal(h)) return U8(WinText(h));
			return {};
		}

		// --- menu (ids differ per build, so a command must be resolved by text) ---

		std::string CleanMenuText(std::wstring w)
		{
			const auto tab = w.find(L'\t');
			if (tab != std::wstring::npos) w.resize(tab);
			return U8(w);
		}

		std::string MenuJson(HMENU m, int depth)
		{
			std::string s = "[";
			const int n = GetMenuItemCount(m);
			for (int i = 0; i < n; ++i)
			{
				MENUITEMINFOW mi{};
				mi.cbSize = sizeof mi;
				mi.fMask = MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_FTYPE;
				GetMenuItemInfoW(m, i, TRUE, &mi);
				wchar_t buf[256] = {};
				GetMenuStringW(m, i, buf, 255, MF_BYPOSITION);

				if (i) s += ",";
				if (mi.fType & MFT_SEPARATOR) { s += "{\"separator\":true}"; continue; }
				s += "{\"index\":" + std::to_string(i) + ",\"text\":" + Quote(U8(buf)) +
					",\"id\":" + std::to_string(static_cast<int>(mi.wID));
				if (mi.fState & MFS_GRAYED) s += ",\"disabled\":true";
				if (mi.fState & MFS_CHECKED) s += ",\"checked\":true";
				if (mi.hSubMenu && depth > 0) s += ",\"submenu\":" + MenuJson(mi.hSubMenu, depth - 1);
				s += "}";
			}
			s += "]";
			return s;
		}

		// A menu command must be addressed by text: ids differ between editor builds.
		int FindMenuCommand(HMENU m, const std::string& want, int depth)
		{
			const std::string target = Lower(StripAmp(want));
			const int n = GetMenuItemCount(m);
			for (int pass = 0; pass < 2; ++pass)
			{
				for (int i = 0; i < n; ++i)
				{
					wchar_t buf[256] = {};
					GetMenuStringW(m, i, buf, 255, MF_BYPOSITION);
					const std::string text = Lower(StripAmp(CleanMenuText(buf)));
					if (text.empty()) continue;
					const bool hit = pass == 0 ? text == target : text.find(target) != std::string::npos;
					if (!hit) continue;

					MENUITEMINFOW mi{};
					mi.cbSize = sizeof mi;
					mi.fMask = MIIM_ID | MIIM_SUBMENU | MIIM_STATE;
					GetMenuItemInfoW(m, i, TRUE, &mi);
					if (mi.hSubMenu && depth > 0)
					{
						const int inner = FindMenuCommand(mi.hSubMenu, want, depth - 1);
						if (inner > 0) return inner;
					}
					if (!(mi.fState & MFS_GRAYED) && mi.wID)
						return static_cast<int>(mi.wID);
				}
			}
			return 0;
		}

		// A menu path must be resolved level by level, so a repeated caption cannot pick the wrong menu.
		int ResolveMenuPath(HMENU menu, const std::vector<std::string>& path, std::size_t at, std::string& error)
		{
			const std::string want = Lower(StripAmp(path[at]));
			const int count = GetMenuItemCount(menu);
			for (int pass = 0; pass < 2; ++pass)
			{
				for (int i = 0; i < count; ++i)
				{
					wchar_t buf[256] = {};
					GetMenuStringW(menu, i, buf, 255, MF_BYPOSITION);
					const std::string text = Lower(StripAmp(CleanMenuText(buf)));
					if (text.empty()) continue;
					if (pass == 0 ? text != want : text.find(want) == std::string::npos) continue;

					MENUITEMINFOW mi{};
					mi.cbSize = sizeof mi;
					mi.fMask = MIIM_ID | MIIM_SUBMENU | MIIM_STATE;
					GetMenuItemInfoW(menu, i, TRUE, &mi);

					if (at + 1 < path.size())
					{
						if (!mi.hSubMenu)
						{
							error = "no submenu under: " + path[at];
							return 0;
						}
						return ResolveMenuPath(mi.hSubMenu, path, at + 1, error);
					}

					if (mi.fState & MFS_GRAYED)
					{
						error = "menu item is disabled: " + path[at];
						return 0;
					}

					return static_cast<int>(mi.wID);
				}
			}

			error = "menu item not found: " + path[at];
			return 0;
		}

		// --- notification (must reach the parent, never the control) ---

		// Constraint: the control's own double-click handler is what opens a cell, and it must be
		// reached by the parent's WM_NOTIFY, so synthesised mouse messages must not be used.
		void NotifyParent(HWND control, UINT code, int item)
		{
			HWND parent = GetParent(control);
			if (!parent) return;

			NMITEMACTIVATE nm{};
			nm.hdr.hwndFrom = control;
			nm.hdr.idFrom = static_cast<UINT_PTR>(GetDlgCtrlID(control));
			nm.hdr.code = code;
			nm.iItem = item;
			Send(parent, WM_NOTIFY, nm.hdr.idFrom, reinterpret_cast<LPARAM>(&nm));
		}

		void SelectListViewRow(HWND h, int index)
		{
			LVITEMW li{};
			li.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
			Send(h, LVM_SETITEMSTATE, static_cast<WPARAM>(-1), reinterpret_cast<LPARAM>(&li));   // invariant: one selection at a time
			li.state = LVIS_SELECTED | LVIS_FOCUSED;
			Send(h, LVM_SETITEMSTATE, index, reinterpret_cast<LPARAM>(&li));
			Send(h, LVM_ENSUREVISIBLE, index, FALSE);
		}

	}

	bool RunEditorTool(const std::string& tool, const std::string& args,
		std::string& payload, std::string& errorCode, std::string& errorMessage)
	{
		using namespace editor;

		// Args must be space separated; a tool that requires free text takes the rest of the line.
		std::vector<std::string> parts;
		{
			std::string rest = args;
			std::size_t at = 0;
			while (at < rest.size())
			{
				while (at < rest.size() && rest[at] == ' ') ++at;
				const std::size_t end = rest.find(' ', at);
				if (end == std::string::npos) { if (at < rest.size()) parts.push_back(rest.substr(at)); break; }
				if (end > at) parts.push_back(rest.substr(at, end - at));
				at = end;
			}
		}

		auto argText = [&](std::size_t index) -> std::string { return index < parts.size() ? parts[index] : std::string(); };
		auto argInt = [&](std::size_t index, int fallback) -> int { return index < parts.size() ? std::atoi(parts[index].c_str()) : fallback; };

		if (tool == "ui.windows")
		{
			payload = WindowsJson();
			return true;
		}

		if (tool == "ui.frame")
		{
			HWND frame = MainFrame();
			if (!frame)
			{
				errorCode = "no_frame";
				errorMessage = "the editor has no window with a menu bar yet";
				return false;
			}
			payload = WindowJson(frame);
			return true;
		}

		if (tool == "ui.tree")
		{
			HWND h = argText(0).empty() ? MainFrame() : ToHwnd(argText(0));
			if (!h || !IsWindow(h))
			{
				errorCode = "bad_hwnd";
				errorMessage = "ui.tree needs a live hwnd";
				return false;
			}
			payload = ControlJson(h, argInt(1, 3), argInt(2, 120));
			return true;
		}

		if (tool == "ui.items")
		{
			HWND h = ToHwnd(argText(0));
			if (!h || !IsWindow(h))
			{
				errorCode = "bad_hwnd";
				errorMessage = "ui.items needs a live hwnd";
				return false;
			}
			payload = ItemsJson(h, argInt(1, 200), argInt(2, 0));
			if (payload.empty())
			{
				errorCode = "no_items";
				errorMessage = "that control has no item list: " + U8(ClassOf(h));
				return false;
			}
			return true;
		}

		if (tool == "ui.select")
		{
			HWND h = ToHwnd(argText(0));
			if (!h || !IsWindow(h))
			{
				errorCode = "bad_hwnd";
				errorMessage = "ui.select needs a live hwnd";
				return false;
			}
			const std::string which = argText(1);
			if (IsClass(h, L"SysTreeView32"))
			{
				const auto item = reinterpret_cast<HTREEITEM>(static_cast<ULONG_PTR>(std::strtoull(which.c_str(), nullptr, 0)));
				if (!item)
				{
					errorCode = "bad_selection";
					errorMessage = "a tree needs the node handle from ui.items";
					return false;
				}
				Send(h, TVM_SELECTITEM, TVGN_CARET, reinterpret_cast<LPARAM>(item));
			}
			else if (IsClass(h, L"SysListView32"))
			{
				SelectListViewRow(h, std::atoi(which.c_str()));
			}
			else if (IsClass(h, L"ListBox") || IsClass(h, L"ComboBox"))
			{
				const int index = std::atoi(which.c_str());
				Send(h, IsClass(h, L"ComboBox") ? CB_SETCURSEL : LB_SETCURSEL, index);
				PostMessageW(GetParent(h), WM_COMMAND,
					MAKEWPARAM(GetDlgCtrlID(h), IsClass(h, L"ComboBox") ? CBN_SELCHANGE : LBN_SELCHANGE),
					reinterpret_cast<LPARAM>(h));
			}
			else
			{
				errorCode = "unsupported_control";
				errorMessage = "ui.select does not handle " + U8(ClassOf(h));
				return false;
			}
			payload = std::string("{\"selected\":") + Quote(which) + "}";
			return true;
		}

		if (tool == "ui.notify")
		{
			HWND h = ToHwnd(argText(0));
			if (!h || !IsWindow(h))
			{
				errorCode = "bad_hwnd";
				errorMessage = "ui.notify needs a live hwnd";
				return false;
			}
			const std::string code = Lower(argText(1));
			const int index = argInt(2, -1);
			if (code == "dblclk" && IsClass(h, L"SysListView32") && index >= 0)
				SelectListViewRow(h, index);   // precondition: the handler acts on the selected row

			const UINT message = code == "rclick" ? NM_RCLICK : code == "click" ? NM_CLICK : NM_DBLCLK;
			NotifyParent(h, message, index);
			payload = std::string("{\"notified\":") + Quote(code) + ",\"control\":" + Quote("0x" + [&] { char b[24]; std::snprintf(b, sizeof b, "%llX", HandleOf(h)); return std::string(b); }()) + "}";
			return true;
		}

		if (tool == "ui.command")
		{
			const std::string target = argText(0);
			const bool dry = Lower(argText(1)) == "dry";
			HWND frame = MainFrame();
			if (!frame)
			{
				errorCode = "no_frame";
				errorMessage = "the editor has no window with a menu bar yet";
				return false;
			}

			if (target.empty())
			{
				errorCode = "bad_args";
				errorMessage = "ui.command needs a caption, a path like File/Data, or a numeric id";
				return false;
			}

			// A path must be resolved level by level, because one caption can appear under several menus.
			int id = 0;
			std::string error;
			if (std::isdigit(static_cast<unsigned char>(target[0])))
			{
				id = std::atoi(target.c_str());
			}
			else if (target.find('/') != std::string::npos || target.find('>') != std::string::npos)
			{
				std::string flat = target;
				for (auto& c : flat)
					if (c == '>') c = '/';

				std::vector<std::string> path;
				std::size_t at = 0;
				while (at <= flat.size())
				{
					const std::size_t end = flat.find('/', at);
					std::string part = flat.substr(at, end == std::string::npos ? std::string::npos : end - at);
					while (!part.empty() && part.front() == ' ') part.erase(part.begin());
					while (!part.empty() && part.back() == ' ') part.pop_back();
					if (!part.empty()) path.push_back(part);
					if (end == std::string::npos) break;
					at = end + 1;
				}

				if (!path.empty())
					id = ResolveMenuPath(GetMenu(frame), path, 0, error);
			}
			else
			{
				id = FindMenuCommand(GetMenu(frame), target);
			}

			if (!id)
			{
				errorCode = "no_command";
				errorMessage = error.empty() ? ("no enabled menu item matches: " + target) : error;
				return false;
			}

			if (!dry)
				PostMessageW(frame, WM_COMMAND, MAKEWPARAM(id, 0), 0);

			payload = "{\"id\":" + std::to_string(id) + ",\"dry\":" + (dry ? "true" : "false") + "}";
			return true;
		}

		if (tool == "ui.modals")
		{
			// Detection must be separable from the fix: listing reports, and reveal is asked for by handle.
			if (Lower(argText(0)) == "reveal")
			{
				HWND h = ToHwnd(argText(1));
				if (!h || !IsWindow(h) || !IsClass(h, L"#32770"))
				{
					errorCode = "bad_hwnd";
					errorMessage = "ui.modals reveal needs the hwnd of a dialog from ui.modals";
					return false;
				}

				ShowWindowAsync(h, SW_SHOW);
				char handle[24];
				std::snprintf(handle, sizeof handle, "%llX", HandleOf(h));
				payload = "{\"revealed\":" + Quote(std::string("0x") + handle) + ",\"title\":" + Quote(U8(WinText(h))) + "}";
				return true;
			}

			payload = ModalsJson();
			return true;
		}

		if (tool == "ui.click")
		{
			// Dismissing what a command opened needs a button press, and it must be by caption, not id.
			HWND root = ToHwnd(argText(0));
			if (!root || !IsWindow(root))
			{
				errorCode = "bad_hwnd";
				errorMessage = "ui.click needs a live window as its first argument";
				return false;
			}

			std::string caption;
			const std::size_t space = args.find(' ');
			if (space != std::string::npos)
			{
				caption = args.substr(space + 1);
				while (!caption.empty() && caption.front() == ' ') caption.erase(caption.begin());
			}

			if (caption.empty())
			{
				errorCode = "bad_args";
				errorMessage = "ui.click needs a button caption after the window handle";
				return false;
			}

			HWND button = FindButtonByText(root, caption);
			if (!button)
			{
				errorCode = "no_button";
				errorMessage = "no button matches: " + caption;
				return false;
			}

			if (!IsWindowEnabled(button))
			{
				errorCode = "disabled";
				errorMessage = "the button is disabled: " + caption;
				return false;
			}

			PostMessageW(button, BM_CLICK, 0, 0);
			char handle[24];
			std::snprintf(handle, sizeof handle, "%llX", HandleOf(button));
			payload = "{\"clicked\":" + Quote(caption) + ",\"hwnd\":" + Quote(std::string("0x") + handle) + "}";
			return true;
		}

		if (tool == "ui.dismiss")
		{
			// Constraint: the button must abandon the pending work, never apply it, so OK is never pressed.
			HWND dialog = ToHwnd(argText(0));
			if (!dialog || !IsWindow(dialog) || !IsClass(dialog, L"#32770"))
			{
				errorCode = "bad_hwnd";
				errorMessage = "ui.dismiss needs the hwnd of a dialog from ui.modals";
				return false;
			}

			const wchar_t* const safe[] = { L"Cancel", L"Close" };
			for (int i = 0; i < 2; ++i)
			{
				HWND button = FindButtonByText(dialog, U8(safe[i]));
				if (!button || !IsWindowEnabled(button)) continue;
				PostMessageW(button, BM_CLICK, 0, 0);
				payload = DismissedJson(U8(safe[i]), argText(0));
				return true;
			}

			// Constraint: a modal with no Cancel button must still end, and WM_CLOSE is its own Esc.
			PostMessageW(dialog, WM_CLOSE, 0, 0);
			payload = DismissedJson("WM_CLOSE", argText(0));
			return true;
		}

		if (tool == "ui.menu")
		{
			HWND frame = argText(0).empty() ? MainFrame() : ToHwnd(argText(0));
			if (!frame || !GetMenu(frame))
			{
				errorCode = "no_menu";
				errorMessage = "that window has no menu bar";
				return false;
			}
			payload = MenuJson(GetMenu(frame), argInt(1, 2));
			return true;
		}

		if (tool == "ui.dataload" || tool == "ui.cells" || tool == "ui.cellload")
			return RunCellTool(tool, args, payload, errorCode, errorMessage);

		errorCode = "unknown_tool";
		errorMessage = tool;
		return false;
	}
}
