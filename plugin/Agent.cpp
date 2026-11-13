// CKAgent transport; every editor call must be dispatched on the editor thread.

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <CKPE.Common.AddressLibrary.h>
#include <CKPE.PluginAPI.PluginAPI.h>
#include <EditorAPI/T/TES.h>
#include <EditorAPI/T/TESRenderControl.h>

#include "Agent.h"
#include "Protocol.h"

namespace ckagent
{
	namespace
	{
		using TES = CKPE::Fallout4::EditorAPI::TES;
		using RenderControl = CKPE::Fallout4::EditorAPI::TESRenderControl;

		// CKPE fixes the image base; this is the TES_Singleton address CKPE records for 1.11.246.0.
		constexpr std::uintptr_t kTesSingleton = 0x143D49150;

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
		std::atomic<bool> g_running{ false };
		std::atomic<bool> g_threadOk{ false };
		bool g_buildSupported = false;

		// Resolution state; only the editor thread must touch it.
		std::uintptr_t g_controlTable = 0;
		std::uintptr_t g_control = 0;
		std::uintptr_t g_controlSlot = 0;
		const char* g_controlSource = "none";
		const char* g_controlWhen = "none";
		std::string g_controlReason;
		bool g_controlAnchored = false;

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

			if (TES::Singleton.Empty())
				TES::Singleton = kTesSingleton;

			TES* tes = TES::Singleton.GetSingleton();
			if (!tes)
				return "{\"supported\":true,\"loaded\":false}";

			const bool empty = tes->Empty();
			const bool interior = tes->HasLoadedInterior();
			const auto coords = tes->GetCurrentRendererCellCoordinates();

			char buf[320];
			std::snprintf(buf, sizeof buf,
				"{\"supported\":true,\"loaded\":%s,\"interior\":%s,"
				"\"gridX\":%d,\"gridY\":%d,\"hasWorldSpace\":%s,"
				"\"pid\":%lu,\"onEditorThread\":%s}",
				empty ? "false" : "true",
				interior ? "true" : "false",
				static_cast<int>(coords.X), static_cast<int>(coords.Y),
				tes->worldSpace ? "true" : "false",
				GetCurrentProcessId(),
				g_threadOk.load() ? "true" : "false");
			return buf;
		}

		// --- render control (offset table from CKPE's TESRenderControl.h) ---
		// Each field is read as raw memory at the documented offset.
		// Offset table: +0x008 DialogWindow, +0x010 DisplayControlID, +0x058 Camera.
		// Camera position must be read at the camera+0xA0 offset (NiAVObject::_World 0x70 + m_Translate 0x30).

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

		struct FindWindowContext
		{
			DWORD pid;
			HWND found;
		};

		BOOL CALLBACK FindRenderWindowProc(HWND hwnd, LPARAM lParam)
		{
			auto* context = reinterpret_cast<FindWindowContext*>(lParam);

			DWORD pid = 0;
			GetWindowThreadProcessId(hwnd, &pid);
			if (pid != context->pid) return TRUE;

			wchar_t name[64]{};
			if (GetClassNameW(hwnd, name, 64) == 0) return TRUE;
			if (wcscmp(name, L"MonitorClass") != 0) return TRUE;

			context->found = hwnd;
			return FALSE;
		}

		HWND FindRenderWindow()
		{
			FindWindowContext context{ GetCurrentProcessId(), nullptr };
			EnumWindows(FindRenderWindowProc, reinterpret_cast<LPARAM>(&context));
			return context.found;
		}

		// The class table must come from CKPE's ID database: one stable ID for every build.
		std::uintptr_t RenderControlTable()
		{
			static constexpr CKPE::Common::ID kTable = RenderControl::VTABLE // findings/ckpe-render-control.md stands behind this address
				;

			if (g_controlTable)
				return g_controlTable;

			const auto iddb = CKPE::Common::AddressLibrary::GetSingleton();
			if (!iddb || !iddb->IsLoaded())
				return 0;

			// A zero offset means no entry for this build; the module base alone must never be used.
			if (!kTable.Id() || !kTable.Offset())
				return 0;

			g_controlTable = kTable.Address();
			return g_controlTable;
		}

		// Exact class table, then the two fields that define the layout: nothing must be dereferenced on a guess.
		bool ReadsAsRenderControl(std::uintptr_t object, HWND renderWindow, bool& anchored)
		{
			anchored = false;

			if (!object || (object & (sizeof(std::uintptr_t) - 1)))
				return false;
			if (!IsReadable(reinterpret_cast<const void*>(object), sizeof(RenderControl)))
				return false;
			if (*reinterpret_cast<const std::uintptr_t*>(object) != g_controlTable)
				return false;

			const auto* control = reinterpret_cast<const RenderControl*>(object);
			if (!control->DialogWindow || !IsWindow(control->DialogWindow))
				return false;

			const auto display = static_cast<int>(control->DisplayControlID);
			if (display <= 0 || display > 0xFFFF)
				return false;

			const auto child = GetDlgItem(control->DialogWindow, display);
			if (!child)
				return false;

			for (auto window = child; window; window = GetParent(window))
			{
				if (window == renderWindow)
				{
					anchored = true;
					break;
				}
			}

			return true;
		}

		// Reads must go through ReadProcessMemory: another editor thread can free a page mid-walk.
		constexpr std::size_t kScanChunk = 32 * 1024;
		constexpr std::uint64_t kScanBudget = 6ull * 1024 * 1024 * 1024;

		template <class Match>
		std::uintptr_t ScanMemory(std::uintptr_t begin, std::uintptr_t end, std::uint64_t& scanned, Match&& match)
		{
			std::vector<std::uint8_t> buffer(kScanChunk);

			for (auto at = begin; at < end && scanned < kScanBudget; )
			{
				const auto want = static_cast<std::size_t>(std::min<std::uintptr_t>(kScanChunk, end - at));
				SIZE_T got = 0;
				if (!ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<const void*>(at),
						buffer.data(), want, &got) || got < sizeof(std::uintptr_t))
				{
					at += want;
					continue;
				}

				scanned += got;

				for (std::size_t i = 0; i + sizeof(std::uintptr_t) <= got; i += sizeof(std::uintptr_t))
				{
					const auto slot = at + i;
					if (slot & (sizeof(std::uintptr_t) - 1))
						continue;

					std::uintptr_t value = 0;
					std::memcpy(&value, buffer.data() + i, sizeof value);
					if (value && match(value, slot))
						return slot;
				}

				at += got;
			}

			return 0;
		}

		enum class ScanArea
		{
			ImageData,
			Private
		};

		// The image must be walked first: it is small, and it is where an owning global lives.
		template <class Match>
		std::uintptr_t ScanAreaFor(ScanArea area, std::uint64_t& scanned, Match&& match)
		{
			const auto moduleBase = reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr));

			SYSTEM_INFO info{};
			GetSystemInfo(&info);

			const auto limit = reinterpret_cast<std::uintptr_t>(info.lpMaximumApplicationAddress);

			for (auto at = reinterpret_cast<std::uintptr_t>(info.lpMinimumApplicationAddress);
				at < limit && scanned < kScanBudget; )
			{
				MEMORY_BASIC_INFORMATION region{};
				if (!VirtualQuery(reinterpret_cast<const void*>(at), &region, sizeof region) || !region.RegionSize)
				{
					at += 0x1000;
					continue;
				}

				const auto base = reinterpret_cast<std::uintptr_t>(region.BaseAddress);
				const auto size = static_cast<std::uintptr_t>(region.RegionSize);

				const bool writable = region.State == MEM_COMMIT &&
					!(region.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
					(region.Protect & (PAGE_READWRITE | PAGE_WRITECOPY));

				const bool wanted = writable && (area == ScanArea::ImageData
					? region.Type == MEM_IMAGE && reinterpret_cast<std::uintptr_t>(region.AllocationBase) == moduleBase
					: region.Type == MEM_PRIVATE);

				if (wanted)
				{
					if (const auto hit = ScanMemory(base, base + size, scanned, match))
						return hit;
				}

				at = base + size;
			}

			return 0;
		}

		// Resolution must run once, so the camera path never walks memory.
		constexpr UINT_PTR kResolveTimer = 1;
		constexpr UINT kResolveInterval = 500;
		constexpr UINT kResolveWindowTicks = 120;
		constexpr UINT kResolveWalks = 2;

		UINT g_resolveTicks = 0;
		UINT g_resolveWalks = 0;

		// One walk over the editor's image data, then its heap; the result must last the session.
		bool ResolveNow(const char* when)
		{
			g_control = 0;
			g_controlSlot = 0;
			g_controlAnchored = false;
			g_controlSource = "none";
			g_controlWhen = when;

			if (!RenderControlTable())
			{
				g_controlReason = "CKPE's ID database has no TESRenderControl class table for this editor build";
				return false;
			}

			const auto window = FindRenderWindow();
			if (!window)
			{
				g_controlReason = "no MonitorClass render window yet";
				return false;
			}

			bool anchored = false;
			std::uint64_t scanned = 0;
			const auto matches = [&](std::uintptr_t, std::uintptr_t slot)
			{
				bool hitAnchored = false;
				if (!ReadsAsRenderControl(slot, window, hitAnchored))
					return false;

				anchored = hitAnchored;
				return true;
			};

			auto slot = ScanAreaFor(ScanArea::ImageData, scanned, matches);
			if (slot)
			{
				g_controlSource = "image data";
			}
			else
			{
				slot = ScanAreaFor(ScanArea::Private, scanned, matches);
				if (slot)
					g_controlSource = "process heap";
			}

			if (!slot)
			{
				char buf[320];
				std::snprintf(buf, sizeof buf,
					"nothing in memory carries CKPE's TESRenderControl class table 0x%llX (scanned %llu MB)",
					static_cast<unsigned long long>(g_controlTable),
					static_cast<unsigned long long>(scanned >> 20));
				g_controlReason = buf;
				return false;
			}

			g_control = slot;
			g_controlAnchored = anchored;

			// A pointer slot in the image that names the instance is the owning global and must be recorded.
			const auto object = slot;
			std::uint64_t slotScan = 0;
			g_controlSlot = ScanAreaFor(ScanArea::ImageData, slotScan,
				[object](std::uintptr_t value, std::uintptr_t candidate)
				{
					return value == object && candidate != object;
				});

			return true;
		}

		// The camera path must only read what load time resolved; it never walks.
		std::uintptr_t CachedRenderControl(HWND& window, std::string& reason)
		{
			window = FindRenderWindow();
			if (!window)
			{
				reason = "no MonitorClass render window yet";
				return 0;
			}

			if (!g_control)
			{
				reason = g_controlReason.empty()
					? "the render control was not resolved when the plugin loaded"
					: g_controlReason;
				return 0;
			}

			bool anchored = false;
			if (!ReadsAsRenderControl(g_control, window, anchored))
			{
				reason = "the render control resolved at load no longer validates; restart the editor";
				return 0;
			}

			g_controlAnchored = anchored;
			return g_control;
		}

		// CKPE loads plugins before the editor creates the render window, so it must be waited for on a timer.
		void ArmResolveTimer()
		{
			if (!g_window)
				return;

			if (!SetTimer(g_window, kResolveTimer, kResolveInterval, nullptr))
				CKPE::PluginAPI::_WARNING("CKAgent: could not arm the render control resolver");
		}

		bool ToolCamera(const std::string& args, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			HWND window = nullptr;
			std::string reason;
			const auto control = CachedRenderControl(window, reason);
			if (!control)
			{
				errorCode = "render_control_unresolved";
				errorMessage = reason;
				return false;
			}

			const auto camera = *reinterpret_cast<std::uintptr_t*>(control + 0x58);
			if (!camera || !IsReadable(reinterpret_cast<void*>(camera), 0xB0))
			{
				errorCode = "camera_unavailable";
				errorMessage = "the render control has no camera; load a cell first";
				return false;
			}

			auto* translate = reinterpret_cast<float*>(camera + 0xA0);
			auto* rotate = reinterpret_cast<float*>(camera + 0x70);
			const float scale = *reinterpret_cast<float*>(camera + 0xAC);

			float x = 0, y = 0, z = 0, yaw = 0, pitch = 0;
			const bool oriented = std::sscanf(args.c_str(), "set %f %f %f %f %f", &x, &y, &z, &yaw, &pitch) == 5;
			const bool moved = oriented || std::sscanf(args.c_str(), "set %f %f %f", &x, &y, &z) == 3;

			if (moved)
			{
				translate[0] = x;
				translate[1] = y;
				translate[2] = z;

				if (oriented)
				{
					// Rotation must stay unverified until Gamebryo's basis is confirmed visually.
					const float cy = std::cos(yaw), sy = std::sin(yaw);
					const float cp = std::cos(pitch), sp = std::sin(pitch);

					rotate[0] = cy;          rotate[1] = sy;         rotate[2] = 0;   rotate[3] = 0;
					rotate[4] = -sy * cp;    rotate[5] = cy * cp;    rotate[6] = sp;  rotate[7] = 0;
					rotate[8] = sy * sp;     rotate[9] = -cy * sp;   rotate[10] = cp; rotate[11] = 0;
				}
			}

			// The reply must say which object was used: a resolution and a guess have to look different.
			char buf[896];
			std::snprintf(buf, sizeof buf,
				"{\"camera\":\"0x%llX\",\"x\":%.4f,\"y\":%.4f,\"z\":%.4f,\"scale\":%.4f,"
				"\"rotate\":[%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f],"
				"\"moved\":%s,\"rotationVerified\":false,"
				"\"control\":\"0x%llX\",\"controlSource\":\"%s\",\"controlAt\":\"%s\","
				"\"global\":\"0x%llX\",\"anchored\":%s}",
				static_cast<unsigned long long>(camera),
				translate[0], translate[1], translate[2], scale,
				rotate[0], rotate[1], rotate[2], rotate[4], rotate[5], rotate[6], rotate[8], rotate[9], rotate[10],
				moved ? "true" : "false",
				static_cast<unsigned long long>(control),
				g_controlSource,
				g_controlWhen,
				static_cast<unsigned long long>(g_controlSlot),
				g_controlAnchored ? "true" : "false");

			payload = buf;
			return true;
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
			else
			{
				call.reply = "RPLY " + seq + " ERR unknown_tool " + tool + "\n";
				return;
			}

			call.reply = "RPLY " + seq + " OK " + payload + "\n";
		}

		LRESULT CALLBACK DispatchProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
		{
			if (msg == WM_TIMER && wParam == kResolveTimer)
			{
				if (!FindRenderWindow())
				{
					// Waiting costs nothing; two walks after the window appears must be the whole cost.
					if (++g_resolveTicks >= kResolveWindowTicks)
					{
						KillTimer(hWnd, kResolveTimer);
						g_controlReason = "the render window never appeared after load";
					}
					return 0;
				}

				KillTimer(hWnd, kResolveTimer);

				if (ResolveNow("deferred"))
					return 0;

				if (++g_resolveWalks < kResolveWalks)
					SetTimer(g_window, kResolveTimer, kResolveInterval, nullptr);

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

		void ServeClient(HANDLE pipe)
		{
			char helo[128];
			std::snprintf(helo, sizeof helo, "HELO %s %s\n", kProtocolVersion, kSupportedEditorBuild);
			if (!WriteAll(pipe, helo))
				return;

			std::string carry;
			std::string line;
			while (g_running.load() && ReadLine(pipe, carry, line))
			{
				if (line.rfind("CALL ", 0) != 0)
					continue;

				Call call;
				call.request = line.substr(5);
				call.done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
				if (!call.done)
					return;

				RunOnEditorThread(call);
				const bool written = WriteAll(pipe, call.reply);
				CloseHandle(call.done);
				if (!written)
					return;
			}
		}

		void WorkerLoop()
		{
			while (g_running.load())
			{
				HANDLE pipe = CreateNamedPipeW(kPipeName,
					PIPE_ACCESS_DUPLEX,
					PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
					1, 64 * 1024, 64 * 1024, 0, nullptr);

				if (pipe == INVALID_HANDLE_VALUE)
				{
					CKPE::PluginAPI::_ERROR("CKAgent: CreateNamedPipe failed (%lu)", GetLastError());
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

	bool Start(std::uint64_t runtimeVersion) noexcept
	{
		g_buildSupported = MatchesSupportedBuild(runtimeVersion);
		g_editorThread = GetCurrentThreadId();
		g_threadOk = g_buildSupported && CreateDispatchWindow();

		if (!g_buildSupported)
			CKPE::PluginAPI::_ERROR("CKAgent: editor build %s is not supported (need %s)",
				FormatBuild(runtimeVersion).c_str(), kSupportedEditorBuild);

		if (!g_threadOk)
			return false;

		// Load-time resolution: the camera path must never walk memory.
		if (ResolveNow("load"))
		{
			CKPE::PluginAPI::_MESSAGE("CKAgent: render control 0x%llX from %s",
				static_cast<unsigned long long>(g_control), g_controlSource);
		}
		else
		{
			CKPE::PluginAPI::_MESSAGE("CKAgent: render control not ready (%s); waiting for the render window",
				g_controlReason.c_str());
			ArmResolveTimer();
		}

		g_running = true;
		g_worker = std::thread(WorkerLoop);

		CKPE::PluginAPI::_MESSAGE("CKAgent: serving on %ls for build %s",
			kPipeName, FormatBuild(runtimeVersion).c_str());
		return true;
	}

	void Stop() noexcept
	{
		if (!g_running.exchange(false))
			return;

					// Stop must unblock ConnectNamedPipe with a throwaway client.
		HANDLE wake = CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
			OPEN_EXISTING, 0, nullptr);
		if (wake != INVALID_HANDLE_VALUE)
			CloseHandle(wake);

		if (g_worker.joinable())
			g_worker.join();

		if (g_window)
		{
			KillTimer(g_window, kResolveTimer);
			DestroyWindow(g_window);
			g_window = nullptr;
		}
	}
}
