# MSDF generator performance — old (flat-min, 9e4af98) vs new (msdfgen-faithful)

The new generator (`MsdfDistance.cs` — `OverlappingContourCombiner<MultiDistanceSelector>`, byte-exact
to msdfgen) does strictly more work per texel than the old flat global per-channel nearest-edge min
(per-channel perpendicular-distance resolution with edge-domain gating + winding-based contour
combination). Naively ported it was ~4.5× slower on a 95-glyph batch. Two allocation/pruning fixes
brought it back to parity with the old generator:

1. **Persistent per-edge `EdgeCache`** (msdfgen's reused `shapeEdgeCache`, sized to `shape.edgeCount()`):
   carried across texels so `isEdgeRelevant` prunes edges far from the running min. The naive port
   allocated a fresh zero cache per edge per texel, which both allocated and disabled pruning.
2. **Allocation-free combine selectors**: the three shape/inner/outer `MultiDistanceSelector`s in the
   overlap combiner are reused and re-initialised with `ClearInfinite` each texel instead of being
   `new`-ed (6 object allocations per texel removed). Output is unchanged — `ClearInfinite` is exactly
   a fresh construction's state.

Neither changes output: byte-exact RAW + CORRECTED parity with msdfgen on all 9 test glyphs is
re-verified by `glyphref` after the optimisation (`GATE PASS`, exit 0).

## Measured (Release .NET 8, NotoSans-Regular, ppem 64, spread 6, median of 11 runs)

`MsdfBuilder.Build(outline, spread=6, errorCorrection:true)`, outlines pre-fetched (FreeType time
excluded). Machine: the CI/dev Windows host; absolute ms are machine-relative, the RATIO is the metric.

| set | old (9e4af98) | new (optimized) | ratio new/old |
|-----|---------------|-----------------|---------------|
| 9 test glyphs (A M W N k x & g @) | 48.2 ms (5.35 ms/glyph) | 46.3 ms (5.14 ms/glyph) | **0.96×** |
| 95 printable ASCII | 239.1 ms (2.54 ms/glyph) | 256.6 ms (2.73 ms/glyph) | **1.07×** |

Both are **well under the 1.5× bar**. (For reference, the naive port before these fixes was 1.59× on
the test glyphs and 4.45× on the ASCII batch.)

The Unity-side timing (`MsdfPerfTests.MsdfGeneration_PerGlyph_WithinBudget`, EditMode Stopwatch over
the 9 test glyphs) asserts the new generator stays within a generous per-glyph budget so a future
allocation regression fails CI.

## Reproduce

```
# new (current tree):
dotnet build -c Release NativeSource~/tests/msdfref/msdfbench -o <bin>
dotnet <bin>/msdfbench.dll <unitext_native.{dll,so,dylib}> Defaults/NotoSans-Regular.ttf 11
# old: check out 9e4af98's Runtime/FontCore/Msdf into a dir, compile msdfbench against it.
```
