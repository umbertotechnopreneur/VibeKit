cmake_minimum_required(VERSION 3.25)
project(CrtDegauss LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "Build with MSVC and the Windows 11 SDK on Windows.")
endif()

find_package(Python3 REQUIRED COMPONENTS Interpreter)

set(capture_source "${CMAKE_CURRENT_SOURCE_DIR}/../../RetroTerminal/Windows")
add_library(CrtDegauss SHARED Main.cpp ../../Shared/Native/Engine.cpp "${capture_source}/Renderer.cpp")
target_include_directories(CrtDegauss PRIVATE "${capture_source}" "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_features(CrtDegauss PRIVATE cxx_std_20)
set_property(TARGET CrtDegauss PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_compile_definitions(CrtDegauss PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(CrtDegauss PRIVATE /W4 /EHsc /permissive- /utf-8)
target_link_libraries(CrtDegauss PRIVATE d3d11 dxgi dcomp d3dcompiler windowsapp
    user32 comctl32 wtsapi32 advapi32 gdi32 shell32 winmm)
set(bank_dir "${CMAKE_CURRENT_BINARY_DIR}/sounds")
set(bank_files)
foreach(sound IN ITEMS dry-relay low-coil long-buzz desktop-monitor pal-television)
    list(APPEND bank_files "${bank_dir}/${sound}.wav")
endforeach()
add_custom_command(OUTPUT ${bank_files}
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/build-sounds.py" "${bank_dir}"
    DEPENDS ../scripts/build-sounds.py ../Shared/Recordings/desktop-monitor.wav ../Shared/Recordings/pal-television.wav
    VERBATIM)
add_custom_target(DegaussSounds DEPENDS ${bank_files})
add_dependencies(CrtDegauss DegaussSounds)

set_target_properties(CrtDegauss PROPERTIES OUTPUT_NAME "WidgetEngine")
install(TARGETS CrtDegauss RUNTIME DESTINATION .)
install(FILES ../../Shared/Native/Shaders/Degauss.hlsl DESTINATION Shaders)
install(FILES ../LICENSE ../Shared/degauss.wav DESTINATION .)
install(FILES ${bank_files} DESTINATION sounds)
