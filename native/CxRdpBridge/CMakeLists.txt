cmake_minimum_required(VERSION 3.22)
project(CxRdpBridge LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(FreeRDP REQUIRED CONFIG)
find_package(WinPR REQUIRED CONFIG)
find_package(OpenSSL REQUIRED)
find_path(FREERDP_INCLUDE_DIR NAMES freerdp/freerdp.h PATH_SUFFIXES freerdp3)
find_library(CJSON_LIBRARY NAMES cjson REQUIRED)
get_filename_component(CJSON_LIBRARY_DIR "${CJSON_LIBRARY}" DIRECTORY)

add_library(CxRdpBridge SHARED
    cxrdp_bridge.cpp
    cxrdp_bridge.h
)

target_include_directories(CxRdpBridge
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}
    PRIVATE
        ${FREERDP_INCLUDE_DIR}
)

target_link_directories(CxRdpBridge PRIVATE ${CJSON_LIBRARY_DIR})

target_link_libraries(CxRdpBridge PRIVATE freerdp winpr ${CJSON_LIBRARY} OpenSSL::Crypto)

target_compile_definitions(CxRdpBridge PRIVATE CX_RDP_BRIDGE_EXPORTS)

if (APPLE)
    set_target_properties(CxRdpBridge PROPERTIES
        INSTALL_RPATH "@loader_path"
        BUILD_WITH_INSTALL_RPATH TRUE
    )
elseif (UNIX)
    set_target_properties(CxRdpBridge PROPERTIES
        INSTALL_RPATH "$ORIGIN"
        BUILD_WITH_INSTALL_RPATH TRUE
    )
endif()
