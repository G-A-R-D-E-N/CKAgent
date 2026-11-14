#pragma once

#include <windows.h>
#include <sddl.h>

#pragma comment(lib, "advapi32.lib")

// Wire protocol; the bridge must serialise one CALL at a time per connection.
// JSON must stay outside the editor process: the plugin only splits lines and composes replies.
// Frames must be UTF-8, newline terminated, single line, in the order HELO, CALL, RPLY.

namespace ckagent
{
	constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\ckagent";
	constexpr char kProtocolVersion[] = "1";

	// The drain window must live on the editor thread; work arrives as kDispatchDrain.
	constexpr wchar_t kDispatchClass[] = L"CKAgentDispatchWindow";
	constexpr UINT kDispatchDrain = WM_APP + 0x41;

	// Must match exactly: CKPE records class layouts and jump targets per build.
	constexpr char kSupportedEditorBuild[] = "1.11.246.0";

	// Constraint: a low-integrity client cannot write up to a medium pipe, so the pipe carries a low label.
	// Invariant: the descriptor is built once, and null means the caller falls back to the default pipe.
	inline SECURITY_ATTRIBUTES PipeSecurity()
	{
		static PSECURITY_DESCRIPTOR descriptor = nullptr;
		static bool attempted = false;

		if (!attempted)
		{
			attempted = true;
			ConvertStringSecurityDescriptorToSecurityDescriptorW(
				L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)S:(ML;;NW;;;LW)",
				SDDL_REVISION_1, &descriptor, nullptr);
			if (!descriptor) attempted = false;
		}

		SECURITY_ATTRIBUTES attributes{};
		attributes.nLength = sizeof(attributes);
		attributes.lpSecurityDescriptor = descriptor;
		attributes.bInheritHandle = FALSE;
		return attributes;
	}
}
