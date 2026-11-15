#pragma once

#include <windows.h>
#include <sddl.h>

#include <string>

#pragma comment(lib, "advapi32.lib")

// Wire protocol; the bridge must serialise one CALL at a time per connection.
// JSON must stay outside the editor process: the plugin only splits lines and composes replies.
// Frames must be UTF-8, newline terminated, single line, in the order HELO, CALL, RPLY.

namespace ckagent
{
	// Invariant: the strict pipe is the one every client tries first, and the loose one exists only when asked for.
	constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\ckagent";
	constexpr wchar_t kLowPipeName[] = L"\\\\.\\pipe\\ckagent-low";
	constexpr char kProtocolVersion[] = "1";

	// The drain window must live on the editor thread; work arrives as kDispatchDrain.
	constexpr wchar_t kDispatchClass[] = L"CKAgentDispatchWindow";
	constexpr UINT kDispatchDrain = WM_APP + 0x41;

	// Must match exactly: CKPE records class layouts and jump targets per build.
	constexpr char kSupportedEditorBuild[] = "1.11.246.0";

	// Constraint: a descriptor with no mandatory label restricts nothing, so the strict pipe names medium explicitly.
	// Invariant: the descriptors are built once, and null means the caller falls back to the default pipe.
	inline SECURITY_ATTRIBUTES PipeSecurity(bool lowLabel)
	{
		static PSECURITY_DESCRIPTOR strict = nullptr;
		static PSECURITY_DESCRIPTOR loose = nullptr;
		static bool attempted = false;

		if (!attempted)
		{
			attempted = true;
			ConvertStringSecurityDescriptorToSecurityDescriptorW(
				L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)S:(ML;;NW;;;ME)",
				SDDL_REVISION_1, &strict, nullptr);
			ConvertStringSecurityDescriptorToSecurityDescriptorW(
				L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)S:(ML;;NW;;;LW)",
				SDDL_REVISION_1, &loose, nullptr);
			if (!strict || !loose) attempted = false;
		}

		SECURITY_ATTRIBUTES attributes{};
		attributes.nLength = sizeof(attributes);
		attributes.lpSecurityDescriptor = lowLabel ? loose : strict;
		attributes.bInheritHandle = FALSE;
		return attributes;
	}

	// Constraint: the opt-in must be a file the operator writes next to the DLL, because only the editor can open pipes.
	inline bool LowIntegrityPipeEnabled()
	{
		static int cached = -1;
		if (cached >= 0)
			return cached != 0;

		cached = 0;

		HMODULE module = nullptr;
		if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
			reinterpret_cast<LPCWSTR>(&LowIntegrityPipeEnabled), &module))
			return false;

		wchar_t path[MAX_PATH]{};
		if (!GetModuleFileNameW(module, path, MAX_PATH))
			return false;

		std::wstring ini(path);
		const auto slash = ini.find_last_of(L"\\/");
		if (slash == std::wstring::npos)
			return false;

		ini = ini.substr(0, slash + 1) + L"CKAgent.ini";
		cached = GetPrivateProfileIntW(L"transport", L"LowIntegrityPipe", 0, ini.c_str()) != 0 ? 1 : 0;
		return cached != 0;
	}
}
