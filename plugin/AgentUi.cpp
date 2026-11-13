// CKAgent UI: diagnostics must land in the CK log, and the window must match the editor's theme.

#include <windows.h>

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <fstream>
#include <mutex>
#include <string>

#include <CKPE.Logger.h>
#include <CKPE.PluginAPI.PluginAPI.h>

#include "AgentUi.h"

namespace ckagent
{
	namespace
	{
		constexpr UINT kMcpCommand = 0x4D01;      // menu id, so the frame can route it to the window
		constexpr UINT_PTR kRefreshTimer = 3;     // timer that repaints the transcript while the window is open
		constexpr UINT kRefreshInterval = 600;
		constexpr std::size_t kKeepLines = 400;   // the transcript must stay bounded or the window grows forever
		constexpr wchar_t kActivityClass[] = L"CKAgentActivity";
		constexpr wchar_t kActivityTitle[] = L"CK Agent \u2014 MCP";

		std::mutex g_logMutex;
		std::deque<std::string> g_lines;
		std::string (*g_stateProvider)() = nullptr;

		WNDPROC g_frameProc = nullptr;
		HWND g_activity = nullptr;
		HWND g_edit = nullptr;
		HFONT g_font = nullptr;
		HBRUSH g_background = nullptr;
		bool g_classReady = false;
		bool g_menuAdded = false;
		bool g_frameMissingLogged = false;

		// The window must look like the editor's own panes, so the theme comes from CKPE's config.
		struct Theme
		{
			COLORREF background{ RGB(30, 30, 30) };
			COLORREF text{ RGB(220, 220, 220) };
			std::wstring font{ L"Consolas" };
			int size{ 10 };
		};

		Theme g_theme;

		// A UI message must be formatted in one place, so the log and the window agree.
		std::string FormatFrom(const char* fmt, ...)
		{
			char buf[512];
			va_list args;
			va_start(args, fmt);
			std::vsnprintf(buf, sizeof buf, fmt, args);
			va_end(args);
			return buf;
		}

		std::string Narrow(const std::wstring& text)
		{
			std::string out;
			for (wchar_t c : text)
				out.push_back(c < 128 ? static_cast<char>(c) : '?');
			return out;
		}

		std::wstring Widen(const std::string& text)
		{
			if (text.empty())
				return std::wstring();

			const int size = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
			std::wstring wide(static_cast<std::size_t>(size), L'\0');
			MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), wide.data(), size);
			return wide;
		}

		std::wstring ConfigPath(const wchar_t* name)
		{
			wchar_t path[MAX_PATH]{};
			GetModuleFileNameW(nullptr, path, MAX_PATH);
			std::wstring full(path);
			const std::size_t slash = full.find_last_of(L"\\/");
			if (slash != std::wstring::npos)
				full.erase(slash + 1);
			return full + name;
		}

		// A value must be read from a "key = value" line, so both toml files parse the same way.
		bool ReadValue(const std::wstring& path, const std::string& key, std::string& value)
		{
			std::ifstream file(Narrow(path));
			if (!file)
				return false;

			std::string line;
			while (std::getline(file, line))
			{
				const std::size_t eq = line.find('=');
				if (eq == std::string::npos)
					continue;

				std::string name = line.substr(0, eq);
				name.erase(0, name.find_first_not_of(" \t"));
				name.erase(name.find_last_not_of(" \t") + 1);
				if (name != key)
					continue;

				value = line.substr(eq + 1);
				value.erase(0, value.find_first_not_of(" \t"));
				value.erase(value.find_last_not_of(" \t\r") + 1);
				return true;
			}
			return false;
		}

		// A custom theme stores colors as [r,g,b], so the order must not be confused with COLORREF.
		bool ReadColor(const std::wstring& path, const std::string& key, COLORREF& color)
		{
			std::string value;
			if (!ReadValue(path, key, value))
				return false;

			int r = 0, g = 0, b = 0;
			if (std::sscanf(value.c_str(), "[%d,%d,%d]", &r, &g, &b) != 3)
				return false;

			color = RGB(r, g, b);
			return true;
		}

		Theme LoadTheme()
		{
			Theme theme;
			const std::wstring base = ConfigPath(L"CreationKitPlatformExtended.toml");

			std::string value;
			bool dark = true;
			int id = 2;

			if (ReadValue(base, "bUIDarkTheme", value))
				dark = value == "true";
			if (ReadValue(base, "uUIDarkThemeId", value))
				id = std::atoi(value.c_str());
			if (ReadValue(base, "sFont", value) && value.size() > 2)
			{
				value.erase(0, value.find_first_not_of("'\""));
				value.erase(value.find_last_not_of("'\"") + 1);
				theme.font = Widen(value);
			}
			if (ReadValue(base, "nFontSize", value))
				theme.size = std::atoi(value.c_str());

			if (!dark)
			{
				theme.background = RGB(240, 240, 240);
				theme.text = RGB(32, 32, 32);
			}
			else if (id == 1)
			{
				theme.background = RGB(30, 30, 30);
				theme.text = RGB(224, 224, 224);
			}
			else if (id == 2)
			{
				theme.background = RGB(27, 32, 45);
				theme.text = RGB(198, 208, 220);
			}

			// A custom palette must win, because it is what the editor is actually drawing with.
			if (dark && id == 3)
			{
				const std::wstring custom = ConfigPath(L"CreationKitPlatformExtendedCustomTheme.toml");
				COLORREF color{};
				if (ReadColor(custom, "rEdit_BkColor", color))
					theme.background = color;
				if (ReadColor(custom, "rText_3", color))
					theme.text = color;
				else if (ReadColor(custom, "rText_2", color))
					theme.text = color;
			}

			return theme;
		}

		std::string Transcript()
		{
			std::string text;

			if (g_stateProvider)
			{
				text += g_stateProvider();
				text += "\r\n---\r\n";
			}

			std::lock_guard<std::mutex> lock(g_logMutex);
			for (const auto& line : g_lines)
			{
				text += line;
				text += "\r\n";
			}
			return text;
		}

		void Refresh()
		{
			if (!g_edit)
				return;

			// The window must show the newest transcript, so the edit is replaced as a whole.
			SetWindowTextW(g_edit, Widen(Transcript()).c_str());
			SendMessageW(g_edit, EM_SETSEL, static_cast<WPARAM>(-1), static_cast<LPARAM>(-1));
			SendMessageW(g_edit, EM_SCROLLCARET, 0, 0);
		}

		void LayoutEdit(HWND hwnd)
		{
			if (!g_edit)
				return;

			RECT client{};
			GetClientRect(hwnd, &client);
			MoveWindow(g_edit, 0, 0, client.right, client.bottom, TRUE);
		}

		LRESULT CALLBACK ActivityProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
		{
			switch (msg)
			{
			case WM_CREATE:
				g_theme = LoadTheme();
				g_background = CreateSolidBrush(g_theme.background);

				// The theme must be visible in the log, so a wrong palette is diagnosable without a screenshot.
				LogLine(FormatFrom("CKAgent: theme background #%02X%02X%02X text #%02X%02X%02X font %s %d",
					GetRValue(g_theme.background), GetGValue(g_theme.background), GetBValue(g_theme.background),
					GetRValue(g_theme.text), GetGValue(g_theme.text), GetBValue(g_theme.text),
					Narrow(g_theme.font).c_str(), g_theme.size));

				g_edit = CreateWindowExW(0, L"EDIT", L"",
					WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
					0, 0, 10, 10, hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
				if (g_edit)
				{
					// The font must be the one the editor uses, or the transcript reads differently.
					g_font = CreateFontW(-MulDiv(g_theme.size, GetDeviceCaps(GetDC(hwnd), LOGPIXELSY), 72), 0, 0, 0,
						FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
						CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY, FIXED_PITCH | FF_MODERN, g_theme.font.c_str());
					if (g_font)
						SendMessageW(g_edit, WM_SETFONT, reinterpret_cast<WPARAM>(g_font), TRUE);
				}

				SetTimer(hwnd, kRefreshTimer, kRefreshInterval, nullptr);
				Refresh();
				return 0;

			case WM_SIZE:
				LayoutEdit(hwnd);
				return 0;

			case WM_ERASEBKGND:
			{
				RECT client{};
				GetClientRect(hwnd, &client);
				FillRect(reinterpret_cast<HDC>(wParam), &client, g_background);
				return 1;
			}

			// An edit control must be told both colors, or it keeps the system palette.
			case WM_CTLCOLOREDIT:
			case WM_CTLCOLORSTATIC:
			{
				HDC dc = reinterpret_cast<HDC>(wParam);
				SetTextColor(dc, g_theme.text);
				SetBkColor(dc, g_theme.background);
				return reinterpret_cast<LRESULT>(g_background);
			}

			case WM_TIMER:
				if (wParam == kRefreshTimer)
				{
					Refresh();
					return 0;
				}
				break;

			// The window must survive its own close button, because the menu item reopens it.
			case WM_CLOSE:
				KillTimer(hwnd, kRefreshTimer);
				ShowWindow(hwnd, SW_HIDE);
				return 0;

			case WM_DESTROY:
				if (g_font)
				{
					DeleteObject(g_font);
					g_font = nullptr;
				}
				if (g_background)
				{
					DeleteObject(g_background);
					g_background = nullptr;
				}
				return 0;
			}

			return DefWindowProcW(hwnd, msg, wParam, lParam);
		}

		void ToggleActivity()
		{
			if (!g_classReady)
				return;

			if (!g_activity)
			{
				g_activity = CreateWindowExW(WS_EX_TOOLWINDOW, kActivityClass, kActivityTitle,
					WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 780, 440,
					nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
			}

			if (!g_activity)
				return;

			const bool visible = IsWindowVisible(g_activity) != FALSE;
			ShowWindow(g_activity, visible ? SW_HIDE : SW_SHOW);
			if (!visible)
				SetForegroundWindow(g_activity);
		}

		// The frame must keep its own behaviour, so every other message goes to the previous procedure.
		LRESULT CALLBACK FrameProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
		{
			if (msg == WM_COMMAND && LOWORD(wParam) == kMcpCommand)
			{
				ToggleActivity();
				return 0;
			}

			return CallWindowProcW(g_frameProc, hwnd, msg, wParam, lParam);
		}

		BOOL CALLBACK FindFrameProc(HWND hwnd, LPARAM param)
		{
			DWORD pid = 0;
			GetWindowThreadProcessId(hwnd, &pid);
			if (pid != GetCurrentProcessId() || !IsWindowVisible(hwnd) || !GetMenu(hwnd))
				return TRUE;

			*reinterpret_cast<HWND*>(param) = hwnd;
			return FALSE;
		}

		// CKPE draws its toolbar itself, so a real menu entry must be the visible control.
		bool InstallMenu(HWND frame)
		{
			HMENU bar = GetMenu(frame);
			if (!bar)
				return false;

			HMENU popup = CreatePopupMenu();
			if (!popup)
				return false;

			AppendMenuW(popup, MF_STRING, kMcpCommand, L"Show activity");
			if (!AppendMenuW(bar, MF_POPUP | MF_STRING, reinterpret_cast<UINT_PTR>(popup), L"MCP"))
			{
				DestroyMenu(popup);
				return false;
			}

			DrawMenuBar(frame);
			return true;
		}
	}

	void SetStateProvider(std::string (*provider)())
	{
		g_stateProvider = provider;
	}

	void LogLine(const std::string& text)
	{
		{
			std::lock_guard<std::mutex> lock(g_logMutex);
			g_lines.push_back(text);
			if (g_lines.size() > kKeepLines)
				g_lines.pop_front();
		}

		// The CK log must carry the same line, so the editor's Log Window shows it next to engine output.
		CKPE::_MESSAGE("%s", text.c_str());
		CKPE::PluginAPI::_MESSAGE("%s", text.c_str());
	}

	bool EnsureUi()
	{
		HWND frame = nullptr;
		EnumWindows(FindFrameProc, reinterpret_cast<LPARAM>(&frame));

		if (!frame)
		{
			// A missing frame must be reported once, or the retry loop hides the real problem.
			if (!g_frameMissingLogged)
			{
				g_frameMissingLogged = true;
				LogLine("CKAgent: the CK frame was not found yet");
			}
			return false;
		}

		if (!g_classReady)
		{
			WNDCLASSEXW wc{};
			wc.cbSize = sizeof wc;
			wc.lpfnWndProc = ActivityProc;
			wc.hInstance = GetModuleHandleW(nullptr);
			wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
			wc.hbrBackground = nullptr;   // the window paints itself so the theme color is exact
			wc.lpszClassName = kActivityClass;
			g_classReady = RegisterClassExW(&wc) != 0;
			if (!g_classReady)
			{
				LogLine(FormatFrom("CKAgent: activity window class failed (%lu)", GetLastError()));
				return false;
			}
		}

		if (!g_frameProc)
		{
			g_frameProc = reinterpret_cast<WNDPROC>(
				SetWindowLongPtrW(frame, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(FrameProc)));
			if (!g_frameProc)
			{
				LogLine(FormatFrom("CKAgent: frame subclass failed (%lu)", GetLastError()));
				return false;
			}
		}

		if (!g_menuAdded)
		{
			g_menuAdded = InstallMenu(frame);
			if (g_menuAdded)
				LogLine(FormatFrom("CKAgent: MCP menu added to frame 0x%llX",
					static_cast<unsigned long long>(reinterpret_cast<std::uintptr_t>(frame))));
			else
				LogLine(FormatFrom("CKAgent: the frame menu could not be extended (%lu)", GetLastError()));
		}

		return g_menuAdded;
	}
}
