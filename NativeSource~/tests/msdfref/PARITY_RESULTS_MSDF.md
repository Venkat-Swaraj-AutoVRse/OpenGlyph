# MSDF field parity vs upstream msdfgen v1.12 (85e8b3d) — measurement baseline

This file records the **pre-port** per-texel parity measurement between OpenGlyph's managed MSDF
pipeline (`Runtime/FontCore/Msdf/*.cs`) and upstream **msdfgen v1.12** (commit `85e8b3d`,
"Version 1.12", MIT, Viktor Chlumsky), captured by the reproducible harness in this directory
(`msdfref` C++ driver + `glyphref/` .NET harness). It is committed BEFORE the faithful port of
`MSDFErrorCorrection` so the measurement survives independently of the port work.

## How it is measured

`glyphref` exports OpenGlyph's **real FreeType outline** for each glyph (via the native
`ut_ft_*` exports, ppem 64, `LOAD_NO_HINTING`, 26.6→whole-pixel y-up — the exact transform
`FreeTypeOutlineSource` uses), frames + colours it with the production managed code
(`Shape.FromOutline` → `OrientContours` → `EdgeColoring.ColorSimple(shape, 3.0, 0)`), and
generates OpenGlyph's RAW (error-correction off) and CORRECTED (on) fields with
`MsdfBuilder.Build(outline, spread=6)`. It then serialises the identical coloured, oriented shape
to an msdfgen shape-description file and runs `msdfref` on it at **identical framing**
(w, h, range = 2·spread = 12, scale = 1, translate = spread − glyph-box-origin) to obtain
msdfgen's own RAW and CORRECTED fields. Comparison is per texel, per channel.

Glyph set / framing match the W-streak gate `MsdfArtifactTests` exactly:
`A M W N k x & g @`, ppem 64, spread 6.

## Pre-port numbers (NotoSans-Regular.ttf)

Per-texel |Δ| between OpenGlyph and msdfgen fields (channel values in [0,1]; 1 byte ≈ 0.0039).
`colours eq` = how many per-edge colours of OpenGlyph's colouring differ from msdfgen's own
`edgeColoringSimple` on the same shape (N/M = N differing of M edges).

| glyph | WxH | colours eq | RAW max | RAW mean | CORRECTED max | CORRECTED mean |
|-------|-----|-----------|---------|----------|---------------|----------------|
| A | 53x58 | 8/17 | R0.14453 G0.10313 B0.05663 | R0.000248 G0.000136 B0.000023 | R0.98363 G0.10313 B0.05663 | R0.001530 G0.000136 B0.000033 |
| M | 58x58 | 22/22 | R0.00000 G0.10189 B0.01975 | R0.000000 G0.000089 B0.000006 | R1.03196 G0.10189 B1.03846 | R0.002508 G0.000089 B0.003073 |
| W | 71x58 | 36/36 | R0.19785 G0.17920 B0.17055 | R0.000434 G0.000319 B0.000267 | R0.83527 G0.74938 B0.17055 | R0.001512 G0.002179 B0.000398 |
| N | 49x58 | 18/18 | R0.04688 G0.04688 B0.06036 | R0.000018 G0.000016 B0.000080 | R0.73047 G0.67033 B0.06036 | R0.000703 G0.000970 B0.000080 |
| k | 41x61 | 18/18 | R0.05990 G0.05546 B0.07552 | R0.000042 G0.000099 B0.000038 | R0.37133 G1.19661 B2.91131 | R0.000686 G0.005443 B0.004671 |
| x | 44x47 | 12/12 | R0.00000 G0.05927 B0.00000 | R0.000000 G0.000029 B0.000000 | R0.52819 G0.05927 B0.51470 | R0.002835 G0.000029 B0.002624 |
| & | 56x60 | 37/45 | R0.10099 G0.10099 B0.00212 | R0.000142 G0.000138 B0.000001 | R0.33750 G0.34188 B0.30020 | R0.001087 G0.001094 B0.000254 |
| g | 43x63 | 35/39 | R0.27225 G0.03906 B0.27225 | R0.002199 G0.000019 B0.000819 | R0.27225 G0.32283 B0.27225 | R0.002199 G0.000445 B0.000819 |
| @ | 63x64 | 50/60 | R0.01574 G0.06740 B0.08603 | R0.000004 G0.000032 B0.000150 | R0.01574 G0.15455 B0.08603 | R0.000004 G0.000099 B0.000154 |

### What the pre-port numbers say

- **RAW mean |Δ| is tiny on every glyph** (≤ 0.0022, mostly ~1e-4): the two generators agree in
  bulk. The nonzero **RAW max** (localised, 0.05–0.27) tracks the **per-edge colour differences** —
  OpenGlyph's `EdgeColoring.ColorSimple` assigns a different (seed-rotated) CYAN/MAGENTA/YELLOW
  labelling than msdfgen's `edgeColoringSimple`. Where the median is invariant under that
  relabelling the field is byte-identical (M, x: RAW max 0 on the unchanged channels); where it is
  not, a few texels differ. **Reaching the 1e-4 raw target therefore requires the colouring to
  match**, which is part of the port scope, not only the error-correction stage.
- **CORRECTED max |Δ| is large** (0.3–2.9): OpenGlyph's current error-correction stage
  (`MsdfErrorCorrection.cs`, the pre-port heuristic version with `InterpolatedMedianExtremum` /
  `IsClashCrossing` / `onEdge`/`firm` constants) diverges massively from msdfgen's
  `MSDFErrorCorrection`. This is the headline justification for the faithful port.

## Reproduce locally

```
# C++ reference driver (fetches msdfgen 85e8b3d via CMake FetchContent, core only):
cmake -S NativeSource~/tests/msdfref -B <build> -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build <build>
# .NET harness (compiles the live Runtime/FontCore/Msdf sources; needs the native DLL + a font):
dotnet build -c Release NativeSource~/tests/msdfref/glyphref -o <bin>
dotnet <bin>/glyphref.dll <unitext_native.{dll,so,dylib}> Defaults/NotoSans-Regular.ttf <build>/msdfref[.exe] <outDir>
```

CI runs this Linux-only, path-filtered, as the `msdfref` job in `.github/workflows/native.yml`.
