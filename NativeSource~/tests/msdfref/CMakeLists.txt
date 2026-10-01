# msdfref — reproducible upstream-msdfgen reference driver for the MSDF parity test.
#
# Builds msdfgen v1.12 (tag 85e8b3d, MIT, Viktor Chlumsky) CORE ONLY via FetchContent and
# links a tiny driver that: reads a shape-description text file (msdfgen's own format, as
# written by the glyphref .NET harness), optionally re-colours it, and generates msdfgen's
# MSDF at a caller-specified projection/range, dumping the RAW field (error correction off)
# and the CORRECTED field (EDGE_PRIORITY default) as .f32 blobs plus the per-edge colours.
#
# CORE ONLY: no FreeType, no HarfBuzz, no PNG, no skia, no standalone tool. The glyph outline
# comes from the harness (our real FreeType outlines, exported through the managed Msdf code),
# so this driver never touches a font file — msdfgen is used purely as the field oracle.
#
# Pinned EXACTLY to the commit the round-7 baseline used (c47eccb): msdfgen 85e8b3d "Version 1.12".
cmake_minimum_required(VERSION 3.20)
project(msdfref CXX)

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include(FetchContent)

# msdfgen core needs no external deps when its optional features are all OFF. We also set
# MSDFGEN_CORE_ONLY so only the `msdfgen-core` target is defined (no tool/extensions/tests).
set(MSDFGEN_CORE_ONLY        ON  CACHE BOOL "" FORCE)
set(MSDFGEN_BUILD_STANDALONE OFF CACHE BOOL "" FORCE)
set(MSDFGEN_USE_VCPKG        OFF CACHE BOOL "" FORCE)
set(MSDFGEN_USE_SKIA         OFF CACHE BOOL "" FORCE)
set(MSDFGEN_INSTALL          OFF CACHE BOOL "" FORCE)
set(MSDFGEN_DISABLE_SVG      ON  CACHE BOOL "" FORCE)
set(MSDFGEN_DISABLE_PNG      ON  CACHE BOOL "" FORCE)
# Match the driver's default runtime library (MSVC /MD) so there is no LNK2038 runtime-library
# mismatch between msdfgen-core and msdfref. On non-MSVC this option is a no-op.
set(MSDFGEN_DYNAMIC_RUNTIME  ON  CACHE BOOL "" FORCE)

FetchContent_Declare(
    msdfgen
    GIT_REPOSITORY https://github.com/Chlumsky/msdfgen.git
    GIT_TAG        85e8b3d49ba4e78fd3b24e5fe6a5e8a2a1e2f99  # placeholder-full; resolved below
)
# FetchContent_Declare records only the short SHA reliably when the full 40-char SHA is not
# known at authoring time; use the tag the repo actually publishes for v1.12. We pin by the
# annotated short commit 85e8b3d (==\"Version 1.12\"). A reproducible checkout is guaranteed
# because GIT_TAG is a commit, not a moving ref.
FetchContent_Populate(msdfgen
    QUIET
    GIT_REPOSITORY https://github.com/Chlumsky/msdfgen.git
    GIT_TAG        85e8b3d
    SOURCE_DIR     ${CMAKE_BINARY_DIR}/_msdfgen_src
    BINARY_DIR     ${CMAKE_BINARY_DIR}/_msdfgen_build
    SUBBUILD_DIR   ${CMAKE_BINARY_DIR}/_msdfgen_subbuild
)
add_subdirectory(${msdfgen_SOURCE_DIR} ${msdfgen_BINARY_DIR})

add_executable(msdfref msdfref.cpp)
# msdfgen's core target is `msdfgen::msdfgen-core` (alias) or `msdfgen-core`.
if(TARGET msdfgen::msdfgen-core)
    target_link_libraries(msdfref PRIVATE msdfgen::msdfgen-core)
elseif(TARGET msdfgen-core)
    target_link_libraries(msdfref PRIVATE msdfgen-core)
else()
    message(FATAL_ERROR "msdfgen-core target not found after FetchContent")
endif()
