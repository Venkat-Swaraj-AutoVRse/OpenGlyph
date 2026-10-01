# OpenGlyph benchmark results (valid runs only)

OpenGlyph **1.1.0-preview** (MIT fork of UniText 1.0) vs **TextMeshPro** and **UI Toolkit**.
Methodology follows UniText's published methodology — **100 objects, 10 iterations,
3 warmups, ~2300 chars/object** (Latin + Arabic + Hebrew + Mixed), `UseParallel=false`
— **plus** a parallel-ON OpenGlyph pass. Fixed random seed 12345.

> **A VALIDITY GATE now guards every number.** After each timed phase the runner
> verifies each of the 100 objects actually produced output *inside the timed
> window*: OpenGlyph `ResultGlyphs.Length>0 && ResultSize.y>0`; TMP
> `textInfo.characterCount>0 && meshInfo[0].vertexCount>0` after
> `ForceMeshUpdate(true,true)`; UI Toolkit `MeasureTextSize>0`. A phase that fails
> is reported **INVALID** and its timing is NOT presented as a real measurement.
> This is why the earlier "TMP 5.7 ms / UITK 0.4 ms" numbers are gone — they were
> not real text generation.

Raw JSON: `Benchmarks~/Results/windows_il2cpp_results.json`,
`Benchmarks~/Results/quest3s_il2cpp_results.json`.

## Environment

| | Windows | Quest 3S |
|---|---|---|
| Device | i5-13420H, 32 GB, RTX 4050 | Meta Quest 3S, Adreno 740 |
| OS | Windows 11 (10.0.26200) | Android 14 / API-34 |
| Unity | 6000.3.19f1 | 6000.3.19f1 |
| Backend | **IL2CPP, Release** | **IL2CPP, Release**, ARM64 |
| Incremental GC | on | on |
| Representative | yes | yes |

## VALID — OpenGlyph per-object timings (median ms, 100 objects, 10 iters)

### Windows x64 IL2CPP Release

| System | Text set | Creation (med / p95) | Full Rebuild | Layout | Mesh | gen0 GC | heap Δ (KB) | valid |
|---|---|---|---|---|---|---|---|---|
| OpenGlyph OFF | Latin  | 89.1 / 94.3  | 79.6  | 28.8 | 15.9 | 50 | 155,052 | ✅ |
| OpenGlyph OFF | Arabic | 145.8 / 157.2| 138.5 | 27.3 | 21.7 | 5  | 35,292  | ✅ |
| OpenGlyph OFF | Hebrew | 99.2 / 103.5 | 91.1  | 21.2 | 16.3 | 6  | 39,624  | ✅ |
| OpenGlyph OFF | Mixed  | 99.7 / 103.1 | 93.1  | 27.4 | 20.6 | 2  | —       | ✅ |
| OpenGlyph ON  | Latin  | 36.0 / 37.3  | 31.2  | 22.7 | 12.2 | 1  | 13,644  | ✅ |
| OpenGlyph ON  | Arabic | 48.7 / 52.1  | 42.0  | 19.5 | 16.5 | 1  | 7,172   | ✅ |
| OpenGlyph ON  | Hebrew | 35.4 / 39.1  | 30.3  | 13.8 | 10.3 | 1  | 2,464   | ✅ |
| OpenGlyph ON  | Mixed  | 38.5 / 39.1  | 33.0  | 18.0 | 14.4 | 1  | 3,448   | ✅ |

### Quest 3S IL2CPP Release (Adreno 740)

| System | Text set | Creation (med) | Full Rebuild | gen0 GC | valid |
|---|---|---|---|---|---|
| OpenGlyph OFF | Latin  | 181.8 | 150.5 | 59 | ✅ |
| OpenGlyph OFF | Arabic | 323.5 | 285.8 | 6  | ✅ |
| OpenGlyph OFF | Hebrew | 225.6 | 199.0 | 6  | ✅ |
| OpenGlyph OFF | Mixed  | 224.2 | 197.6 | 1  | ✅ |
| OpenGlyph ON  | Latin  | 118.0 | 97.2  | 2  | ✅ |
| OpenGlyph ON  | Arabic | 153.5 | 128.6 | 1  | ✅ |
| OpenGlyph ON  | Hebrew | 114.9 | 92.1  | 1  | ✅ |
| OpenGlyph ON  | Mixed  | 123.7 | 96.4  | 2  | ✅ |

**Parallel speedup (creation):** Windows ~2.5× (Latin 89→36, Arabic 146→49);
Quest ~1.5–2.1× (Latin 182→118, Arabic 324→154). The weaker Quest scaling reflects
fewer/slower mobile cores. Parallel also **collapses GC pressure**: Windows Latin
drops from 50 gen0 collections (155 MB heap growth) to 1 collection (13.6 MB).

## INVALID — not presented as measurements

| System | Status | Reason |
|---|---|---|
| **TextMeshPro** (all sets, both platforms) | ❌ INVALID | `TMP_Settings` resolves null (`Resources.Load<TMP_Settings>("TMP Settings")` returns null in editor AND player, even with the essentials committed under `Assets/TextMesh Pro/Resources`, `link.xml` + Minimal stripping, and the matching script GUID). `TextMeshProUGUI.Awake` / `TMP_FontAsset.CreateFontAsset` therefore NRE on `TMP_Settings.get_clearDynamicDataOnBuild`. The runner catches this and marks every TMP row INVALID rather than timing an empty mesh. **TMP's settings pointer is not registered in this fresh project** — fixing it needs the TMP settings asset wired via Project Settings → TextMesh Pro (an interactive step), which was out of reach in batchmode within the time box. |
| **UI Toolkit** (all sets) | ⚠️ SUSPECT | `MeasureTextSize` returns 0 width even with a default runtime theme assigned, so the ~0.4 ms times do **not** represent real glyph generation (UI Toolkit generates text lazily during panel repaint, which the harness could not force to completion here). Reported as not-validly-generating, not as a real comparison. |

**Consequence:** there is **no valid TMP or UI Toolkit timing** to compare against
in this round. The earlier "OpenGlyph is 9–21× slower than TMP" claim is **withdrawn**
— it rested on TMP numbers that were not real generation. What is valid is the
OpenGlyph-vs-OpenGlyph parallel comparison and the glyph-raster figure below.

## VALID — glyph rasterization (add 200 glyphs to the atlas)

| Engine | Platform | Glyphs | ms/glyph | valid |
|---|---|---|---|---|
| OpenGlyph FreeType (SDF pack) | Windows IL2CPP | 200 | **0.279** | ✅ |
| OpenGlyph FreeType (SDF pack) | Quest 3S | 200 | **0.476** | ✅ |
| Unity FontEngine (TMP dynamic) | both | 0 | — | ❌ `TryAddCharacters`/`CreateFontAsset` NRE (same TMP_Settings issue) |

The round-1 device bug (`font path not found`) is **fixed**: fonts now load as
`Resources` TextAssets (`Assets/Benchmarks/Resources/Fonts/*.bytes`), readable
inside the APK where `StreamingAssets`/`File.*` is not.

## OpenGlyph — where the time goes (per-object Stopwatch splits, Windows IL2CPP)

| Text set | shape+raster | layout | mesh | total (ms/obj) | parallel |
|---|---|---|---|---|---|
| Latin  | 0.65 | 0.35 | 0.19 | 1.19 | OFF |
| Latin  | 0.52 | 0.30 | 0.15 | 0.97 | ON |
| Arabic | 1.18 | 0.35 | 0.36 | 1.89 | OFF |

**Shaping + first-time rasterization dominates** (≈55–62% of per-object cost),
layout and mesh generation are the minority. For the rendering phase: the biggest
lever is the shaping/raster path (HarfBuzz + FreeType SDF), especially on Arabic
where contextual shaping roughly doubles the shape cost vs Latin. (`shapeMsPerObj =
total − layout − mesh`; layout and mesh are isolated re-dirties, so "shape+raster"
also absorbs any first-pass atlas population.)

## Allocation / GC notes (honest limits)

- `GC.GetAllocatedBytesForCurrentThread()` returns **0 under IL2CPP** (not
  implemented), so exact per-op KB is unavailable on these runs. Instead we report
  **GC collection-count deltas (gen0/1/2)** and **`GC.GetTotalMemory` heap-growth
  deltas**, which are valid under IL2CPP and tell the real story: parallel-off Latin
  = 50 gen0 collections / 155 MB heap growth vs parallel-on = 1 / 13.6 MB.
- Incremental GC was **on** for all runs (recorded in the JSON).
- A negative heap delta (e.g. Mixed OFF) means a GC ran mid-phase; treat small/negative
  values as "below the noise floor".

## What ran where

| Run | Status | Backend |
|---|---|---|
| Windows x64 standalone player | ✅ built + ran, OpenGlyph valid | IL2CPP/Release |
| Quest 3S APK | ✅ built, installed, ran, pulled (round 2) | IL2CPP/Release ARM64 |
| Editor play-mode | smoke only (not representative) | IL2CPP editor |

## Still invalid / open

- **TMP**: blocked by the TMP_Settings-null issue above; needs the settings asset
  registered via Project Settings (interactive). Until then, no valid TMP numbers.
- **UI Toolkit**: `MeasureTextSize` returns 0; needs a forced panel repaint/layout
  with a resolved font to generate text before it can be validly timed.
- A clean OpenGlyph-vs-TMP comparison therefore remains **pending** a working TMP.

## Reproduce

```
powershell -ExecutionPolicy Bypass -File Benchmarks~\Tools~\run-benchmarks.ps1 -Do windows
powershell -ExecutionPolicy Bypass -File Benchmarks~\Tools~\run-android.ps1   # on a machine with the Quest + adb
```
Unity **6000.3.19f1**. All heavy build output is directed to D: (TEMP/TMP/UPM_CACHE_ROOT);
`Benchmarks~/Library` is a junction to D:.
