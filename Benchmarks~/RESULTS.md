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

## B. PR #5 gate — main (f00e898) vs phase2-families (f8a6a41)

Same harness, Windows IL2CPP, OpenGlyph-only (phase2 changes OpenGlyph, not TMP/UITK),
all four sets, parallel off/on. **Finding: phase2 regresses the LAYOUT phase on every
text set (>5%), and Arabic mesh rebuild.** Per-phase delta % (phase2 vs main):

| System | Set | Creation | Full Rebuild | Layout | Mesh | regression |
|---|---|---|---|---|---|---|
| OFF | Latin  | +6.5% | +0.7% | +8.0% | +5.9% | creation, layout, mesh |
| OFF | Arabic | +3.2% | +3.4% | **+15.2%** | **+20.7%** | layout, mesh |
| OFF | Hebrew | +5.1% | +4.0% | +12.4% | −1.5% | creation, layout |
| OFF | Mixed  | −0.7% | +4.1% | +13.8% | +1.4% | layout |
| ON  | Latin  | +6.4% | −2.0% | +6.0% | +4.6% | creation, layout |
| ON  | Arabic | −0.7% | −3.3% | **+17.4%** | +6.0% | layout, mesh |
| ON  | Hebrew | +2.1% | −0.9% | +11.5% | −7.8% | layout |
| ON  | Mixed  | +2.3% | −9.6% | +5.6% | −1.4% | layout |

**Where the time goes (profiler split, per object, ParallelOff):**

| Set | main shape+raster / layout / mesh | phase2 shape+raster / layout / mesh |
|---|---|---|
| Latin  | 0.61 / 0.34 / 0.18 | **1.07** / 0.32 / 0.15 |
| Arabic | 1.15 / 0.26 / 0.18 | 1.18 / **0.32** / **0.22** |

**Verdict: REGRESSION.** phase2's font-families + variable-font work raises the
Layout phase 6–17% across the board and Arabic mesh rebuild +21%. The split shows
the cost lands in the shaping/layout path (Latin shape+raster 0.61→1.07 ms/obj;
Arabic layout 0.26→0.32, mesh 0.18→0.22). Full Rebuild and Creation are mostly within
noise. Recommend profiling the variable-font/families code path added by phase2
before merge — the per-layout overhead is systematic, not a single-set outlier.

Raw JSON: `Results/windows_il2cpp_results.json` (goal A, full matrix),
`Results/win_main_ogonly.json`, `Results/win_phase2_ogonly.json` (goal B).

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
