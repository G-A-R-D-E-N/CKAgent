#pragma once

#include <cstddef>
#include <string>

namespace ckagent
{
	// The frame tool must receive resolved anchors, because only Agent.cpp holds the rebasing state.
	struct FrameTarget
	{
		void* camera = nullptr;
		void* cell = nullptr;
	};

	bool RunFrameTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage);
}
