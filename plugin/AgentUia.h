#pragma once

#include <string>

namespace ckagent
{
	namespace uia
	{
		// Constraint: the editor's save dialog keeps its file name box inside DirectUIHWND with no handle,
		// so the name is set through UI Automation and the Save button is invoked the same way.
		        bool AnswerFileDialog(const std::string& args, std::string& payload,
			std::string& errorCode, std::string& errorMessage);

		// Constraint: this build's name box exposes no value pattern, so the existing target file is selected in the
		// dialog's list instead - which is also what tells the editor which plugin to write.
		bool SaveByListItem(const std::string& args, std::string& payload,
			std::string& errorCode, std::string& errorMessage);
	}
}
