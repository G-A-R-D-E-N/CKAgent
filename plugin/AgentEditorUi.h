#pragma once

#include <windows.h>

#include <string>
#include <vector>

namespace ckagent
{
	namespace editor
	{
		// Constraint: the Data item lives under File, so a path resolves through the submenu it names.
		int ResolveMenuPath(HMENU menu, const std::vector<std::string>& path, std::size_t at, std::string& error);
	}

	// Every tool here must run on the editor thread: it sends window messages whose handlers
	// re-enter the editor, so the editor thread is a precondition for every call here.
	bool RunEditorTool(const std::string& tool, const std::string& args, const void* cell,
		std::string& payload, std::string& errorCode, std::string& errorMessage);
}
