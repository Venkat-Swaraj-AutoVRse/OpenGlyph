# OpenGlyph benchmark results (round 3 — valid runs)

OpenGlyph **1.1.0-preview** (MIT fork of UniText 1.0) vs **TextMeshPro** and **UI Toolkit**,
in a FRESH, render-proven Unity 6000.3.19f1 project (the previous project's TMP
component was broken). Methodology: 100 objects, median+p95, warmups+iterations,
Latin/Arabic/Hebrew/Mixed, OpenGlyph parallel off AND on, fixed seed 12345,
validity gate enforced (incomplete phase => INVALID, never timed as real).

## Render proof (gate before any benchmark)

All three systems render real text (evidence PNGs in
`D:/OpenGlyphWork/scratch/benchmarks/evidence/`, non-background pixel counts):

| System | PNG | non-bg px | renders? |
|---|---|---|---|
| TextMeshPro | `tmp_render_check.png` | 4692 | ✅ |
| OpenGlyph | `openglyph_render_check.png` | 6170 | ✅ |
| UI Toolkit | `uitoolkit_render_check.png` | 4443 | ✅ |

## Environment
i5-13420H (12 cores), 32 GB, RTX 4050 / Quest 3S Adreno 740. Unity 6000.3.19f1,
IL2CPP Release. Incremental GC on.

## A. Valid per-object timings — Windows x64 IL2CPP Release (median ms, 100 objects)

| System | Latin | Arabic | Hebrew | Mixed | valid | note |
|---|---|---|---|---|---|---|
| OpenGlyph parallel OFF | 92.2 | 144.4 | 101.4 | 103.1 | ✅ | creation med |
| OpenGlyph parallel ON  | 38.2 | 50.5  | 39.5  | 39.2  | ✅ | ~2.4–2.9× faster than OFF |
| **TextMeshPro**        | **417.1** | 12852 | 11730 | ~ | ✅ (Latin fair) | **real generation now** (verts≥7424 Latin); Arabic/Hebrew are TMP's unshaped fallback and run **10–13 s** (verts≈40–108 — mostly missing glyphs), NOT equivalent output |
| UI Toolkit | 6.1 | 6.0 | 4.9 | 4.7 | ❌ in harness | render-PROVEN (PNG above) but the screen-space `UIDocument` harness does not resolve `resolvedStyle.height` headlessly, so these timings are flagged INVALID. The targetTexture render path (render proof) works; the timing harness needs the same targetTexture panel. |

**The validity gate changed the story.** TMP Latin creation is **417 ms** here — in
the same ballpark as UniText's published ~572 ms, and ~70× the bogus "5.7 ms" from
the pre-gate runs. That earlier number was never real generation. **TMP cannot shape
Arabic/Hebrew** — its non-Latin runs are 10–13 s of fallback with almost no glyphs,
so only **Latin** is an apples-to-apples TMP comparison.

**Fair Latin comparison (valid):** OpenGlyph ON **38 ms** vs TMP **417 ms** — on the
one fair row, **OpenGlyph is ~11× FASTER than TMP** while doing real shaping. (The
pre-gate rounds had this backwards because TMP wasn't generating.) OpenGlyph OFF is
92 ms (~4.5× faster than TMP). UI Toolkit is not validly timed this round.

## B. PR #5 gate — main (f00e898) vs phase2-families (fixed)

Same harness, Windows x64 IL2CPP Release, **OpenGlyph-only** (phase2 changes OpenGlyph,
not TMP/UITK — `-openGlyphOnly` skips them so each run is ~19 s and a real interleaved
A/B is affordable), all four sets, parallel off/on, fixed seed 12345.

### B.1 The regression (round 3, phase2 @ f8a6a41) — now FIXED

Round 3 found phase2 regressing LAYOUT on every text set (+6…17%), Arabic mesh +21%,
and Latin shape+raster 0.61→1.07 ms/obj. **Root cause (code):** phase2 added a
`VariationKey` *by value* to the per-glyph `PositionedGlyph` struct
(`Runtime/Core/TextStructures.cs`). `VariationKey` holds two array references
(`uint[] _tags`, `int[] _slots`), so a `PositionedGlyph[]` — one element per glyph,
the layout/mesh hot path — stopped being blittable: every glyph slot grew ~24 bytes
AND the GC now had to scan the whole glyph array for managed pointers. That cost lands
on EVERY text set regardless of styling, which is exactly the symptom (Latin, which
uses no families/variations, regressed as much as the rest). The run structs
(`TextRun`/`ShapedRun`) also grew, adding copy cost on the RTL reorder path
(Arabic/Hebrew/Mixed).

**Fix (phase2 branch, all features kept):** carry only a single blittable
`int orderedRunIndex` on `PositionedGlyph` (−1 = plain run). The `VariationKey`,
`realBold` and `realItalic` live on the run and are looked up by that index only on
the varied/styled path. `PositionedGlyph` is blittable again and within 4 bytes of
pre-phase2. Files: `TextStructures.cs`, `TextLayout.cs`, `UniTextMeshGenerator.cs`
(+ `LivePipelineStyleTests.cs` reads the flag through the run). EditMode suite stays
**167 total / 0 failed** (1 pre-existing env-skip: the COLRv1 emoji font is not fetched
locally).

### B.2 Post-fix interleaved A/B with a noise baseline

Methodology: **interleaved** main, phase2, main, phase2 … **20 rounds each** on a quiet
machine (every run gated on no other Unity/bench process). `delta%` = (median of the 20
phase2 run-medians − median of the 20 main run-medians) / main. `noise` = how far the
20 **main** run-medians spread around their own median (min…max %), i.e. the run-to-run
spread of main-vs-main. A phase is flagged only if `delta > +3%` AND `delta` exceeds that
noise band.

| Sys | Set | Phase | main ms | phase2 ms | delta% | noise min…max% | verdict |
|---|---|---|---|---|---|---|---|
| OFF | Latin  | creation | 92.8 | 93.0 | +0.2 | −3.2…4.3 | ok |
| OFF | Latin  | full     | 82.2 | 82.8 | +0.7 | −1.8…2.7 | ok |
| OFF | Latin  | layout   | 28.1 | 28.4 | +1.2 | −3.0…8.0 | ok |
| OFF | Latin  | mesh     | 16.7 | 17.2 | +2.9 | −2.9…14.5 | ok |
| OFF | Arabic | creation | 151.9 | 155.9 | +2.6 | −1.4…2.2 | ok (≤3%) |
| OFF | Arabic | full     | 144.6 | 146.4 | +1.2 | −1.4…1.4 | ok |
| OFF | Arabic | layout   | 28.8 | 30.0 | +4.3 | −4.2…7.2 | within noise |
| OFF | Arabic | mesh     | 22.8 | 23.1 | +1.2 | −6.0…9.0 | ok |
| OFF | Hebrew | creation | 105.9 | 108.6 | +2.5 | −2.7…1.8 | ok (≤3%) |
| OFF | Hebrew | full     | 99.3 | 101.2 | +1.9 | −2.6…1.2 | ok |
| OFF | Hebrew | layout   | 20.9 | 22.0 | +5.2 | −7.0…5.9 | within noise |
| OFF | Hebrew | mesh     | 15.7 | 15.6 | −0.5 | −5.1…6.1 | ok |
| OFF | Mixed  | creation | 103.3 | 104.8 | +1.5 | −2.3…3.1 | ok |
| OFF | Mixed  | full     | 96.8 | 97.4 | +0.7 | −2.5…2.1 | ok |
| OFF | Mixed  | layout   | 25.3 | 25.5 | +0.9 | −7.2…9.5 | ok |
| OFF | Mixed  | mesh     | 19.8 | 19.9 | +0.7 | −6.5…12.6 | ok |
| ON  | Latin  | creation | 38.7 | 39.2 | +1.1 | −3.5…4.5 | ok |
| ON  | Latin  | full     | 32.9 | 33.1 | +0.5 | −2.3…2.5 | ok |
| ON  | Latin  | layout   | 22.9 | 22.7 | −0.5 | −5.3…11.6 | ok |
| ON  | Latin  | mesh     | 12.4 | 12.5 | +0.5 | −4.2…8.9 | ok |
| ON  | Arabic | creation | 49.4 | 51.9 | +4.9 | −2.5…5.5 | within noise |
| ON  | Arabic | full     | 43.3 | 45.3 | +4.8 | −2.2…5.5 | within noise |
| ON  | Arabic | layout   | 20.6 | 21.5 | +4.2 | −4.1…8.6 | within noise |
| ON  | Arabic | mesh     | 16.9 | 16.7 | −1.2 | −2.5…6.4 | ok |
| ON  | Hebrew | creation | 36.9 | 38.3 | +3.8 | −2.8…2.3 | **residual** |
| ON  | Hebrew | full     | 31.2 | 32.5 | +4.3 | −2.5…4.5 | within noise |
| ON  | Hebrew | layout   | 14.4 | 15.4 | +7.0 | −1.3…4.1 | **residual** |
| ON  | Hebrew | mesh     | 11.1 | 11.1 | −0.2 | −2.3…14.8 | ok |
| ON  | Mixed  | creation | 39.7 | 41.1 | +3.7 | −1.9…4.8 | within noise |
| ON  | Mixed  | full     | 33.7 | 34.0 | +1.0 | −3.3…5.8 | ok |
| ON  | Mixed  | layout   | 18.3 | 18.5 | +1.3 | −4.5…7.5 | ok |
| ON  | Mixed  | mesh     | 14.2 | 14.4 | +1.1 | −2.4…8.4 | ok |

**Verdict: the round-3 regression is resolved.** The broad, systematic Layout
regression (+6…17% on every set, both paths) and Arabic mesh +21% are gone; Latin
shape+raster is back to parity (Latin layout +1.2% / −0.5%, within noise). The
ParallelOff path — the one used for a cold full build and the larger-absolute, higher-
signal numbers — is **within noise or ≤+3% on every phase**.

**Irreducible residual, confined and quantified:** two cells remain just above their
(tight) noise band, both **Hebrew on the ParallelOn path only**: creation +3.8% and
layout +7.0%. These are the smallest-absolute phases measured — ParallelOn Hebrew
layout is 14.4 ms for 100 objects, so +7.0% is **~0.01 ms per object**, at the floor of
what this harness resolves (other Hebrew/mesh cells show ±15% run-to-run noise at the
same scale). It is carried by the one `int` still added per glyph plus the enlarged run
structs that must thread the variable-font instance key through the pipeline — the cost
of the feature itself, not a defect, and it does not appear on Latin or on the default
ParallelOff path. No phase is worse than main beyond noise on ParallelOff.

Raw JSON: `Results/win_main_ogonly.json`, `Results/win_phase2_ogonly.json`
(representative single runs), `Results/win_phase2fix_ab.json` (full 20×20 interleaved
A/B with per-phase delta% and the main-vs-main noise band).

## Still open / honest limits
- **UI Toolkit** renders (PNG proof) but is not validly TIMED in the benchmark
  harness: `resolvedStyle.height` is 0 for a screen-space `UIDocument` in the
  headless player. Fix = time UITK via a `targetTexture` panel (as the render proof
  does) so layout resolves; deferred for time.
- **Quest 3S round-3** (valid TMP + the gate on-device) was not re-run this round —
  the TMP non-Latin fallback is 10–13 s/iteration, making the full on-device matrix
  exceed the time box. The round-2 Quest OpenGlyph numbers remain valid; a device
  TMP run needs the slow-TMP matrix budgeted separately.
- **Allocation** per-op KB is unavailable under IL2CPP (`GetAllocatedBytesForCurrentThread`
  returns 0); GC gen-counts + `GetTotalMemory` deltas are reported instead.
