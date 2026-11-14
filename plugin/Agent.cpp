// CKAgent transport; every editor call must be dispatched on the editor thread.

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cctype>
#include <cstdarg>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <CKPE.Common.AddressLibrary.h>
#include <CKPE.PluginAPI.PluginAPI.h>
#include <EditorAPI/B/BGSRenderWindowCamera.h>
#include <EditorAPI/T/TESObjectCELL.h>

#include "Agent.h"
#include "AgentCellFlow.h"
#include "AgentEditorShared.h"
#include "AgentFrame.h"
#include "AgentEditorUi.h"
#include "AgentUi.h"
#include "Protocol.h"
#include "AgentWire.h"

namespace ckagent
{
	namespace
	{
		using CameraApi = CKPE::Fallout4::EditorAPI::BGSRenderWindowCamera;
		using EditorCell = CKPE::Fallout4::EditorAPI::Forms::TESObjectCELL;
		using NiPoint3 = CKPE::Fallout4::EditorAPI::NiPoint3;
		using NiMatrix43 = CKPE::Fallout4::EditorAPI::NiMatrix43;

		// Every diagnostic must be one line, so the editor's Log Window stays readable.
		std::string Format(const char* fmt, ...)
		{
			char buf[1024];
			va_list args;
			va_start(args, fmt);
			std::vsnprintf(buf, sizeof buf, fmt, args);
			va_end(args);
			return buf;
		}

		// Every image address must be rebased: the editor is relocated and never loads at 0x140000000.
		constexpr std::uintptr_t kTesSingletonOffset = 0x3D49150;       // offset of the TES singleton pointer
		constexpr std::uintptr_t kRenderWindowSlotOffset = 0x3D491E8;   // offset of the BGSRenderWindow pointer (findings/ckpe-render-control.md)
		constexpr std::uintptr_t kRenderWindowTableOffset = 0x2FD40B8;  // offset of BGSRenderWindow's class table
		constexpr std::uintptr_t kWindowCameraOffset = 0xC0;            // offset of the BGSRenderWindowCamera pointer
		constexpr std::uintptr_t kCameraTableOffset = 0x2FD4828;        // offset of BGSRenderWindowCamera's class table
		constexpr std::size_t kCameraSize = 0x88;                       // BGSRenderWindowCamera size, so a read stays inside it
		constexpr std::uintptr_t kRenderWindowSize = 0x180;             // BGSRenderWindow size, so a read stays inside it

		// The view must be driven through the scene camera (findings/ckpe-render-control.md holds the chain).
		constexpr std::uintptr_t kCameraNiCameraOffset = 0x10;  // offset of the NiCamera pointer inside the camera object
		constexpr std::uintptr_t kNiCameraTableOffset = 0x3430CC0;  // offset of NiCamera's class table
		constexpr std::uintptr_t kNiCameraWorld = 0x70;         // offset of NiTransform _World, so rotation lives here
		constexpr std::uintptr_t kNiCameraTranslate = 0xA0;     // offset of _World m_Translate (0x70 + 0x30)
		constexpr std::uintptr_t kNiCameraScale = 0xAC;         // offset of _World m_fScale (0x70 + 0x3C)
		constexpr std::uintptr_t kNiRowStride = 4;              // rotation row stride: NiMatrix43 is float[3][4] (CKPE NiMatrix.h)
		constexpr std::uintptr_t kNiCameraReadable = 0xB0;      // bytes that must be readable from the scene camera

		std::uintptr_t g_imageBase = 0;  // runtime image base, captured at load so each offset rebases correctly

		// No image offset may be used raw: the editor picks its base at load, so every read rebases first.
		std::uintptr_t ImageAddress(std::uintptr_t offset)
		{
			return g_imageBase ? g_imageBase + offset : 0;
		}

		struct Call
		{
			std::string request;   // after "CALL <seq> "
			std::string reply;     // full line to write back, including trailing newline
			HANDLE done = nullptr;
		};

		std::mutex g_mutex;
		std::deque<Call*> g_queue;
		HWND g_window = nullptr;
		DWORD g_editorThread = 0;
		HANDLE g_pipe = nullptr;
		std::thread g_worker;
		std::thread g_lowWorker;
		std::atomic<bool> g_running{ false };
		std::atomic<bool> g_threadOk{ false };
		bool g_buildSupported = false;

		// Resolution state; only the editor thread must touch it.
		std::uintptr_t g_renderWindow = 0;
		std::uintptr_t g_camera = 0;
		const char* g_cameraSource = "none";
		const char* g_cameraWhen = "none";
		std::string g_cameraReason;

		std::string FormatBuild(std::uint64_t version)
		{
			char buf[64];
			std::snprintf(buf, sizeof buf, "%u.%u.%u.%u",
				GET_EXE_VERSION_EX_MAJOR(version),
				GET_EXE_VERSION_EX_MINOR(version),
				GET_EXE_VERSION_EX_BUILD(version),
				GET_EXE_VERSION_EX_REVISION(version));
			return buf;
		}

		bool MatchesSupportedBuild(std::uint64_t version)
		{
			return GET_EXE_VERSION_EX_MAJOR(version) == 1 &&
				GET_EXE_VERSION_EX_MINOR(version) == 11 &&
				GET_EXE_VERSION_EX_BUILD(version) == 246 &&
				GET_EXE_VERSION_EX_REVISION(version) == 0;
		}

		// --- tools (must run on the editor thread) ---

		std::string ToolStatus()
		{
			if (!g_buildSupported)
			{
				char buf[256];
				std::snprintf(buf, sizeof buf,
					"{\"supported\":false,\"build\":\"%s\",\"required\":\"%s\"}",
					kSupportedEditorBuild, kSupportedEditorBuild);
				return buf;
			}

			const Placement where = ReadPlacement(ImageAddress(kTesSingletonOffset));
			if (!where.hasSingleton)
				return "{\"supported\":true,\"loaded\":false}";

			// A blocked editor must say so here, or a caller sees only a disabled frame and no reason.
			const editor::ModalBlock modal = editor::HiddenModal();

			char buf[900];
			std::snprintf(buf, sizeof buf,
				"{\"supported\":true,\"loaded\":%s,\"interior\":%s,"
				"\"gridX\":%d,\"gridY\":%d,\"hasWorldSpace\":%s,"
				"\"pid\":%lu,\"onEditorThread\":%s,\"blockedBy\":\"%s\",\"modalButtons\":%s,"
				"\"dismissBy\":\"%s\"}",
				where.empty ? "false" : "true",
				where.interior ? "true" : "false",
				where.gridX, where.gridY,
				where.hasWorldSpace ? "true" : "false",
				GetCurrentProcessId(),
				g_threadOk.load() ? "true" : "false",
				modal.title.c_str(), modal.buttons.c_str(), modal.dismiss.c_str());
			return buf;
		}

		// --- render window: one address in image data, plus the camera inside it (findings/ckpe-render-control.md) ---
		// No walk must happen: the editor stores its window pointer in image data and the camera inside it.

		bool IsReadable(const void* address, std::size_t bytes)
		{
			if (!address) return false;

			MEMORY_BASIC_INFORMATION info{};
			if (!VirtualQuery(address, &info, sizeof info)) return false;
			if (info.State != MEM_COMMIT) return false;
			if (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;

			const auto start = reinterpret_cast<std::uintptr_t>(address);
			const auto end = reinterpret_cast<std::uintptr_t>(info.BaseAddress) + info.RegionSize;
			return start + bytes >= start && start + bytes <= end;
		}

		// The pointer must come from the image slot, and its class table must match before anything is read.
		std::uintptr_t AnchoredRenderWindow()
		{
			const auto slot = ImageAddress(kRenderWindowSlotOffset);
			if (!IsReadable(reinterpret_cast<const void*>(slot), sizeof(std::uintptr_t)))
				return 0;

			const auto window = *reinterpret_cast<const std::uintptr_t*>(slot);
			if (!window || (window & (sizeof(std::uintptr_t) - 1)))
				return 0;
			if (!IsReadable(reinterpret_cast<const void*>(window), kRenderWindowSize))
				return 0;
			if (*reinterpret_cast<const std::uintptr_t*>(window) != ImageAddress(kRenderWindowTableOffset))
				return 0;

			return window;
		}

		// The camera must carry BGSRenderWindowCamera's class table; a leftover pointer on the heap must not pass.
		std::uintptr_t AnchoredCamera(std::uintptr_t window, std::string& reason)
		{
			if (!window)
			{
				reason = "the render window global is empty";
				return 0;
			}

			if (!IsReadable(reinterpret_cast<const void*>(window), kWindowCameraOffset + sizeof(std::uintptr_t)))
			{
				reason = "the render window cannot be read";
				return 0;
			}

			const auto camera = *reinterpret_cast<const std::uintptr_t*>(window + kWindowCameraOffset);
			if (!camera || (camera & (sizeof(std::uintptr_t) - 1)))
			{
				reason = "the render window has no camera yet; load a cell first";
				return 0;
			}

			if (!IsReadable(reinterpret_cast<const void*>(camera), kCameraSize) ||
				*reinterpret_cast<const std::uintptr_t*>(camera) != ImageAddress(kCameraTableOffset))
			{
				reason = "the window's camera is not a BGSRenderWindowCamera";
				return 0;
			}

			return camera;
		}

		// Resolution must be retried on a timer: CKPE loads plugins before the editor builds its window.
		constexpr UINT_PTR kResolveTimer = 1;
		constexpr UINT_PTR kUiTimer = 2;      // the button must appear even when the camera never resolves
		bool g_uiTicked = false;              // the first timer tick must be visible in the log
		constexpr UINT kResolveInterval = 500;
		constexpr UINT kResolveWindowTicks = 120;

		UINT g_resolveTicks = 0;

		// Both anchors must hold at once: the window pointer and the camera object inside it.
		bool ResolveNow(const char* when)
		{
			g_renderWindow = 0;
			g_camera = 0;
			g_cameraSource = "none";
			g_cameraWhen = when;

			g_renderWindow = AnchoredRenderWindow();
			if (!g_renderWindow)
			{
				g_cameraReason = "the render window pointer slot is empty or does not carry its class table"
					" (findings/ckpe-render-control.md)";
				return false;
			}

			std::string reason;
			g_camera = AnchoredCamera(g_renderWindow, reason);
			if (!g_camera)
			{
				g_cameraReason = reason;
				return false;
			}

			g_cameraSource = "image global";
			g_cameraReason.clear();
			return true;
		}

		// CKPE loads plugins before the editor creates the render window, so it must be waited for on a timer.
		void ArmResolveTimer()
		{
			if (!g_window)
				return;

			if (!SetTimer(g_window, kResolveTimer, kResolveInterval, nullptr))
				LogLine("CKAgent: could not arm the camera resolver");
		}

		// The window's camera must carry the scene's NiCamera; the transform is only read through it.
		std::uintptr_t AnchoredNiCamera(std::uintptr_t camera, std::string& reason)
		{
			if (!IsReadable(reinterpret_cast<const void*>(camera), kCameraSize))
			{
				reason = "the camera object cannot be read";
				return 0;
			}

			const auto niCamera = *reinterpret_cast<const std::uintptr_t*>(camera + kCameraNiCameraOffset);
			if (!niCamera || (niCamera & (sizeof(std::uintptr_t) - 1)))
			{
				reason = "the camera has no scene camera yet; load a cell first";
				return 0;
			}

			if (!IsReadable(reinterpret_cast<const void*>(niCamera), kNiCameraReadable) ||
				*reinterpret_cast<const std::uintptr_t*>(niCamera) != ImageAddress(kNiCameraTableOffset))
			{
				reason = "the scene camera is not an NiCamera";
				return 0;
			}

			return niCamera;
		}

		// The row stride must be 4: a 3-wide read mixes rows and reports a valid matrix as broken.
		float RowLength(const float* rotate, int row)
		{
			const float x = rotate[row * kNiRowStride + 0];
			const float y = rotate[row * kNiRowStride + 1];
			const float z = rotate[row * kNiRowStride + 2];
			return std::sqrt(x * x + y * y + z * z);
		}

		float RowDot(const float* rotate, int a, int b)
		{
			return rotate[a * kNiRowStride + 0] * rotate[b * kNiRowStride + 0] +
				rotate[a * kNiRowStride + 1] * rotate[b * kNiRowStride + 1] +
				rotate[a * kNiRowStride + 2] * rotate[b * kNiRowStride + 2];
		}

		// A rotation must be orthonormal and scaled before one float is trusted; a half-built scene must be refused.
		bool ReadsAsTransform(std::uintptr_t niCamera, std::string& reason)
		{
			const auto* rotate = reinterpret_cast<const float*>(niCamera + kNiCameraWorld);
			const float scale = *reinterpret_cast<const float*>(niCamera + kNiCameraScale);

			const bool orthogonal =
				std::fabs(RowLength(rotate, 0) - 1.0f) < 0.01f &&
				std::fabs(RowLength(rotate, 1) - 1.0f) < 0.01f &&
				std::fabs(RowLength(rotate, 2) - 1.0f) < 0.01f &&
				std::fabs(RowDot(rotate, 0, 1)) < 0.01f &&
				std::fabs(RowDot(rotate, 0, 2)) < 0.01f &&
				std::fabs(RowDot(rotate, 1, 2)) < 0.01f;

			if (!orthogonal || !(scale > 0.0f && scale < 1000.0f))
			{
				reason = "the scene camera has no usable transform yet; load a cell first";
				return false;
			}

			return true;
		}

		// A lookat must be judged on the scene camera's view row, because that is the one the editor renders.
		bool AimsAt(void* camera, const NiPoint3& from, float tx, float ty, float tz)
		{
			float forward[3] = { 0.0f, 0.0f, 0.0f };
			if (!ReadSceneView(camera, forward)) return false;

			const double dx = static_cast<double>(tx) - from.x;
			const double dy = static_cast<double>(ty) - from.y;
			const double dz = static_cast<double>(tz) - from.z;
			const double span = std::sqrt(dx * dx + dy * dy + dz * dz);
			if (span < 1e-6) return false;

			return (forward[0] * dx + forward[1] * dy + forward[2] * dz) / span > 0.999;
		}

		bool ToolCamera(const std::string& args, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			if (!ResolveNow(g_cameraWhen))
			{
				errorCode = "camera_unavailable";
				errorMessage = g_cameraReason;
				return false;
			}

			std::string reason;
			const auto niCamera = AnchoredNiCamera(g_camera, reason);
			if (!niCamera)
			{
				errorCode = "camera_unavailable";
				errorMessage = reason;
				return false;
			}

			if (!ReadsAsTransform(niCamera, reason))
			{
				errorCode = "camera_transform_unverified";
				errorMessage = reason;
				return false;
			}

			if (args.rfind("lens", 0) == 0) return RunLensTool(args, FrameTarget{ reinterpret_cast<void*>(g_camera), nullptr }, payload, errorCode, errorMessage);
			if (args.rfind("rotate", 0) == 0) return RunRotateTool(args, FrameTarget{ reinterpret_cast<void*>(g_camera), nullptr }, payload, errorCode, errorMessage);

			// Constraint: the slave is copied from the camera node each frame, so a write goes to the object.
			auto* camera = reinterpret_cast<CameraApi*>(g_camera);
			auto* node = camera->GetNode();
			const auto* worldTranslate = node ? reinterpret_cast<const float*>(reinterpret_cast<std::uintptr_t>(node) + kNiCameraTranslate) : nullptr;
			const auto* worldRotate = node ? reinterpret_cast<const float*>(reinterpret_cast<std::uintptr_t>(node) + kNiCameraWorld) : nullptr;

			const NiPoint3 before = camera->GetPosition();
			float beforeWorld[3] = { 0, 0, 0 };
			if (worldTranslate) { beforeWorld[0] = worldTranslate[0]; beforeWorld[1] = worldTranslate[1]; beforeWorld[2] = worldTranslate[2]; }

			float x = 0, y = 0, z = 0, yaw = 0, pitch = 0;
			const bool oriented = std::sscanf(args.c_str(), "set %f %f %f %f %f", &x, &y, &z, &yaw, &pitch) == 5;
			const bool moved = oriented || std::sscanf(args.c_str(), "set %f %f %f", &x, &y, &z) == 3;

			// Ground truth: MoveLocal must define the camera axes, so a forward direction is measured, not assumed.
			float mx = 0, my = 0, mz = 0;
			const bool stepped = std::sscanf(args.c_str(), "move %f %f %f", &mx, &my, &mz) == 3;

			// LookAt is the editor's own idea of where the camera points, so it must settle the forward axis.
			float lx = 0, ly = 0, lz = 0;
			const bool looking = std::sscanf(args.c_str(), "lookat %f %f %f", &lx, &ly, &lz) == 3;

			bool lookatFromEditor = false;

			if (stepped)
				camera->MoveLocal(mx, my, mz);

			if (looking)
			{
				// Constraint: the editor's LookAt is tried first, but what it achieved must be measured, not assumed.
				const NiPoint3 from = camera->GetPosition();
				camera->LookAt(NiPoint3{ lx, ly, lz });
				lookatFromEditor = true;

				if (!AimsAt(reinterpret_cast<void*>(g_camera), from, lx, ly, lz))
				{
					// A LookAt that missed must still leave the camera pointed at the target, so the pose is computed.
					const double dx = static_cast<double>(lx) - camera->GetPosition().x;
					const double dy = static_cast<double>(ly) - camera->GetPosition().y;
					const double dz = static_cast<double>(lz) - camera->GetPosition().z;
					const double flat = std::sqrt(dx * dx + dy * dy);

					if (flat > 1e-6)
					{
						NiMatrix43 computed{};
						const ViewMatrix view = MakeViewMatrix(static_cast<float>(std::atan2(dy, dx)),
							static_cast<float>(std::atan2(dz, flat)));
						std::memcpy(&computed, view.m, sizeof computed);
						camera->SetRotation(computed);
					}

					lookatFromEditor = false;
				}
			}

			if (moved)
			{
				camera->SetPosition(NiPoint3{ x, y, z });

				if (oriented)
				{
					// The pose must use the measured basis: row0 right, row1 forward, row2 up.
					NiMatrix43 rotate{};
					const ViewMatrix view = MakeViewMatrix(yaw, pitch);
					std::memcpy(&rotate, view.m, sizeof rotate);
					camera->SetRotation(rotate);
				}
			}

			const NiPoint3 after = camera->GetPosition();
			const NiMatrix43 rotation = camera->GetRotate();

			// The forward row and the achieved aim must be reported, so a lookat that missed cannot look like one that hit.
			float forwardRow[3] = { 0.0f, 0.0f, 0.0f };
			const bool haveForward = ReadSceneView(reinterpret_cast<void*>(g_camera), forwardRow);
			double lookatAim = 0.0;
			if (haveForward && looking)
			{
				const double ax = static_cast<double>(lx) - after.x;
				const double ay = static_cast<double>(ly) - after.y;
				const double az = static_cast<double>(lz) - after.z;
				const double span = std::sqrt(ax * ax + ay * ay + az * az);
				if (span > 1e-6)
					lookatAim = (forwardRow[0] * ax + forwardRow[1] * ay + forwardRow[2] * az) / span;
			}

			const auto* sceneTranslate = reinterpret_cast<const float*>(niCamera + kNiCameraTranslate);
			const auto* sceneRotate = reinterpret_cast<const float*>(niCamera + kNiCameraWorld);
			const float scale = *reinterpret_cast<const float*>(niCamera + kNiCameraScale);

			float stepDelta[3] = { 0, 0, 0 };
			if (stepped && worldTranslate)
			{
				stepDelta[0] = worldTranslate[0] - beforeWorld[0];
				stepDelta[1] = worldTranslate[1] - beforeWorld[1];
				stepDelta[2] = worldTranslate[2] - beforeWorld[2];
			}

			// A pose must be verified as a whole: position alone would pass while the orientation stayed stale.
			const double drift = worldTranslate
				? std::fabs(worldTranslate[0] - sceneTranslate[0]) + std::fabs(worldTranslate[1] - sceneTranslate[1]) + std::fabs(worldTranslate[2] - sceneTranslate[2])
				: 999.0;

			double rotationDrift = worldRotate ? 0.0 : 999.0;
			for (int row = 0; worldRotate && row < 3; ++row)
				for (int col = 0; col < 3; ++col)
					rotationDrift += std::fabs(worldRotate[row * kNiRowStride + col] - sceneRotate[row * kNiRowStride + col]);

			// Invariant: the scene camera's rotation is the node's shifted by one row, so only translation is
			// comparable directly; the rotation difference is reported as a pose-dependent offset instead.
			const bool synced = drift < 0.5;

			// The reply must name every object it touched, and a lookat must say which path aimed it.
			const char* via = !looking ? "direct" : (lookatFromEditor ? "lookat-editor" : "lookat-computed");

			char buf[1400];
			std::snprintf(buf, sizeof buf,
				"{\"camera\":\"0x%llX\",\"sceneCamera\":\"0x%llX\","
				"\"x\":%.4f,\"y\":%.4f,\"z\":%.4f,\"wasX\":%.4f,\"wasY\":%.4f,\"wasZ\":%.4f,"
				"\"rotate\":[%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f],\"scale\":%.4f,"
				"\"sceneRotate\":[%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f],\"scene\":[%.4f,%.4f,%.4f],"
				"\"rotationOffset\":%.4f,\"positionSynced\":%s,"
				"\"step\":[%.4f,%.4f,%.4f],\"moved\":%s,"
				"\"forward\":[%.4f,%.4f,%.4f],\"aim\":%.4f,\"via\":\"%s\","
				"\"window\":\"0x%llX\",\"resolvedAt\":\"%s\"}",
				static_cast<unsigned long long>(g_camera),
				static_cast<unsigned long long>(niCamera),
				worldTranslate ? worldTranslate[0] : after.x,
				worldTranslate ? worldTranslate[1] : after.y,
				worldTranslate ? worldTranslate[2] : after.z,
				before.x, before.y, before.z,
				worldRotate ? worldRotate[0] : 0.0f, worldRotate ? worldRotate[1] : 0.0f, worldRotate ? worldRotate[2] : 0.0f,
				worldRotate ? worldRotate[kNiRowStride + 0] : 0.0f, worldRotate ? worldRotate[kNiRowStride + 1] : 0.0f, worldRotate ? worldRotate[kNiRowStride + 2] : 0.0f,
				worldRotate ? worldRotate[kNiRowStride * 2 + 0] : 0.0f, worldRotate ? worldRotate[kNiRowStride * 2 + 1] : 0.0f, worldRotate ? worldRotate[kNiRowStride * 2 + 2] : 0.0f,
				scale,
				sceneRotate[0], sceneRotate[1], sceneRotate[2], sceneRotate[kNiRowStride + 0], sceneRotate[kNiRowStride + 1], sceneRotate[kNiRowStride + 2], sceneRotate[kNiRowStride * 2 + 0], sceneRotate[kNiRowStride * 2 + 1], sceneRotate[kNiRowStride * 2 + 2],
				sceneTranslate[0], sceneTranslate[1], sceneTranslate[2], rotationDrift,
				synced ? "true" : "false",
				stepDelta[0], stepDelta[1], stepDelta[2],
				moved ? "true" : "false",
				forwardRow[0], forwardRow[1], forwardRow[2], lookatAim, via,
				static_cast<unsigned long long>(g_renderWindow),
				g_cameraWhen);

			// The local matrix must still be read, so a rotated parent stays diagnosable from one reply.
			(void)rotation;

			payload = buf;
			return true;
		}

		// A cell load can replace the camera object, so one explicit pass must refresh both anchors.
		std::string ToolResolve()
		{
			const bool resolved = ResolveNow("manual");

			char buf[512];
			std::snprintf(buf, sizeof buf,
				"{\"resolved\":%s,\"window\":\"0x%llX\",\"camera\":\"0x%llX\",\"reason\":\"%s\"}",
				resolved ? "true" : "false",
				static_cast<unsigned long long>(g_renderWindow),
				static_cast<unsigned long long>(g_camera),
				resolved ? "" : g_cameraReason.c_str());

			return buf;
		}

		// A cell load must be observable: the pointer the render window holds is the evidence.
		std::string ToolCell()
		{
			if (!ResolveNow(g_cameraWhen))
			{
				char buf[320];
				std::snprintf(buf, sizeof buf, "{\"resolved\":false,\"cell\":null,\"reason\":\"%s\"}", g_cameraReason.c_str());
				return buf;
			}

			const auto cell = *reinterpret_cast<const std::uintptr_t*>(g_renderWindow + kCurrentCellOffset);

			const Placement where = ReadPlacement(ImageAddress(kTesSingletonOffset));

			// A confirmed load must name the cell, because a pointer alone says nothing about which one it is.
			std::string editorId, formId;
			bool readable = false;
			if (cell && IsReadable(reinterpret_cast<const void*>(cell), 0x180))
			{
				auto* loaded = reinterpret_cast<EditorCell*>(cell);
				const char* id = loaded->GetFormEditorID();
				editorId = id ? id : "";
				char idbuf[16];
				std::snprintf(idbuf, sizeof idbuf, "%08X", loaded->GetFormID());
				formId = idbuf;
				readable = true;
			}

			char buf[768];
			std::snprintf(buf, sizeof buf,
				"{\"resolved\":true,\"window\":\"0x%llX\",\"cell\":\"0x%llX\",\"loaded\":%s,\"readable\":%s,"
				"\"editorId\":%s,\"formId\":%s,"
				"\"interior\":%s,\"gridX\":%d,\"gridY\":%d,\"hasWorldSpace\":%s}",
				static_cast<unsigned long long>(g_renderWindow),
				static_cast<unsigned long long>(cell),
				cell ? "true" : "false",
				readable ? "true" : "false",
				editor::Quote(editorId).c_str(),
				editor::Quote(formId).c_str(),
				where.interior ? "true" : "false",
				where.gridX, where.gridY,
				where.hasWorldSpace ? "true" : "false");
			return buf;
		}

		void Dispatch(Call& call)
		{
			// Request must be "<seq> <tool> [args]".
			std::size_t first = call.request.find(' ');
			if (first == std::string::npos)
			{
				call.reply = "RPLY 0 ERR bad_request expected \"<seq> <tool>\"\n";
				return;
			}
			const std::string seq = call.request.substr(0, first);

			std::string tool, args;
			std::size_t second = call.request.find(' ', first + 1);
			if (second == std::string::npos)
			{
				tool = call.request.substr(first + 1);
			}
			else
			{
				tool = call.request.substr(first + 1, second - first - 1);
				args = call.request.substr(second + 1);
			}

			std::string payload, errorCode, errorMessage;

			if (tool == "status")
			{
				payload = ToolStatus();
			}
			else if (tool == "camera")
			{
				if (!ToolCamera(args, payload, errorCode, errorMessage))
				{
					call.reply = "RPLY " + seq + " ERR " + errorCode + " " + errorMessage + "\n";
					return;
				}
			}
			else if (tool == "resolve")
			{
				payload = ToolResolve();
			}
			else if (tool == "cell")
			{
				payload = ToolCell();
			}
			else if (tool == "frame" || tool == "aim" || tool == "orbit")
			{
				// A pose must run on this thread with resolved anchors, so the lookup and the write agree.
				FrameTarget target;
				if (!ResolveNow(g_cameraWhen))
				{
					errorCode = "camera_unavailable";
					errorMessage = g_cameraReason;
				}
				else
				{
					target.camera = reinterpret_cast<void*>(g_camera);
					target.cell = reinterpret_cast<void*>(
						*reinterpret_cast<const std::uintptr_t*>(g_renderWindow + kCurrentCellOffset));
				}

				const bool posed = errorCode.empty() &&
					(tool == "frame" ? RunFrameTool(args, target, payload, errorCode, errorMessage)
					 : tool == "aim" ? RunAimTool(args, target, payload, errorCode, errorMessage)
					                 : RunOrbitTool(args, target, payload, errorCode, errorMessage));

				if (!posed)
				{
					call.reply = "RPLY " + seq + " ERR " + errorCode + " " + errorMessage + "\n";
					return;
				}
			}
			else if (tool.rfind("ui.", 0) == 0)
			{
				// Constraint: the editor's own controls must be driven from this thread, where a handler
				// that opens a dialog or requires a cell load runs inline and the reply reports it.
				if (!RunEditorTool(g_renderWindow, tool, args, payload, errorCode, errorMessage))
				{
					call.reply = "RPLY " + seq + " ERR " + errorCode + " " + errorMessage + "\n";
					return;
				}
			}
			else
			{
				call.reply = "RPLY " + seq + " ERR unknown_tool " + tool + "\n";
				return;
			}

			call.reply = "RPLY " + seq + " OK " + payload + "\n";
		}

		LRESULT CALLBACK DispatchProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
		{
			if (msg == WM_TIMER && wParam == kUiTimer)
			{
				// A silent timer and a missing frame must be distinguishable in the log.
				if (!g_uiTicked)
				{
					g_uiTicked = true;
					LogLine("CKAgent: the editor UI timer is running");
				}

				// The button must be retried until the frame exists, then this timer is done.
				if (EnsureUi())
					KillTimer(hWnd, kUiTimer);
				return 0;
			}

			if (msg == WM_TIMER && wParam == kResolveTimer)
			{
				if (ResolveNow("deferred"))
				{
					KillTimer(hWnd, kResolveTimer);
					return 0;
				}

				// The editor must create its window before the anchors can hold; give up after the same window.
				if (++g_resolveTicks >= kResolveWindowTicks)
				{
					KillTimer(hWnd, kResolveTimer);
					g_cameraReason = "the render window never appeared after load";
				}

				return 0;
			}

			if (msg == kDispatchDrain)
			{
				for (;;)
				{
					Call* call = nullptr;
					{
						std::lock_guard<std::mutex> lock(g_mutex);
						if (g_queue.empty())
							break;
						call = g_queue.front();
						g_queue.pop_front();
					}

					Dispatch(*call);
					SetEvent(call->done);
				}
				return 0;
			}

			return DefWindowProcW(hWnd, msg, wParam, lParam);
		}

		bool CreateDispatchWindow()
		{
			WNDCLASSEXW wc{};
			wc.cbSize = sizeof wc;
			wc.lpfnWndProc = DispatchProc;
			wc.hInstance = GetModuleHandleW(nullptr);
			wc.lpszClassName = kDispatchClass;

			if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
				return false;

			g_window = CreateWindowExW(0, kDispatchClass, L"CKAgent", 0, 0, 0, 0, 0,
				HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
			return g_window != nullptr;
		}

		// Must run on the editor thread and wait for the answer.
		bool RunOnEditorThread(Call& call)
		{
			if (!g_threadOk.load())
			{
				call.reply = "RPLY 0 ERR wrong_thread plugin was not loaded on the editor thread\n";
				return false;
			}

			{
				std::lock_guard<std::mutex> lock(g_mutex);
				g_queue.push_back(&call);
			}

			if (!PostMessageW(g_window, kDispatchDrain, 0, 0))
			{
				std::lock_guard<std::mutex> lock(g_mutex);
				for (auto it = g_queue.begin(); it != g_queue.end(); ++it)
				{
					if (*it == &call)
					{
						g_queue.erase(it);
						break;
					}
				}
				call.reply = "RPLY 0 ERR dispatch_failed PostMessage failed\n";
				return false;
			}

			WaitForSingleObject(call.done, 60'000);
			return true;
		}

		// --- pipe (the worker must never touch editor state) ---

		void ServeClient(HANDLE pipe)
		{
			char helo[128];
			std::snprintf(helo, sizeof helo, "HELO %s %s\n", kProtocolVersion, kSupportedEditorBuild);
			if (!wire::WriteAll(pipe, helo))
				return;

			std::string carry;
			std::string line;
			while (g_running.load() && wire::ReadLine(pipe, carry, line))
			{
				if (line.rfind("CALL ", 0) != 0)
					continue;

				Call call;
				call.request = line.substr(5);
				call.done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
				if (!call.done)
					return;

				RunOnEditorThread(call);
				LogLine("CALL " + call.request + " -> " + wire::Trimmed(call.reply));
				const bool written = wire::WriteAll(pipe, call.reply);
				CloseHandle(call.done);
				if (!written)
					return;
			}
		}

		// The window must report what the plugin actually resolved, not what it hopes for.
		std::string StateText()
		{
			return Format("CKAgent  build %s  pid %lu\r\n"
				"window 0x%llX   camera 0x%llX   via %s\r\n"
				"camera: %s",
				kSupportedEditorBuild,
				GetCurrentProcessId(),
				static_cast<unsigned long long>(g_renderWindow),
				static_cast<unsigned long long>(g_camera),
				g_cameraSource,
				g_camera ? "resolved" : (g_cameraReason.empty() ? "unresolved" : g_cameraReason.c_str()));
		}

		void WorkerLoop(const wchar_t* pipeName, bool lowLabel)
		{
			// The worker must announce itself: a pipe that never appears has to be distinguishable from one that failed.
			LogLine(Format("CKAgent: worker started on %ls (pid %lu)", pipeName, GetCurrentProcessId()));

			while (g_running.load())
			{
				SECURITY_ATTRIBUTES security = PipeSecurity(lowLabel);
				HANDLE pipe = CreateNamedPipeW(pipeName,
					PIPE_ACCESS_DUPLEX,
					PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
					1, 64 * 1024, 64 * 1024, 0, &security);

				if (pipe == INVALID_HANDLE_VALUE)
				{
					LogLine(Format("CKAgent: CreateNamedPipe on %ls failed (%lu)", pipeName, GetLastError()));
					return;
				}

				g_pipe = pipe;
				const BOOL connected = ConnectNamedPipe(pipe, nullptr);
				if (connected || GetLastError() == ERROR_PIPE_CONNECTED)
					ServeClient(pipe);

				DisconnectNamedPipe(pipe);
				CloseHandle(pipe);
				g_pipe = nullptr;
			}
		}
	}

	// Offset arithmetic must use the scene camera, because the editor copies the node into it every frame.
	bool ReadSceneView(void* cameraPointer, float out[3])
	{
		std::string reason;
		const auto scene = AnchoredNiCamera(reinterpret_cast<std::uint64_t>(cameraPointer), reason);
		if (!scene) return false;

		// Constraint: row 0 of the scene camera is the view it renders, which is the node's row 1.
		const auto* rotate = reinterpret_cast<const float*>(scene + kNiCameraWorld);
		if (!IsReadable(rotate, kNiCameraReadable)) return false;

		out[0] = rotate[0];
		out[1] = rotate[1];
		out[2] = rotate[2];
		return true;
	}

	bool Start(std::uint64_t runtimeVersion) noexcept
	{
		// The image base must be read before any offset is used: the editor is relocated by ASLR.
		g_imageBase = reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr));

		g_buildSupported = MatchesSupportedBuild(runtimeVersion);
		g_editorThread = GetCurrentThreadId();
		g_threadOk = g_buildSupported && CreateDispatchWindow();

		if (!g_buildSupported)
			LogLine(Format("CKAgent: editor build %s is not supported (need %s)",
				FormatBuild(runtimeVersion).c_str(), kSupportedEditorBuild));

		if (!g_threadOk)
			return false;

		// The window must be able to describe the plugin state on its own.
		SetStateProvider(StateText);

		// Load-time resolution: the camera path must never walk memory.
		if (ResolveNow("load"))
		{
			LogLine(Format("CKAgent: render window 0x%llX, camera 0x%llX from %s",
				static_cast<unsigned long long>(g_renderWindow),
				static_cast<unsigned long long>(g_camera),
				g_cameraSource));
		}
		else
		{
			LogLine(Format("CKAgent: camera not ready (%s); waiting for the render window",
				g_cameraReason.c_str()));
			ArmResolveTimer();
		}

		// The button must be installed as soon as the frame exists, so a timer retries it.
		SetTimer(g_window, kUiTimer, 500, nullptr);

		g_running = true;
		g_worker = std::thread(WorkerLoop, kPipeName, false);

		// Constraint: the low-labelled pipe is opt-in, so the strict pipe is served alone unless the ini asks for more.
		if (LowIntegrityPipeEnabled())
			g_lowWorker = std::thread(WorkerLoop, kLowPipeName, true);

		LogLine(Format("CKAgent: serving on %ls for build %s", kPipeName, FormatBuild(runtimeVersion).c_str()));

		if (g_lowWorker.joinable())
			LogLine(Format("CKAgent: also serving on %ls, from CKAgent.ini [transport] LowIntegrityPipe=1",
				kLowPipeName));

		return true;
	}

	void Stop() noexcept
	{
		if (!g_running.exchange(false))
			return;

		// Constraint: Stop unblocks ConnectNamedPipe with a throwaway client on every pipe that was opened.
		HANDLE wake = CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
			OPEN_EXISTING, 0, nullptr);
		if (wake != INVALID_HANDLE_VALUE)
			CloseHandle(wake);

		if (g_lowWorker.joinable())
		{
			HANDLE lowWake = CreateFileW(kLowPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
				OPEN_EXISTING, 0, nullptr);
			if (lowWake != INVALID_HANDLE_VALUE)
				CloseHandle(lowWake);
		}

		if (g_worker.joinable())
			g_worker.join();

		if (g_lowWorker.joinable())
			g_lowWorker.join();

		if (g_window)
		{
			KillTimer(g_window, kResolveTimer);
			KillTimer(g_window, kUiTimer);
			DestroyWindow(g_window);
			g_window = nullptr;
		}
	}
}
