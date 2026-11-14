# Constraint: the installed DLL must be the one just linked, so a held file has to fail the build instead of passing.
#
# Run from the POST_BUILD step as: cmake -P <generated script>, which sets SRC and DEST first.
#
# CMake's plain copy step can leave the previous DLL in place while the build still looks successful, which is how a
# stale plugin ends up in CKPEPlugins with no sign of trouble. Copying through execute_process keeps the result, so a
# sharing violation becomes a message that names the cause, and the hash check afterwards catches a copy that reported
# success onto a file the editor already had open.

if(NOT SRC OR NOT DEST)
  message(FATAL_ERROR "InstallPlugin.cmake needs SRC and DEST set")
endif()

if(NOT DEST_DIR)
  get_filename_component(DEST_DIR "${DEST}" DIRECTORY)
endif()

if(NOT EXISTS "${SRC}")
  # Constraint: a multi-config generator writes the DLL into a per-configuration folder, so it is looked up there.
  get_filename_component(SRC_DIR "${SRC}" DIRECTORY)
  file(GLOB config_dlls LIST_DIRECTORIES false "${SRC_DIR}/*/CKAgentPlugin.dll")
  list(LENGTH config_dlls config_count)
  if(config_count EQUAL 1)
    set(SRC "${config_dlls}")
  elseif(config_count GREATER 1)
    message(FATAL_ERROR "several built plugins under ${SRC_DIR}: ${config_dlls}\nRemove the configuration that is not being installed, then build again.")
  else()
    message(FATAL_ERROR "the built plugin is missing at ${SRC}")
  endif()
endif()

file(SHA256 "${SRC}" expected)

# Constraint: the destination is never read before the copy, because a file the editor holds cannot even be read.
execute_process(
  COMMAND "${CMAKE_COMMAND}" -E make_directory "${DEST_DIR}"
  RESULT_VARIABLE make_result
  ERROR_VARIABLE make_error)
if(NOT make_result EQUAL 0)
  message(FATAL_ERROR "could not create ${DEST_DIR}: ${make_error}")
endif()

execute_process(
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${SRC}" "${DEST}"
  RESULT_VARIABLE copy_result
  ERROR_VARIABLE copy_error)
if(NOT copy_result EQUAL 0)
  message(FATAL_ERROR
    "CKAgent.dll could not be installed into ${DEST}\n"
    "  ${copy_error}\n"
    "The editor is almost certainly holding the file: close CreationKit.exe and build again.\n"
    "The DLL on disk is still the previous build, so nothing was installed.")
endif()

file(SHA256 "${DEST}" installed)
if(NOT installed STREQUAL expected)
  message(FATAL_ERROR
    "the installed CKAgent.dll does not match the one just built\n"
    "  built:     ${SRC} ${expected}\n"
    "  installed: ${DEST} ${installed}\n"
    "Check whether another copy of the plugin is loaded, then build again.")
endif()

message(STATUS "installed CKAgent.dll into ${DEST} (${expected})")
