// Constraint: the check's own report is captured here, so a verdict never depends on re-reading a log offset.

#include <windows.h>

#include <cstdint>
#include <fstream>
#include <string>
#include <utility>
#include <vector>

#include "AgentEditorShared.h"
#include "AgentNavmesh.h"
#include "AgentNavmeshCheck.h"
#include "AgentNavmeshFindings.h"

namespace ckagent
{
	namespace navmeshcheck
	{
		using namespace editor;

		// Constraint: a check that keeps writing for longer than this is reported unsettled rather than waited on.
		constexpr ULONGLONG kCheckSettleMs = 15000;
		constexpr std::size_t kMaxCheckLines = 4000;
		// Constraint: one reply must stay bounded, so at most the last 256 KiB of the report is read.
		constexpr std::uint64_t kMaxCheckBytes = 256 * 1024;
		constexpr std::size_t kMaxLineLength = 1000;

		struct CheckRun
		{
			bool active = false;
			bool moved = false;
			int stage = 0;
			ULONGLONG started = 0;
			std::wstring path;
			std::string source;
			std::uint64_t startOffset = 0;
			std::uint64_t offset = 0;
			std::vector<std::string> lines;
		};

		CheckRun g_check;

		struct CheckSnapshot
		{
			bool valid = false;
			std::string source;
			std::uint64_t startOffset = 0;
			std::vector<std::string> lines;
		};

		CheckSnapshot g_last;

		void ResetCheck()
		{
			g_check = CheckRun();
		}

		void Abandon()
		{
			if (!g_check.active) return;

			ResetCheck();
		}

		std::wstring ModuleDirectory()
		{
			HMODULE module = nullptr;
			if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
				reinterpret_cast<LPCWSTR>(&ModuleDirectory), &module))
				return std::wstring();

			wchar_t path[MAX_PATH] = {};
			const DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
			if (!length) return std::wstring();

			const std::wstring full(path, length);
			const std::size_t cut = full.find_last_of(L"\\/");
			return cut == std::wstring::npos ? std::wstring() : full.substr(0, cut);
		}

		std::wstring ParentOf(const std::wstring& path)
		{
			if (path.empty()) return std::wstring();

			const std::size_t cut = path.find_last_of(L"\\/");
			return cut == std::wstring::npos ? std::wstring() : path.substr(0, cut);
		}

		// Constraint: the plugin sits in <game>\CKPEPlugins, so the report path comes from its own module, not a setting.
		std::wstring ReportPath()
		{
			const std::wstring game = ParentOf(ModuleDirectory());
			if (game.empty()) return std::wstring();

			return game + L"\\Logs\\CKPE\\CreationKitPlatformExtended.log";
		}

		std::uint64_t FileSize(const std::wstring& path)
		{
			std::ifstream file(path, std::ios::binary | std::ios::ate);
			if (!file) return 0;

			const std::streamoff size = file.tellg();
			return size > 0 ? static_cast<std::uint64_t>(size) : 0;
		}

		bool ReadAppended(const std::wstring& path, const std::uint64_t offset, std::uint64_t& end, std::vector<std::string>& lines)
		{
			std::ifstream file(path, std::ios::binary | std::ios::ate);
			if (!file) return false;

			const std::streamoff size = file.tellg();
			if (size < 0) return false;

			end = static_cast<std::uint64_t>(size);
			const std::uint64_t from = offset <= end ? offset : 0;
			if (end <= from) return true;

			const std::uint64_t skip = end - from > kMaxCheckBytes ? end - from - kMaxCheckBytes : 0;
			const std::uint64_t begin = from + skip;
			file.seekg(static_cast<std::streamoff>(begin), std::ios::beg);
			if (!file) return false;

			std::string text(static_cast<std::size_t>(end - begin), '\0');
			file.read(text.data(), static_cast<std::streamsize>(text.size()));
			if (!file && !file.eof()) return false;

			std::size_t at = 0;
			while (at <= text.size())
			{
				const std::size_t next = text.find('\n', at);
				std::string line = text.substr(at, next == std::string::npos ? std::string::npos : next - at);

				// Constraint: a partly written last line must be dropped, or its fragment reads as a finding.
				if (next == std::string::npos)
				{
					end = begin + at;
					break;
				}

				while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
				if (!line.empty()) lines.push_back(line.substr(0, kMaxLineLength));

				at = next + 1;
			}

			return true;
		}

		std::string SourceJson()
		{
			return g_check.source.empty() ? std::string("null") : Quote(g_check.source);
		}

		std::string SettledJson()
		{
			return std::to_string(GetTickCount64() - g_check.started);
		}

		std::string PendingJson(const char* stage)
		{
			return std::string("{\"stage\":") + Quote(stage) +
				",\"pending\":true,\"done\":false,\"source\":" + SourceJson() +
				",\"startOffset\":" + std::to_string(g_check.startOffset) +
				",\"offset\":" + std::to_string(g_check.offset) +
				",\"lineCount\":" + std::to_string(g_check.lines.size()) +
				",\"settledMs\":" + SettledJson() + "}";
		}

		std::string DoneJson(const bool settled)
		{
			std::string lines;
			for (const std::string& line : g_check.lines)
			{
				if (!lines.empty()) lines += ",";
				lines += Quote(line);
			}

			const std::string source = SourceJson();
			const std::string fields = navmeshfindings::FieldsJson(g_check.lines);

			return std::string("{\"stage\":\"done\",\"pending\":false,\"done\":true,\"settled\":") + (settled ? "true" : "false") +
				",\"source\":" + source +
				",\"startOffset\":" + std::to_string(g_check.startOffset) +
				",\"settledMs\":" + SettledJson() +
				"," + fields +
				",\"lines\":[" + lines + "]" +
				",\"report\":{\"source\":" + source +
				",\"count\":" + std::to_string(g_check.lines.size()) +
				",\"startOffset\":" + std::to_string(g_check.startOffset) +
				"," + fields + "}}";
		}

		void BeginCapture()
		{
			ResetCheck();
			g_last = CheckSnapshot();

			g_check.path = ReportPath();
			if (!g_check.path.empty() && GetFileAttributesW(g_check.path.c_str()) != INVALID_FILE_ATTRIBUTES)
			{
				g_check.source = U8(g_check.path);
				// Constraint: the offset is taken before the post, so only what this check wrote is read back.
				g_check.startOffset = FileSize(g_check.path);
				g_check.offset = g_check.startOffset;
			}
			else
			{
				g_check.path.clear();
			}

			g_check.started = GetTickCount64();
			g_check.active = true;
			g_check.stage = 1;
		}

		std::string LastReportJson()
		{
			if (!g_last.valid) return "null";

			std::string lines;
			for (const std::string& line : g_last.lines)
			{
				if (!lines.empty()) lines += ",";
				lines += Quote(line);
			}

			return "{\"source\":" + (g_last.source.empty() ? std::string("null") : Quote(g_last.source)) +
				",\"startOffset\":" + std::to_string(g_last.startOffset) +
				",\"count\":" + std::to_string(g_last.lines.size()) +
				"," + navmeshfindings::FieldsJson(g_last.lines) +
				",\"lines\":[" + lines + "]}";
		}

		std::string LastVerdictFields()
		{
			return navmeshfindings::FieldsJson(g_last.lines);
		}

		bool Collect(std::string& payload, std::string&, std::string&)
		{
			g_check.moved = false;
			if (!g_check.path.empty())
			{
				std::vector<std::string> fresh;
				std::uint64_t end = g_check.offset;
				if (ReadAppended(g_check.path, g_check.offset, end, fresh))
				{
					g_check.moved = end > g_check.offset;
					g_check.offset = end;

					for (std::string& line : fresh)
					{
						if (g_check.lines.size() >= kMaxCheckLines) break;
						g_check.lines.push_back(std::move(line));
					}
				}
			}

			const bool expired = GetTickCount64() - g_check.started >= kCheckSettleMs;
			const bool quiet = g_check.stage >= 2 && !g_check.moved;

			if (expired || quiet)
			{
				payload = DoneJson(quiet);

				g_last.valid = true;
				g_last.source = g_check.source;
				g_last.startOffset = g_check.startOffset;
				g_last.lines = g_check.lines;

				ResetCheck();
				return true;
			}

			g_check.stage = 2;
			payload = PendingJson("collecting");
			return true;
		}

		bool RunCheck(std::string& payload, std::string& errorCode, std::string& errorMessage)
		{
			if (g_check.active) return Collect(payload, errorCode, errorMessage);

			BeginCapture();

			std::string posted;
			if (!PostNavmeshCommand("Check NavMeshes", posted, errorCode, errorMessage))
			{
				ResetCheck();
				return false;
			}

			payload = PendingJson("posted");
			return true;
		}
	}
}
