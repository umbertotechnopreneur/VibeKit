cmake_minimum_required(VERSION 3.25)
project(HardDriveSound VERSION 0.1.0 LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "Build with MSVC and the Windows SDK.")
endif()

find_package(Python3 REQUIRED COMPONENTS Interpreter)
add_executable(HardDriveSound WIN32 Main.cpp ../Shared/Mixer.cpp)
target_compile_features(HardDriveSound PRIVATE cxx_std_20)
target_compile_definitions(HardDriveSound PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(HardDriveSound PRIVATE /W4 /EHsc /utf-8)
set_property(TARGET HardDriveSound PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_link_libraries(HardDriveSound PRIVATE user32 gdi32 shell32 ole32 advapi32 winmm pdh)
add_custom_command(TARGET HardDriveSound POST_BUILD COMMAND "${Python3_EXECUTABLE}"
    "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/build-samples.py" "$<TARGET_FILE_DIR:HardDriveSound>/default")
install(TARGETS HardDriveSound RUNTIME DESTINATION .)
install(DIRECTORY "$<TARGET_FILE_DIR:HardDriveSound>/default" DESTINATION .)
install(FILES ../LICENSE DESTINATION .)
