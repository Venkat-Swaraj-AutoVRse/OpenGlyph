# OpenGlyph native layer — clean-room rebuild of unitext_native{,_editor}
#
# Builds:
#   unitext_native         (runtime: FreeType + HarfBuzz + Blend2D, 110 ut_* exports)
#   unitext_native_editor  (editor: harfbuzz-subset + native file dialog, 8 exports)
#
# Upstream sources are FETCHED at PINNED versions (no vendored copies, no closed source).
# Third-party licenses recorded in ../Third-Party Notices.txt.
#
# Windows x64 build (VS generator, ninja not required):
#   cmake -S NativeSource~ -B build -G "Visual Studio 17 2022" -A x64 -DCMAKE_BUILD_TYPE=Release
#   cmake --build build --config Release
#
cmake_minimum_required(VERSION 3.24)
project(openglyph_native LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# ---- Pinned upstream versions -------------------------------------------------
# FreeType: 2.13.3 — has SDF/BSDF modules, COLRv1 paint API, sbix bitmap_top fix,
#           and the MM/variations API needed for Task 3.
set(OPENGLYPH_FREETYPE_TAG   "VER-2-13-3")
# HarfBuzz: 12.x as required by the brief.
set(OPENGLYPH_HARFBUZZ_TAG   "12.2.0")
# Blend2D: commit-versioned (no semver releases). Pin an exact commit before first build.
#          Depends on asmjit (also commit-pinned). REPLACE the placeholders below with the
#          exact SHAs you intend to ship, then record them in Third-Party Notices.txt.
set(OPENGLYPH_BLEND2D_TAG    "REPLACE_WITH_PINNED_BLEND2D_COMMIT")
set(OPENGLYPH_ASMJIT_TAG     "REPLACE_WITH_PINNED_ASMJIT_COMMIT")

include(FetchContent)

# ---- FreeType (BSD-style FTL / GPLv2 dual) -----------------------------------
FetchContent_Declare(freetype
  GIT_REPOSITORY https://github.com/freetype/freetype.git
  GIT_TAG        ${OPENGLYPH_FREETYPE_TAG}
  GIT_SHALLOW    TRUE)
# Build FreeType without external deps for a self-contained DLL.
set(FT_DISABLE_ZLIB    ON  CACHE BOOL "" FORCE)
set(FT_DISABLE_BZIP2   ON  CACHE BOOL "" FORCE)
set(FT_DISABLE_PNG     ON  CACHE BOOL "" FORCE)
set(FT_DISABLE_HARFBUZZ ON CACHE BOOL "" FORCE)  # avoid circular dep; HB links FT, not vice-versa
set(FT_DISABLE_BROTLI  ON  CACHE BOOL "" FORCE)

# ---- HarfBuzz 12.x (MIT "Old") -----------------------------------------------
FetchContent_Declare(harfbuzz
  GIT_REPOSITORY https://github.com/harfbuzz/harfbuzz.git
  GIT_TAG        ${OPENGLYPH_HARFBUZZ_TAG}
  GIT_SHALLOW    TRUE)
set(HB_HAVE_FREETYPE   ON  CACHE BOOL "" FORCE)   # hb_ft_* + ot funcs
set(HB_BUILD_SUBSET    ON  CACHE BOOL "" FORCE)   # editor subsetting
set(HB_BUILD_TESTS     OFF CACHE BOOL "" FORCE)
set(HB_BUILD_UTILS     OFF CACHE BOOL "" FORCE)
set(SKIP_INSTALL_ALL   ON  CACHE BOOL "" FORCE)

# ---- Blend2D (Zlib) + asmjit (Zlib) ------------------------------------------
# NOTE: enable once the commit SHAs above are pinned. Kept gated so the FT+HB
#       build is exercisable before Blend2D is wired.
option(OPENGLYPH_ENABLE_BLEND2D "Fetch and link Blend2D" OFF)
if(OPENGLYPH_ENABLE_BLEND2D)
  FetchContent_Declare(asmjit
    GIT_REPOSITORY https://github.com/asmjit/asmjit.git
    GIT_TAG        ${OPENGLYPH_ASMJIT_TAG})
  set(ASMJIT_STATIC ON CACHE BOOL "" FORCE)
  FetchContent_Declare(blend2d
    GIT_REPOSITORY https://github.com/blend2d/blend2d.git
    GIT_TAG        ${OPENGLYPH_BLEND2D_TAG})
  set(BLEND2D_STATIC ON CACHE BOOL "" FORCE)
  set(ASMJIT_DIR ${asmjit_SOURCE_DIR} CACHE PATH "" FORCE)
  FetchContent_MakeAvailable(asmjit blend2d)
endif()

FetchContent_MakeAvailable(freetype harfbuzz)

# ---- Runtime library: unitext_native -----------------------------------------
add_library(unitext_native SHARED
  src/ut_ft.c
  src/ut_ft_variations.c
  src/ut_colr.c
  src/ut_hb.c
  src/ut_bl.cpp
  src/ut_version.c
  src/ut_exports.def)   # explicit export list: exactly the 110 ut_* symbols
target_link_libraries(unitext_native PRIVATE freetype harfbuzz)
if(OPENGLYPH_ENABLE_BLEND2D)
  target_link_libraries(unitext_native PRIVATE blend2d::blend2d)
  target_compile_definitions(unitext_native PRIVATE OPENGLYPH_HAVE_BLEND2D=1)
endif()
target_include_directories(unitext_native PRIVATE include)
set_target_properties(unitext_native PROPERTIES PREFIX "" OUTPUT_NAME "unitext_native")

# ---- Editor library: unitext_native_editor -----------------------------------
add_library(unitext_native_editor SHARED
  src/editor/subset.c
  src/editor/dialog_win32.c
  src/editor/ut_editor_exports.def)
target_link_libraries(unitext_native_editor PRIVATE harfbuzz-subset harfbuzz)
target_include_directories(unitext_native_editor PRIVATE include)
set_target_properties(unitext_native_editor PROPERTIES PREFIX "" OUTPUT_NAME "unitext_native_editor")
if(WIN32)
  target_link_libraries(unitext_native_editor PRIVATE comdlg32)
endif()
