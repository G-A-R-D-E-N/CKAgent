#pragma once

#include <string>

namespace ckagent
{
	// Constraint: placement goes through the editor's own edit commands, so its entry takes the loaded cell.
	bool RunPlaceTool(const std::string& tool, const std::string& args, const void* cell,
		std::string& payload, std::string& errorCode, std::string& errorMessage);
}
