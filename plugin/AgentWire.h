#pragma once

#include <windows.h>

#include <cstddef>
#include <string>

// Constraint: JSON must stay outside the plugin, so this unit only splits and joins newline frames.
namespace ckagent::wire
{
	bool ReadLine(HANDLE pipe, std::string& carry, std::string& line);
	bool WriteAll(HANDLE pipe, const std::string& text);
	std::string Trimmed(const std::string& text);
}
