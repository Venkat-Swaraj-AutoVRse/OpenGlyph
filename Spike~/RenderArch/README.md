# Render-Arch Feasibility Spike (Round 1) — THROWAWAY

**This directory (`Spike~/`) is throwaway proof-of-concept code**, excluded from
the Unity asset import by the trailing `~`. It exists to de-risk the proposed
rendering architecture in `../Documentation/Design/RenderArchitecture.md`. Delete
once the architecture is validated and the production implementation lands.

Clean-room: authored from public SDF/MSDF shading knowledge and this repo's own
`Shaders/UniText_Properties.cginc` + `Shaders/UniText_MSDF.cginc`. Nothing from
UniText 2.0/Platinum or any unlicensed upstream.

## What it proves

The riskiest claim of the design: **N text runs using 3 different fonts + emoji +
MSDF, with a per-span outline, can draw in ONE draw call** via a single
`Texture2DArray` + one uber-shader (per-glyph `glyphMode`) + a per-glyph/per-span
style `StructuredBuffer`.

- `OpenGlyphSpikeUber.shader` — one Pass. Samples a `Texture2DArray`; a per-glyph
  `glyphMode` (0 SDF `.a`, 1 MSDF `median(rgb)`, 2 bitmap coverage, 3 COLR color)
  selects the reconstruction branch; a per-glyph `styleIdx` reads a
  `StructuredBuffer<GlyphStyle>` and composites face + outline + underlay/shadow
  in the single pass. SDF/MSDF differ by one line, exactly as in the current
  `UniText_MSDF.cginc`.
- `SpikeHarness.cs` (Editor, `-executeMethod SpikeHarness.Run`) — builds a
  5-slice array (3 SDF "fonts" + 1 MSDF + 1 emoji/COLR), one mesh whose quads mix
  `glyphMode` and `styleIdx` (run #2 carries a per-span outline style), renders on
  a Screen Space Overlay canvas and a World Space canvas, and reports the
  structural CanvasRenderer count (= UI draw-call groups) for the unified path vs
  a baseline that mimics the current one-renderer-per-slice model.
- `SpikeBootstrap.cs` + `SpikeAndroidBuild.cs` — build the same shader path into
  an Android player to confirm it compiles for GLES3/Vulkan.

## Measured result (Unity 6000.3.19f1, 2026-10-01)

```
Unity 6000.3.19f1  gfx=Null (-nographics; UnityStats GPU counters read 0,
                             so the harness reports STRUCTURAL renderer count,
                             which is what determines UI draw calls)
Texture2DArray supported: True
maxTextureSize: 8192

structural CanvasRenderer count (= UI draw-call groups):
  Overlay : baseline(current)=5   proposed(unified)=1
  World   : baseline(current)=5   proposed(unified)=1
VERDICT: PASS
```

**5 → 1 on both canvas types.** The uber-shader compiled without error; the
`Texture2DArray` + `StructuredBuffer` material bound and rendered through a single
`CanvasRenderer` across all five mixed-mode runs including the per-span outline.

> Why structural count, not `UnityStats.drawCalls`: a batchmode `-nographics` run
> has no GPU, so GPU-submit counters are 0. UI draw calls are determined by how
> many CanvasRenderers cannot batch together; one CanvasRenderer binding one
> texture + one material = one draw call. Round 2 on a real GPU will confirm with
> Frame Debugger + `ProfilerRecorder("Draw Calls Count","Batches Count")`.

## Android / GLES3 status — PARTIAL (blocked by host disk, not by the design)

The Android IL2CPP arm64 build (GLES3 + Vulkan) was attempted. It:
- compiled all scripts for Android,
- resolved the bundled SDK/NDK/OpenJDK/Gradle,
- ran IL2CPP and began producing the native player (`libunity.so` arm64-v8a, ~35
  MB of player data), **with no shader-compile errors for the spike shader**,

then failed at the LLVM native-strip stage with:

```
LLVM ERROR: IO failure on output stream: No space left on device
```

The host's C: drive was at **0 bytes free**. This is an environmental limit, not
a toolchain or shader problem — the build progressed well past shader compilation
and asset processing. `supports2DArrayTextures=True`, `maxTextureSize=8192`, and
`sampler2DArray` + `StructuredBuffer` are standard on GLES3.1+/Vulkan (the Quest
target). **Re-run the APK build on a host with disk headroom to complete this
check.** WebGL2 supports `sampler2DArray` (core in GLES3.0/WebGL2) but NOT
`StructuredBuffer` in UI shaders, so WebGL2 uses the float-texture style-table
fallback (design §3.4).

## Reproduce

```powershell
$ed="C:\Program Files\Unity\Hub\Editor\6000.3.19f1\Editor\Unity.exe"
# copy Spike~/RenderArch/*.cs (Editor ones under Assets/Editor) + .shader into a
# throwaway project with com.unity.ugui in Packages/manifest.json (BOM-FREE json!)
& $ed -batchmode -nographics -projectPath <proj> -executeMethod SpikeHarness.Run -logFile run.log
# -> <proj>/spike_result.txt
& $ed -batchmode -nographics -projectPath <proj> -buildTarget Android -executeMethod SpikeAndroidBuild.Build -logFile apk.log
# -> <proj>/android_build_result.txt + Build/ogspike.apk (needs disk headroom)
```
