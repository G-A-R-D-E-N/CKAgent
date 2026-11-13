#pragma once

#include <windows.h>

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
}
