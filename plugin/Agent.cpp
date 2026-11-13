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

#include "Agent.h"
#include "Protocol.h"

namespace ckagent
{
	namespace
	{
		using TES = CKPE::Fallout4::EditorAPI::TES;

		// CKPE fixes the image base; this is the TES_Singleton address CKPE records for 1.11.246.0.
		constexpr std::uintptr_t kTesSingleton = 0x143D49150;

		// The image base must stay 0x140000000 for these addresses; findings/ckpe-render-control.md holds the walk.
		constexpr std::uintptr_t kRenderWindowGlobal = 0x143D491E8;  // address of the BGSRenderWindow pointer
		constexpr std::uintptr_t kRenderWindowTable = 0x142FD40B8;   // BGSRenderWindow class table address
		constexpr std::uintptr_t kRenderWindowOffset = 0x180;        // BGSRenderWindow size, so a read stays inside it
		constexpr std::uintptr_t kWindowCameraOffset = 0xC0;         // offset of the BGSRenderWindowCamera pointer
		constexpr std::uintptr_t kCameraTable = 0x142FD4828;         // BGSRenderWindowCamera class table address
		constexpr std::size_t kCameraSize = 0x88;                    // BGSRenderWindowCamera size, so a read stays inside it

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
			if (!IsReadable(reinterpret_cast<const void*>(kRenderWindowGlobal), sizeof(std::uintptr_t)))
				return 0;

			const auto window = *reinterpret_cast<const std::uintptr_t*>(kRenderWindowGlobal);
			if (!window || (window & (sizeof(std::uintptr_t) - 1)))
				return 0;
			if (!IsReadable(reinterpret_cast<const void*>(window), kRenderWindowOffset))
				return 0;
			if (*reinterpret_cast<const std::uintptr_t*>(window) != kRenderWindowTable)
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
				*reinterpret_cast<const std::uintptr_t*>(camera) != kCameraTable)
			{
				reason = "the window's camera is not a BGSRenderWindowCamera";
				return 0;
			}

			return camera;
		}

		// Resolution must be retried on a timer: CKPE loads plugins before the editor builds its window.
		constexpr UINT_PTR kResolveTimer = 1;
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
				g_cameraReason = "the render window pointer at 0x143D491E8 is empty or does not carry its class table"
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
				CKPE::PluginAPI::_WARNING("CKAgent: could not arm the camera resolver");
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

			// BGSRenderWindowCamera's pose fields must be mapped before a float is read or written.
			if (args.rfind("set", 0) == 0)
			{
				errorCode = "camera_transform_unmapped";
				errorMessage = "BGSRenderWindowCamera's pose offsets are unmapped (findings/ckpe-render-control.md)";
				return false;
			}

			char buf[384];
			std::snprintf(buf, sizeof buf,
				"{\"window\":\"0x%llX\",\"camera\":\"0x%llX\","
				"\"cameraClass\":\"BGSRenderWindowCamera\",\"transform\":\"unmapped\","
				"\"resolvedAt\":\"%s\"}",
				static_cast<unsigned long long>(g_renderWindow),
				static_cast<unsigned long long>(g_camera),
				g_cameraWhen);

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
			CKPE::PluginAPI::_MESSAGE("CKAgent: render window 0x%llX, camera 0x%llX from %s",
				static_cast<unsigned long long>(g_renderWindow),
				static_cast<unsigned long long>(g_camera),
				g_cameraSource);
		}
		else
		{
			CKPE::PluginAPI::_MESSAGE("CKAgent: camera not ready (%s); waiting for the render window",
				g_cameraReason.c_str());
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
