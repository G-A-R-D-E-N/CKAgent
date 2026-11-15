#pragma once

#include <string>
#include <vector>

namespace ckagent
{
	namespace navmeshfindings
	{
		// Constraint: a raw pipe client must see a check's findings without the server's parser.
		std::string FieldsJson(const std::vector<std::string>& lines);
	}
}
