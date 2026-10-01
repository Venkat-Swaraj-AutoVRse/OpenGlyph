# MSDF field parity vs upstream msdfgen v1.12 (85e8b3d) — gated test

Per-texel parity between OpenGlyph's managed MSDF pipeline (`Runtime/FontCore/Msdf/*.cs`) and
upstream **msdfgen v1.12** (commit `85e8b3d`, "Version 1.12", MIT, Viktor Chlumsky). Reproducible
harness: `msdfref` (C++, FetchContent msdfgen core) + `glyphref/` (.NET, compiles the live managed
sources). CI: Linux-only `msdfref` job in `.github/workflows/native.yml`, which **FAILS** when any
glyph's RAW or CORRECTED(no-dist) field exceeds the gate threshold.

## How it is measured

`glyphref` exports OpenGlyph's **real FreeType outline** per glyph (native `ut_ft_*`, ppem 64,
`LOAD_NO_HINTING`, 26.6→whole-pixel y-up), frames + colours it with the production code
(`Shape.FromOutline` → `OrientContours` → `EdgeColoring.ColorSimple(shape, 3.0, 0)`), generates
OpenGlyph's RAW (error-correction off) and CORRECTED (on) fields with `MsdfBuilder.Build(outline,
spread=6)`, serialises the identical coloured shape to an msdfgen shape-description, and runs
`msdfref` at identical framing (w, h, range=2·spread=12, scale=1, translate=spread−box-origin).
Comparison is per texel, per channel. Glyph set / framing match the W-streak gate `MsdfArtifactTests`:
`A M W N k x & g @`, ppem 64, spread 6.

## Final numbers (NotoSans-Regular.ttf) — after the faithful generator + error-correction port

| glyph | WxH | colours | RAW max | CORRECTED(no-dist) max | CORR default max |
|-------|-----|---------|---------|------------------------|------|
| A | 53x58 | 8/17 relabel | 0.000000 | 0.000000 | 0.0000 |
| M | 58x58 | 22/22 relabel | 0.000000 | 0.000000 | 0.0000 |
| W | 71x58 | 36/36 relabel | 0.000000 | 0.000000 | 0.0000 |
| N | 49x58 | 18/18 relabel | 0.000000 | 0.000000 | 0.0000 |
| k | 41x61 | 18/18 relabel | 0.000000 | 0.000000 | 0.0000 |
| x | 44x47 | 12/12 relabel | 0.000000 | 0.000000 | 0.0000 |
| & | 56x60 | 37/45 relabel | 0.000000 | 0.000000 | 0.0000 |
| g | 43x63 | 35/39 relabel | 0.000000 | 0.000000 | 0.0000 |
| @ | 63x64 | 50/60 relabel | 0.000000 | 0.000000 | 0.0000 |

**RAW and CORRECTED are byte-exact to msdfgen on all 9 glyphs** (max |Δ| 0.00000, i.e. < 5e-6 per
channel). CORR default (vs msdfgen's shipping CHECK_DISTANCE_AT_EDGE) is also 0.0000, so for these
glyphs the un-ported ShapeDistanceChecker refinement makes no difference.

## The RAW root cause (correcting the earlier, self-contradictory note)

The earlier draft blamed `EdgeColoring` labelling, which was wrong: msdfref consumes OpenGlyph's OWN
colours, so colouring could not explain a field delta. The real cause was the **generator**.
OpenGlyph's old `MsdfGenerator.Generate` took, per channel, a flat global nearest-edge true-distance
min over every edge of every contour, then one `distanceToPerpendicularDistance`. msdfgen's
`generateMSDF(overlapSupport=true)` instead uses `OverlappingContourCombiner<MultiDistanceSelector>`:
per contour it tracks the true distance AND separate min negative/positive **perpendicular**
distances with prev/next-edge domain gating (`add`/`bdd`), then combines contours by winding
(inner/outer/shape resolution). The two coincide inside a single contour (so bulk fields matched: RAW
mean was ~0) but diverge at edge-domain boundaries (corners/junctions) and at overlapping contours —
exactly W/g/&/@, which measured RAW max 0.05–0.27. Porting the selectors + combiner
(`Runtime/FontCore/Msdf/MsdfDistance.cs`, citing `core/edge-selectors.cpp` + `core/contour-combiners.cpp`)
drove RAW to 0.

## The `colours` column

`eq` = every per-edge colour label matches msdfgen's `edgeColoringSimple` on the same shape.
`D/T relabel` = D of T edges carry a DIFFERENT label. **This is REPORTED, not gated**: a label
difference is a field-invariant CMY relabelling (seed/rotation), proven by RAW |Δ|=0 even on the
glyphs whose labels differ (e.g. g 35/39, @ 50/60). OpenGlyph's colouring and msdfgen's are therefore
field-equivalent; matching the exact labels is cosmetic and would require seeding `ColorSimple` to
msdfgen's rotation, which changes no output.

## The gate

`glyphref` returns non-zero (failing the CI job, which has no `|| true`) when any glyph's RAW max OR
CORRECTED(no-dist) max exceeds **1e-4 per channel**. Justification: with the port both are 0.00000 on
all 9 glyphs, so 1e-4 tolerates float-order noise while catching any real regression. The failure
path is exercised — the pre-port runs returned exit 1 at these same checks.

## Unity test gate (6000.3.19f1, EditMode)

Full `Msdf*` EditMode suite: **30/30 pass** with the new generator (incl. the W-streak gate
`RealExport_NoCoverageStreaksOrSpecks_DecodedThroughShaderSmoothstep`, the artifact/visual tests, and
the shader-PNG montage test with graphics on).

## Reproduce

```
cmake -S NativeSource~/tests/msdfref -B <build> -G Ninja -DCMAKE_BUILD_TYPE=Release && cmake --build <build>
dotnet build -c Release NativeSource~/tests/msdfref/glyphref -o <bin>
dotnet <bin>/glyphref.dll <unitext_native.{dll,so,dylib}> Defaults/NotoSans-Regular.ttf <build>/msdfref[.exe] <outDir>
```
