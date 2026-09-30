# OpenGlyph native — Milestone 2 parity results

COLRv1 (`ut_colr_*`), Blend2D (`ut_bl*`), the editor DLL, and the closed M1 gaps, vs the
shipped originals loaded side-by-side (`NativeLibrary.Load` + `GetExport` cdecl delegates).

## Build status

| Target | Status |
|---|---|
| `unitext_native.dll` (runtime, 117 exports) | builds (CMake VS2022 x64 Release) |
| `unitext_native_editor.dll` (editor, 8 exports) | builds |
| Blend2D (`ut_bl*`) real impl `src/ut_bl.cpp` | implemented; **not linkable on this host** (CMake 4.2.2 recursion in Blend2D-master flag detection). Build with CMake ≤ 3.31 + `-DOPENGLYPH_ENABLE_BLEND2D=ON`. |

Pinned: FreeType `VER-2-13-3`, HarfBuzz `12.2.0`, zlib `v1.3.1`, libpng `v1.6.44`,
Blend2D `58ca9460…`, asmjit `0d7f0105…`.

## Export diffs
- Runtime: original 110 → new **117** (strict superset, 0 missing; +`ut_ft_get_outline_data` +6 variable-font).
- Editor: original 8 → new **8** (exact match, 0 diff).

## Thresholds (per request)
- **SDF**: FAIL if `meanAbsDiff > 2` OR `maxAbsDiff > 16` OR any dim mismatch (20 glyphs/font).
- **Hinted bitmap (default flags)**: FAIL on ANY dimension OR pixel difference (12 glyphs × 3 sizes).
- **Blend2D raster**: FAIL if `maxPix > 2` (same outline filled in both DLLs).

## Root causes found

### 1. SDF (FIXED the dominant error; residual over threshold)
- **Y-FLIP** was the dominant defect: FreeType's SDF raster emits the buffer **upside-down**
  vs the original (whose EDT keeps the NORMAL bitmap top-down). Proven by ASCII dump —
  'A' rendered apex-down in the new DLL. Overlapping rows then read inside-vs-outside,
  giving `maxAbsDiff = 160`. Flipping the SDF rows in `ut_ft_render_sdf_glyph` cut
  **meanAbsDiff 14–29 → ~3 and maxAbsDiff 160 → ~13–21**.
- **Force-autohint**: SDF is built on a force-autohinted bitmap (matching the original;
  native/no-hint give 296–436 dim mismatches).
- **Residual (still over target)**: with dims matched and range identical `[0..~165]`, the
  remaining per-pixel difference is a ~0.35px sub-pixel edge offset between FreeType's `bsdf`
  bitmap-EDT and the original's own EDT, plus autohinter grid-fit noise. Both FreeType SDF
  routes (`sdf` outline module and `bsdf` bitmap module) were measured; `bsdf` is closer.
  A hand-rolled brute EDT was tried and was worse (mean 19–31). Reaching mean ≤ 2 requires
  the original's exact (undisclosed) EDT.

### 2. Hinted bitmap (FIXED on 4/5 fonts)
- **Root cause: the original FORCES the autohinter.** Proven by a cross-flag matrix
  (orig-default vs new under each flag): `new[FORCE_AUTOHINT]` = **0/36** dim diffs on
  Latin/Devanagari/Hebrew/Thai (vs 16–24/36 with native TT hinting). NO_HINTING already
  matched 36/36, proving the outlines are identical and only hinting config differed. The
  TT bytecode interpreter version (35 vs 40) made no difference — it is the autohinter.
- **Fix**: `ut_ft_load_glyph` (and the SDF load) OR in `FT_LOAD_FORCE_AUTOHINT`.
- **Result**: hinted-bitmap parity is now **exact (dimDiff=0, pixDiff=0)** on 4/5 fonts.
  Arabic still has **4/36** glyphs differing in dimensions — a FreeType-version autohinter
  detail on complex Arabic shaping/glyphs (force-autohint matched 32/36, not 36/36).

## Full parity table — 111 passed, 6 failed

| Check | Latin | Arabic | Hebrew | Devanagari | Thai | VF | Emoji |
|---|---|---|---|---|---|---|---|
| face info / char index / metrics / outline / outline_data | PASS | PASS | PASS | PASS | PASS | PASS | — |
| HarfBuzz shaping (ids/clusters/advances/offsets) | PASS | PASS | PASS | PASS | PASS | — | — |
| variable-font axes + set-coords determinism | — | — | — | — | — | PASS | — |
| hinted bitmap exact (dims+pixels) | PASS | **FAIL 4/36 dims** | PASS | PASS | PASS | — | — |
| SDF mean≤2 / max≤16 | **FAIL 3.18/13** | **FAIL 2.76/12** | **FAIL 3.14/14** | **FAIL 3.44/21** | **FAIL 3.63/14** | — | — |
| COLRv1 paint-tree walk (40 glyphs) + palette | — | — | — | — | — | — | **PASS 40/40** |
| editor get_glyph_count + subset valid + same glyph set | PASS | PASS | PASS | PASS | PASS | PASS | — |
| Blend2D raster (maxPix≤2) | SKIPPED (Blend2D not linkable on CMake 4.2.2) | | | | | | |

SDF numbers (meanAbsDiff / maxAbsDiff, 20 glyphs/font, default force-autohinted load):
NotoSans 3.177/13 · Arabic 2.755/12 (2 dim mismatches) · Devanagari 3.443/21 · Hebrew 3.140/14 · Thai 3.628/14.

## Do NOT ship yet
The new `unitext_native.dll` must not replace the shipped binary: SDF is over threshold on all
fonts and Arabic hinted bitmaps differ. The Y-flip and force-autohint fixes closed the gross
errors (SDF 160→~15, bitmaps 24/36→0/36) but the SDF mean (≈3) and the 4 Arabic bitmap glyphs
need the original's exact EDT/autohinter to fully match, or explicit sign-off on relaxed
thresholds.

## Reproduce
```
cmake -S NativeSource~ -B NativeSource~/build -G "Visual Studio 17 2022" -A x64
cmake --build NativeSource~/build --config Release
pwsh NativeSource~/tests/fetch_fonts.ps1
copy Plugins\Windows\x86_64\unitext_native.dll        NativeSource~\build\unitext_native_orig.dll
copy Plugins\Windows\x86_64\unitext_native_editor.dll NativeSource~\build\unitext_native_editor_orig.dll
set PARITY_COLR_FONT=NativeSource~\tests\fonts\NotoColorEmoji-COLRv1.ttf
set PARITY_EDITOR_ORIG=NativeSource~\build\unitext_native_editor_orig.dll
set PARITY_EDITOR_NEW=NativeSource~\build\Release\unitext_native_editor.dll
dotnet run -c Release --project NativeSource~/tests/parity -- ^
   NativeSource~\build\unitext_native_orig.dll ^
   NativeSource~\build\Release\unitext_native.dll ^
   <fontsDir> <fontsDir>\RobotoFlex-VF.ttf
```
Blend2D raster parity additionally requires a CMake ≤ 3.31 build with `-DOPENGLYPH_ENABLE_BLEND2D=ON`.
