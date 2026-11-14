#pragma once

#include <string>

namespace ckagent
{
	// Constraint: cell and master-file flows must run on the editor thread, like every editor call.
	bool RunCellTool(const std::string& tool, const std::string& args,
		std::string& payload, std::string& errorCode, std::string& errorMessage);
}
