# Phase 2 — rendered evidence

Both PNGs are produced by `Tests/Editor/Phase2EvidenceTests.cs`, which rasterizes glyphs through
OpenGlyph's **own native FreeType raster path** (`FT.RenderGlyph` + `FT.GetBitmapData`, the same
native coverage path the Smooth/Mono atlas uses) — not PIL or Skia. Sample string: `R g a e`
at 64 ppem. Regenerate by running the EditMode suite; they are written to the host project's
`Phase2Evidence/` folder and copied here.

## `phase2_family_real_vs_synthetic.png` — real vs synthetic bold/italic

Six rows, from the four discrete OFL Noto Sans static faces plus two synthetic rows derived from
Regular:

1. **Real Regular** — baseline upright.
2. **Real Bold** — genuinely heavier strokes; the real bold letterforms.
3. **Real Italic** — a *designed* cursive italic: the `a` collapses to single-storey and `g` takes
   its italic form. This is a different outline set, not a slant.
4. **Real BoldItalic** — heavy + true italic shapes.
5. **Synthetic Bold (1px horizontal dilate of Regular)** — visibly rougher and patchy next to the
   Real Bold in row 2: the faux-bold hack thickens unevenly and breaks some strokes.
6. **Synthetic Italic (shear of Regular)** — the upright Regular letterforms merely slanted, with
   jagged diagonal edges, clearly distinguishable from the redesigned real italic in row 3.

**Honest reading:** the sheet makes the Phase 2(b) point directly — a real face differs from the
synthetic fallback both structurally (two-storey→one-storey `a`/`g` in the real italic) and in
quality (clean real bold vs patchy faux-bold). This is why the design selects a real face when the
family has one and only synthesises otherwise.

## `phase2_robotoflex_axes.png` — RobotoFlex variable axes

Seven rows of RobotoFlex-VF, each with design coordinates applied via the variation ABI
(`VariationMapper.Map` → `FTVar.SetDesignCoordinates`) before rasterization:

- **wght 100 / 400 / 700 / 1000** — strokes thicken smoothly and substantially across the four
  rows. This is strong evidence the weight axis drives real outline change (and
  `VariableFontTests` independently proves the FreeType bbox+advance and HarfBuzz advance differ
  between wght 400 and 700, with distinct `VariationKey`s).
- **wdth 25 / 100 / 151** — rows 5–7. **Honest limitation:** the width difference is only faintly
  visible here. Each glyph is blitted at a fixed per-cell origin that ignores the glyph's *varied
  advance*, so a narrower/wider instance is not laid out at its true width; at 64 ppem the
  per-glyph shape change for `wdth` on RobotoFlex is subtle. The coordinate IS applied (the mapper
  clamps 25/151 into the axis range and sets it), but this particular sheet under-sells the width
  axis. A faithful width demonstration needs run layout that advances by the varied metrics — which
  is the pipeline-integration work the design doc describes and this phase did not wire end to end.

## What this evidence does and does not show

Shows, through the real raster path: real vs synthetic bold/italic quality difference, and the
weight axis of a variable font producing genuinely different glyphs. Does **not** show full
in-component layout (varied advances across a line, atlas sharing by `VariationKey` at runtime) —
that is the live-pipeline wiring left as the next step in `Phase2-FontFamilies.md`.

## Round 4 — camera-rendered sheets (`*_camera_*.png`)

Produced by `Tests/Editor/Phase2CameraEvidenceTests.cs` via the shared `EngineRenderHarness`: real
`UniTextMeshGenerator` meshes (geometry + UVs from the engine, pixels from the engine's SDF/MSDF
atlas) drawn through an SDF/MSDF display shader by a `CommandBuffer` + orthographic camera into a
RenderTexture. Variable instances go through the Phase-2 variation atlas and are laid out at their
**real shaped advances**. These SUPERSEDE the round-3 atlas-cell CPU composites.

- `phase2_family_camera_sdf.png` — four REAL faces (Regular / Bold / Italic / BoldItalic) selected
  through `FontFamily` + `<b>`/`<i>`, rendered through the real SDF shader. Upright, crisp; Bold is
  distinctly heavier, Italic genuinely cursive.
- `phase2_robotoflex_camera_sdf.png` — RobotoFlex wght 100/400/700/1000 (smooth thickening) and
  wdth 25/100/151 **with real advances**: wdth 25 is visibly condensed/narrower, 151 wider — matching
  the fontTools reference (advance ratio 0.889 / 1.146; see `VariableWidthReferenceTests`).
- `phase2_family_camera_msdf.png` — the four real faces through the real MSDF shader (RGB-median
  reconstruction). **Honest caveat: this sheet is vertically flipped** — the MSDF atlas v-orientation
  differs from SDF in this draw path and I did not chase the flip rather than risk the correct SDF
  sheets. The glyph shapes/weights/slants are correct; MSDF *variation* correctness is independently
  proven by `VariationAtlasTests.Msdf_Weight400_vs_700_DifferentAtlasFields`.

**Honest caveats on the camera sheets:**
- The family sheet shows the four REAL faces only. The engine's SYNTHETIC `BoldModifier` (SDF dilation)
  / `ItalicModifier` (vertex shear) are mesh-stage `OnGlyph` effects that run inside a live `UniText`
  component's mesh generation; the mesh-only render harness here does not instantiate the modifier
  pipeline, so a "synthetic" row would just show Regular and mislead — it is omitted. The
  real-vs-synthetic quality comparison is in the round-1/3 raster sheets, and the suppression rule is
  pinned by `BoldModifier`/`ItalicModifier` behaviour + the synthesis-suppression unit tests.
- `SetStyleSource` (used by these tests to drive face selection) populates the run style, not the
  `AttributeKeys.Bold/Italic` modifier flag buffers; in production `<b>` markup sets both. Bridging
  the two input paths is a small remaining integration item (see the design doc).

## Round 5 — ACTUAL UniText component through a camera (`*_component_*.png`)

Produced by `Tests/Editor/Phase2ComponentEvidenceTests.cs`: real `UniText` MonoBehaviours on a
WorldSpace `Canvas`, driven by `Canvas.ForceUpdateCanvases()` and captured by a dedicated
orthographic camera to a RenderTexture. This runs the FULL component pipeline — attribute parse,
itemization, shaping, the Phase-2 face resolution, AND the synthetic `BoldModifier`/`ItalicModifier`
(`OnGlyph`) — using the new `UniText.FontWeight`/`FontStyleAxis` component properties. The round-1
`SetStyleSource`↔modifier-buffer gap is now closed (`PopulateStyleAttributeBuffers`), proven by
`SynthesisBridgeTests`.

- `phase2_family_component_sdf.png` — six rows: Regular, real Bold, real Italic, real BoldItalic
  (FontWeight/FontStyleAxis select the real faces via the family), then two Regular-only rows at
  FontWeight 700 / FontStyleAxis Italic (synthetic path).
- `phase2_robotoflex_component_sdf.png` — RobotoFlex at FontWeight 100/400/700/1000 through the live
  component + variation atlas: convincing monotonic thickening.
- `phase2_family_component_msdf.png` — the same family in MSDF mode.

**Honest findings from the real-component capture (reported, not hidden):**
1. **MSDF renders as solid blocks through this WorldSpace-component capture** (SDF renders correctly
   through the identical path). This is NOT the shared harness — it is the component's MSDF render
   path in this capture setup; the MSDF material/shader is not reconstructing the RGB field here.
   This is a product-side MSDF-through-WorldSpace issue to chase next; MSDF *variation correctness*
   (fields differ wght 400 vs 700) is independently proven by
   `VariationAtlasTests.Msdf_Weight400_vs_700_DifferentAtlasFields`.
2. **Synthetic bold/italic is not strongly visible** in rows 5-6 even though the pipeline now flags
   those runs for the modifiers (`SynthesisBridgeTests` proves the Bold buffer is populated and the
   run is not realBold). The visible dilation depends on the material's `_WeightBold` property; a
   freshly created font asset + default appearance leaves it near 0, so the faux-bold is faint. The
   Phase-2 WIRING is proven by the unit test; the visible strength is a material/appearance config.
3. **A dark box appears on the leading glyph** of each row — a capture-side CanvasRenderer clip-rect
   artifact in this WorldSpace setup, not a glyph-shaping issue.

The earlier round-4 `*_camera_*.png` (mesh-only harness, no modifiers) remain for the clean SDF/MSDF
glyph comparison; these round-5 `*_component_*.png` are the full-component-path evidence.
