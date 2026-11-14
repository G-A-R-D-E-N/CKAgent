cmake_minimum_required(VERSION 3.25)

project(CKAgentPlugin LANGUAGES CXX)

# CKPE cannot be built on the CI host yet: its own solution is a full MSVC build with
# dependencies of its own. Point CKPE_ROOT at a built CKPE checkout instead, so this
# project fails loudly with an actionable message rather than half-configuring.
#
#   cmake -S CKAgent/plugin -B build/ckagent -G Ninja ^
#         -DCKPE_ROOT=<path to a built Creation-Kit-Platform-Extended checkout>
#
set(CKPE_ROOT "" CACHE PATH "Built CKPE checkout (contains CKPE/, x64/)")

if(NOT MSVC)
  message(FATAL_ERROR "CKAgentPlugin builds with MSVC x64 only")
endif()

if(NOT CKPE_ROOT)
  message(FATAL_ERROR
    "CKPE_ROOT is not set. Build Creation-Kit-Platform-Extended first, then configure with "
    "-DCKPE_ROOT=<checkout>. The plugin links CKPE.lib, CKPE.Common.lib and CKPE.PluginAPI.lib "
    "from <CKPE_ROOT>/x64.")
endif()

set(CKPE_LIB_DIR "${CKPE_ROOT}/x64" CACHE PATH "Directory holding the CKPE import libraries")

foreach(lib CKPE CKPE.Common CKPE.PluginAPI)
  if(NOT EXISTS "${CKPE_LIB_DIR}/${lib}.lib")
    message(FATAL_ERROR "Missing ${CKPE_LIB_DIR}/${lib}.lib — build CKPE (x64) before this project")
  endif()
endforeach()

add_library(CKAgentPlugin SHARED
  dllmain.cpp
  Agent.cpp
  AgentWire.cpp
  AgentUi.cpp
  AgentEditorUi.cpp
  AgentCellFlow.cpp
  AgentFrame.cpp
)

target_include_directories(CKAgentPlugin PRIVATE
  "${CKPE_ROOT}/CKPE/Include"
  "${CKPE_ROOT}/CKPE.Common/Include"
  "${CKPE_ROOT}/CKPE.PluginAPI/Include"
  "${CKPE_ROOT}/CKPE.Fallout4/Include"
)

target_compile_features(CKAgentPlugin PRIVATE cxx_std_23)
target_compile_definitions(CKAgentPlugin PRIVATE
  UNICODE
  _UNICODE
  WIN32_LEAN_AND_MEAN
  NOMINMAX
)

target_link_libraries(CKAgentPlugin PRIVATE
  "${CKPE_LIB_DIR}/CKPE.lib"
  "${CKPE_LIB_DIR}/CKPE.Common.lib"
  "${CKPE_LIB_DIR}/CKPE.PluginAPI.lib"
)

set_target_properties(CKAgentPlugin PROPERTIES
  PREFIX ""
  SUFFIX ".dll"
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL"
)

# CKPE loads plugins from <GAMEDIR>\CKPEPlugins\.
set(CKPE_PLUGIN_DIR "" CACHE PATH "Optional: copy the built plugin here (…\\Fallout 4\\CKPEPlugins)")
if(CKPE_PLUGIN_DIR)
  # Constraint: the install verifies what it copied, because a DLL the editor holds is otherwise left stale.
  # Constraint: the paths travel inside a generated script, since a -D argument splits on the space in "Fallout 4".
  set(CKAGENT_INSTALL_SCRIPT "${CMAKE_CURRENT_BINARY_DIR}/install-plugin.cmake")
  file(WRITE "${CKAGENT_INSTALL_SCRIPT}"
    "set(SRC \"${CMAKE_CURRENT_BINARY_DIR}/CKAgentPlugin.dll\")\n"
    "set(DEST \"${CKPE_PLUGIN_DIR}/CKAgent.dll\")\n"
    "include(\"${CMAKE_CURRENT_SOURCE_DIR}/InstallPlugin.cmake\")\n")

  add_custom_command(TARGET CKAgentPlugin POST_BUILD
    COMMAND ${CMAKE_COMMAND} -P "${CKAGENT_INSTALL_SCRIPT}"
    COMMENT "Installing CKAgent.dll into ${CKPE_PLUGIN_DIR}"
    VERBATIM)
endif()
