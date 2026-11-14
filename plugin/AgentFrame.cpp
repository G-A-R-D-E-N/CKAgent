// Framing must come from the loaded cell's own references, so the object is found rather than guessed.

#include <windows.h>

#include <cmath>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include <EditorAPI/B/BGSRenderWindowCamera.h>
#include <EditorAPI/T/TESObjectCELL.h>

#include "AgentFrame.h"

namespace ckagent
{
	namespace
	{
		using CameraApi = CKPE::Fallout4::EditorAPI::BGSRenderWindowCamera;
		using NiPoint3 = CKPE::Fallout4::EditorAPI::NiPoint3;
		using NiMatrix43 = CKPE::Fallout4::EditorAPI::NiMatrix43;
		using EditorForm = CKPE::Fallout4::EditorAPI::Forms::TESForm;
		using EditorCell = CKPE::Fallout4::EditorAPI::Forms::TESObjectCELL;
		using EditorRef = CKPE::Fallout4::EditorAPI::Forms::TESObjectREFR;

		std::vector<std::string> SplitWords(const std::string& args)
		{
			std::vector<std::string> words;
			std::string current;
			for (char c : args)
			{
				if (c == ' ' || c == '\t')
				{
					if (!current.empty()) { words.push_back(current); current.clear(); }
				}
				else
				{
					current.push_back(c);
				}
			}
			if (!current.empty()) words.push_back(current);
			return words;
		}

		// Invariant: a pointer read out of the render window must be readable before it is dereferenced.
		bool Readable(const void* address, std::size_t bytes)
		{
			MEMORY_BASIC_INFORMATION info{};
			if (!address || VirtualQuery(address, &info, sizeof info) == 0) return false;
			if (info.State != MEM_COMMIT || (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;

			const auto start = reinterpret_cast<std::uintptr_t>(address);
			const auto end = start + bytes;
			const auto regionEnd = reinterpret_cast<std::uintptr_t>(info.BaseAddress) + info.RegionSize;
			return end >= start && end <= regionEnd;
		}

		std::string Lower(std::string value)
		{
			for (auto& c : value) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
			return value;
		}

		// Both pose tools must name the object they used, so the lookup is shared rather than copied.
		struct Match
		{
			EditorRef* ref = nullptr;
			std::string id;
			std::string baseId;
			int matches = 0;
			std::uint32_t count = 0;
		};

		bool ResolveReference(EditorCell* cell, const std::string& want, Match& out,
			std::string& errorCode, std::string& errorMessage)
		{
			// Constraint: a cell mid-load can leave a stale pointer, and a bad read here would crash the editor.
			if (!Readable(cell, 0x180))
			{
				errorCode = "cell_unreadable";
				errorMessage = "the loaded cell is not readable yet; retry once the load settles";
				return false;
			}

			const std::string wantLower = Lower(want);
			std::uint32_t wantId = 0;
			if (wantLower.size() > 2 && wantLower.rfind("0x", 0) == 0)
				wantId = static_cast<std::uint32_t>(std::strtoul(wantLower.c_str() + 2, nullptr, 16));
			else if (wantLower.size() == 8 && wantLower.find_first_not_of("0123456789abcdef") == std::string::npos)
				wantId = static_cast<std::uint32_t>(std::strtoul(wantLower.c_str(), nullptr, 16));

			const std::uint32_t count = cell->GetItemCount();
			EditorRef** items = cell->GetItems();
			out.count = count;

			// Invariant: the item array must be readable for every count claimed, or the loop reads freed memory.
			if (count > 200000 || (count != 0 && (!items || !Readable(items, static_cast<std::size_t>(count) * sizeof(void*)))))
			{
				errorCode = "cell_unreadable";
				errorMessage = "the loaded cell reports " + std::to_string(count) + " references that are not readable";
				return false;
			}

			for (std::uint32_t i = 0; items && i < count; ++i)
			{
				auto* refr = items[i];
				if (!refr) continue;

				const char* id = refr->GetFormEditorID();
				EditorForm* base = refr->GetParent();
				const char* baseId = base ? base->GetFormEditorID() : nullptr;
				const bool named = (id && Lower(id) == wantLower) || (baseId && Lower(baseId) == wantLower);
				const bool numbered = wantId != 0 && refr->GetFormID() == wantId;
				if (!named && !numbered) continue;

				if (++out.matches == 1)
				{
					out.ref = refr;
					out.id = id ? id : "";
					out.baseId = baseId ? baseId : "";
				}
			}

			if (out.ref)
				return true;

			// An unknown name must come back with real candidates, or the caller has nothing to try next.
			std::string sample;
			int shown = 0;
			for (std::uint32_t i = 0; items && i < count && shown < 8; ++i)
			{
				auto* refr = items[i];
				if (!refr) continue;
				const char* id = refr->GetFormEditorID();
				EditorForm* base = refr->GetParent();
				const char* baseId = base ? base->GetFormEditorID() : nullptr;
				if ((!id || !*id) && (!baseId || !*baseId)) continue;

				if (shown++) sample += ", ";
				sample += id && *id ? id : baseId;
			}

			errorCode = "ref_not_found";
			errorMessage = "no reference in the loaded cell matches " + want + " among " +
				std::to_string(count) + " references; for example: " + (sample.empty() ? "none are named" : sample);
			return false;
		}

		// The pose must be the verified pair: SetPosition plus the yaw matrix, with pitch left level.
		void SetYawPose(CameraApi* camera, float x, float y, float z, float yaw)
		{
			camera->SetPosition(NiPoint3{ x, y, z });

			NiMatrix43 rotate{};
			rotate.m_pEntry[0][0] = std::cos(yaw);  rotate.m_pEntry[0][1] = std::sin(yaw);  rotate.m_pEntry[0][2] = 0;
			rotate.m_pEntry[1][0] = -std::sin(yaw); rotate.m_pEntry[1][1] = std::cos(yaw);  rotate.m_pEntry[1][2] = 0;
			rotate.m_pEntry[2][0] = 0;              rotate.m_pEntry[2][1] = 0;              rotate.m_pEntry[2][2] = 1;
			camera->SetRotation(rotate);
		}
	}

	bool RunFrameTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage)
	{
		auto* camera = reinterpret_cast<CameraApi*>(target.camera);
		if (!camera)
		{
			errorCode = "camera_unavailable";
			errorMessage = "the render window has no camera object";
			return false;
		}

		auto* cell = reinterpret_cast<EditorCell*>(target.cell);
		if (!cell)
		{
			errorCode = "no_cell";
			errorMessage = "no cell is loaded; load one before framing";
			return false;
		}

		const std::vector<std::string> words = SplitWords(args);
		if (words.empty())
		{
			errorCode = "bad_args";
			errorMessage = "frame needs a reference EditorID, its base object EditorID, or a form id";
			return false;
		}

		const float distance = words.size() > 1 ? static_cast<float>(std::atof(words[1].c_str())) : 256.0f;
		const float yaw = words.size() > 2 ? static_cast<float>(std::atof(words[2].c_str())) : 0.0f;
		if (!(distance > 1.0f))
		{
			errorCode = "bad_args";
			errorMessage = "frame distance must be greater than 1";
			return false;
		}

		Match hit;
		if (!ResolveReference(cell, words[0], hit, errorCode, errorMessage))
			return false;

		const NiPoint3 targetPoint = hit.ref->GetPosition();
		SetYawPose(camera, targetPoint.x - distance * std::cos(yaw),
			targetPoint.y - distance * std::sin(yaw), targetPoint.z, yaw);

		const NiPoint3 after = camera->GetPosition();
		const NiMatrix43 read = camera->GetRotate();

		// Framing must be checked against the target, because the object has to sit ahead along the view row.
		const double dx = static_cast<double>(targetPoint.x) - after.x;
		const double dy = static_cast<double>(targetPoint.y) - after.y;
		const double dz = static_cast<double>(targetPoint.z) - after.z;
		const double span = std::sqrt(dx * dx + dy * dy + dz * dz);
		const double aim = span > 1e-6
			? (read.m_pEntry[0][0] * dx + read.m_pEntry[0][1] * dy + read.m_pEntry[0][2] * dz) / span
			: 0.0;

		char buf[720];
		std::snprintf(buf, sizeof buf,
			"{\"reference\":\"%s\",\"base\":\"%s\",\"formId\":\"%08X\",\"matches\":%d,"
			"\"target\":[%.4f,%.4f,%.4f],\"camera\":[%.4f,%.4f,%.4f],\"distance\":%.2f,\"yaw\":%.4f,"
			"\"span\":%.2f,\"aim\":%.4f,\"aligned\":%s,\"references\":%lu}",
			hit.id.c_str(), hit.baseId.c_str(), hit.ref->GetFormID(), hit.matches,
			targetPoint.x, targetPoint.y, targetPoint.z,
			after.x, after.y, after.z,
			distance, yaw, span, aim,
			aim > 0.999 ? "true" : "false",
			static_cast<unsigned long>(hit.count));
		payload = buf;
		return true;
	}

	bool RunAimTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage)
	{
		auto* camera = reinterpret_cast<CameraApi*>(target.camera);
		if (!camera)
		{
			errorCode = "camera_unavailable";
			errorMessage = "the render window has no camera object";
			return false;
		}

		auto* cell = reinterpret_cast<EditorCell*>(target.cell);
		if (!cell)
		{
			errorCode = "no_cell";
			errorMessage = "no cell is loaded; load one before aiming";
			return false;
		}

		const std::vector<std::string> words = SplitWords(args);
		if (words.empty())
		{
			errorCode = "bad_args";
			errorMessage = "aim needs a reference EditorID, its base object EditorID, or a form id";
			return false;
		}

		const float distance = words.size() > 1 ? static_cast<float>(std::atof(words[1].c_str())) : 0.0f;

		Match hit;
		if (!ResolveReference(cell, words[0], hit, errorCode, errorMessage))
			return false;

		const NiPoint3 targetPoint = hit.ref->GetPosition();
		const NiPoint3 before = camera->GetPosition();

		// Constraint: the yaw must come from the direction that actually joins camera to target, not a guess.
		const double dx = static_cast<double>(targetPoint.x) - before.x;
		const double dy = static_cast<double>(targetPoint.y) - before.y;
		const double dz = static_cast<double>(targetPoint.z) - before.z;
		const double horizontal = std::sqrt(dx * dx + dy * dy);
		if (horizontal < 1e-3)
		{
			errorCode = "too_close";
			errorMessage = "the camera is directly above or below " + hit.id + "; step sideways before aiming";
			return false;
		}

		const float yaw = static_cast<float>(std::atan2(dy, dx));

		// Constraint: a standoff is optional, so the camera must keep its own height and only back away when asked.
		const float px = distance > 1.0f ? targetPoint.x - distance * static_cast<float>(std::cos(yaw))
		                                 : before.x;
		const float py = distance > 1.0f ? targetPoint.y - distance * static_cast<float>(std::sin(yaw))
		                                 : before.y;
		SetYawPose(camera, px, py, before.z, yaw);

		const NiPoint3 after = camera->GetPosition();
		const NiMatrix43 read = camera->GetRotate();

		// The pitch cannot be set through this matrix, so a target out of level must be reported as a miss.
		const double ax = static_cast<double>(targetPoint.x) - after.x;
		const double ay = static_cast<double>(targetPoint.y) - after.y;
		const double az = static_cast<double>(targetPoint.z) - after.z;
		const double span = std::sqrt(ax * ax + ay * ay + az * az);
		const double aim = span > 1e-6
			? (read.m_pEntry[0][0] * ax + read.m_pEntry[0][1] * ay + read.m_pEntry[0][2] * az) / span
			: 0.0;
		const double vertical = span > 1e-6 ? az / span : 0.0;

		char buf[720];
		std::snprintf(buf, sizeof buf,
			"{\"reference\":\"%s\",\"base\":\"%s\",\"formId\":\"%08X\",\"matches\":%d,"
			"\"target\":[%.4f,%.4f,%.4f],\"camera\":[%.4f,%.4f,%.4f],\"distance\":%.2f,\"yaw\":%.4f,"
			"\"span\":%.2f,\"aim\":%.4f,\"vertical\":%.4f,\"aligned\":%s,\"references\":%lu}",
			hit.id.c_str(), hit.baseId.c_str(), hit.ref->GetFormID(), hit.matches,
			targetPoint.x, targetPoint.y, targetPoint.z,
			after.x, after.y, after.z,
			distance, yaw, span, aim, vertical,
			aim > 0.999 ? "true" : "false",
			static_cast<unsigned long>(hit.count));
		payload = buf;
		return true;
	}
}
