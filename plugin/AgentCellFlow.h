#pragma once

#include <cstdint>
#include <string>

namespace ckagent
{
	// Invariant: status and cell answer from one placement read, so their numbers cannot disagree.
	struct Placement
	{
		bool hasSingleton = false;
		bool empty = true;
		bool interior = false;
		int gridX = 0;
		int gridY = 0;
		bool hasWorldSpace = false;
	};

	// Must be called on the editor thread; slot is the rebased address of the TES singleton pointer.
	Placement ReadPlacement(std::uintptr_t slot);

	// Constraint: cell and master-file flows must run on the editor thread, like every editor call.
	bool RunCellTool(std::uintptr_t window, const std::string& tool, const std::string& args,
		std::string& payload, std::string& errorCode, std::string& errorMessage);
}
