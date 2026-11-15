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

		constexpr float kFrameFit = 2.4f;           // standoff in bounding-sphere radii, so the box fits the view
		constexpr float kFrameElevation = 0.5236f;  // 30 degrees above the target, so its top face is visible
		constexpr float kMinRadius = 8.0f;          // a degenerate bound still frames instead of dividing to zero
		constexpr float kMaxRadius = 8192.0f;       // larger than any interior cell, so a broken box cannot fly off

		struct Bounds
		{
			NiPoint3 center{};
			NiPoint3 min{};
			NiPoint3 max{};
			float radius = kMinRadius;
			bool fromReference = false;
		};

		// Invariant: bounds are in the reference's own units, so the centre is its position plus the box middle.
		Bounds ReadBounds(EditorRef* refr)
		{
			Bounds bounds;
			bounds.center = refr->GetPosition();
			const NiPoint3 min = refr->GetBoundMin();
			const NiPoint3 max = refr->GetBoundMax();
			bounds.min = min;
			bounds.max = max;
			const float ex = max.x - min.x;
			const float ey = max.y - min.y;
			const float ez = max.z - min.z;
			if (std::isfinite(ex) && std::isfinite(ey) && std::isfinite(ez) && ex > 0.0f && ey > 0.0f && ez > 0.0f)
			{
				bounds.center.x += (min.x + max.x) * 0.5f;
				bounds.center.y += (min.y + max.y) * 0.5f;
				bounds.center.z += (min.z + max.z) * 0.5f;
				bounds.radius = 0.5f * std::sqrt(ex * ex + ey * ey + ez * ez);
				bounds.fromReference = true;
			}
			if (bounds.radius > kMaxRadius) bounds.radius = kMaxRadius;
			return bounds;
		}

		bool IsNumber(const std::string& word)
		{
			if (word.empty()) return false;
			char* end = nullptr;
			std::strtod(word.c_str(), &end);
			return end && *end == '\0';
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

		// Constraint: the standoff and the centre come from the same bounds read, so frame and orbit agree.
		const Bounds bounds = ReadBounds(hit.ref);
		const NiPoint3 center = bounds.center;
		const float radius = bounds.radius;
		const char* boundsSource = bounds.fromReference ? "reference" : "fallback";
		const float distance = requestedDistance > 0.0f ? requestedDistance : kFrameFit * radius;

		// Constraint: the near and far planes must enclose the whole box, or the framed object is clipped.
		const float nearFit = distance - radius * 1.5f;
		const float nearPlane = nearFit > 1.0f ? nearFit : 1.0f;
		const float farPlane = distance + radius * 2.0f;

		const float pitch = -kFrameElevation;
		const float cosPitch = std::cos(pitch);

		SetViewPose(camera, center.x - distance * cosPitch * std::cos(yaw),
			center.y - distance * cosPitch * std::sin(yaw),
			center.z - distance * std::sin(pitch), yaw, pitch);

		// Constraint: the planes go in after the pose, because the editor re-derives them when it re-poses.
		auto* slave = camera->GetCamera();
		bool planesApplied = false;
		if (slave)
		{
			auto frustum = slave->GetFrustum();
			frustum.m_fNear = nearPlane;
			slave->SetFrustum(frustum);
			planesApplied = true;
		}

		const NiPoint3 after = camera->GetPosition();

		// Framing must be checked against the box centre, because the object has to sit ahead along the view row.
		const double dx = static_cast<double>(center.x) - after.x;
		const double dy = static_cast<double>(center.y) - after.y;
		const double dz = static_cast<double>(center.z) - after.z;
		const double span = std::sqrt(dx * dx + dy * dy + dz * dz);
		const double aim = AimAgainst(camera, after, center);

		const float nearRead = slave ? slave->GetFrustum().m_fNear : 0.0f;
		const float farRead = slave ? slave->GetFrustum().m_fFar : 0.0f;
		const bool farHeld = std::fabs(farRead - farPlane) < 0.5f;

		char buf[1160];
		std::snprintf(buf, sizeof buf,
			"{\"reference\":\"%s\",\"base\":\"%s\",\"formId\":\"%08X\",\"matches\":%d,"
			"\"target\":[%.4f,%.4f,%.4f],\"center\":[%.4f,%.4f,%.4f],\"camera\":[%.4f,%.4f,%.4f],"
			"\"bounds\":[%.4f,%.4f,%.4f,%.4f,%.4f,%.4f],\"boundsSource\":\"%s\",\"radius\":%.2f,"
			"\"distance\":%.2f,\"near\":%.4f,\"far\":%.4f,\"nearRead\":%.4f,\"farRead\":%.4f,"
			"\"farHeld\":%s,\"planesApplied\":%s,\"yaw\":%.4f,\"pitch\":%.4f,"
			"\"span\":%.2f,\"aim\":%.4f,\"aligned\":%s,\"references\":%lu}",
			hit.id.c_str(), hit.baseId.c_str(), hit.ref->GetFormID(), hit.matches,
			targetPoint.x, targetPoint.y, targetPoint.z,
			center.x, center.y, center.z,
			after.x, after.y, after.z,
			bounds.min.x, bounds.min.y, bounds.min.z, bounds.max.x, bounds.max.y, bounds.max.z,
			boundsSource, radius, distance, nearPlane, farPlane, nearRead, farRead,
			farHeld ? "true" : "false", planesApplied ? "true" : "false", yaw, pitch, span, aim,
			aim > 0.999 ? "true" : "false",
			static_cast<unsigned long>(hit.count));
		payload = buf;
		return true;
	}

	// Invariant: the frustum is read through the slave NiCamera, which is the one the editor renders with.
	struct Lens
	{
		float left = 0.0f, right = 0.0f, top = 0.0f, bottom = 0.0f, nearPlane = 0.0f, farPlane = 0.0f;
		bool ortho = false;
		unsigned viewMode = 0;
	};

	namespace
	{
		Lens ReadLens(CameraApi* camera, CKPE::Fallout4::EditorAPI::NiCamera* slave)
		{
			Lens lens;
			const auto& frustum = slave->GetFrustum();
			lens.left = frustum.m_fLeft; lens.right = frustum.m_fRight;
			lens.top = frustum.m_fTop; lens.bottom = frustum.m_fBottom;
			lens.nearPlane = frustum.m_fNear; lens.farPlane = frustum.m_fFar;
			lens.ortho = frustum.m_bOrtho;
			lens.viewMode = static_cast<unsigned>(camera->GetViewMode());
			return lens;
		}

		// Constraint: the frustum holds the tangents of the half-angles, so degrees come back out of atan.
		double DegreesOf(double tangent)
		{
			return std::atan(tangent) * 57.29577951308232;
		}

		double VerticalFov(const Lens& lens)
		{
			return DegreesOf(lens.top) - DegreesOf(lens.bottom);
		}

		constexpr double kDefaultFov = 56.6941;  // the render window's own vertical field of view, in degrees

		// Invariant: zoom is measured against the lens the render window starts with, so it reads 1 at home.
		double LensZoom(const Lens& lens)
		{
			const double fov = VerticalFov(lens);
			return fov > 1e-3 ? kDefaultFov / fov : 1.0;
		}

		double HorizontalFov(const Lens& lens)
		{
			return DegreesOf(lens.right) - DegreesOf(lens.left);
		}
	}

	bool RunLensTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage)
	{
		auto* camera = reinterpret_cast<CameraApi*>(target.camera);
		if (!camera)
		{
			errorCode = "camera_unavailable";
			errorMessage = "the render window has no camera object";
			return false;
		}

		auto* slave = camera->GetCamera();
		if (!slave)
		{
			errorCode = "camera_unavailable";
			errorMessage = "the render window camera has no scene NiCamera to read the lens from";
			return false;
		}

		const std::vector<std::string> words = SplitWords(args);
		const Lens before = ReadLens(camera, slave);

		float requestedFov = 0.0f;
		float zoomFactor = 0.0f;
		const bool settingFov = words.size() > 2 && words[1] == "fov";
		const bool zooming = words.size() > 2 && words[1] == "zoom";
		if (settingFov) requestedFov = static_cast<float>(std::atof(words[2].c_str()));
		if (zooming) zoomFactor = static_cast<float>(std::atof(words[2].c_str()));

		if (settingFov && !(requestedFov > 1.0f && requestedFov < 179.0f))
		{
			errorCode = "bad_args";
			errorMessage = "fov must be between 1 and 179 degrees";
			return false;
		}

		// Constraint: the editor's Zoom leaves the frustum unchanged (measured), so a zoom scales the field of view.
		if (zooming && !(zoomFactor > 0.0f && zoomFactor <= 20.0f))
		{
			errorCode = "bad_args";
			errorMessage = "zoom must be a factor between 0 and 20, where 1 leaves the lens alone";
			return false;
		}

		if ((settingFov || zooming) && before.viewMode != 0)
		{
			errorCode = "not_perspective";
			errorMessage = "the render window is in orthographic view, where a field of view does not apply";
			return false;
		}

		const double wantedFov = settingFov ? static_cast<double>(requestedFov)
			: zooming ? VerticalFov(before) / static_cast<double>(zoomFactor) : 0.0;

		if (settingFov || zooming)
		{
			if (!(wantedFov > 1.0 && wantedFov < 179.0))
			{
				errorCode = "bad_args";
				errorMessage = "that zoom would push the field of view outside 1 to 179 degrees";
				return false;
			}
		}

		if (settingFov || zooming)
		{
			// Constraint: the result must be judged by a fresh read, not by the value that was sent.
			const auto was = slave->GetFrustum();
			const double height = static_cast<double>(was.m_fTop) - was.m_fBottom;
			const double aspect = std::fabs(height) > 1e-6 ? (was.m_fRight - was.m_fLeft) / height : 1.0;

			const double half = std::tan(wantedFov * 0.5 * 0.017453292519943295);
			auto wanted = was;
			wanted.m_fTop = static_cast<float>(half);
			wanted.m_fBottom = static_cast<float>(-half);
			wanted.m_fRight = static_cast<float>(half * aspect);
			wanted.m_fLeft = static_cast<float>(-half * aspect);
			slave->SetFrustum(wanted);
			camera->Update();
		}

		const Lens after = ReadLens(camera, slave);
		const double fov = VerticalFov(after);
		const double applied = (settingFov || zooming) ? std::fabs(fov - wantedFov) < 0.05 : true;

		char buf[960];
		std::snprintf(buf, sizeof buf,
			"{\"fov\":%.4f,\"horizontalFov\":%.4f,\"wasFov\":%.4f,\"requested\":%.4f,"
			"\"frustum\":[%.5f,%.5f,%.5f,%.5f,%.4f,%.4f],\"near\":%.4f,\"far\":%.4f,"
			"\"zoom\":%.4f,\"zoomRequested\":%.4f,\"ortho\":%s,\"viewMode\":\"%s\","
			"\"applied\":%s,\"changed\":%s}",
			fov, HorizontalFov(after), VerticalFov(before), settingFov ? requestedFov : 0.0f,
			after.left, after.right, after.top, after.bottom, after.nearPlane, after.farPlane,
			after.nearPlane, after.farPlane, LensZoom(after), zoomFactor,
			after.ortho ? "true" : "false", after.viewMode == 0 ? "perspective" : "orthographic",
			applied ? "true" : "false",
			std::fabs(fov - VerticalFov(before)) > 0.01 ? "true" : "false");
		payload = buf;
		return true;
	}

	bool RunRotateTool(const std::string& args, const FrameTarget& target, std::string& payload,
		std::string& errorCode, std::string& errorMessage)
	{
		auto* camera = reinterpret_cast<CameraApi*>(target.camera);
		if (!camera)
		{
			errorCode = "camera_unavailable";
			errorMessage = "the render window has no camera object";
			return false;
		}

		const std::vector<std::string> words = SplitWords(args);
		if (words.size() < 4)
		{
			errorCode = "bad_args";
			errorMessage = "rotate needs x y z steps, in radians, around the camera's own axes";
			return false;
		}

		const float sx = static_cast<float>(std::atof(words[1].c_str()));
		const float sy = static_cast<float>(std::atof(words[2].c_str()));
		const float sz = static_cast<float>(std::atof(words[3].c_str()));
		if (!std::isfinite(sx) || !std::isfinite(sy) || !std::isfinite(sz) ||
			(std::fabs(sx) < 1e-6f && std::fabs(sy) < 1e-6f && std::fabs(sz) < 1e-6f))
		{
			errorCode = "bad_args";
			errorMessage = "rotate needs a non-zero step, in radians";
			return false;
		}

		const NiPoint3 beforePosition = camera->GetPosition();
		float was[3] = { 0.0f, 0.0f, 0.0f };
		float now[3] = { 0.0f, 0.0f, 0.0f };
		const bool wasReadable = ReadSceneView(reinterpret_cast<void*>(camera), was);
		const auto wasMatrix = camera->GetRotate();

		camera->RotateBy(sx, sy, sz);
		const auto nowMatrix = camera->GetRotate();
		const bool nowReadable = ReadSceneView(reinterpret_cast<void*>(camera), now);

		// Invariant: the whole matrix is compared, because a roll changes neither yaw nor pitch.
		double matrixDelta = 0.0;
		for (int row = 0; row < 3; ++row)
			for (int col = 0; col < 3; ++col)
				matrixDelta += std::fabs(static_cast<double>(nowMatrix.m_pEntry[row][col]) - wasMatrix.m_pEntry[row][col]);

		// Invariant: the yaw and pitch come from the scene row the editor rotated.
		const double wasYaw = std::atan2(static_cast<double>(was[1]), static_cast<double>(was[0]));
		const double wasPitch = std::asin(std::fmax(-1.0, std::fmin(1.0, static_cast<double>(was[2]))));
		const double yaw = std::atan2(static_cast<double>(now[1]), static_cast<double>(now[0]));
		const double pitch = std::asin(std::fmax(-1.0, std::fmin(1.0, static_cast<double>(now[2]))));

		double deltaYaw = yaw - wasYaw;
		while (deltaYaw > 3.14159265358979) deltaYaw -= 6.28318530717959;
		while (deltaYaw < -3.14159265358979) deltaYaw += 6.28318530717959;
		const double deltaPitch = pitch - wasPitch;

		const NiPoint3 afterPosition = camera->GetPosition();
		const double moved = std::fabs(static_cast<double>(afterPosition.x) - beforePosition.x) +
			std::fabs(static_cast<double>(afterPosition.y) - beforePosition.y) +
			std::fabs(static_cast<double>(afterPosition.z) - beforePosition.z);
		const bool changed = matrixDelta > 1e-4;

		// A rotate that leaves the view where it was must fail here, or a silent no-op reads as a step taken.
		if (wasReadable && nowReadable && !changed)
		{
			errorCode = "rotate_noop";
			errorMessage = "the editor's RotateBy left the scene camera's view row unchanged, so the step did nothing";
			return false;
		}

		char buf[720];
		std::snprintf(buf, sizeof buf,
			"{\"step\":[%.4f,%.4f,%.4f],\"via\":\"editor-rotateby\","
			"\"forward\":[%.4f,%.4f,%.4f],\"wasForward\":[%.4f,%.4f,%.4f],"
			"\"yaw\":%.6f,\"pitch\":%.6f,\"deltaYaw\":%.6f,\"deltaPitch\":%.6f,"
			"\"moved\":%.4f,\"matrixDelta\":%.6f,\"changed\":%s,\"viewReadable\":%s}",
			sx, sy, sz,
			now[0], now[1], now[2], was[0], was[1], was[2],
			yaw, pitch, deltaYaw, deltaPitch,
			moved, matrixDelta, changed ? "true" : "false", (wasReadable && nowReadable) ? "true" : "false");
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

		// Invariant: level is a word of its own, or a standoff asked for beside it would be read as its number.
		bool holdHorizon = false;
		float distance = 0.0f;
		for (std::size_t i = 1; i < words.size(); ++i)
		{
			if (Lower(words[i]) == "level") holdHorizon = true;
			else if (distance == 0.0f) distance = static_cast<float>(std::atof(words[i].c_str()));
		}

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
		double yawAimOut = 0.0;
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
			// Constraint: level holds the pitch at zero, which is what a shot that needs a horizon is asking for.
			const float pitch = holdHorizon ? 0.0f
				: (flatSpan > 1e-4 ? static_cast<float>(std::atan2(outZ, flatSpan))
				                   : (outZ > 0.0 ? 1.5707963f : -1.5707963f));
			SetViewPose(camera, px, py, before.z, yaw, pitch);

			const NiPoint3 after = camera->GetPosition();

			// The aim must be measured from the scene camera's view row, which is what the editor renders.
			const double ax = static_cast<double>(targetPoint.x) - after.x;
			const double ay = static_cast<double>(targetPoint.y) - after.y;
			const double az = static_cast<double>(targetPoint.z) - after.z;
			const double span = std::sqrt(ax * ax + ay * ay + az * az);
			const double aim = AimAgainst(camera, after, targetPoint);
			const double vertical = span > 1e-6 ? az / span : 0.0;

			// Constraint: yawAim is measured against the heading chosen for this aim, not against the raw offset.
			float view[3] = { 0.0f, 0.0f, 0.0f };
			double yawAim = 0.0;
			if (ReadSceneView(camera, view))
			{
				const double flatView = std::sqrt(static_cast<double>(view[0]) * view[0] +
					static_cast<double>(view[1]) * view[1]);
				if (flatView > 1e-4)
				{
					yawAim = (static_cast<double>(view[0]) * headingX +
						static_cast<double>(view[1]) * headingY) / flatView;
				}
			}

			char step[512];
			std::snprintf(step, sizeof step,
				"%s{\"reference\":\"%s\",\"formId\":\"%08X\",\"yaw\":%.4f,\"pitch\":%.4f,\"aim\":%.4f,"
				"\"yawAim\":%.4f,\"aligned\":%s}",
				steps.empty() ? "" : ",", hit.id.c_str(), hit.ref->GetFormID(), yaw, pitch, aim, yawAim,
				(holdHorizon ? yawAim : aim) > 0.999 ? "true" : "false");
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
			yawAimOut = yawAim;
			verticalOut = vertical;
		}

		char buf[4096];
		std::snprintf(buf, sizeof buf,
			"{\"reference\":\"%s\",\"base\":\"%s\",\"formId\":\"%08X\",\"matches\":%d,"
			"\"target\":[%.4f,%.4f,%.4f],\"camera\":[%.4f,%.4f,%.4f],\"distance\":%.2f,\"yaw\":%.4f,\"pitch\":%.4f,"
			"\"span\":%.2f,\"aim\":%.4f,\"yawAim\":%.4f,\"level\":%s,\"vertical\":%.4f,\"aligned\":%s,"
			"\"references\":%lu,\"steps\":%d,\"chain\":[%s]}",
			lastReference.c_str(), lastBase.c_str(), lastFormId, matches,
			targetOut[0], targetOut[1], targetOut[2],
			lastAfter.x, lastAfter.y, lastAfter.z,
			distance, yawOut, pitchOut, spanOut, aimOut, yawAimOut, holdHorizon ? "true" : "false", verticalOut,
			(holdHorizon ? yawAimOut : aimOut) > 0.999 ? "true" : "false",
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
		if (words.empty())
		{
			errorCode = "bad_args";
			errorMessage = "orbit needs x y z distance angle [pitch], or a reference name [distance] [angle] [pitch]";
			return false;
		}

		NiPoint3 pivot{ 0.0f, 0.0f, 0.0f };
		float distance = 0.0f;
		float angle = 0.0f;
		float pitch = 0.0f;
		std::string reference;

		// Constraint: a named target resolves through the loaded cell, so an orbit can name what frame framed.
		if (!IsNumber(words[0]))
		{
			auto* cell = reinterpret_cast<EditorCell*>(target.cell);
			if (!cell)
			{
				errorCode = "no_cell";
				errorMessage = "no cell is loaded; load one before orbiting a reference";
				return false;
			}

			Match hit;
			if (!ResolveReference(cell, words[0], hit, errorCode, errorMessage))
				return false;

			// Constraint: the pivot is the same box centre frame uses, so the two tools agree on the object.
			const Bounds bounds = ReadBounds(hit.ref);
			pivot = bounds.center;
			reference = hit.id.empty() ? hit.baseId : hit.id;
			// Constraint: a zero radius means "size it from the bounds", the same default frame uses.
			const float asked = words.size() > 1 ? static_cast<float>(std::atof(words[1].c_str())) : 0.0f;
			distance = asked > 0.0f ? asked : kFrameFit * bounds.radius;
			angle = words.size() > 2 ? static_cast<float>(std::atof(words[2].c_str())) : 0.0f;
			pitch = words.size() > 3 ? static_cast<float>(std::atof(words[3].c_str())) : 0.0f;
		}
		else
		{
			if (words.size() < 5)
			{
				errorCode = "bad_args";
				errorMessage = "orbit needs x y z distance angle [pitch], or a reference name [distance] [angle] [pitch]";
				return false;
			}

			pivot = NiPoint3{ static_cast<float>(std::atof(words[0].c_str())),
				static_cast<float>(std::atof(words[1].c_str())),
				static_cast<float>(std::atof(words[2].c_str())) };
			distance = static_cast<float>(std::atof(words[3].c_str()));
			angle = static_cast<float>(std::atof(words[4].c_str()));
			pitch = words.size() > 5 ? static_cast<float>(std::atof(words[5].c_str())) : 0.0f;
		}

		// Constraint: a radius of zero or less is not a circle, so the distance must be positive.
		if (!(distance > 0.0f))
		{
			errorCode = "bad_args";
			errorMessage = "distance must be greater than zero";
			return false;
		}

		// Constraint: the editor orbits and zooms about its own pivot, so the focus must be set to this point.
		camera->SetPivot(pivot, 1.0f, false);

		// Position must come from the pivot minus the view direction, so the circle keeps the radius asked for.
		const double cp = std::cos(pitch);
		camera->SetPosition(NiPoint3{
			pivot.x - distance * static_cast<float>(std::cos(angle) * cp),
			pivot.y - distance * static_cast<float>(std::sin(angle) * cp),
			pivot.z - distance * static_cast<float>(std::sin(pitch)) });

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

		char buf[900];
		std::snprintf(buf, sizeof buf,
			"{\"reference\":\"%s\",\"pivot\":[%.4f,%.4f,%.4f],\"pivotRead\":[%.4f,%.4f,%.4f],\"hasPivot\":%s,"
			"\"angle\":%.4f,\"pitch\":%.4f,\"distance\":%.2f,\"camera\":[%.4f,%.4f,%.4f],"
			"\"span\":%.2f,\"aim\":%.4f,\"aligned\":%s}",
			reference.c_str(),
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
