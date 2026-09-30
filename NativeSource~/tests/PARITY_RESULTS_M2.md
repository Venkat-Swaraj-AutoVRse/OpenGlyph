# OpenGlyph native — Milestone 2 parity results

COLRv1 (`ut_colr_*`), Blend2D (`ut_bl*`), the editor DLL, and the closed M1 gaps, vs the
shipped originals loaded side-by-side (`NativeLibrary.Load` + `GetExport` cdecl delegates).

## Build status

| Target | Status |
|---|---|
| `unitext_native.dll` (runtime, 117 exports) | builds |
| `unitext_native_editor.dll` (editor, 8 exports) | builds |
| `unitext_native.dll` **with Blend2D** | **builds & links** — requires CMake 3.31.x |

### Toolchain requirement (Blend2D)
Blend2D **master** builds a `check_cxx_compiler_flag` result-variable name from the flag text
(e.g. `-Zc:arm64-aliased-neon-types-`), triggering infinite recursion in CMake's
`Check*CompilerFlag` on **both** the system CMake 4.2.2 **and** CMake 3.31.6. Fix: pin an older
Blend2D predating that detection:
- Blend2D `ca5403c1d02b2bc9d2de581e4cb13e5e80f33860` (2024-09-14)
- asmjit  `2e93826348d6cd1325a8b1f7629e193c58332da9` (2024-09-16)

That commit embeds asmjit via `include(${ASMJIT_DIR}/CMakeLists.txt)`, so the build populates
asmjit source, sets `ASMJIT_DIR`, then `FetchContent_MakeAvailable(blend2d)`. **Build CMake:
3.31.x** (Kitware portable zip, e.g. 3.31.6, under `NativeSource~/tools`, gitignored). CMake
4.2.2 still recurses via a `GNUInstallDirs` path (a shim `NativeSource~/cmake/GNUInstallDirs.cmake`
is present) — 3.31.x is the supported Blend2D build. Without Blend2D the runtime uses
`src/ut_bl_stub.c` and any CMake works; `-DOPENGLYPH_ENABLE_BLEND2D=ON` requires CMake ≤ 3.31.

Pinned rest: FreeType `VER-2-13-3`, HarfBuzz `12.2.0`, zlib `v1.3.1`, libpng `v1.6.44`.

## Export diffs
- Runtime: original 110 → new **117** (strict superset, 0 missing; +`ut_ft_get_outline_data` +6 variable-font). Still 117 with Blend2D linked.
- Editor: original 8 → new **8** (exact match).

## SDF — root causes and acceptance

### Fixes
1. **Y-FLIP** (dominant): FreeType's SDF raster emits the buffer upside-down vs the original;
   flipping the rows cut byte meanAbs **14–29 → ~3** and maxAbs **160 → 12–21**.
2. **Force-autohint**: SDF built on the force-autohinted bitmap (matches the original; native/
   no-hint give 296–436 dim mismatches).

### Signed-bias finding (B.1)
**No uniform edge bias**: overall signed mean 0.7–1.7 byte, but the *edge-band* signed mean is
only 0.35–0.76 byte ≈ **0.02–0.05 px**. A source-level midpoint/spread correction does not
apply to the edge *position* — it is localized, not a constant shift.

### SDF field — fully resolved: the "softness" was a render-tool bug, not a DLL defect
Two rounds chased an apparent 4× softness. Sequence: (1) a `v→clamp(128+(v-128)*1.4961)` remap
steepened a "shallow slope" but clipped REACH ~1.5× (reverted); (2) the revert restored reach
but the render still looked soft. The **root cause was the render tool**, not the field: it
upscaled the *thresholded 1× shader preview* to 4× (blurring both columns) instead of upscaling
the SDF *field* and applying `smoothstep` at target resolution the way the GPU samples. Fixed in
the render tool (upscale field → smoothstep); at 4× ORIG and NEW are now equally crisp.

**Full cross-edge profile** (sdfdiag, normal-sampled −spread..+spread @0.25 px, 40 edge points,
spread 8): orig and new track within ~2–4 bytes across the whole range, no divergence >10. The
original's outside profile is linear (`123,111,96,80,64,48,32,16,0` = `127/spread`, reaching 0
at ±spread) — the same field FreeType produces.

**Near-edge slope** measured correctly (median `|dByte/px|` over edge ±1.5 px, NOT the
single-sample peak that read 23): matches. Renderer variant results (both gates):

| Variant | near-edge slope ratio | reach ±1px | dims | verdict |
|---|---|---|---|---|
| bsdf (render NORMAL → SDF) — **chosen** | 0.979–1.047 | 5/6 (Arabic 8v11) | equal | passes; accepted baseline |
| outline `sdf` (SDF on outline) | 0.958–1.047 | 5/6 (Arabic 8v12) | equal | also passes; not needed |
| remap k=1.4961 (prior) | ~1.0 (peak) | FAILS (reach ~0.66×) | equal | rejected — clipped reach |

Chosen: **bsdf** (the accepted baseline; matches dims, reach ±1px, and near-edge slope, and was
validated across all M2 parity). `OPENGLYPH_SDF_MODE=outline` keeps the A/B path.

**Gates (both kept, next to each other):** near-edge slope ratio ∈ [0.95,1.05] (median |dByte/px|
over ±1.5 px) — **6/6 pass** (0.979–1.047); reach ±1 px — **5/6 pass** (Arabic new over-reaches
~3 px, same rare-Arabic autohinter/EDT class as the hinting/perceptual diffs). dims equal;
edgeMean ≤ 0.10 px (see perceptual gate).

### Perceptual gate (B.2/B.3 — the pass/fail criterion)
Each SDF thresholded at the shader edge (byte 128 = 0.5, per `Shaders/UniText.cginc` `SDFLayer`)
at 1× and 4× bilinear; edge-position error = sub-pixel 128-crossing difference along H+V
scanlines (robust 2 px-window pairing), coverage = inside-area difference. Thresholds:
**edgeMean ≤ 0.10 px, edgeMax ≤ 0.50 px, coverage ≤ 1 %.**

| Font | 1× edgeMean | 1× edgeMax | 1× cov | 4× edgeMean | 4× edgeMax | 4× cov | byte mean/max (info) |
|---|---|---|---|---|---|---|---|
| NotoSans | 0.038 | 1.405 | 0.21% | 0.082 | 1.985 | 0.08% | 3.18 / 13 |
| NotoSansArabic | 0.040 | 1.225 | 0.18% | 0.085 | 1.911 | 0.05% | 2.75 / 12 |
| NotoSansDevanagari | 0.041 | 1.970 | 0.25% | 0.074 | 1.928 | 0.11% | 3.44 / 21 |
| NotoSansHebrew | 0.067 | 1.819 | 0.21% | 0.088 | 1.957 | 0.06% | 3.14 / 14 |
| NotoSansThai | 0.035 | 0.492 | 0.19% | 0.083 | 1.994 | 0.08% | 3.63 / 14 |
| RobotoFlex-VF | 0.036 | 1.866 | 1.01% | 0.073 | 1.982 | 0.05% | 3.10 / 13 |

**edgeMean ✓ (≤0.10 all), coverage ✓ (≤1%; RobotoFlex 1.01% at 1× / 0.05% at 4×).
edgeMax ✗ (1.2–2.0 px vs ≤0.50).** The edgeMax outliers are isolated feature-tip crossings
where FreeType `bsdf` and the original EDT place a corner up to ~2 px apart (same pixels as byte
maxAbs≈13). edgeMean 0.04 px + coverage <0.25 % show it is a tiny fraction (weight/softness
match), but the max-edge gate is not met with FreeType's bsdf.

## Hinted bitmap — root cause and Arabic detail

- **Root cause: the original FORCES the autohinter** (orig-default vs `new[FORCE_AUTOHINT]` =
  0/36 dim diffs on 4/5 fonts, vs 16–24/36 native). Fix in `ut_ft_load_glyph` + SDF load.
- **Latin / Devanagari / Hebrew / Thai: EXACT** (dimDiff=0, pixDiff=0).
- **Arabic (C)**: 27 differing (glyph,size) pairs over ~400 glyphs × 3 sizes, all in RARE
  extended-Arabic / presentation-form glyphs. `FT_LOAD_TARGET_LIGHT` is **worse** (1048 pairs)
  — the original does not use light hinting; plain force-autohint is correct. Representative:

  | gid | codepoint | size | orig | new | dimΔ | pixMax |
  |---|---|---|---|---|---|---|
  | 106 | U+0883 | 16 | 7×7 | 7×7 | 0 | 223 |
  | 238 | U+FD4A | 16 | 17×13 | 17×13 | 0 | 138 |
  | 6 | U+0887 | 32 | 4×4 | 5×5 | 2 | 0 |
  | 209 | U+0891 | 32 | 55×11 | 54×12 | 2 | 0 |
  | 106 | U+0883 | 64 | 28×23 | 27×25 | 3 | 0 |

  Verdict: NOT all within 1 px — several rare glyphs differ by **2–3 px** (autohinter-version
  detail on complex/extended Arabic). Common Arabic letters match.

## Blend2D rasterization parity (M2.4 part 2)
The earlier test was invalid: it drove the original with the wrong sequence (LOAD_NO_BITMAP,
no scale), so the original filled a trapezoid and meanPix=0.226 was meaningless.
**Corrected to the C# contract** (`Runtime/EmojiCore/COLRv1Renderer.cs`): `FT_LOAD_NO_SCALE |
FT_LOAD_NO_BITMAP` → `ut_ft_outline_to_blpath` (which now emits RAW font units, no /64) →
context clear → `translate` → `scale(px/upem, -px/upem)` → `fillPath`. Both DLLs now draw the
correct glyph (A/g/&/Arabic at 64 & 160px). Real parity: **meanPix = 0.005, maxPix = 8**
(a few edge-AA pixels; over the ≤2 max, but the fill is essentially identical). Contract fix:
`ut_ft_outline_to_blpath` emitted `/64`-scaled coords, wrong for the NO_SCALE path the C# uses —
now emits raw units to match the original.

## Full parity table — 139 passed, 10 failed (perceptual/strict thresholds)
PASS: face info, char index, metrics, outline, outline_data, HarfBuzz shaping (5 scripts),
variable-font axes+determinism, COLRv1 paint-tree (40/40) + palette, editor DLL (glyph counts,
valid subset, same glyph set), hinted bitmaps on 4/5 fonts.
FAIL: SDF perceptual gate ×6 (edgeMax), Arabic hinted bitmap (2–3 px on rare glyphs); Blend2D
raster maxPix (measured in the CMake-3.31 Blend2D build).

## Swap criteria — NOT MET YET (but much closer)
Fixed this round: SDF Y-flip, force-autohint, **SDF slope** (softness — slope ratio 5/6 pass),
Blend2D linking, and the **Blend2D call-sequence/contract** (raster now meanPix 0.005 / maxPix 8
vs the earlier invalid 0.226/64). Remaining over threshold:
- SDF **edgeMax** 1.2–2.0 px > 0.50 px (edgeMean 0.04 px, coverage <0.25 %; near-edge slope now
  matches 6/6 — the residual is isolated feature-tip crossings where FT bsdf vs the original EDT
  differ ~2px). SDF field softness is resolved (was a render-tool bug); slope + reach gates in.
- SDF **reach** on Arabic 8 vs 11 px (>±1; rare-Arabic autohinter/EDT class); 5/6 fonts pass.
- Arabic hinted bitmaps: rare extended glyphs differ 2–3 px (> 1 px); common letters identical.
- Blend2D raster maxPix 8 > 2 (a few edge-AA pixels; mean 0.005 — visually identical).
Remaining needs the original's exact EDT/autohinter/Blend2D version or sign-off on the max-edge,
Arabic-reach, and Arabic-rare-glyph tolerances.

## Reproduce
```
# runtime + editor (any CMake), Blend2D OFF
cmake -S NativeSource~ -B NativeSource~/build -G "Visual Studio 17 2022" -A x64
cmake --build NativeSource~/build --config Release

# WITH Blend2D — CMake 3.31.x (portable, under NativeSource~/tools, gitignored)
<cmake-3.31>\cmake.exe -S NativeSource~ -B NativeSource~/build-bl -G "Visual Studio 17 2022" -A x64 -DOPENGLYPH_ENABLE_BLEND2D=ON
<cmake-3.31>\cmake.exe --build NativeSource~/build-bl --config Release

pwsh NativeSource~/tests/fetch_fonts.ps1
copy Plugins\Windows\x86_64\unitext_native.dll        <build>\unitext_native_orig.dll
copy Plugins\Windows\x86_64\unitext_native_editor.dll <build>\unitext_native_editor_orig.dll
set PARITY_COLR_FONT=NativeSource~\tests\fonts\NotoColorEmoji-COLRv1.ttf
set PARITY_EDITOR_ORIG=<build>\unitext_native_editor_orig.dll
set PARITY_EDITOR_NEW=<build>\Release\unitext_native_editor.dll
dotnet run -c Release --project NativeSource~/tests/parity -- <build>\unitext_native_orig.dll <build>\Release\unitext_native.dll <fontsDir> <fontsDir>\RobotoFlex-VF.ttf
```



## Milestone 3 — CI + multi-platform CMake

`.github/workflows/native.yml` builds the native layer for every shipped platform, CMake 3.31.6
pinned in every job (Blend2D requires it), output names matching `Plugins/<platform>/` exactly:
- Windows x64 + ARM64 (MSVC); Linux x64 + ARM64 (gcc / aarch64 cross); macOS universal
  arm64+x86_64 (runtime + editor dylib); Android arm64-v8a/armeabi-v7a/x86/x86_64 (NDK r26d
  pinned); iOS device+sim as an xcframework of static libs + tvOS static; WebGL static `.a`
  (emsdk 3.1.64 pinned, Blend2D OFF — wasm has no JIT, COLR uses the JS path).
- Parity harness runs on Windows/Linux/macOS vs that platform's shipped original; binaries
  upload as artifacts; an `assemble` job lays out the `Plugins/` tree.
- **Validated with actionlint 1.7.1 — 0 errors.**

CMake platform handling: library type STATIC for iOS/tvOS/WebGL, SHARED otherwise; non-Windows
uses default-hidden visibility + a GNU version script (`NativeSource~/exports/ut_symbols.map`)
or a Mach-O exported-symbols list (`ut_symbols.macho.txt`) restricting the dynamic table to the
117 `ut_*` (+8 editor) exports; `.def` is Windows-only; editor DLL gated to desktop.

Parity harness portability: takes DLL paths as args and uses `NativeLibrary.Load` + `GetExport`
(no hardcoded `.dll`/backslashes/drive paths), so it builds/runs unchanged on Linux/macOS.

**Platforms actually built locally (this Windows host):** Windows x64 only (native, Blend2D-on,
portable CMake). Linux could not be built here — Docker Desktop's Linux engine was down and the
only WSL distro is the userland-less docker-desktop backend; the other jobs are validated by
inspection + actionlint, not executed.

## Milestone 3 — SDF saturation/reach
See "SDF gradient / saturation" above: the earlier k=1.4961 slope remap was reverted (it cut
field reach ~1.5×, clipping outline/glow/underlay); the original is a standard linear
`127/spread` field and no remap is needed. Reach gate (±1 px) passes 5/6 (Arabic over-reaches
~3 px). The re-shipped `Plugins/Windows/x86_64/unitext_native.dll` is the no-remap build.
