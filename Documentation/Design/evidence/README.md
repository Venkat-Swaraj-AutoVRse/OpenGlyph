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
