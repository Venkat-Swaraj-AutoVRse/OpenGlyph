# SDF text edge quality — wavy/scalloped edges under magnification

Investigation branch: `openglyph/sdf-quality` (from `openglyph/main` @ bad740e).

## Symptom
Large white "sac" (glyphs ~400 px tall, ~4.4× the 90 px atlas sampling): OpenGlyph SDF
edges wobble/scallop and read noticeably **soft/blurry**, while TextMeshPro at the same size
is crisp. Default settings, SDF render mode.

## What was measured (not guessed)

### 1. Raw distance field — fork vs upstream UniText 1.0 are near-identical
Dumped the SDF tile for A/o/g/s/a/c directly from both native DLLs through the **same**
`ut_ft_render_sdf_glyph` export (upstream 1.0's `unitext_native.dll` exports it too):
- At spread 23 (the default) and spread 8, byte ramps match within ~5 bytes; tile dims identical.
- Upstream 1.0 is **also** `bsdf` (render NORMAL → SDF, EDT on an AA raster). The fork reproduced
  it faithfully. The fork's extra near-edge remap (`a=8,G=1.55,B=24` in `ut_ft_render_sdf_glyph`)
  changes the near-edge slope by at most ~1.4× on a clean convex edge and is **not** the cause.
- **In-engine A/B** (`OPENGLYPH_SDF_MODE=outline` vs default bsdf+remap) renders are visually
  identical — both wavy+soft. ⇒ **Hypotheses 1 (raster-vs-outline) and the remap are ruled out.**

### 2. Atlas texture is correct
SDF atlas = `TextureFormat.Alpha8` (linear, single channel, no sRGB), `mipChain=false`,
Bilinear filter. ⇒ **Hypothesis 3 (format/filter/mipmap/sRGB) ruled out.**

### 3. ppem is correct
`ChoosePixelPerfectPpem` only applies to pixel/bitmap coverage fonts; SDF renders at
`faceInfo.pointSize` (90). ⇒ **Hypothesis 4 ruled out.**

### 4. Shader AA is derivative-based (correct form) — but tuned for spread ratio 0.1
`UniText_SDF-SSD`: `pixelSize=|ddx(uv.y)|+|ddy(uv.y)|; baseScale=1/pixelSize*(_Sharpness+1);
scale=baseScale*xScaleVal*gradientScale; d=atlas.a*scale; saturate(d-bias)`. Derivative AA,
`half` only on the final scaled distance. The whole effect-normalization is built around
`REFERENCE_SPREAD_RATIO = 0.1` (Padding 9 / PointSize 90), and `gradientScale = pointSize*padding/72`.

### 5. ROOT CAUSE — default `spreadStrength = 0.25` + 8-bit atlas
`UniTextFont.spreadStrength` defaults to **0.25** ⇒ padding = round(90*0.25) = **23** px, spread
ratio **0.256** — 2.5× the shader's reference ratio (0.1) and 2.5× TMP's padding (9, ratio 0.1).

Direct measurement on the actual 400 px renders (anti-aliased band width per edge crossing,
`out/render/`, Noto Sans, same scene/size):

| Render | ink px | AA band px | **AA width / edge (px)** |
|---|---|---|---|
| OpenGlyph default (spread 0.25) | 38,275 | 5,370 | **3.52** |
| OpenGlyph spread 0.10 | 44,491 | 5,142 | 3.09 |
| TMP (spread 0.10) | 43,112 | 3,342 | **2.03** |

- OpenGlyph's default edge AA band is **~73 % wider than TMP** → the "soft/blurry" look, and it
  eats ~12 % of glyph coverage (ink 38.3k vs 43.1k) so strokes look thin+feathered.
- A wide spread means each atlas texel spans a large distance step (byte/px slope ~5.5 at
  spread 23 vs ~14 at spread 9). Under 4–5× magnification the coarse **8-bit** steps of that
  shallow ramp span several screen pixels, and bilinear reconstruction of the stepped ramp makes
  the 0.5 iso-contour wobble with ~1-texel period = the scalloping.
- **Dropping spreadStrength to 0.10 (= TMP's ratio) makes OpenGlyph crisp and smooth** (see
  `openglyph_sac_spread01.png` vs `openglyph_sac.png`). Residual gap to TMP (3.09 vs 2.03 px)
  is a secondary shader-scale effect: `pointSize*padding/72` does not yield exactly 1 px AA for
  the FreeType byte-encoding, so a small `gradientScale`/`_Sharpness` calibration remains.

## Regression vs inherited
**Inherited, not a fork regression.** `spreadStrength = 0.25f`, the `pointSize*padding/72`
gradientScale, and 8-bit Alpha8 all come verbatim from upstream UniText 1.0 (`origin/1.0.0`
`UniTextFont.cs:78`, commit `c563a5e "UniText 1.0.0"`). DLL-vs-DLL byte parity confirms the
fork reproduced the field. TMP simply ships a tighter default spread, so it looks better at
magnification.

## Recommended fix (ordered)
1. **Lower the default `spreadStrength` to ~0.10–0.125** (match TMP's reference ratio). Biggest
   visual win, no native/CI golden churn. Note: effect widths (outline/glow/underlay) are
   expressed relative to spread, so re-check those default ranges.
2. **Calibrate the shader AA** so matched-spread OpenGlyph equals TMP's ~2 px band (revisit the
   `/72` constant / `_Sharpness` default for the FreeType byte-encoding).
3. Optional, if a wide spread must stay the default for thick effects: store SDF in a
   **≥12-bit** single-channel format (e.g. R16) to remove the 8-bit stepping at large spread.

## Evidence files (on D:, not committed — heavy)
- Renders: `D:\OpenGlyphWork\scratch\sdf-quality\out\render\{openglyph_sac,tmp_sac,openglyph_sac_spread01}.png`
- SDF tiles / ramps: `D:\OpenGlyphWork\scratch\sdf-quality\out\{tiles_orig_vs_fork,eq_*}`
- Diagnostic tools (reusable): `sdfdiag`, `edgequal`, `contour`, `softness.py` under
  `D:\OpenGlyphWork\scratch\sdf-quality\out\`
- Key before/after PNGs copied into `Documentation/Design/evidence/` for the record.

## Outcome — SHIPPED (branch `openglyph/sdf-quality`)

The decision (default 0.10, regenerate existing fonts) is implemented. Commits `6aaf5a2`
(code + assets + tool) and `5dcd7b3` (.meta), on top of `openglyph/main` @ `e8efa75`
(PR #10 unified renderer + PR #11 fix), pushed to remote `openglyph`.

### 1. Default lowered to 0.10
- `UniTextFont.spreadStrength` field default `0.25f → 0.1f`; `UniTextFont.CreateFontAsset`
  default param `0.25f → 0.1f` (both editor Create-Font menus and `UniTextFontToolsWindow`
  inherit it). `[Range(0.1f,1f)]` unchanged (0.10 is the min). Explicit test fixtures that
  pass `0.25f` were deliberately left as-is.

### 2. Bundled fonts regenerated
- All **20** bundled `UniTextFont` assets (3 in `Defaults/`, 17 in `Samples~/`) set to
  `spreadStrength: 0.10` (single-line YAML edit each; CRLF preserved). Atlases are
  `[NonSerialized]` (the `.asset` stores no glyph table or atlas texture — only `fontData`,
  `faceInfo`, `atlasSize`, `spreadStrength`, `atlasRenderMode`), so padding is re-derived from
  the new spread at runtime; there is no stale serialized atlas/glyph data to clear.

### 3. Project-wide editor tool
- `Tools/OpenGlyph/Set SDF Spread on All Fonts...` (`Editor/SetSdfSpreadTool.cs`): finds every
  `UniTextFont` in the project, shows a dry-run list (font, path, current→new spread, mode),
  applies on confirm with per-asset `Undo`, regenerates each atlas via `ClearDynamicData()`
  (the inspector's own Apply path), is idempotent, and skips non-SDF/MSDF (coverage/bitmap/color)
  fonts since spread does not apply to them.

### 4. Effects (outline / underlay / glow)
- The Uber shader normalizes effect strength by `normFactor = REFERENCE_SPREAD_RATIO / spreadRatio`
  (`= 0.1 / spreadStrength`), so a given outline/underlay/dilate/offset renders the **same visual
  width at any spread** — normal effect widths are unaffected by the default change.
- The only real consequence: the SDF encodes distance out to ±spread texels, so the **maximum
  achievable** effect distance halves (padding 23→9 px at pointSize 90). Extreme outline/underlay/
  glow widths that previously reached toward 23 px now saturate near 9 px and clip earlier. This is
  inherent to a tighter spread (TMP behaves the same at its 0.1 ratio) and is a maximum-range
  reduction, not a defect at normal widths.
- Verified by the full EditMode suite run with the unified renderer forced on
  (`UNITEXT_FORCE_UNIFIED=1`): the old-vs-new pixel-equivalence tests (SDF, MSDF, `<color>`,
  outline, underlay) and the outline/underlay style tests all pass at spread 0.10. No test
  exercising a near-maximum effect width regressed.

### 5. Evidence (1280×720, World-Space canvas, real GPU)
Rendered "sac" with Noto Sans at FontSize 400 (~4–5× the 90 px atlas), 5× zoom crops.
`Documentation/Design/evidence/`:
- `openglyph_sac_spread025{,_zoom5x,_s_zoom5x}.png` — before (0.25): soft/feathered edges, ink
  coverage 43,645 px.
- `openglyph_sac_spread010{,_zoom5x,_s_zoom5x}.png` — after (0.10): crisp edges, ink coverage
  49,633 px (**+13.7 %**; the wide spread had been eating ~12 % of coverage).
- `tmp_sac{,_zoom5x,_s_zoom5x}.png` — TMP reference (crisp). OpenGlyph at 0.10 is now on par;
  only a small secondary shader-scale softness gap remains (see Recommended fix #2).

**Right side of the "s" — clipping verdict:** NOT clipped at 0.10 (nor at 0.25 in the current
post-merge renderer). The "s" right terminal is a smoothly tapering rounded curve — column ink
height ramps `0→24→35→43→48→53→57→62→65→68…` across the edge, the signature of a curve, not a
hard vertical wall. The hard-vertical cut-off noted earlier was in the pre-renderer-merge path and
does not reproduce here; no glyph-bounds/padding/quad fix was needed.

### 6. Tests (Unity 6000.3.19f1, batchmode)
| Platform | Renderer | passed | failed | skipped | inconclusive |
|---|---|---|---|---|---|
| EditMode | legacy (default) | 218 | 0 | 1 | 0 |
| EditMode | unified (`UNITEXT_FORCE_UNIFIED=1`) | 218 | 0 | 1 | 0 |
| PlayMode | legacy | 1 | 0 | 0 | 0 |
| PlayMode | unified | 1 | 0 | 0 | 0 |

No failures in any mode. (The earlier "214 passed / 4 skipped / 1 inconclusive" baseline predates
the `openglyph/main` merge, which added the unified-renderer tests; the invariant that matters —
zero failures — holds, including with the unified renderer forced on.)

## Not done / out of scope
- Recommended fix #2 (secondary shader-AA calibration so matched-spread OpenGlyph equals TMP's
  ~2 px band exactly) and #3 (≥12-bit atlas) are separate follow-ups; this change implements #1.
- No PR opened (per task); changes are pushed to `openglyph/sdf-quality` for review.
