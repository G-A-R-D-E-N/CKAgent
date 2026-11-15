#include "AgentWire.h"

namespace ckagent::wire
{
	bool ReadLine(HANDLE pipe, std::string& carry, std::string& line)
	{
		for (;;)
		{
			const std::size_t newline = carry.find('\n');
			if (newline != std::string::npos)
			{
				line = carry.substr(0, newline);
				carry.erase(0, newline + 1);
				if (!line.empty() && line.back() == '\r')
					line.pop_back();
				return true;
			}

			char buf[4096];
			DWORD read = 0;
			if (!ReadFile(pipe, buf, sizeof buf, &read, nullptr) || read == 0)
				return false;

			carry.append(buf, read);
			if (carry.size() > (1 << 20)) // a runaway client must not grow this forever
				return false;
		}
	}

	bool WriteAll(HANDLE pipe, const std::string& text)
	{
		std::size_t sent = 0;
		while (sent < text.size())
		{
			DWORD wrote = 0;
			if (!WriteFile(pipe, text.data() + sent, static_cast<DWORD>(text.size() - sent), &wrote, nullptr) || wrote == 0)
				return false;
			sent += wrote;
		}
		return true;
	}

	// Constraint: a logged reply must not carry its newline into the window transcript.
	std::string Trimmed(const std::string& text)
	{
		std::size_t end = text.size();
		while (end > 0 && (text[end - 1] == '\n' || text[end - 1] == '\r'))
			--end;
		return text.substr(0, end);
	}
}
