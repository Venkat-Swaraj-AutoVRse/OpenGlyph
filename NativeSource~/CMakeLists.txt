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

# ---- Static CRT on MSVC (no VC++ redistributable dependency) ------------------
# The shipped Windows DLLs must NOT import VCRUNTIME140{,_1}.dll / MSVCP140.dll /
# api-ms-win-crt-*: a Unity player on a machine without the VC++ redistributable then fails to
# load unitext_native.dll with DllNotFoundException. Unity's own UnityPlayer.dll links the CRT
# statically and imports neither; we match it. CMAKE_MSVC_RUNTIME_LIBRARY selects /MT (static,
# MultiThreaded) in Release and /MTd (MultiThreadedDebug) in Debug instead of the default
# /MD(/MDd) dynamic CRT. It is honoured ONLY under policy CMP0091=NEW (abstraction via the
# MSVC_RUNTIME_LIBRARY target property rather than hardcoded /MD flags in CMAKE_*_FLAGS).
#
# This MUST be set BEFORE the first project()/enable_language and before any FetchContent
# dependency defines a target, so every subproject (FreeType, HarfBuzz, zlib, libpng,
# Blend2D/asmjit) inherits it and links the SAME static CRT — a mix of /MT and /MD across the
# statically-linked deps would both reintroduce the dynamic-CRT import and risk CRT-heap
# mismatches. A CACHE+FORCE write makes it win over any default a dependency's own CMakeLists
# might set (see the Blend2D guard further down). Non-MSVC platforms are untouched: the policy
# and variable have no effect off MSVC.
cmake_policy(SET CMP0091 NEW)
set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING
    "Static MSVC CRT (/MT, /MTd) so the DLLs carry no VC++ runtime import" FORCE)

project(openglyph_native LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

# iOS/tvOS ship STATIC libs (no linking/bundling happens here), but the Xcode generator still
# runs a code-signing phase and FAILS CI with "Signing for ... requires a development team"
# because the hosted runner has no provisioning profile. A static archive is never signed, so
# disable signing for every target in this build. Set BEFORE FetchContent defines any target
# so the fetched dependency targets (freetype, harfbuzz, png, zlib, blend2d) inherit it too.
# No effect on Windows/Linux/macOS-dylib/Android (non-Xcode or non-embedded) builds.
if(IOS OR CMAKE_SYSTEM_NAME STREQUAL "tvOS")
  set(CMAKE_XCODE_ATTRIBUTE_CODE_SIGNING_ALLOWED  "NO" CACHE STRING "" FORCE)
  set(CMAKE_XCODE_ATTRIBUTE_CODE_SIGNING_REQUIRED "NO" CACHE STRING "" FORCE)
  set(CMAKE_XCODE_ATTRIBUTE_CODE_SIGN_IDENTITY    ""   CACHE STRING "" FORCE)
  set(CMAKE_XCODE_ATTRIBUTE_DEVELOPMENT_TEAM      ""   CACHE STRING "" FORCE)
  set(CMAKE_XCODE_ATTRIBUTE_ENABLE_BITCODE        "NO" CACHE STRING "" FORCE)
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
# Blend2D (M2): commit-versioned. Pinned to a Sept-2024 commit whose CMake flag-detection
# builds cleanly (blend2d master's newer blend2d_detect_cflags recurses in
# Check*CompilerFlag on flags like '-Zc:arm64-aliased-neon-types-' on CMake 3.31 AND 4.2).
set(OPENGLYPH_BLEND2D_TAG    "ca5403c1d02b2bc9d2de581e4cb13e5e80f33860")
set(OPENGLYPH_ASMJIT_TAG     "2e93826348d6cd1325a8b1f7629e193c58332da9")

include(FetchContent)

# ---- zlib (Zlib license) ------------------------------------------------------
FetchContent_Declare(zlib
  GIT_REPOSITORY https://github.com/madler/zlib.git
  GIT_TAG        ${OPENGLYPH_ZLIB_TAG}
  GIT_SHALLOW    TRUE)
set(ZLIB_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(zlib)
# zlib's CMakeLists unconditionally defines BOTH a shared `zlib` and a static `zlibstatic`
# target, and on UNIX-like platforms (which includes Emscripten) sets OUTPUT_NAME=z on both.
# On a single-config generator where dynamic linking is unavailable (Emscripten/wasm), the
# `zlib` SHARED target is silently downgraded to STATIC and then collides with `zlibstatic`
# on the same output file `libz.a` -> "ninja: multiple rules generate _deps/zlib-build/libz.a".
# We only ever link the static `zlibstatic`, so give the shared target a distinct output name
# and drop it from the default build; this removes the collision without patching the source.
if(TARGET zlib)
  set_target_properties(zlib PROPERTIES OUTPUT_NAME zlibdyn EXCLUDE_FROM_ALL ON)
endif()
# Point find_package(ZLIB) (used by libpng and FreeType) at the fetched build USING THE TARGET,
# not a hardcoded, config- and platform-specific library file path. The previous approach wrote
# a literal "<bindir>/$<CONFIG>/zlibstatic.lib" into ZLIB_LIBRARY: the generator expression is
# not evaluated by find_package consumers (seen verbatim in Linux logs) and the ".lib" / name is
# MSVC-only, so non-Windows builds leaked a Windows path. Seed FindZLIB from the imported-style
# ZLIB::ZLIB target instead, which carries the correct per-platform artifact and include dirs.
set(ZLIB_INCLUDE_DIR  "${zlib_SOURCE_DIR};${zlib_BINARY_DIR}" CACHE PATH "" FORCE)
set(ZLIB_INCLUDE_DIRS "${zlib_SOURCE_DIR};${zlib_BINARY_DIR}" CACHE PATH "" FORCE)
if(TARGET zlibstatic AND NOT TARGET ZLIB::ZLIB)
  add_library(ZLIB::ZLIB ALIAS zlibstatic)
endif()
set(ZLIB_LIBRARY  ZLIB::ZLIB CACHE STRING "" FORCE)
set(ZLIB_LIBRARIES ZLIB::ZLIB CACHE STRING "" FORCE)
set(ZLIB_FOUND TRUE CACHE BOOL "" FORCE)

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
# Disable libpng's CPU-specific optimizations. On the macOS universal (arm64;x86_64) and
# iOS/Android-arm builds libpng's arm/*_neon_intrinsics.c fail to compile ("NEON intrinsics
# not available with the soft-float ABI") because the NEON path is selected per-file but the
# per-file flags don't carry a NEON-capable float ABI through CMake's universal/Xcode driver.
# FreeType only uses libpng's baseline (non-SIMD) decode for color-bitmap strikes, so the
# hardware paths are dead weight here; turning them off removes the broken translation units
# on every arch without changing decode output.
set(PNG_HARDWARE_OPTIMIZATIONS OFF CACHE BOOL "" FORCE)
set(PNG_ARM_NEON "off" CACHE STRING "" FORCE)
set(PNG_INTEL_SSE "off" CACHE STRING "" FORCE)
set(PNG_POWERPC_VSX "off" CACHE STRING "" FORCE)
set(PNG_MIPS_MSA "off" CACHE STRING "" FORCE)
set(SKIP_INSTALL_ALL ON CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(libpng)
# Seed FindPNG (used by FreeType) at the fetched static libpng, same technique as ZLIB:
# bind to the PNG::PNG target rather than a hardcoded "<bindir>/$<CONFIG>/libpng16_static.lib"
# path. That literal name is MSVC-only (ELF/Mach-O produce libpng16.a, no $<CONFIG> dir on
# single-config generators) and previously leaked a Windows ".lib" path into Linux/macOS/
# Android/WebGL builds. The target carries the correct artifact name per platform.
if(TARGET png_static)
  set(PNG_PNG_INCLUDE_DIR "${libpng_SOURCE_DIR};${libpng_BINARY_DIR}" CACHE PATH "" FORCE)
  set(PNG_INCLUDE_DIR     "${libpng_SOURCE_DIR};${libpng_BINARY_DIR}" CACHE PATH "" FORCE)
  set(PNG_INCLUDE_DIRS    "${libpng_SOURCE_DIR};${libpng_BINARY_DIR}" CACHE PATH "" FORCE)
  if(NOT TARGET PNG::PNG)
    add_library(PNG::PNG ALIAS png_static)
  endif()
  set(PNG_LIBRARY   PNG::PNG CACHE STRING "" FORCE)
  set(PNG_LIBRARIES PNG::PNG CACHE STRING "" FORCE)
  set(PNG_FOUND TRUE CACHE BOOL "" FORCE)
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

# ---- Build-order wiring for FreeType's zlib/png dependencies -------------------
# FreeType enables zlib + libpng support and #includes png.h, which in turn includes the
# GENERATED header pnglibconf.h (emitted into libpng's binary dir by libpng's own custom
# targets). find_package(PNG) hands FreeType the include dir but creates NO target-level
# ordering, so on generators that don't infer it from a linked target at compile time
# (Visual Studio, Xcode) FreeType's sources can compile before pnglibconf.h exists:
#   png.h(329): fatal error C1083: Cannot open include file: 'pnglibconf.h'
# Make the ordering explicit: FreeType must wait for the png/zlib static libs (which already
# depend on their generated headers) to be built first. This is pure ordering; it does not
# change what FreeType links or how it is compiled.
if(TARGET freetype)
  if(TARGET png_static)
    add_dependencies(freetype png_static)
  endif()
  if(TARGET zlibstatic)
    add_dependencies(freetype zlibstatic)
  endif()
  # Belt-and-suspenders: if libpng exposed a dedicated header-generation target, order on it
  # too so the generated pnglibconf.h is guaranteed present before any FreeType translation
  # unit is compiled, independent of the static-lib target's own dependency edges.
  foreach(_png_gentarget png_genfiles pnglibconf_h genfiles)
    if(TARGET ${_png_gentarget})
      add_dependencies(freetype ${_png_gentarget})
    endif()
  endforeach()
endif()

# ---- Blend2D (M2, gated) ------------------------------------------------------
# Blend2D is implemented (src/ut_bl.cpp) and pinned, but its master CMakeLists triggers a
# recursion bug in the installed CMake 4.2.2 (Check*CompilerFlag on flags like
# '-Zc:arm64-aliased-neon-types-'; see PARITY_RESULTS_M2.md). Default OFF here so the runtime
# DLL builds; turn ON with a compatible CMake (<= 3.31): -DOPENGLYPH_ENABLE_BLEND2D=ON.
option(OPENGLYPH_ENABLE_BLEND2D "Fetch and link Blend2D (needs CMake <= 3.31)" OFF)
if(OPENGLYPH_ENABLE_BLEND2D)
  # This Blend2D commit embeds asmjit by include()-ing ASMJIT_DIR/CMakeLists.txt itself,
  # so fetch asmjit SOURCE only (no separate target) and point ASMJIT_DIR at it.
  FetchContent_Declare(asmjit
    GIT_REPOSITORY https://github.com/asmjit/asmjit.git
    GIT_TAG        ${OPENGLYPH_ASMJIT_TAG})
  FetchContent_MakeAvailable(freetype)  # no-op if already available; keeps order explicit
  FetchContent_GetProperties(asmjit)
  if(NOT asmjit_POPULATED)
    FetchContent_Populate(asmjit)
  endif()
  set(ASMJIT_STATIC ON CACHE BOOL "" FORCE)
  set(ASMJIT_EMBED  ON CACHE BOOL "" FORCE)
  set(ASMJIT_DIR "${asmjit_SOURCE_DIR}" CACHE PATH "" FORCE)
  # Static-CRT guard for the embedded asmjit + Blend2D. CMAKE_MSVC_RUNTIME_LIBRARY is already
  # set FORCE at the top of this file and inherited here, but assert it again right before these
  # deps are configured, and keep CMP0091 at NEW for them, so neither can quietly re-select the
  # dynamic CRT. asmjit historically carried an ASMJIT_NO_CUSTOM_FLAGS escape and a static-runtime
  # toggle; pin both to our choice. Blend2D exposes BLEND2D_STATIC_RUNTIME — turn it ON so, even
  # on a Blend2D version that reads its own switch instead of CMAKE_MSVC_RUNTIME_LIBRARY, the
  # static CRT is still selected. All MSVC-only; no effect on other toolchains.
  set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING "" FORCE)
  set(ASMJIT_NO_CUSTOM_FLAGS   TRUE CACHE BOOL "" FORCE)
  set(BLEND2D_STATIC_RUNTIME   ON   CACHE BOOL "" FORCE)
  FetchContent_Declare(blend2d
    GIT_REPOSITORY https://github.com/blend2d/blend2d.git
    GIT_TAG        ${OPENGLYPH_BLEND2D_TAG})
  set(BLEND2D_STATIC ON CACHE BOOL "" FORCE)
  set(BLEND2D_NO_INSTALL ON CACHE BOOL "" FORCE)
  FetchContent_MakeAvailable(blend2d)
  # On a macOS universal (arm64;x86_64) pass, Blend2D compiles its AArch64 JIT path
  # (pipecompiler_a64.cpp etc., which #include <asmjit/a64.h>) but its own
  # target_include_directories(blend2d ... ${ASMJIT_INCLUDE_DIRS}) does not reliably cover
  # every core TU for the secondary arch, so context.cpp fails with "'asmjit/a64.h' file not
  # found". The embedded asmjit sources live under <asmjit>/src; add that dir to the blend2d
  # target's include path unconditionally so the asmjit umbrella headers always resolve.
  if(TARGET blend2d AND EXISTS "${asmjit_SOURCE_DIR}/src")
    target_include_directories(blend2d BEFORE PRIVATE "${asmjit_SOURCE_DIR}/src")
  endif()
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

# ---- Platform / packaging model ----------------------------------------------
# iOS, tvOS and WebGL(emscripten) ship STATIC libs (linked into the app / __Internal);
# Windows, Linux, macOS and Android ship SHARED libs. Detect and pick.
if(IOS OR CMAKE_SYSTEM_NAME STREQUAL "tvOS" OR EMSCRIPTEN)
  set(OPENGLYPH_LIBTYPE STATIC)
else()
  set(OPENGLYPH_LIBTYPE SHARED)
endif()

# Default-hidden visibility on non-Windows; the ut_* symbols carry
# __attribute__((visibility("default"))) via UT_API in ut_native.h. A GNU version script
# (Linux/Android) or a macho exported-symbols list (macOS/iOS/tvOS) additionally restricts
# the dynamic symbol table to exactly the ut_* / editor exports.
if(NOT MSVC)
  set(CMAKE_C_VISIBILITY_PRESET hidden)
  set(CMAKE_CXX_VISIBILITY_PRESET hidden)
  set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
endif()

# Windows uses the .def; other platforms must drop it from the source list.
set(OPENGLYPH_RT_EXPORTS "")
set(OPENGLYPH_ED_EXPORTS "")
if(WIN32)
  set(OPENGLYPH_RT_EXPORTS src/ut_exports.def)
  set(OPENGLYPH_ED_EXPORTS src/editor/ut_editor_exports.def)
endif()

add_library(unitext_native ${OPENGLYPH_LIBTYPE}
  src/ut_ft.c
  src/ut_ft_variations.c
  src/ut_hb.c
  src/ut_colr.c
  ${OPENGLYPH_BL_SRC}
  src/ut_version.c
  ${OPENGLYPH_RT_EXPORTS})
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
# Output name to match Plugins/<platform>/: Windows 'unitext_native.dll'; ELF/Mach-O get the
# 'lib' prefix (libunitext_native.so/.dylib/.a) which CMake adds by default on those platforms.
if(WIN32)
  set_target_properties(unitext_native PROPERTIES PREFIX "" OUTPUT_NAME "unitext_native")
else()
  set_target_properties(unitext_native PROPERTIES OUTPUT_NAME "unitext_native")
endif()
# Restrict exported symbols on shared ELF/Mach-O builds to the ut_* set.
if(OPENGLYPH_LIBTYPE STREQUAL "SHARED" AND NOT WIN32)
  if(APPLE)
    target_link_options(unitext_native PRIVATE
      "-Wl,-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/exports/ut_symbols.macho.txt")
  elseif(NOT EMSCRIPTEN)
    target_link_options(unitext_native PRIVATE
      "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports/ut_symbols.map")
  endif()
endif()

# ---- Editor library: unitext_native_editor (desktop only: Win/Linux/macOS) --------
# Not shipped for Android/iOS/tvOS/WebGL (no editor tooling there). dialog_win32.c has a
# non-Windows stub for the two dialog exports so subset+shape still build everywhere.
if(WIN32 OR (UNIX AND NOT ANDROID AND NOT IOS AND NOT EMSCRIPTEN AND NOT CMAKE_SYSTEM_NAME STREQUAL "tvOS"))
  set(OPENGLYPH_BUILD_EDITOR_DLL_DEFAULT ON)
else()
  set(OPENGLYPH_BUILD_EDITOR_DLL_DEFAULT OFF)
endif()
option(OPENGLYPH_BUILD_EDITOR_DLL "Build unitext_native_editor (desktop)" ${OPENGLYPH_BUILD_EDITOR_DLL_DEFAULT})
if(OPENGLYPH_BUILD_EDITOR_DLL)
  add_library(unitext_native_editor SHARED
    src/editor/subset.c
    src/editor/dialog_win32.c
    ${OPENGLYPH_ED_EXPORTS})
  target_link_libraries(unitext_native_editor PRIVATE harfbuzz-subset harfbuzz)
  # harfbuzz + harfbuzz-subset are separate static libs that both embed the same CFF/number
  # translation units, so the linker sees duplicate (byte-identical) symbols. Allow multiple
  # definitions on every linker; the editor DLL is functionally verified by the parity harness
  # subset test. MSVC: /FORCE:MULTIPLE. GNU ld (Linux/Android): --allow-multiple-definition.
  # Apple ld64 treats duplicate archive symbols as a warning, not an error, so it needs no flag
  # (and --allow-multiple-definition is not accepted there).
  if(MSVC)
    target_link_options(unitext_native_editor PRIVATE /FORCE:MULTIPLE /IGNORE:4088)
  elseif(NOT APPLE)
    target_link_options(unitext_native_editor PRIVATE "-Wl,--allow-multiple-definition")
  endif()
  target_include_directories(unitext_native_editor PRIVATE include)
  if(WIN32)
    set_target_properties(unitext_native_editor PROPERTIES PREFIX "" OUTPUT_NAME "unitext_native_editor")
    target_link_libraries(unitext_native_editor PRIVATE comdlg32)
  else()
    set_target_properties(unitext_native_editor PROPERTIES OUTPUT_NAME "unitext_native_editor")
    if(APPLE)
      target_link_options(unitext_native_editor PRIVATE
        "-Wl,-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/exports/ut_editor_symbols.macho.txt")
    else()
      target_link_options(unitext_native_editor PRIVATE
        "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports/ut_editor_symbols.map")
    endif()
  endif()
endif()
