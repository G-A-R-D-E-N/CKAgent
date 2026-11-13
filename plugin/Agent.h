#pragma once

#include <cstdint>

namespace ckagent
{
	// Must be called from CKPEPlugin_Load on the editor thread; false means the transport failed.
	bool Start(std::uint64_t runtimeVersion) noexcept;

	// Safe when Start was never called; the worker must not outlive this call.
	void Stop() noexcept;
}
