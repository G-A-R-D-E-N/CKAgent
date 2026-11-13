// CKAgent plugin entry points; the export shape must match CKPE's TestPlugin.

#include <windows.h>

#include <CKPE.Module.h>
#include <CKPE.PathUtils.h>
#include <CKPE.PluginAPI.PluginAPI.h>

#include "Agent.h"

extern "C"
{
	__declspec(dllexport) CKPE::PluginAPI::CKPEPluginVersionData CKPEPlugin_Version =
	{
		CKPE::PluginAPI::CKPEPluginVersionData::kVersion,
		1,                                        // plugin version
		"CKAgent",
		"GARDEN",                                 // author
		CKPE::PluginAPI::CKPEPluginVersionData::kGameFallout4,
		0,                                        // minimum CKPE.Common version
		{ MAKE_EXE_VERSION_EX(1, 11, 246, 0), 0 }, // compatible editor builds, zero terminated
		0                                         // flags
	};

	__declspec(dllexport) bool CKPEPlugin_HasDependencies() noexcept(true)
	{
		return false;
	}

	__declspec(dllexport) std::uint32_t CKPEPlugin_GetDependCount() noexcept(true)
	{
		return 0;
	}

	__declspec(dllexport) const char* CKPEPlugin_GetDependAt(std::uint32_t) noexcept(true)
	{
		return nullptr;
	}

	__declspec(dllexport) std::uint32_t CKPEPlugin_Load(const CKPE::PluginAPI::CKPEPluginInterface* intf) noexcept(true)
	{
		if (!intf)
			return false;

		auto logPath = CKPE::PathUtils::GetCKPELogsPluginPath() + L"CKAgent.log";
		CKPE::PathUtils::CreateFolder(CKPE::PathUtils::ExtractFilePath(CKPE::PathUtils::Normalize(logPath)));

		if (!CKPE::PluginAPI::UserPluginLogger.Open(logPath))
		{
			CKPE::_ERROR(L"CKAgent: could not open \"%s\"", logPath.c_str());
			return false;
		}

		CKPE::PluginAPI::_MESSAGE("CKAgent: loading, editor runtime 0x%llX", intf->RuntimeVersion);

		if (!ckagent::Start(intf->RuntimeVersion))
		{
			CKPE::PluginAPI::_ERROR("CKAgent: transport failed to start");
			return false;
		}

		return true;
	}
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
	// DllMain must not call Stop(): it runs under the loader lock and joining the pipe thread can deadlock.
	(void)reason;
	return TRUE;
}
