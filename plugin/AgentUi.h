#pragma once

#include <windows.h>

#include <string>

namespace ckagent
{
	// A diagnostic must reach the CK's own log, so a problem shows up where the user already looks.
	void LogLine(const std::string& text);

	// The button must exist before it can be pressed, so installation is retried until the frame appears.
	// Must be called on the editor thread; true means the button is in place.
	bool EnsureUi();

	// The window must be able to report its own binding state without the caller knowing the UI.
	void SetStateProvider(std::string (*provider)());
}
