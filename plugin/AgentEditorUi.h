#pragma once

#include <cstdint>
#include <string>

namespace ckagent
{
	// Every tool here must run on the editor thread: it sends window messages whose handlers
	// re-enter the editor, so the editor thread is a precondition for every call here.
	bool RunEditorTool(std::uintptr_t window, const std::string& tool, const std::string& args,
		std::string& payload, std::string& errorCode, std::string& errorMessage);
}
