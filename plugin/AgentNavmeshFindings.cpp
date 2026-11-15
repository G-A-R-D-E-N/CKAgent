// Constraint: this table is checked against NavmeshVerdict.cs by tools/navmesh-command.test.ps1, so it cannot drift.

#include <windows.h>

#include <cctype>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include "AgentEditorShared.h"
#include "AgentNavmeshFindings.h"

namespace ckagent
{
	namespace navmeshfindings
	{
		using namespace editor;

		// Constraint: one reply must stay bounded, so at most 200 findings are reported.
		constexpr std::size_t kMaxFindings = 200;
		constexpr std::size_t kMaxLineLength = 1000;
		constexpr std::size_t kMaxFieldLength = 120;

		enum class Severity
		{
			Error,
			Warning
		};

		struct Rule
		{
			const char* fragment;
			Severity severity;
		};

		// Constraint: every fragment is a literal of a message this editor build writes, never a keyword guess.
		const Rule Rules[] =
		{
			{ "Bad index in CheckNavMesh", Severity::Error },
			{ "is degenerate", Severity::Error },
			{ "has invalid cover data", Severity::Warning },
			{ "is altered but not checked out", Severity::Error },
			{ "Potentially overlapping navmesh detected", Severity::Warning },
			{ "invalid door portal", Severity::Warning },
			{ "portal pointing to invalid NavMesh ID", Severity::Warning },
			{ "Found Two Road markers", Severity::Warning },
			{ "Degenerate triangles found in CheckNavMesh", Severity::Error },
			{ "point to the same triangle", Severity::Error },
			{ "Opposite normals in linked triangles", Severity::Error },
			{ "Triangle missing a link", Severity::Error },
			{ "Triangle linking is mismatched", Severity::Error },
			{ "Linked triangle vertices do not match", Severity::Error },
			{ "NAVMESH ERROR", Severity::Error },
			{ "has warnings that need to be fixed", Severity::Warning },
			{ "has a large number of triangles", Severity::Warning },
			{ "Unconnected NavMesh", Severity::Warning },
			{ "small number of triangles in a single navmesh", Severity::Warning }
		};

		// Constraint: a line that only announces the check is recognisable but is not itself a finding.
		const char* Informational[] =
		{
			"Checking all navmeshes in WorldSpace",
			"Checking out Navmeshes and removing current portals",
			"Checking Navmesh Info Map",
			"Check NavMeshes"
		};

		std::string LowerOf(const std::string& text)
		{
			std::string lower(text);
			for (char& c : lower)
				c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
			return lower;
		}

		std::string Trim(const std::string& text)
		{
			std::size_t first = 0;
			while (first < text.size() && (text[first] == ' ' || text[first] == '\t')) ++first;

			std::size_t last = text.size();
			while (last > first && (text[last - 1] == ' ' || text[last - 1] == '\t' || text[last - 1] == '\r')) --last;

			return text.substr(first, last - first);
		}

		// Constraint: a cell name is taken from the message's own 'in cell' clause or its 'Cell name,' clause.
		std::string CellOf(const std::string& line)
		{
			const std::string lower = LowerOf(line);
			std::size_t at = lower.find(" in cell ");
			std::size_t skip = 9;
			if (at == std::string::npos)
			{
				at = lower.find(" cell ");
				skip = 6;
			}

			if (at == std::string::npos) return std::string();

			const std::size_t start = at + skip;
			std::size_t end = start;
			while (end < line.size() && line[end] != ',' && line[end] != ')' && line[end] != '(') ++end;

			const std::string cell = Trim(line.substr(start, end - start));
			return cell.size() > kMaxFieldLength ? cell.substr(0, kMaxFieldLength) : cell;
		}

		// Constraint: the navmesh id is the first eight hex digit token, which is how this build writes it.
		std::string NavmeshOf(const std::string& line)
		{
			std::size_t at = 0;
			while (at + 8 <= line.size())
			{
				const std::string token = line.substr(at, 8);
				bool hex = true;
				for (char c : token)
				{
					const bool digit = c >= '0' && c <= '9';
					const bool upper = c >= 'A' && c <= 'F';
					const bool lower = c >= 'a' && c <= 'f';
					if (!digit && !upper && !lower) { hex = false; break; }
				}

				if (hex && (at == 0 || line[at - 1] == ' ' || line[at - 1] == '(' || line[at - 1] == '='))
					return token;

				++at;
			}

			return std::string();
		}

		struct Tally
		{
			std::uint32_t errors = 0;
			std::uint32_t warnings = 0;
			std::uint32_t recognized = 0;
			std::string verdict = "unknown";
		};

		std::string FieldsJson(const std::vector<std::string>& lines)
		{
			std::string findings;
			std::size_t count = 0;
			Tally tally;

			for (const std::string& line : lines)
			{
				const std::string text = line.substr(0, kMaxLineLength);
				const std::string lower = LowerOf(text);
				bool matched = false;

				for (const Rule& rule : Rules)
				{
					if (lower.find(LowerOf(rule.fragment)) == std::string::npos) continue;

					++tally.recognized;
					matched = true;

					if (rule.severity == Severity::Error) ++tally.errors;
					else ++tally.warnings;

					// Constraint: the cap bounds the reply, so past it the line is counted but not reported.
					if (count < kMaxFindings)
					{
						const std::string cell = CellOf(text);
						const std::string navmesh = NavmeshOf(text);

						if (!findings.empty()) findings += ",";
						findings += std::string("{\"severity\":\"") + (rule.severity == Severity::Error ? "error" : "warning") +
							"\",\"cell\":" + (cell.empty() ? std::string("null") : Quote(cell)) +
							",\"navmesh\":" + (navmesh.empty() ? std::string("null") : Quote(navmesh)) +
							",\"message\":" + Quote(rule.fragment) +
							",\"line\":" + Quote(text) + "}";
						++count;
					}

					break;
				}

				if (matched) continue;

				for (const char* informational : Informational)
				{
					if (lower.find(LowerOf(informational)) == std::string::npos) continue;
					++tally.recognized;
					break;
				}
			}

			// Constraint: silence is not a pass, so a report with nothing recognisable in it stays unknown.
			if (tally.errors > 0) tally.verdict = "errors";
			else if (tally.warnings > 0) tally.verdict = "warnings";
			else if (tally.recognized > 0) tally.verdict = "clean";

			return std::string("\"verdict\":") + Quote(tally.verdict) +
				",\"counts\":{\"errors\":" + std::to_string(tally.errors) +
				",\"warnings\":" + std::to_string(tally.warnings) +
				",\"recognized\":" + std::to_string(tally.recognized) +
				",\"reported\":" + std::to_string(count) + "}" +
				",\"findings\":[" + findings + "]";
		}
	}
}
