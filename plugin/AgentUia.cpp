// Constraint: the name box of the editor's save dialog is a DirectUI element with no window handle, so window
// messages and synthetic keys cannot reach it. UI Automation can: it addresses the element itself.

#include <windows.h>
#include <objbase.h>
#include <oleacc.h>
#include <oleauto.h>
#include <UIAutomation.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "AgentUia.h"

namespace ckagent
{
	namespace uia
	{
		namespace
		{
			IUIAutomation* Automation()
			{
				static IUIAutomation* automation = nullptr;
				if (automation) return automation;

				// The editor thread may already be in a different apartment, which is fine: the object is already there.
				const HRESULT ready = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
				if (FAILED(ready) && ready != RPC_E_CHANGED_MODE && ready != S_FALSE) return nullptr;

				IUIAutomation* created = nullptr;
				if (FAILED(CoCreateInstance(__uuidof(CUIAutomation), nullptr, CLSCTX_INPROC_SERVER,
					__uuidof(IUIAutomation), reinterpret_cast<void**>(&created))))
				{
					return nullptr;
				}

				automation = created;
				return automation;
			}

			std::wstring Wide(const std::string& s)
			{
				if (s.empty()) return {};
				const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
				if (n <= 0) return {};
				std::wstring w(static_cast<std::size_t>(n), 0);
				MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
				return w;
			}

			std::string Quote(const std::string& s)
			{
				std::string out = "\"";
				for (const char c : s)
				{
					if (c == '"' || c == '\\') out += '\\';
					if (c == '\n') { out += "\\n"; continue; }
					out += c;
				}
				out += "\"";
				return out;
			}

			BSTR Name(IUIAutomationElement* element)
			{
				BSTR name = nullptr;
				if (!element || FAILED(element->get_CurrentName(&name))) return nullptr;
				return name;
			}

			// Constraint: the box is an Edit with no name of its own on this build, so the first Edit in the dialog wins.
			IUIAutomationElement* FirstOfType(IUIAutomation* automation, IUIAutomationElement* root, CONTROLTYPEID type)
			{
				VARIANT wanted;
				VariantInit(&wanted);
				wanted.vt = VT_I4;
				wanted.lVal = type;

				IUIAutomationCondition* condition = nullptr;
				if (FAILED(automation->CreatePropertyCondition(UIA_ControlTypePropertyId, wanted, &condition))) return nullptr;

				IUIAutomationElement* found = nullptr;
				root->FindFirst(TreeScope_Descendants, condition, &found);
				condition->Release();
				return found;
			}

			std::string FromWide(const std::wstring& w)
			{
				if (w.empty()) return {};
				const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
				if (n <= 0) return {};
				std::string s(static_cast<std::size_t>(n), 0);
				WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
				return s;
			}

			IUIAutomationElement* FindWindowByTitle(IUIAutomation* automation, IUIAutomationElement* desktop)
			{
				VARIANT wanted;
				VariantInit(&wanted);
				wanted.vt = VT_BSTR;
				wanted.bstrVal = SysAllocString(L"Select Target Plugin");

				IUIAutomationCondition* condition = nullptr;
				if (FAILED(automation->CreatePropertyCondition(UIA_NamePropertyId, wanted, &condition)))
				{
					VariantClear(&wanted);
					return nullptr;
				}

				IUIAutomationElement* window = nullptr;
				desktop->FindFirst(TreeScope_Children, condition, &window);
				condition->Release();
				VariantClear(&wanted);

				return window;
			}

			std::vector<IUIAutomationElement*> AllOfType(IUIAutomation* automation, IUIAutomationElement* root,
				CONTROLTYPEID type);

			std::vector<IUIAutomationElement*> AllButtons(IUIAutomation* automation, IUIAutomationElement* root)
			{
				return AllOfType(automation, root, UIA_ButtonControlTypeId);
			}

			std::vector<IUIAutomationElement*> AllOfType(IUIAutomation* automation, IUIAutomationElement* root,
				CONTROLTYPEID type)
			{
				std::vector<IUIAutomationElement*> buttons;

				VARIANT wanted;
				VariantInit(&wanted);
				wanted.vt = VT_I4;
				wanted.lVal = type;

				IUIAutomationCondition* condition = nullptr;
				if (FAILED(automation->CreatePropertyCondition(UIA_ControlTypePropertyId, wanted, &condition))) return buttons;

				IUIAutomationElementArray* array = nullptr;
				if (SUCCEEDED(root->FindAll(TreeScope_Descendants, condition, &array)) && array)
				{
					int count = 0;
					array->get_Length(&count);
					for (int i = 0; i < count; ++i)
					{
						IUIAutomationElement* button = nullptr;
						if (SUCCEEDED(array->GetElement(i, &button)) && button) buttons.push_back(button);
					}
					array->Release();
				}

				condition->Release();
				return buttons;
			}
		}

		bool SaveByListItem(const std::string& args, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			const std::size_t space = args.find(' ');
			if (space == std::string::npos)
			{
				errorCode = "bad_args";
				errorMessage = "uia.save needs a dialog handle and the plugin file name to select";
				return false;
			}

			const std::string handle = args.substr(0, space);
			std::string file = args.substr(space + 1);
			while (!file.empty() && file.front() == ' ') file.erase(file.begin());

			IUIAutomation* automation = Automation();
			if (!automation)
			{
				errorCode = "uia_unavailable";
				errorMessage = "UI Automation could not be started in the editor process";
				return false;
			}

			const HWND dialog = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(std::strtoull(handle.c_str(), nullptr, 16)));
			IUIAutomationElement* root = nullptr;

			// Constraint: the dialog is found the instant it appears, and its UIA provider is ready a moment later.
			for (int attempt = 0; attempt < 15 && !root; ++attempt)
			{
				if (attempt) Sleep(200);
				if (FAILED(automation->ElementFromHandle(dialog, &root))) root = nullptr;
			}

			if (!root)
			{
				errorCode = "no_dialog";
				errorMessage = "UI Automation cannot see that dialog handle";
				return false;
			}

			// Constraint: the shell list is virtualised, so every item is read before the wanted name is compared.
			std::vector<IUIAutomationElement*> items = AllOfType(automation, root, UIA_ListItemControlTypeId);
			IUIAutomationElement* match = nullptr;
			std::wstring wanted = Wide(file);
			CharLowerBuffW(&wanted[0], static_cast<DWORD>(wanted.size()));

			for (IUIAutomationElement* item : items)
			{
				BSTR name = Name(item);
				if (name)
				{
					std::wstring have = name;
					SysFreeString(name);
					if (!have.empty()) CharLowerBuffW(&have[0], static_cast<DWORD>(have.size()));
					if (have == wanted && !match) match = item;
				}

				if (item != match) item->Release();
			}

			if (!match)
			{
				root->Release();
				errorCode = "no_list_item";
				errorMessage = file + " is not in the dialog's list; the editor caches Data at startup, so the file "
					"must exist before the editor loads";
				return false;
			}

			IUIAutomationSelectionItemPattern* selection = nullptr;
			const HRESULT picked = match->GetCurrentPatternAs(UIA_SelectionItemPatternId,
				__uuidof(IUIAutomationSelectionItemPattern), reinterpret_cast<void**>(&selection));
			if (SUCCEEDED(picked) && selection)
			{
				selection->Select();
				selection->Release();
			}
			match->Release();

			bool invited = false;
			for (IUIAutomationElement* button : AllOfType(automation, root, UIA_ButtonControlTypeId))
			{
				BSTR name = Name(button);
				const bool save = name && std::wstring(name).find(L"Save") != std::wstring::npos;
				if (name) SysFreeString(name);

				if (!invited && save)
				{
					IUIAutomationInvokePattern* press = nullptr;
					if (SUCCEEDED(button->GetCurrentPatternAs(UIA_InvokePatternId,
						__uuidof(IUIAutomationInvokePattern), reinterpret_cast<void**>(&press))) && press)
					{
						invited = SUCCEEDED(press->Invoke());
						press->Release();
					}
				}

				button->Release();
			}

			root->Release();

			if (!invited)
			{
				errorCode = "no_save_button";
				errorMessage = "the file was selected but no Save button could be invoked";
				return false;
			}

			payload = "{\"selected\":" + Quote(file) + ",\"saved\":true}";
			return true;
		}

		bool AnswerFileDialog(const std::string& args, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			const std::size_t space = args.find(' ');
			if (space == std::string::npos)
			{
				errorCode = "bad_args";
				errorMessage = "uia.answer needs a dialog handle and the file name to type into it";
				return false;
			}

			const std::string handle = args.substr(0, space);
			std::string file = args.substr(space + 1);
			while (!file.empty() && file.front() == ' ') file.erase(file.begin());
			if (file.empty())
			{
				errorCode = "bad_args";
				errorMessage = "uia.answer needs a file name after the dialog handle";
				return false;
			}

			IUIAutomation* automation = Automation();
			if (!automation)
			{
				errorCode = "uia_unavailable";
				errorMessage = "UI Automation could not be started in the editor process";
				return false;
			}

			const HWND dialog = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(std::strtoull(handle.c_str(), nullptr, 16)));
			IUIAutomationElement* root = nullptr;
			HRESULT found = automation->ElementFromHandle(dialog, &root);
			std::string from = "handle";

			// Constraint: the handle can be stale by the time the name is set, so the desktop is searched by title.
			if (FAILED(found) || !root)
			{
				IUIAutomationElement* desktop = nullptr;
				if (SUCCEEDED(automation->GetRootElement(&desktop)) && desktop)
				{
					root = FindWindowByTitle(automation, desktop);
					desktop->Release();
					from = "title";
				}
			}

			if (!root)
			{
				errorCode = "no_dialog";
				errorMessage = "UI Automation cannot see the save dialog (handle call returned " +
					std::to_string(static_cast<long>(found)) + ")";
				return false;
			}

			IUIAutomationElement* edit = FirstOfType(automation, root, UIA_EditControlTypeId);
			if (!edit)
			{
				root->Release();
				errorCode = "no_file_box";
				errorMessage = "the dialog has no editable file name box this build exposes to UI Automation";
				return false;
			}

			IUIAutomationValuePattern* value = nullptr;
			const HRESULT gotValue = edit->GetCurrentPatternAs(UIA_ValuePatternId, __uuidof(IUIAutomationValuePattern),
				reinterpret_cast<void**>(&value));
			edit->Release();
			if (FAILED(gotValue) || !value)
			{
				root->Release();
				errorCode = "no_value_pattern";
				errorMessage = "the file name box does not expose a value pattern";
				return false;
			}

			const std::wstring wanted = Wide(file);
			BSTR text = SysAllocString(wanted.c_str());
			const HRESULT set = value->SetValue(text);
			SysFreeString(text);
			value->Release();
			if (FAILED(set))
			{
				root->Release();
				errorCode = "set_failed";
				errorMessage = "UI Automation refused the file name";
				return false;
			}

			bool invoked = false;
			for (IUIAutomationElement* button : AllButtons(automation, root))
			{
				BSTR name = Name(button);
				const std::wstring caption = name ? name : L"";
				if (name) SysFreeString(name);

				if (!invoked && caption == L"Save")
				{
					IUIAutomationInvokePattern* press = nullptr;
					if (SUCCEEDED(button->GetCurrentPatternAs(UIA_InvokePatternId,
						__uuidof(IUIAutomationInvokePattern), reinterpret_cast<void**>(&press))) && press)
					{
						invoked = SUCCEEDED(press->Invoke());
						press->Release();
					}
				}

				button->Release();
			}

			root->Release();

			if (!invoked)
			{
				errorCode = "no_save_button";
				errorMessage = "the file name was set but no Save button could be invoked";
				return false;
			}

			payload = "{\"filled\":" + Quote(file) + ",\"foundBy\":" + Quote(from) + ",\"saved\":true}";
			return true;
		}
	}
}
