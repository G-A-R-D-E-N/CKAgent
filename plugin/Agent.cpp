// CKAgent transport; every editor call must be dispatched on the editor thread.

#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>
#include <thread>

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

		// Nothing is dereferenced until DialogWindow and DisplayControlID both check out: the published object must match.
		std::uintptr_t ResolveRenderControl(HWND& window, std::string& reason)
		{
			window = FindRenderWindow();
			if (!window)
			{
				reason = "no MonitorClass render window yet";
				return 0;
			}

			auto candidate = reinterpret_cast<std::uintptr_t>(GetWindowLongPtrW(window, GWLP_USERDATA));
			if (!candidate)
			{
				reason = "the render window published nothing in GWLP_USERDATA";
				return 0;
			}

			if (!IsReadable(reinterpret_cast<void*>(candidate), 0x68))
			{
				reason = "GWLP_USERDATA is not a readable object";
				return 0;
			}

			const auto dialog = *reinterpret_cast<HWND*>(candidate + 0x08);
			const auto displayId = *reinterpret_cast<std::uint32_t*>(candidate + 0x10);

			char buf[256];
			if (!dialog || !IsWindow(dialog))
			{
				std::snprintf(buf, sizeof buf,
					"the published object is not a TESRenderControl (DialogWindow=0x%llX); "
					"run tools/ck-surface-probe/render-control-probe.ps1",
					static_cast<unsigned long long>(reinterpret_cast<std::uintptr_t>(dialog)));
				reason = buf;
				return 0;
			}

			if (displayId == 0 || displayId > 65535 || !GetDlgItem(dialog, static_cast<int>(displayId)))
			{
				reason = "DialogWindow resolved but DisplayControlID does not name a child control";
				return 0;
			}

			return candidate;
		}

		bool ToolCamera(const std::string& args, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			HWND window = nullptr;
			std::string reason;
			const auto control = ResolveRenderControl(window, reason);
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

			char buf[640];
			std::snprintf(buf, sizeof buf,
				"{\"camera\":\"0x%llX\",\"x\":%.4f,\"y\":%.4f,\"z\":%.4f,\"scale\":%.4f,"
				"\"rotate\":[%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f],"
				"\"moved\":%s,\"rotationVerified\":false}",
				static_cast<unsigned long long>(camera),
				translate[0], translate[1], translate[2], scale,
				rotate[0], rotate[1], rotate[2], rotate[4], rotate[5], rotate[6], rotate[8], rotate[9], rotate[10],
				moved ? "true" : "false");

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
			DestroyWindow(g_window);
			g_window = nullptr;
		}
	}
}
