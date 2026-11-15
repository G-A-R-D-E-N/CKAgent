#pragma once

#include <string>

namespace ckagent
{
	namespace navmeshcheck
	{
		// Must run on the editor thread: it posts the editor's check command and reads the report it writes.
		bool RunCheck(std::string& payload, std::string& errorCode, std::string& errorMessage);

		// Constraint: apply posts the check itself, so it opens the capture before that post and collects after it.
		void BeginCapture();
		bool Collect(std::string& payload, std::string&, std::string&);

		// Constraint: apply must classify the same lines the check captured, so the finished report is readable.
		std::string LastReportJson();

		// Constraint: apply must report the verdict a check would, so the server need not re-parse its lines.
		std::string LastVerdictFields();

		// Constraint: an abandoned apply may have opened a capture without posting, so it must be droppable.
		void Abandon();
	}
}
