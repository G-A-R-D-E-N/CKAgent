// Framing must come from the loaded cell's own references, so the object is found rather than guessed.

#include <windows.h>

#include <cmath>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstring>
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

		// The pose must be the verified pair: SetPosition plus the measured view matrix.
		void SetViewPose(CameraApi* camera, float x, float y, float z, float yaw, float pitch)
		{
			camera->SetPosition(NiPoint3{ x, y, z });

			NiMatrix43 rotate{};
			const ViewMatrix view = MakeViewMatrix(yaw, pitch);
			std::memcpy(&rotate, view.m, sizeof rotate);
			camera->SetRotation(rotate);
		}

		// The view row must come from the scene camera, so a pose the editor rejected cannot pass as aimed.
		double AimAgainst(CameraApi* camera, const NiPoint3& from, const NiPoint3& target)
		{
			float forward[3] = { 0.0f, 0.0f, 0.0f };
			if (!ReadSceneView(camera, forward)) return 0.0;

			const double dx = static_cast<double>(target.x) - from.x;
			const double dy = static_cast<double>(target.y) - from.y;
			const double dz = static_cast<double>(target.z) - from.z;
			const double span = std::sqrt(dx * dx + dy * dy + dz * dz);
			if (span < 1e-6) return 0.0;

			return (forward[0] * dx + forward[1] * dy + forward[2] * dz) / span;
		}
	}

	ViewMatrix MakeViewMatrix(float yaw, float pitch)
	{
		const float cy = std::cos(yaw), sy = std::sin(yaw);
		const float cp = std::cos(pitch), sp = std::sin(pitch);

		// Constraint: rows must be right, forward, up, as the editor's own LookAt writes them.
		ViewMatrix view;
		view.m[0] = sy;         view.m[1] = -cy;        view.m[2] = 0.0f;
		view.m[4] = cy * cp;    view.m[5] = sy * cp;    view.m[6] = sp;
		view.m[8] = -cy * sp;   view.m[9] = -sy * sp;   view.m[10] = cp;
		return view;
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

		const float requestedDistance = words.size() > 1 ? static_cast<float>(std::atof(words[1].c_str())) : 0.0f;
		const float yaw = words.size() > 2 ? static_cast<float>(std::atof(words[2].c_str())) : 0.0f;
		// Constraint: a zero distance means "fit the bounds", so only a positive value below 1 is refused.
		if (requestedDistance != 0.0f && !(requestedDistance > 1.0f))
		{
			errorCode = "bad_args";
			errorMessage = "frame distance must be greater than 1, or 0 to fit the reference's bounds";
			return false;
		}

		Match hit;
		if (!ResolveReference(cell, words[0], hit, errorCode, errorMessage))
			return false;

		const NiPoint3 targetPoint = hit.ref->GetPosition();

		// Invariant: bounds are in the reference's own units, so the box centre is its position plus the box middle.
		constexpr float kFrameFit = 2.4f;           // standoff in bounding-sphere radii, so the box fits the view
		constexpr float kFrameElevation = 0.5236f;  // 30 degrees above the target, so its top face is visible
		constexpr float kMinRadius = 8.0f;          // a degenerate bound still frames instead of dividing to zero
		constexpr float kMaxRadius = 8192.0f;       // larger than any interior cell, so a broken box cannot fly off

		const NiPoint3 boundsMin = hit.ref->GetBoundMin();
		const NiPoint3 boundsMax = hit.ref->GetBoundMax();
		const float extentX = boundsMax.x - boundsMin.x;
		const float extentY = boundsMax.y - boundsMin.y;
		const float extentZ = boundsMax.z - boundsMin.z;
		const bool haveBounds = std::isfinite(extentX) && std::isfinite(extentY) && std::isfinite(extentZ) &&
			extentX > 0.0f && extentY > 0.0f && extentZ > 0.0f;

		NiPoint3 center = targetPoint;
		float radius = 0.0f;
		const char* boundsSource = "fallback";
		if (haveBounds)
		{
			center.x = targetPoint.x + (boundsMin.x + boundsMax.x) * 0.5f;
			center.y = targetPoint.y + (boundsMin.y + boundsMax.y) * 0.5f;
			center.z = targetPoint.z + (boundsMin.z + boundsMax.z) * 0.5f;
			radius = 0.5f * std::sqrt(extentX * extentX + extentY * extentY + extentZ * extentZ);
			boundsSource = "reference";
		}

		if (!(radius > kMinRadius)) radius = kMinRadius;
		if (radius > kMaxRadius) radius = kMaxRadius;

		const float distance = requestedDistance > 0.0f ? requestedDistance : kFrameFit * radius;
		const float pitch = -kFrameElevation;
		const float cosPitch = std::cos(pitch);

		SetViewPose(camera, center.x - distance * cosPitch * std::cos(yaw),
			center.y - distance * cosPitch * std::sin(yaw),
			center.z - distance * std::sin(pitch), yaw, pitch);

		const NiPoint3 after = camera->GetPosition();

		// Framing must be checked against the box centre, because the object has to sit ahead along the view row.
		const double dx = static_cast<double>(center.x) - after.x;
		const double dy = static_cast<double>(center.y) - after.y;
		const double dz = static_cast<double>(center.z) - after.z;
		const double span = std::sqrt(dx * dx + dy * dy + dz * dz);
		const double aim = AimAgainst(camera, after, center);

		char buf[980];
		std::snprintf(buf, sizeof buf,
			"{\"reference\":\"%s\",\"base\":\"%s\",\"formId\":\"%08X\",\"matches\":%d,"
			"\"target\":[%.4f,%.4f,%.4f],\"center\":[%.4f,%.4f,%.4f],\"camera\":[%.4f,%.4f,%.4f],"
			"\"bounds\":[%.4f,%.4f,%.4f,%.4f,%.4f,%.4f],\"boundsSource\":\"%s\",\"radius\":%.2f,"
			"\"distance\":%.2f,\"yaw\":%.4f,\"pitch\":%.4f,"
			"\"span\":%.2f,\"aim\":%.4f,\"aligned\":%s,\"references\":%lu}",
			hit.id.c_str(), hit.baseId.c_str(), hit.ref->GetFormID(), hit.matches,
			targetPoint.x, targetPoint.y, targetPoint.z,
			center.x, center.y, center.z,
			after.x, after.y, after.z,
			boundsMin.x, boundsMin.y, boundsMin.z, boundsMax.x, boundsMax.y, boundsMax.z,
			boundsSource, radius, distance, yaw, pitch, span, aim,
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

		// Constraint: a chain is one argument with commas, so the optional standoff keeps its own word.
		std::vector<std::string> chain;
		{
			std::string item;
			for (char c : words[0])
			{
				if (c == ',')
				{
					if (!item.empty()) chain.push_back(item);
					item.clear();
				}
				else item.push_back(c);
			}
			if (!item.empty()) chain.push_back(item);
		}

		if (chain.empty())
		{
			errorCode = "bad_args";
			errorMessage = "aim needs at least one reference";
			return false;
		}

		// Constraint: a chain exists to rotate in place, so a standoff must be refused rather than moving it.
		if (chain.size() > 1 && distance > 1.0f)
		{
			errorCode = "bad_args";
			errorMessage = "aim a chain without a distance: it rotates in place and a standoff moves the camera";
			return false;
		}

		std::string steps;
		std::string lastReference;
		std::string lastBase;
		unsigned int lastFormId = 0;
		int matches = 0;
		unsigned long references = 0;
		double yawOut = 0.0;
		double pitchOut = 0.0;
		double spanOut = 0.0;
		double aimOut = 0.0;
		double verticalOut = 0.0;
		double targetOut[3] = { 0.0, 0.0, 0.0 };
		NiPoint3 lastAfter{};

		for (std::size_t i = 0; i < chain.size(); ++i)
		{
			Match hit;
			if (!ResolveReference(cell, chain[i], hit, errorCode, errorMessage))
			{
				// Constraint: a failed step must name itself, or a chain that stops halfway looks like a bad name.
				if (chain.size() > 1)
				{
					errorMessage = "step " + std::to_string(i + 1) + " of " + std::to_string(chain.size()) +
						" (" + chain[i] + "): " + errorMessage;
				}
				return false;
			}

			const NiPoint3 targetPoint = hit.ref->GetPosition();
			const NiPoint3 before = camera->GetPosition();

			// Constraint: the pitch must come from where the camera ends up, or a standoff tilts at the old distance.
			const double dx = static_cast<double>(targetPoint.x) - before.x;
			const double dy = static_cast<double>(targetPoint.y) - before.y;
			const double dz = static_cast<double>(targetPoint.z) - before.z;
			const double horizontal = std::sqrt(dx * dx + dy * dy);

			// Invariant: a target straight above or below has no yaw of its own, so the view heading is kept.
			float headingX = 1.0f;
			float headingY = 0.0f;
			if (horizontal > 1e-3)
			{
				headingX = static_cast<float>(dx / horizontal);
				headingY = static_cast<float>(dy / horizontal);
			}
			else
			{
				float forward[3] = { 0.0f, 0.0f, 0.0f };
				if (ReadSceneView(camera, forward))
				{
					const double flat = std::sqrt(static_cast<double>(forward[0]) * forward[0] +
						static_cast<double>(forward[1]) * forward[1]);
					if (flat > 1e-4)
					{
						headingX = static_cast<float>(forward[0] / flat);
						headingY = static_cast<float>(forward[1] / flat);
					}
				}
			}

			// Constraint: a standoff is optional, so the camera must keep its own height and only back away when asked.
			const float px = distance > 1.0f ? targetPoint.x - distance * headingX : before.x;
			const float py = distance > 1.0f ? targetPoint.y - distance * headingY : before.y;

			const double outX = static_cast<double>(targetPoint.x) - px;
			const double outY = static_cast<double>(targetPoint.y) - py;
			const double outZ = static_cast<double>(targetPoint.z) - before.z;
			const double flatSpan = std::sqrt(outX * outX + outY * outY);
			const double reach = std::sqrt(outX * outX + outY * outY + outZ * outZ);
			if (reach < 1e-3)
			{
				errorCode = "too_close";
				errorMessage = "the camera is on top of " + hit.id + "; there is no direction to aim";
				return false;
			}

			// Invariant: at the pole the yaw is whatever the heading was, and the pitch is the pole itself.
			const float yaw = flatSpan > 1e-4 ? static_cast<float>(std::atan2(outY, outX))
			                                 : static_cast<float>(std::atan2(headingY, headingX));
			const float pitch = flatSpan > 1e-4 ? static_cast<float>(std::atan2(outZ, flatSpan))
			                                   : (outZ > 0.0 ? 1.5707963f : -1.5707963f);
			SetViewPose(camera, px, py, before.z, yaw, pitch);

			const NiPoint3 after = camera->GetPosition();

			// The aim must be measured from the scene camera's view row, which is what the editor renders.
			const double ax = static_cast<double>(targetPoint.x) - after.x;
			const double ay = static_cast<double>(targetPoint.y) - after.y;
			const double az = static_cast<double>(targetPoint.z) - after.z;
			const double span = std::sqrt(ax * ax + ay * ay + az * az);
			const double aim = AimAgainst(camera, after, targetPoint);
			const double vertical = span > 1e-6 ? az / span : 0.0;

			char step[512];
			std::snprintf(step, sizeof step,
				"%s{\"reference\":\"%s\",\"formId\":\"%08X\",\"yaw\":%.4f,\"pitch\":%.4f,\"aim\":%.4f,\"aligned\":%s}",
				steps.empty() ? "" : ",", hit.id.c_str(), hit.ref->GetFormID(), yaw, pitch, aim,
				aim > 0.999 ? "true" : "false");
			steps += step;

			lastReference = hit.id;
			lastBase = hit.baseId;
			lastFormId = hit.ref->GetFormID();
			matches = hit.matches;
			references = hit.count;
			targetOut[0] = targetPoint.x;
			targetOut[1] = targetPoint.y;
			targetOut[2] = targetPoint.z;
			lastAfter = after;
			yawOut = yaw;
			pitchOut = pitch;
			spanOut = span;
			aimOut = aim;
			verticalOut = vertical;
		}

		char buf[4096];
		std::snprintf(buf, sizeof buf,
			"{\"reference\":\"%s\",\"base\":\"%s\",\"formId\":\"%08X\",\"matches\":%d,"
			"\"target\":[%.4f,%.4f,%.4f],\"camera\":[%.4f,%.4f,%.4f],\"distance\":%.2f,\"yaw\":%.4f,\"pitch\":%.4f,"
			"\"span\":%.2f,\"aim\":%.4f,\"vertical\":%.4f,\"aligned\":%s,\"references\":%lu,\"steps\":%d,\"chain\":[%s]}",
			lastReference.c_str(), lastBase.c_str(), lastFormId, matches,
			targetOut[0], targetOut[1], targetOut[2],
			lastAfter.x, lastAfter.y, lastAfter.z,
			distance, yawOut, pitchOut, spanOut, aimOut, verticalOut,
			aimOut > 0.999 ? "true" : "false",
			references, static_cast<int>(chain.size()), steps.c_str());
		payload = buf;
		return true;
	}

	// The orbit must circle the point at the distance it was given, which is what the editor's own orbit keeps.
	bool RunOrbitTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage)
	{
		auto* camera = reinterpret_cast<CameraApi*>(target.camera);
		if (!camera)
		{
			errorCode = "camera_unavailable";
			errorMessage = "the camera is not resolved";
			return false;
		}

		const std::vector<std::string> words = SplitWords(args);
		if (words.size() < 5)
		{
			errorCode = "bad_args";
			errorMessage = "orbit needs x y z distance angle [pitch]";
			return false;
		}

		const float x = static_cast<float>(std::atof(words[0].c_str()));
		const float y = static_cast<float>(std::atof(words[1].c_str()));
		const float z = static_cast<float>(std::atof(words[2].c_str()));
		const float distance = static_cast<float>(std::atof(words[3].c_str()));
		const float angle = static_cast<float>(std::atof(words[4].c_str()));
		const float pitch = words.size() > 5 ? static_cast<float>(std::atof(words[5].c_str())) : 0.0f;

		// Constraint: a radius of zero or less is not a circle, so the distance must be positive.
		if (!(distance > 0.0f))
		{
			errorCode = "bad_args";
			errorMessage = "distance must be greater than zero";
			return false;
		}

		const NiPoint3 pivot{ x, y, z };

		// Constraint: the editor orbits and zooms about its own pivot, so the focus must be set to this point.
		camera->SetPivot(pivot, 1.0f, false);

		// Position must come from the pivot minus the view direction, so the circle keeps the radius asked for.
		const double cp = std::cos(pitch);
		camera->SetPosition(NiPoint3{
			x - distance * static_cast<float>(std::cos(angle) * cp),
			y - distance * static_cast<float>(std::sin(angle) * cp),
			z - distance * static_cast<float>(std::sin(pitch)) });

		NiMatrix43 rotate{};
		const ViewMatrix view = MakeViewMatrix(angle, pitch);
		std::memcpy(&rotate, view.m, sizeof rotate);
		camera->SetRotation(rotate);

		const NiPoint3 after = camera->GetPosition();
		const double dx = static_cast<double>(pivot.x) - after.x;
		const double dy = static_cast<double>(pivot.y) - after.y;
		const double dz = static_cast<double>(pivot.z) - after.z;
		const double span = std::sqrt(dx * dx + dy * dy + dz * dz);
		const double aim = AimAgainst(camera, after, pivot);

		// The pivot must read back, because a focus the editor never took leaves the next orbit starting elsewhere.
		const NiPoint3 reported = camera->GetPivot();
		const bool hasPivot = camera->HasPivot();

		char buf[768];
		std::snprintf(buf, sizeof buf,
			"{\"pivot\":[%.4f,%.4f,%.4f],\"pivotRead\":[%.4f,%.4f,%.4f],\"hasPivot\":%s,"
			"\"angle\":%.4f,\"pitch\":%.4f,\"distance\":%.2f,\"camera\":[%.4f,%.4f,%.4f],"
			"\"span\":%.2f,\"aim\":%.4f,\"aligned\":%s}",
			pivot.x, pivot.y, pivot.z,
			reported.x, reported.y, reported.z,
			hasPivot ? "true" : "false",
			angle, pitch, distance,
			after.x, after.y, after.z, span, aim,
			aim > 0.999 ? "true" : "false");
		payload = buf;
		return true;
	}
}
