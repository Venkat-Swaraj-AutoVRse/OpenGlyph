# LSE BenchmarkWorkshop — OpenGlyph vs TMP vs UI Toolkit

Benchmarked **OpenGlyph** (MIT fork of UniText 1.0) with the upstream author's own benchmark
harness, on **Windows IL2CPP** and **Quest 3S** (Vulkan, IL2CPP), against **TextMeshPro** and
**UI Toolkit**. The harness is `LightSideKittens/LightSideEcosystem` (**no LICENSE** — used only
locally as a measurement harness; **none of its files are committed here**). OpenGlyph stands in
for the harness's missing private UniText submodule, which is sound because OpenGlyph kept
UniText's assembly GUIDs — the harness's `UniText.Test.asmdef` references OpenGlyph's own
`LightSide.UniText.asmdef` GUID directly.

Date: **2026-10-02**. Unity **6000.3.19f1**, IL2CPP, Release.

> **Workload (identical on both platforms, recorded in every result file's `config` + per-system
> `validity`):** **100 text objects × 2,405 chars/object = 240,500 expected characters**,
> **10 timed iterations** after **3 warmups**. Same runner path, same object count, same text on
> Windows and Quest.

---

## 1. OpenGlyph version benchmarked — and the version caveat

**Benchmarked at OpenGlyph commit `0123788`** (`fix: player builds fail - GetMaterials read
editor-only UniTextSettings.DefaultAppearance`), consumed as a local `file:` UPM package
(`com.openglyph.text` → `D:\OpenGlyphWork\scratch\lse-bench\openglyph-pkg`, a git worktree of the
OpenGlyph repo). Confirmed the package's `Runtime/Core/UniTextSettings.cs` carries the **runtime**
`DefaultAppearance` accessor, i.e. the IL2CPP player-build fix is present (no local source patch
is needed any more — the earlier hand-patch is now upstream).

**Caveat — this is below the nominal `6537bf9`+ floor.** Current `openglyph/main` is `afdc733`.
`0123788` is an ancestor of `6537bf9` but `6537bf9` is **not** an ancestor of `0123788`, so the
benchmark package predates the floor by these commits:

- `52016bf` PR #12 (player-build fix merge — **content already in `0123788`**)
- `6537bf9` PR #13 GlyphMeshPro (new TMP-parity component — **not exercised by this harness**)
- `6aaf5a2` SDF `spreadStrength 0.25 → 0.10` + **regenerated bundled fonts**, `b42fecd`/`ef447d3`
  SDF edge-quality fixes (**visual SDF quality only**)

Why the gap does **not** change these numbers: the missing commits alter SDF *visual quality*
(spread, edge crop) and add an unused component. They do **not** change the shaping/layout/mesh
work or the vertex count per glyph (still one quad = 4 verts/char), which is what fullRebuild /
layout / meshRebuild time. **Timing is representative of current main; the exact SDF pixels are
not.** A re-run on `afdc733` is listed as open issue #1 for anyone who wants pixel-exact currency.

## 2. What the scene measures

`UniText_BenchmarkTest.unity` instantiates 100 text objects and times, per system:
**creation/destruction**, **fullRebuild** (assign text → full shape+layout+mesh), **layout**
(4 wrap/autosize variants), **meshRebuild** (colour-only). Per phase it records median/min/max
frame time, managed alloc, GC gen0/1/2. The OpenGlyph build adds per-system **validity**:
expected vs observed characters and observed vertices (quad mesh ⇒ 4 verts/char), rendered **N/A**
when a system exposes no readable vertex buffer (never faked as 0).

## 3. Correction — every earlier OpenGlyph number in this file was invalid

The previous revision (`9febd7d`) reported OpenGlyph at ~1 ms (Windows) / ~11 ms (Quest) for a
100×2,405-char full rebuild and called the cache suspicion "disproved". **That was wrong.** The
benchmark project had no `Resources/UniTextSettings.asset`, so in player builds OpenGlyph could
not load its Unicode data and **generated no text at all**; the timings only measured assigning
the string. The old validity check compared the *assigned* string length, so it passed anyway.

Fix: the settings asset was added to the scratch project's `Resources/`, and validity now counts
work done **inside the timed frame** (`inFrameGenMeshCalls`, `inFrameGlyphs`, `inFrameVerts`).
Every OpenGlyph row below shows **100/100 mesh generations and ~220k–245k glyphs per timed frame**.
Sanity check: OpenGlyph single-thread is now **2.0–2.2× slower on Quest than Windows**, matching
OpenGlyph's own benchmark (~2×); the invalid numbers had a 10–20× gap.

Product issue raised by this: OpenGlyph fails **silently** (log line, no text) when the settings
asset is missing from a player build. Tracked as open issue #1.

## 3a. Same-text vs different-text (verified runs)

**Suspicion going in:** OpenGlyph looked "too fast" because every iteration re-assigned the *same*
text, so a text-content fast path (the `UniText.Text` setter's `text == value` no-op, or
shaper/layout result reuse) might skip the real work.

**Test added** (`TextBenchmarkBase.uniqueText` + `MakeUniqueText`, driven by a second pass in
`BenchmarkRunner`): a **distinct interior-spliced string per object per iteration**, so no
content fast path can fire, while total length stays within ~1 char of the shared workload
(validity still holds). Results land under `*_unique` keys beside the shared-text keys.

### fullRebuild — median of per-run medians (ms, lower = faster)

| System | win **same** | win **different** | quest **same** | quest **different** |
|---|--:|--:|--:|--:|
| **OpenGlyph (single-thread)** | **148.0** | **160.9** | **304.0** | **328.6** |
| **OpenGlyph (parallel)** | **46.8** | **46.7** | **134.6** | **146.5** |
| TextMeshPro | 228.9 | 250.2 | 501.0 | 542.0 |
| UI Toolkit | 371.5 | N/A¹ | 980.3 | N/A¹ |

¹ UI Toolkit is not re-run in the unique-text pass and is unverified either way (see §5).

**Verdict:** different text costs OpenGlyph **+8–9 %** single-threaded (shaping/glyph reuse is
lost) and ~0–9 % in parallel; TMP pays a similar **+8–9 %**. So repeated text gives OpenGlyph
only a modest advantage in this harness, and the comparison against TMP holds in both modes.

## 4. Full results — median of the 3 run-medians (ms, lower = faster)

Workload on both platforms: 100 objects × 2,405 chars, 10 timed iterations after 3 warmups.

### Windows IL2CPP — Acer Nitro ANV15-51, i5-13420H, RTX 4050, Direct3D11, 3 runs

| System | creation | fullRebuild | fullRebuild (diff text) | layout wrap+auto | meshRebuild |
|---|--:|--:|--:|--:|--:|
| **OpenGlyph (single-thread)** | 172.2 | 148.0 | 160.9 | 61.0 | 28.4 |
| **OpenGlyph (parallel)** | 58.7 | 46.8 | 46.7 | 49.5 | 15.3 |
| TextMeshPro | 322.2 | 228.9 | 250.2 | 735.5 | 236.9 |
| UI Toolkit (unverified) | 411.1 | 371.5 | N/A | 78.4 | 47.9 |

### Quest 3S — Android, Vulkan, IL2CPP, 3 runs (battery 36 % → 24 %, USB-powered)

| System | creation | fullRebuild | fullRebuild (diff text) | layout wrap+auto | meshRebuild |
|---|--:|--:|--:|--:|--:|
| **OpenGlyph (single-thread)** | 375.9 | 304.0 | 328.6 | 144.1 | 49.6 |
| **OpenGlyph (parallel)** | 192.7 | 134.6 | 146.5 | 124.4 | 33.3 |
| TextMeshPro | 743.0 | 501.0 | 542.0 | 1,374.2 | 514.5 |
| UI Toolkit (unverified) | 1,064.5 | 980.3 | N/A | 297.6 | 183.4 |

**Read-out:** on Quest 3S, single-threaded OpenGlyph rebuilds ~1.6× faster than TMP, parallel
~3.7×; auto-size layout is ~9.5× (ST) / ~11× (parallel) faster; colour-only mesh rebuild ~10×.

## 5. Render / work check (per-system evidence)

- **OpenGlyph** — every timed frame: `inFrameGenMeshCalls = 100/100`, `inFrameGlyphs` 220,100
  (Win) / 219,800 (Quest) for shared text and ~244–245k for unique text, `inFrameVerts`
  760–845k; chars 240,500/240,500. **Did the work: yes.**
- **TMP** — chars 238,500 (wrapping/segmentation, within 2 %), **791,600 verts** on Quest.
  In-frame counters are not exposed by TMP. **Rendered: yes.**
- **UI Toolkit** — no readable vertex buffer → **N/A / unverified** (not 0).
- Windows OpenGlyph render frame (Latin/Arabic/Hebrew/markup): `./win_render_d3d11.png`.

## 6. TMP 0-glyph on Quest — root cause and fix (verified on-device)

**Symptom (earlier Quest build):** TMP produced 0 chars / 0 verts on the Quest — nothing drew —
so its mobile numbers were meaningless.

**Root cause:** the benchmark TMP font `NotoSans-Regular SDF.asset` shipped with an **empty glyph
table**, `AtlasPopulationMode = Dynamic`, and `ClearDynamicDataOnBuild = 1`. In a player the atlas
therefore starts empty and must re-rasterize every glyph on-device via FreeType; on the Quest 3S
that path yielded nothing, so TMP rendered 0 glyphs. (OpenGlyph and TMP-on-Windows were unaffected.)

**Fix** (`Assets/Scripts/Editor/LseBenchBuild.PrewarmTmpAtlas`, scratch project only): before the
Android build, bake the glyphs the benchmark needs into the atlas in-editor
(`TryAddCharacters` over printable ASCII + Latin-1) **and** set `ClearDynamicDataOnBuild = false`
so the baked atlas/glyph/character tables **ship** in the APK. The device then renders from the
static atlas with no on-device rasterization dependency.

**Fix bug found and corrected this session:** the first version of `PrewarmTmpAtlas` looked up
`TryAddCharacters` via `GetMethod(name, {string, string&})` — a 2-type array that does **not**
match TMP's real `TryAddCharacters(string, out string, bool includeFontFeatures = false)`
(3 params), so the lookup returned null and **no glyphs were baked** (only the clear-flag flipped).
The 09:54Z APK therefore still shipped an empty atlas. Rewrote the lookup to find the
`(string, out string, …)` overload by shape and supply the optional arg. Rebuild log now reports
`TryAddCharacters ok=True missing='' (params=3)`, and the on-device run confirms **238,500 chars /
791,600 verts** for TMP. **This is why the Quest TMP column is now populated and VALID.**

## 7. Allocation / GC (creation phase, median run)

`managedAlloc` reads 0 for every system in IL2CPP players, so `totalAlloc` is shown. Windows and
Quest agree to within 1 %.

| System | total alloc (creation) | GC collections |
|---|--:|--:|
| OpenGlyph (single-thread) | ~429 MB | 1 |
| OpenGlyph (parallel) | ~430 MB | 1 |
| TextMeshPro | ~557 MB | 31 |
| UI Toolkit | ~67 MB | 0 |

The earlier ~10.6 MB OpenGlyph figure was from the invalid runs (§3).

## 8. Every change made to the author's scene/scripts (scratch project only — NOT committed here)

- Removed the two empty UniText submodules + `.gitmodules`; added OpenGlyph as a `file:` UPM package.
- Added `Assets/Resources/UniTextSettings.asset` (copied from OpenGlyph) — without it OpenGlyph generates nothing in a player (§3).
- `Assets/Scripts/Editor/LseBenchBuild.cs` — **new** build driver (Win64/Android IL2CPP Release) + `PrewarmTmpAtlas` (see §6).
- `BenchmarkWorkshop/TextBenchmarkBase.cs` — added `uniqueText` + `MakeUniqueText` (different-text mode, §3) and per-system `ValidityInfo` capture.
- `BenchmarkWorkshop/BenchmarkRunner.cs` — added the second (unique-text) pass writing `*_unique` keys; guarded the UniText glyph-rasterization phase behind `#if UNITEXT_2X_SDF` (OpenGlyph lacks the 2.0 SDF `GlyphAtlas`/`PrimaryFont` API) and records that in `errors[]`.
- `BenchmarkWorkshop/UniTextBenchmark.cs` — dropped two `GlyphAtlas.ForceSingleThreaded` lines (OpenGlyph governs threading via `UniText.UseParallel`); vertex-count reads OpenGlyph's own `MeshGenerator` with an N/A fallback for IL2CPP.
- Disabled 2.0-only tests (never-defined `#if`): `UniTextInteractiveTest`, `UniText_GlyphRasterizationBenchmark`, `GoldenFileTestRunner`, `COLRv1Test`.
- TMP SDF font asset `NotoSans-Regular SDF.asset` is baked + reflagged by `PrewarmTmpAtlas` at Android build time (local only).

## 9. Reference — upstream's published UniText **2.0** numbers (their hardware; NOT 1:1 comparable)

From the harness's committed `BenchmarkReport.html` (platform/editor unspecified, upstream
hardware, UniText **2.0** not OpenGlyph). Shape-only sanity check (text engine ≪ TMP ≈/≪ UITK):

| Phase | UniText 2.0 | TMP | UI Toolkit |
|---|--:|--:|--:|
| object creation | 217 | 542 | 725 |
| rebuild | 75 | 275 | 333 |
| layout | 58 | 666 | 142 |
| mesh rebuild | 33 | 167 | 50 |

## 10. Evidence
- Raw per-run JSON: `./raw/win_run1..3.json` (Windows, 2026-10-02 12:27–12:31Z) and
  `./raw/quest_run1..3.json` (Quest 3S, 12:51–13:00Z), all with the settings asset present and
  in-frame work counters.
- Windows render frame: `./win_render_d3d11.png`.
- Build/run logs (scratch): `D:\OpenGlyphWork\scratch\lse-bench\logs\`, `...\build\android_build.log`.

## 11. Open issues
1. **OpenGlyph fails silently without `Resources/UniTextSettings.asset` in a player.** Ship a default, or raise an obvious error, so a misconfigured project cannot render nothing unnoticed.
2. **Benchmark package is `0123788`, below current main** (§1). Timing is representative; SDF pixels are not current-main.
3. UI Toolkit vertex output is unreadable by this harness → its numbers are **unverified on both platforms**; needs a UITK-aware mesh probe.
4. The harness's UniText **glyph-rasterization** phase is 2.0-SDF-specific and was disabled for OpenGlyph.
5. OpenGlyph creation allocates ~429 MB total for 100×2,405 chars (1 GC) — worth profiling for the memory-budget work.
