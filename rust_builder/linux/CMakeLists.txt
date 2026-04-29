# The Flutter tooling requires that developers have CMake 3.10 or later
# installed. You should not increase this version, as doing so will cause
# the plugin to fail to compile for some customers of the plugin.
cmake_minimum_required(VERSION 3.10)

# Project-level configuration.
set(PROJECT_NAME "rust_lib_floss")
project(${PROJECT_NAME} LANGUAGES CXX)

add_library(${PROJECT_NAME}_plugin SHARED
  "rust_lib_floss_plugin.cc"
)

apply_standard_settings(${PROJECT_NAME}_plugin)
target_compile_definitions(${PROJECT_NAME}_plugin PRIVATE FLUTTER_PLUGIN_IMPL)
target_include_directories(${PROJECT_NAME}_plugin INTERFACE
  "${CMAKE_CURRENT_SOURCE_DIR}/include"
)
target_link_libraries(${PROJECT_NAME}_plugin PRIVATE flutter)

include("../cargokit/cmake/cargokit.cmake")
apply_cargokit(${PROJECT_NAME} ../../rust rust_lib_floss "")

# List of absolute paths to libraries that should be bundled with the plugin.
# This list could contain prebuilt libraries, or libraries created by an
# external build triggered from this build file.
set(rust_lib_floss_bundled_libraries
  "${${PROJECT_NAME}_cargokit_lib}"
  PARENT_SCOPE
)
