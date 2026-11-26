cmake_minimum_required(VERSION 3.25)
project(CrtDegauss VERSION 0.1.1 LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "Build with MSVC and the Windows 11 SDK on Windows.")
endif()

find_package(Python3 REQUIRED COMPONENTS Interpreter)

set(capture_source "${CMAKE_CURRENT_SOURCE_DIR}/../../RetroTerminal/Windows")
add_executable(CrtDegauss WIN32 Main.cpp CrtDegauss.manifest "${capture_source}/Renderer.cpp")
target_include_directories(CrtDegauss PRIVATE "${capture_source}")
target_compile_features(CrtDegauss PRIVATE cxx_std_20)
set_property(TARGET CrtDegauss PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_compile_definitions(CrtDegauss PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(CrtDegauss PRIVATE /W4 /EHsc /permissive- /utf-8)
target_link_libraries(CrtDegauss PRIVATE d3d11 dxgi dcomp d3dcompiler windowsapp
    user32 comctl32 wtsapi32 advapi32 gdi32 shell32 winmm)
add_custom_command(TARGET CrtDegauss POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "$<TARGET_FILE_DIR:CrtDegauss>/Shaders"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${CMAKE_CURRENT_SOURCE_DIR}/Shaders/Degauss.hlsl"
        "$<TARGET_FILE_DIR:CrtDegauss>/Shaders/Degauss.hlsl"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${CMAKE_CURRENT_SOURCE_DIR}/../Shared/degauss.wav"
        "$<TARGET_FILE_DIR:CrtDegauss>/degauss.wav"
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/build-sounds.py"
        "$<TARGET_FILE_DIR:CrtDegauss>/sounds")

install(TARGETS CrtDegauss RUNTIME DESTINATION .)
install(FILES Shaders/Degauss.hlsl DESTINATION Shaders)
install(FILES ../LICENSE ../Shared/degauss.wav DESTINATION .)
install(DIRECTORY "$<TARGET_FILE_DIR:CrtDegauss>/sounds" DESTINATION .)
