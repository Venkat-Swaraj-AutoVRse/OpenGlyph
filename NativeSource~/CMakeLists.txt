# OpenGlyph native layer — clean-room rebuild of unitext_native{,_editor}
#
# Builds:
#   unitext_native         (runtime: FreeType + HarfBuzz [+ Blend2D in M2], ut_* exports)
#   unitext_native_editor  (editor: harfbuzz-subset + native file dialog)  -- M2
#
# Upstream sources are FETCHED at PINNED versions (no vendored copies, no closed source).
# Third-party licenses recorded in ../Third-Party Notices.txt.
#
# Windows x64 build (VS generator, ninja not required):
#   cmake -S NativeSource~ -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
#
cmake_minimum_required(VERSION 3.24)
project(openglyph_native LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

# Work around a CMake 4.2 GNUInstallDirs bug (infinite recursion in
# _GNUInstallDirs_RUNSTATEDIR_get_default) triggered when Blend2D includes it: shadow the
# stock module with a lean shim (cmake/GNUInstallDirs.cmake) placed first on the module path.
list(PREPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/cmake")
set(CMAKE_INSTALL_RUNSTATEDIR "run" CACHE PATH "" FORCE)
include(GNUInstallDirs)

# ---- Pinned upstream versions -------------------------------------------------
# FreeType 2.13.3: SDF/BSDF modules, COLRv1 paint API, sbix bitmap_top fix, MM/variations.
set(OPENGLYPH_FREETYPE_TAG   "VER-2-13-3")
# HarfBuzz 12.x as required by the brief.
set(OPENGLYPH_HARFBUZZ_TAG   "12.2.0")
# zlib + libpng: required by FreeType for 'sbix'/'CBDT' PNG-compressed color-bitmap strikes
# (the C# side reads PIXEL_MODE_BGRA / LOAD_COLOR / sbix — see FT.cs, EmojiFont.cs).
set(OPENGLYPH_ZLIB_TAG       "v1.3.1")
set(OPENGLYPH_LIBPNG_TAG     "v1.6.44")
# Blend2D (M2): commit-versioned. Pinned to specific commits (verified via git ls-remote).
set(OPENGLYPH_BLEND2D_TAG    "58ca9460b4138af6e793184682e9324e0a8053b2")
set(OPENGLYPH_ASMJIT_TAG     "0d7f01052f2d3f6d6b9bb6714ef32bd7b80177eb")

include(FetchContent)

# ---- zlib (Zlib license) ------------------------------------------------------
FetchContent_Declare(zlib
  GIT_REPOSITORY https://github.com/madler/zlib.git
  GIT_TAG        ${OPENGLYPH_ZLIB_TAG}
  GIT_SHALLOW    TRUE)
set(ZLIB_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(zlib)
# Point find_package(ZLIB) (used by libpng and FreeType) at the fetched build. Under the VS
# multi-config generator the static lib lands at <bindir>/<Config>/zlibstatic.lib. Seed the
# FindZLIB cache vars + ZLIB_FOUND so no configure-time file existence check is needed, and
# provide the ZLIB::ZLIB imported-style target for consumers that prefer it.
set(ZLIB_INCLUDE_DIR  "${zlib_SOURCE_DIR};${zlib_BINARY_DIR}" CACHE PATH "" FORCE)
set(ZLIB_INCLUDE_DIRS "${zlib_SOURCE_DIR};${zlib_BINARY_DIR}" CACHE PATH "" FORCE)
set(ZLIB_LIBRARY  "${zlib_BINARY_DIR}/$<CONFIG>/zlibstatic.lib" CACHE STRING "" FORCE)
set(ZLIB_LIBRARIES zlibstatic CACHE STRING "" FORCE)
set(ZLIB_FOUND TRUE CACHE BOOL "" FORCE)
if(TARGET zlibstatic AND NOT TARGET ZLIB::ZLIB)
  add_library(ZLIB::ZLIB ALIAS zlibstatic)
endif()

# ---- libpng (libpng/PNG license) ---------------------------------------------
FetchContent_Declare(libpng
  GIT_REPOSITORY https://github.com/pnggroup/libpng.git
  GIT_TAG        ${OPENGLYPH_LIBPNG_TAG}
  GIT_SHALLOW    TRUE)
set(PNG_SHARED   OFF CACHE BOOL "" FORCE)
set(PNG_STATIC   ON  CACHE BOOL "" FORCE)
set(PNG_TESTS    OFF CACHE BOOL "" FORCE)
set(PNG_TOOLS    OFF CACHE BOOL "" FORCE)
set(PNG_FRAMEWORK OFF CACHE BOOL "" FORCE)
set(SKIP_INSTALL_ALL ON CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(libpng)
# Seed FindPNG (used by FreeType) at the fetched static libpng, same technique as ZLIB.
if(TARGET png_static)
  set(PNG_PNG_INCLUDE_DIR "${libpng_SOURCE_DIR};${libpng_BINARY_DIR}" CACHE PATH "" FORCE)
  set(PNG_INCLUDE_DIR     "${libpng_SOURCE_DIR};${libpng_BINARY_DIR}" CACHE PATH "" FORCE)
  set(PNG_INCLUDE_DIRS    "${libpng_SOURCE_DIR};${libpng_BINARY_DIR}" CACHE PATH "" FORCE)
  set(PNG_LIBRARY   "${libpng_BINARY_DIR}/$<CONFIG>/libpng16_static.lib" CACHE STRING "" FORCE)
  set(PNG_LIBRARIES png_static CACHE STRING "" FORCE)
  set(PNG_FOUND TRUE CACHE BOOL "" FORCE)
  if(NOT TARGET PNG::PNG)
    add_library(PNG::PNG ALIAS png_static)
  endif()
endif()

# ---- FreeType (FTL / GPLv2 dual) ---------------------------------------------
FetchContent_Declare(freetype
  GIT_REPOSITORY https://github.com/freetype/freetype.git
  GIT_TAG        ${OPENGLYPH_FREETYPE_TAG}
  GIT_SHALLOW    TRUE)
# Enable zlib + PNG (color bitmaps); disable the rest for a lean self-contained DLL.
set(FT_DISABLE_ZLIB     OFF CACHE BOOL "" FORCE)
set(FT_REQUIRE_ZLIB     ON  CACHE BOOL "" FORCE)
set(FT_DISABLE_PNG      OFF CACHE BOOL "" FORCE)
set(FT_REQUIRE_PNG      ON  CACHE BOOL "" FORCE)
set(FT_DISABLE_BZIP2    ON  CACHE BOOL "" FORCE)
set(FT_DISABLE_HARFBUZZ ON  CACHE BOOL "" FORCE)  # avoid circular dep (HB links FT, not vice-versa)
set(FT_DISABLE_BROTLI   ON  CACHE BOOL "" FORCE)

# ---- HarfBuzz 12.x (MIT "Old") -----------------------------------------------
FetchContent_Declare(harfbuzz
  GIT_REPOSITORY https://github.com/harfbuzz/harfbuzz.git
  GIT_TAG        ${OPENGLYPH_HARFBUZZ_TAG}
  GIT_SHALLOW    TRUE)
set(HB_HAVE_FREETYPE   ON  CACHE BOOL "" FORCE)
set(HB_BUILD_SUBSET    ON  CACHE BOOL "" FORCE)
set(HB_BUILD_TESTS     OFF CACHE BOOL "" FORCE)
set(HB_BUILD_UTILS     OFF CACHE BOOL "" FORCE)

FetchContent_MakeAvailable(freetype harfbuzz)

# ---- Blend2D (M2, gated) ------------------------------------------------------
# Blend2D is implemented (src/ut_bl.cpp) and pinned, but its master CMakeLists triggers a
# recursion bug in the installed CMake 4.2.2 (Check*CompilerFlag on flags like
# '-Zc:arm64-aliased-neon-types-'; see PARITY_RESULTS_M2.md). Default OFF here so the runtime
# DLL builds; turn ON with a compatible CMake (<= 3.31): -DOPENGLYPH_ENABLE_BLEND2D=ON.
option(OPENGLYPH_ENABLE_BLEND2D "Fetch and link Blend2D (needs CMake <= 3.31)" OFF)
if(OPENGLYPH_ENABLE_BLEND2D)
  FetchContent_Declare(asmjit
    GIT_REPOSITORY https://github.com/asmjit/asmjit.git
    GIT_TAG        ${OPENGLYPH_ASMJIT_TAG})
  set(ASMJIT_STATIC ON CACHE BOOL "" FORCE)
  set(ASMJIT_NO_INSTALL ON CACHE BOOL "" FORCE)   # avoid GNUInstallDirs (CMake 4.2 recursion bug)
  FetchContent_Declare(blend2d
    GIT_REPOSITORY https://github.com/blend2d/blend2d.git
    GIT_TAG        ${OPENGLYPH_BLEND2D_TAG})
  set(BLEND2D_STATIC ON CACHE BOOL "" FORCE)
  set(BLEND2D_NO_INSTALL ON CACHE BOOL "" FORCE)  # avoid GNUInstallDirs (CMake 4.2 recursion bug)
  set(ASMJIT_DIR ${asmjit_SOURCE_DIR} CACHE PATH "" FORCE)
  FetchContent_MakeAvailable(asmjit blend2d)
endif()

# ---- Runtime library: unitext_native -----------------------------------------
# Milestone 1 source set. ut_bl* and ut_colr* are STUBS this milestone (return 0/failure)
# so the DLL links and exports the full ABI; real impls land in M2.
# Blend2D source: real impl when enabled, stub otherwise (keeps the ABI exported either way).
if(OPENGLYPH_ENABLE_BLEND2D)
  set(OPENGLYPH_BL_SRC src/ut_bl.cpp)
else()
  set(OPENGLYPH_BL_SRC src/ut_bl_stub.c)
endif()

add_library(unitext_native SHARED
  src/ut_ft.c
  src/ut_ft_variations.c
  src/ut_hb.c
  src/ut_colr.c
  ${OPENGLYPH_BL_SRC}
  src/ut_version.c
  src/ut_exports.def)
target_link_libraries(unitext_native PRIVATE freetype harfbuzz)
# FreeType references zlib + libpng symbols (sbix/CBDT PNG strikes); pull the statics in.
if(TARGET zlibstatic)
  target_link_libraries(unitext_native PRIVATE zlibstatic)
endif()
if(TARGET png_static)
  target_link_libraries(unitext_native PRIVATE png_static)
endif()
if(OPENGLYPH_ENABLE_BLEND2D)
  target_link_libraries(unitext_native PRIVATE blend2d::blend2d)
  target_compile_definitions(unitext_native PRIVATE OPENGLYPH_HAVE_BLEND2D=1)
endif()
target_include_directories(unitext_native PRIVATE include)
set_target_properties(unitext_native PROPERTIES PREFIX "" OUTPUT_NAME "unitext_native")

# ---- Editor library: unitext_native_editor (milestone 2) ---------------------
option(OPENGLYPH_BUILD_EDITOR_DLL "Build unitext_native_editor (milestone 2)" ON)
if(OPENGLYPH_BUILD_EDITOR_DLL)
  add_library(unitext_native_editor SHARED
    src/editor/subset.c
    src/editor/dialog_win32.c
    src/editor/ut_editor_exports.def)
  target_link_libraries(unitext_native_editor PRIVATE harfbuzz-subset harfbuzz)
  # harfbuzz + harfbuzz-subset are separate static libs that both embed the same CFF/number
  # translation units, so MSVC sees duplicate (byte-identical) symbols. Allow multiple
  # definitions; the editor DLL is functionally verified by the parity harness subset test.
  if(MSVC)
    target_link_options(unitext_native_editor PRIVATE /FORCE:MULTIPLE /IGNORE:4088)
  endif()
  target_include_directories(unitext_native_editor PRIVATE include)
  set_target_properties(unitext_native_editor PROPERTIES PREFIX "" OUTPUT_NAME "unitext_native_editor")
  if(WIN32)
    target_link_libraries(unitext_native_editor PRIVATE comdlg32)
  endif()
endif()
