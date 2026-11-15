#pragma once

#include <string>

namespace ckagent
{
	// Constraint: the check module posts through the same resolver the pipeline uses, so captions cannot drift.
	bool PostNavmeshCommand(const std::string& caption, std::string& payload,
		std::string& errorCode, std::string& errorMessage);

	// Must run on the editor thread: it reads the loaded cell's forms and posts menu commands.
	// The cell pointer must be the render window's current cell, or null when no cell is loaded.
	bool RunNavmeshTool(const std::string& tool, const std::string& args, const void* cell,
		std::string& payload, std::string& errorCode, std::string& errorMessage);
}
