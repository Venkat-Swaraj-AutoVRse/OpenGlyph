# LSE BenchmarkWorkshop — OpenGlyph vs TMP vs UI Toolkit

Benchmarked OpenGlyph (MIT fork of UniText 1.0, `openglyph/main` @ `e8efa75`) with the
upstream author's own benchmark harness, on **Windows IL2CPP** and **Quest 3S**, against
**TextMeshPro** and **UI Toolkit**. The harness is `LightSideKittens/LightSideEcosystem`
(no LICENSE — used only locally as a measurement harness; **none of its files are committed
here**). OpenGlyph stands in for the harness's missing private UniText submodule (`build B uses
OpenGlyph in its place`), which is sound because OpenGlyph is a fork that kept UniText's assembly
GUIDs — the harness's `UniText.Test.asmdef` references OpenGlyph's own
`LightSide.UniText.asmdef` GUID `6572381e8157783499bf6ce8f56b9292` directly.

Date: 2026-10-02. Unity **6000.3.19f1** (project asked 6000.3.11f1 — same patch line; upgraded on open). IL2CPP, Release.

---

## 1. Branch chosen

`2.0.0` of LightSideEcosystem — earliest 2.x release line, nominally closest to UniText 1.0
(OpenGlyph's fork point). Its bundled UniText reads `"version": "2.0.0"` (public
`package.json`); the runtime itself lives in a **private/deleted** submodule
(`LightSideMeowshop/UniText-Dev`, `remote: Repository not found`), so the harness's *own*
UniText arm cannot be built — which is exactly why OpenGlyph is dropped in as the stand-in.

## 2. What the scene measures

`UniText_BenchmarkTest.unity` creates **100 text objects**, runs **10 timed iterations** after
**3 warmups**, across four text sets (Latin / Arabic / Hebrew / Mixed), per system, and times
these phases: **objectCreation**, **fullRebuild**, **layout** (several wrap/autosize variants),
**meshRebuild** (plus destruction). It records per-phase median/min/max frame times, managed
allocation and GC gen0/1/2 deltas, and serialises to JSON. The OpenGlyph build additionally
emits per-system **validity**: expected vs observed character counts and observed vertex counts
(a quad mesh ⇒ 4 verts/char), with a `*Valid` flag per phase. A second scene
(`GlyphRasterization_BenchmarkTest`) times raw glyph rasterization.

## 3. How OpenGlyph was integrated (every change, scratch project only — nothing below is committed to this repo)

- Removed the two empty UniText submodules (`Assets/UniText`, `Assets/UniText-Public`) and `.gitmodules` from the scratch clone.
- Added OpenGlyph `e8efa75` as a `file:` UPM package (`com.openglyph.text`) from a throwaway worktree at `D:\OpenGlyphWork\scratch\lse-bench\openglyph-pkg` (NOT the user's VRseBuilder package folder).
- `Assets/Scripts/Editor/LseBenchBuild.cs` — new build driver; calls the project's `CIBuildSettings.ConfigureBuild()` (`-ciBenchmark true`) then `BuildPipeline.BuildPlayer` for Win64 / Android IL2CPP Release.
- `UniTextBenchmark.cs` — dropped two `GlyphAtlas.ForceSingleThreaded = …` lines; OpenGlyph governs threading via `UniText.UseParallel` (already set on the preceding line). TMP/UITK arms unchanged.
- **Disabled** (UniText-2.0-only APIs OpenGlyph lacks; wrapped in a never-defined `#if`): `UniTextInteractiveTest.cs` (2.0 `Style` markup — not a timed phase, not in scene), `UniText_GlyphRasterizationBenchmark.cs` + `GoldenTests/GoldenFileTestRunner.cs` (2.0 SDF `GlyphAtlas`/`PrimaryFont`), `COLRv1Test.cs` (2.0 8-arg `TryRenderGlyph`).
- `BenchmarkRunner.cs` — guarded the UniText glyph-rasterization phase behind the same `#if`; when disabled it records the fact in `errors[]`. TMP glyph-raster and the four main text phases are untouched.
- **OpenGlyph package patch (scratch copy only, flagged as an open issue):** `openglyph-pkg/Runtime/Core/UniTextSettings.cs` — OpenGlyph `e8efa75` declares `DefaultAppearance` inside `#if UNITY_EDITOR` but references it from runtime code (`UniText.cs`, `UniTextFontProvider.cs`), which breaks **IL2CPP player builds** (`CS0117`). Added a non-editor fallback `DefaultAppearance => null` (callers already treat null as "no legacy appearance").

### Steps switched off
- UniText(OpenGlyph) **glyph-rasterization phase** — needs 2.0 SDF `GlyphAtlas`; reported in `errors[]`. (TMP glyph-rasterization still ran on Windows.)

## 4. Results — median of run-medians (lower = faster)

### Windows IL2CPP — Acer Nitro, i5-13420H, **RTX 4050 Laptop**, D3D11, 5 runs (runs 2 & 4 "noisy": another agent's Unity was open; runs 1/3/5 clean)

| System | objectCreation | fullRebuild | layout(wrap) | meshRebuild |
|---|--:|--:|--:|--:|
| **OpenGlyph (single-thread)** | **8.54** | **1.10** | **2.68** | **1.00** |
| **OpenGlyph (parallel)** | **8.32** | 1.15 | 2.63 | 0.98 |
| TextMeshPro | 330.62 | 223.21 | 247.59 | 231.90 |
| UI Toolkit | 401.20 | 361.20 | 46.76 | 45.98 |

All four produced real output on Windows; OpenGlyph rendered visibly (evidence image). **Validity: all VALID on Windows.**

### Quest 3S — **Adreno 740**, IL2CPP, 3 runs, Latin text set

| System | objectCreation | fullRebuild | layout | meshRebuild | Validity |
|---|--:|--:|--:|--:|---|
| **OpenGlyph (parallel off)** | 174.86 | 149.75 | 64.60 | 25.84 | **VALID** — 2241/2245 chars, 8964 verts |
| **OpenGlyph (parallel on)** | 116.81 | 94.56 | 59.38 | 22.06 | **VALID** |
| TextMeshPro | 0 | 0 | 0 | 0 | **INVALID** — 0 chars / 0 verts observed |
| UI Toolkit | 1.57 | 0.21 | 0.06 | 0.09 | **UNVERIFIED** — flag true but 0 verts readable |

**Per-phase validity verdicts:**
- OpenGlyph (both modes, all four text sets Latin/Arabic/Hebrew/Mixed): observed chars ≈ expected and verts = 4×chars across objectCreation/fullRebuild/layout/meshRebuild → **all phases VALID**.
- TMP on Quest: observed chars = 0, verts = 0 → **all phases INVALID** (TMP's dynamic atlas did not populate in this on-device build; its Quest numbers are not trustworthy and are shown as 0). TMP *is* valid on Windows.
- UI Toolkit: the harness cannot read UITK's internal vertex buffer, so its validity flag is set true by exception but there is **no vertex evidence**; its sub-millisecond Quest "times" reflect unmeasured work, not real speed. **Treat as unverified.**

### Allocation / GC (Windows, creation phase, run 1)

| System | managed alloc (creation) | GC gen0 |
|---|--:|--:|
| OpenGlyph (ST) | 10.57 MB | 6 |
| OpenGlyph (Par) | 10.55 MB | 4 |
| TextMeshPro | 557.25 MB | 63 |
| UI Toolkit | 67.16 MB | 0 |

## 5. Reference — upstream author's published UniText 2.0 numbers (their hardware, NOT comparable 1:1)

From the harness's committed `BenchmarkReport.html` (platform/editor unspecified; **upstream's own hardware**, measuring **UniText 2.0**, not OpenGlyph). Reference only:

| Phase (report order) | UniText 2.0 (upstream) | TMP (upstream) | UI Toolkit (upstream) |
|---|--:|--:|--:|
| 1 (object creation) | 217 | 542 | 725 |
| 2 (rebuild) | 75 | 275 | 333 |
| 3 (layout) | 58 | 666 | 142 |
| 4 (mesh rebuild) | 33 | 167 | 50 |

These are a different engine (UniText 2.0 with the SDF rewrite) on unknown hardware; use only to sanity-check the *shape* (text engine ≪ TMP ≪/≈ UITK on creation), not the absolute ms.

## 6. Evidence
- Windows render frame (OpenGlyph drawing Latin/Arabic/Hebrew/markup): `D:\OpenGlyphWork\scratch\lse-bench\evidence\win_render_d3d11.png`
- Raw per-run JSON (copied into this docs folder): `./raw/win_run1..5.json`, `./raw/quest_run1..3.json`
- Build/run logs: `D:\OpenGlyphWork\scratch\lse-bench\logs\`

## 7. Open issues
1. **OpenGlyph `e8efa75` does not compile for IL2CPP player builds** out of the box: `UniTextSettings.DefaultAppearance` is editor-only but referenced from runtime (`CS0117`). Worth fixing in OpenGlyph itself. (Patched locally in the scratch package copy to let the benchmark build.)
2. **TMP renders nothing on Quest in this harness** (0 glyphs) — its on-device numbers are invalid; needs a TMP font-atlas warmup for a fair mobile comparison.
3. **UI Toolkit work is not measurable** by this harness (no vertex readout) — its numbers are unverified on both platforms; a UITK-aware mesh probe is needed.
4. The harness's **UniText glyph-rasterization phase** is 2.0-SDF-specific and was disabled for OpenGlyph; a 1.0-compatible rasterization benchmark would be needed to cover it.
5. Upstream published numbers lack hardware/editor metadata, so the reference column cannot be aligned to ours.
