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

	// Constraint: every pose writer must use one formula (rows right, forward, up).
	struct ViewMatrix
	{
		float m[16] = {};   // NiMatrix43 layout: three rows of three floats, padded to four
	};

	ViewMatrix MakeViewMatrix(float yaw, float pitch);

	// Constraint: verification must read the scene camera's view row, not the matrix the tool just wrote.
	bool ReadSceneView(void* camera, float out[3]);

	bool RunOrbitTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage);

	bool RunFrameTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage);

	// Constraint: the lens must be read and written through the slave NiCamera, which is what renders.
	bool RunLensTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage);

	// Constraint: an aim must keep the camera where it is, so the caller sees the view it already chose.
	bool RunAimTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage);
}
