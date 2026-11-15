cmake_minimum_required(VERSION 3.25)

project(CKAgentPlugin LANGUAGES CXX)

# CKPE cannot be built on the CI host yet: its own solution is a full MSVC build with
# dependencies of its own. Point CKPE_ROOT at a CKPE checkout instead, so this project
# fails loudly with an actionable message rather than half-configuring.
#
#   cmake -S CKAgent/plugin -B build/ckagent -G Ninja ^
#         -DCKPE_ROOT=<path to a Creation-Kit-Platform-Extended checkout>
#
# A compile-only configure proves every plugin source still compiles against the CKPE headers and needs no
# built CKPE, which is what the workflow does; linking the DLL needs the import libraries CKPE's own build
# produces in <CKPE_ROOT>/x64.
set(CKPE_ROOT "" CACHE PATH "CKPE checkout (contains CKPE/, and x64/ when it has been built)")
option(CKAGENT_COMPILE_ONLY "Compile the plugin sources without linking against a built CKPE" OFF)

if(NOT MSVC)
  message(FATAL_ERROR "CKAgentPlugin builds with MSVC x64 only")
endif()

if(NOT CKPE_ROOT)
  message(FATAL_ERROR
    "CKPE_ROOT is not set. Point it at a Creation-Kit-Platform-Extended checkout: the headers are enough for "
    "-DCKAGENT_COMPILE_ONLY=ON, and a full build also needs CKPE.lib, CKPE.Common.lib and CKPE.PluginAPI.lib "
    "in <CKPE_ROOT>/x64.")
endif()

set(CKPE_LIB_DIR "${CKPE_ROOT}/x64" CACHE PATH "Directory holding the CKPE import libraries")

if(NOT CKAGENT_COMPILE_ONLY)
  foreach(lib CKPE CKPE.Common CKPE.PluginAPI)
    if(NOT EXISTS "${CKPE_LIB_DIR}/${lib}.lib")
      message(FATAL_ERROR "Missing ${CKPE_LIB_DIR}/${lib}.lib — build CKPE (x64) before this project")
    endif()
  endforeach()
endif()

set(CKAGENT_PLUGIN_SOURCES
  dllmain.cpp
  Agent.cpp
  AgentUi.cpp
  AgentEditorUi.cpp
  AgentUia.cpp
  AgentCellFlow.cpp
  AgentFrame.cpp
  AgentNavmesh.cpp
  AgentNavmeshCheck.cpp
  AgentNavmeshFindings.cpp
  AgentNavmeshRead.cpp
  AgentPlace.cpp
  AgentWire.cpp
)

if(CKAGENT_COMPILE_ONLY)
  add_library(CKAgentPlugin OBJECT ${CKAGENT_PLUGIN_SOURCES})
else()
  add_library(CKAgentPlugin SHARED ${CKAGENT_PLUGIN_SOURCES})
endif()

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

if(NOT CKAGENT_COMPILE_ONLY)
  target_link_libraries(CKAgentPlugin PRIVATE
    "${CKPE_LIB_DIR}/CKPE.lib"
    "${CKPE_LIB_DIR}/CKPE.Common.lib"
    "${CKPE_LIB_DIR}/CKPE.PluginAPI.lib"
    UIAutomationCore.lib
  )

  set_target_properties(CKAgentPlugin PROPERTIES
    PREFIX ""
    SUFFIX ".dll"
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL"
  )
endif()

# CKPE loads plugins from <GAMEDIR>\CKPEPlugins\.
set(CKPE_PLUGIN_DIR "" CACHE PATH "Optional: copy the built plugin here (…\\Fallout 4\\CKPEPlugins)")
if(CKPE_PLUGIN_DIR AND NOT CKAGENT_COMPILE_ONLY)
  add_custom_command(TARGET CKAgentPlugin POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CKPE_PLUGIN_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "$<TARGET_FILE:CKAgentPlugin>" "${CKPE_PLUGIN_DIR}/CKAgent.dll"
    COMMENT "Installing CKAgent.dll into ${CKPE_PLUGIN_DIR}")
endif()
