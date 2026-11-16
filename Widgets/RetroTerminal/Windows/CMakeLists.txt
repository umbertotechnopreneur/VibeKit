cmake_minimum_required(VERSION 3.25)
project(RetroTerminal LANGUAGES CXX)
if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "Build with MSVC and the Windows 11 SDK on Windows.")
endif()
add_executable(RetroTerminal WIN32 Main.cpp Renderer.cpp RetroTerminal.manifest)
set_property(TARGET RetroTerminal PROPERTY CXX_STANDARD 20)
set_property(TARGET RetroTerminal PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_compile_definitions(RetroTerminal PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(RetroTerminal PRIVATE /W4 /EHsc /permissive- /utf-8)
target_link_libraries(RetroTerminal PRIVATE d3d11 dxgi dcomp d3dcompiler windowsapp user32 comctl32 comdlg32 wtsapi32 advapi32 gdi32)
add_custom_command(TARGET RetroTerminal POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "$<TARGET_FILE_DIR:RetroTerminal>/Shaders"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${CMAKE_CURRENT_SOURCE_DIR}/Shaders/Retro.hlsl" "$<TARGET_FILE_DIR:RetroTerminal>/Shaders/Retro.hlsl")
