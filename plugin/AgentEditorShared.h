#pragma once

#include <windows.h>

#include <string>
#include <vector>

namespace ckagent
{
	// Constraint: every helper here must be called on the editor thread, where the sends are inline.
	namespace editor
	{
		std::string U8(const std::wstring& w);
		std::string Lower(std::string s);
		std::string StripAmp(std::string s);
		std::string JsonEsc(const std::string& s);
		std::string Quote(const std::string& s);

		LRESULT Send(HWND h, UINT msg, WPARAM w = 0, LPARAM l = 0, DWORD ms = 20000);
		std::wstring ClassOf(HWND h);
		std::wstring WinText(HWND h);
		bool IsClass(HWND h, const wchar_t* name);

		unsigned long long HandleOf(HWND h);
		HWND ToHwnd(const std::string& text);
		std::vector<HWND> Children(HWND parent);
		std::vector<HWND> TopWindows();
		bool IsHiddenModal(HWND h);

		std::string WindowJson(HWND h);
		std::string ItemsJson(HWND h, int max, int offset);

		HWND MainFrame();
		int FindMenuCommand(HMENU m, const std::string& want, int depth = 3);
		HWND FindButtonByText(HWND root, const std::string& text);

		void NotifyParent(HWND control, UINT code, int item);
		void SelectListViewRow(HWND h, int index);
	}
}
