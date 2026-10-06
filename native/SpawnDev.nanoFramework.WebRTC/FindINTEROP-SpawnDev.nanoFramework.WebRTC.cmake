#
# Copyright (c) 2026 Todd Tanner (LostBeard) and SpawnDev.nanoFramework.WebRTC contributors. MIT License.
#
# nf-interpreter interop module for SpawnDev.nanoFramework.WebRTC. Used from OUTSIDE nf-interpreter: configure the
# firmware with
#   -DNF_INTEROP_ASSEMBLIES="SpawnDev.nanoFramework.WebRTC ..."
#   -DNF_INTEROP_SEARCH_PATHS=<this folder>
#   -DNF_EXTRA_IDF_COMPONENT_DIRS=<this repo>/native/components/libpeer   (our libpeer overrides the IDF registry copy)
# (nf-interpreter branch minirover/esp32-wrover added those two options.)
#

# sources live next to this module
set(BASE_PATH_FOR_THIS_MODULE ${CMAKE_CURRENT_LIST_DIR})

list(APPEND SpawnDev.nanoFramework.WebRTC_INCLUDE_DIRS ${PROJECT_SOURCE_DIR}/src/CLR/Core)
list(APPEND SpawnDev.nanoFramework.WebRTC_INCLUDE_DIRS ${PROJECT_SOURCE_DIR}/src/CLR/Include)
list(APPEND SpawnDev.nanoFramework.WebRTC_INCLUDE_DIRS ${PROJECT_SOURCE_DIR}/src/HAL/Include)
list(APPEND SpawnDev.nanoFramework.WebRTC_INCLUDE_DIRS ${PROJECT_SOURCE_DIR}/src/PAL/Include)
list(APPEND SpawnDev.nanoFramework.WebRTC_INCLUDE_DIRS ${BASE_PATH_FOR_THIS_MODULE})
# libpeer public headers (peer.h) from this repo's libpeer, the one the firmware links
list(APPEND SpawnDev.nanoFramework.WebRTC_INCLUDE_DIRS ${BASE_PATH_FOR_THIS_MODULE}/../components/libpeer/include)

set(SpawnDev.nanoFramework.WebRTC_SRCS
    SpawnDev_nanoFramework_WebRTC.cpp
    SpawnDev_nanoFramework_WebRTC_SpawnDev_nanoFramework_WebRTC_PeerConnection_mshl.cpp
    SpawnDev_nanoFramework_WebRTC_SpawnDev_nanoFramework_WebRTC_PeerConnection.cpp
)

foreach(SRC_FILE ${SpawnDev.nanoFramework.WebRTC_SRCS})
    # reset the cached result each time, or find_file reuses the first hit for every file
    unset(SpawnDev.nanoFramework.WebRTC_SRC_FILE CACHE)
    find_file(SpawnDev.nanoFramework.WebRTC_SRC_FILE ${SRC_FILE}
        PATHS ${BASE_PATH_FOR_THIS_MODULE}
        NO_DEFAULT_PATH
        CMAKE_FIND_ROOT_PATH_BOTH
    )
    if(NOT SpawnDev.nanoFramework.WebRTC_SRC_FILE)
        message(FATAL_ERROR "SpawnDev.nanoFramework.WebRTC: ${SRC_FILE} not found in ${BASE_PATH_FOR_THIS_MODULE}")
    endif()
    list(APPEND SpawnDev.nanoFramework.WebRTC_SOURCES ${SpawnDev.nanoFramework.WebRTC_SRC_FILE})
endforeach()

include(FindPackageHandleStandardArgs)
FIND_PACKAGE_HANDLE_STANDARD_ARGS(SpawnDev.nanoFramework.WebRTC DEFAULT_MSG SpawnDev.nanoFramework.WebRTC_INCLUDE_DIRS SpawnDev.nanoFramework.WebRTC_SOURCES)
