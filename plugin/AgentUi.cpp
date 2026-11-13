// CKAgent UI: diagnostics must land in the CK log, and the button must live in the CK frame itself.

#include <windows.h>
#include <commctrl.h>

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>

#include <CKPE.Logger.h>
#include <CKPE.PluginAPI.PluginAPI.h>

#include "AgentUi.h"

namespace ckagent
{
	namespace
	{
		constexpr UINT kMcpCommand = 0x4D01;      // idCommand of the toolbar button, so the frame can route it
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
		bool g_classReady = false;
		bool g_buttonAdded = false;
		bool g_frameMissingLogged = false;
		unsigned g_buttonAttempts = 0;

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

		std::wstring Widen(const std::string& text)
		{
			if (text.empty())
				return std::wstring();

			const int size = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
			std::wstring wide(static_cast<std::size_t>(size), L'\0');
			MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), wide.data(), size);
			return wide;
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
				g_edit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
					WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
					0, 0, 10, 10, hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
				if (g_edit)
					SendMessageW(g_edit, WM_SETFONT,
						reinterpret_cast<WPARAM>(GetStockObject(ANSI_FIXED_FONT)), TRUE);
				SetTimer(hwnd, kRefreshTimer, kRefreshInterval, nullptr);
				Refresh();
				return 0;

			case WM_SIZE:
				LayoutEdit(hwnd);
				return 0;

			case WM_TIMER:
				if (wParam == kRefreshTimer)
				{
					Refresh();
					return 0;
				}
				break;

			// The window must survive its own close button, because the toolbar button reopens it.
			case WM_CLOSE:
				KillTimer(hwnd, kRefreshTimer);
				ShowWindow(hwnd, SW_HIDE);
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
					WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 760, 420,
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

		BOOL CALLBACK FindToolbarProc(HWND child, LPARAM param);

		// The frame must be identified by what it owns: its class name is not stable across editor builds.
		BOOL CALLBACK FindFrameProc(HWND hwnd, LPARAM param)
		{
			DWORD pid = 0;
			GetWindowThreadProcessId(hwnd, &pid);
			if (pid != GetCurrentProcessId() || !IsWindowVisible(hwnd) || !GetMenu(hwnd))
				return TRUE;

			// A frame that carries a toolbar must win over any other top-level window with a menu.
			HWND toolbar = nullptr;
			EnumChildWindows(hwnd, FindToolbarProc, reinterpret_cast<LPARAM>(&toolbar));

			auto* best = reinterpret_cast<HWND*>(param);
			if (!*best || toolbar)
				*best = hwnd;

			return toolbar ? FALSE : TRUE;
		}

		// The toolbar must be searched for at any depth: CKPE keeps it inside a rebar, not on the frame.
		BOOL CALLBACK FindToolbarProc(HWND child, LPARAM param)
		{
			wchar_t cls[64]{};
			GetClassNameW(child, cls, 64);
			if (wcscmp(cls, L"ToolbarWindow32") == 0)
			{
				*reinterpret_cast<HWND*>(param) = child;
				return FALSE;
			}
			return TRUE;
		}

		bool InstallButton(HWND frame)
		{
			HWND toolbar = nullptr;
			EnumChildWindows(frame, FindToolbarProc, reinterpret_cast<LPARAM>(&toolbar));
			if (!toolbar)
				return false;

			const LRESULT index = SendMessageW(toolbar, TB_ADDSTRINGW, 0,
				reinterpret_cast<LPARAM>(L"MCP\0"));
			if (index < 0)
				return false;

			TBBUTTON button{};
			button.iBitmap = I_IMAGENONE;   // a text button must carry no image index
			button.idCommand = static_cast<int>(kMcpCommand);
			button.fsState = TBSTATE_ENABLED;
			button.fsStyle = BTNS_AUTOSIZE | BTNS_SHOWTEXT;
			button.iString = index;

			const LRESULT before = SendMessageW(toolbar, TB_BUTTONCOUNT, 0, 0);
			if (!SendMessageW(toolbar, TB_ADDBUTTONS, 1, reinterpret_cast<LPARAM>(&button)))
				return false;

			SendMessageW(toolbar, TB_AUTOSIZE, 0, 0);
			LogLine(FormatFrom("CKAgent: MCP button added to toolbar 0x%llX (%lld -> %lld buttons)",
				static_cast<unsigned long long>(reinterpret_cast<std::uintptr_t>(toolbar)),
				static_cast<long long>(before),
				static_cast<long long>(SendMessageW(toolbar, TB_BUTTONCOUNT, 0, 0))));
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
				LogLine("CKAgent: the CK frame was not found by class name");
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
			wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
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

		if (!g_buttonAdded)
		{
			g_buttonAdded = InstallButton(frame);

			// A missing toolbar must be reported once, or the retry loop stays silent about it.
			if (!g_buttonAdded && ++g_buttonAttempts == 40)
				LogLine("CKAgent: no toolbar found in the frame; the MCP button cannot be added");
		}

		return g_buttonAdded;
	}
}
