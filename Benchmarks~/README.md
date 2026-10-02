# OpenGlyph benchmark suite

Self-contained Unity benchmark project comparing **OpenGlyph** vs **TextMeshPro**
vs **UI Toolkit**, plus a feature comparison. Lives under `Benchmarks~/` so the
OpenGlyph package importer ignores it (tilde-suffixed folder).

## Layout

- `Benchmarks~/Packages/manifest.json` — references OpenGlyph via `file:../../`,
  pins `com.unity.ugui@2.0.0` (TMP) + UI Toolkit modules.
- `Benchmarks~/Assets/Benchmarks/`
  - `BenchmarkTexts.cs` — original ~2300-char Latin/Arabic/Hebrew/Mixed sample sets.
  - `BenchmarkResults.cs` — result DTOs + median/p95 stats + JSON.
  - `BenchmarkRunner.cs` — the MonoBehaviour that measures everything.
  - `Editor/BenchBuild.cs` — `-executeMethod` targets (BuildScene/EditorSmoke/
    BuildWindows/BuildAndroid, + guarded `*CI` variants that always Exit).
- `Benchmarks~/Assets/StreamingAssets/Fonts/` — Noto Sans/Arabic/Hebrew (OFL).
- `Benchmarks~/Tools~/run-benchmarks.ps1` — editor smoke + Windows build+run + Android.
- `Benchmarks~/Tools~/run-android.ps1` — adb install/run/pull on your device.
- `Benchmarks~/RESULTS.md` — tables, environment, charts, honest caveats.
- `Benchmarks~/Results/*.json` — committed raw results.
- `Documentation/FeatureComparison.md` — OpenGlyph vs TMP vs UI Toolkit, cited.

## Run

```powershell
# Representative Windows player (IL2CPP if the module is installed, else Mono):
pwsh Benchmarks~/Tools~/run-benchmarks.ps1 -Do windows
# Editor smoke (fast, NOT representative):
pwsh Benchmarks~/Tools~/run-benchmarks.ps1 -Do smoke
# Android APK (build only; run on device):
pwsh Benchmarks~/Tools~/run-benchmarks.ps1 -Do android
pwsh Benchmarks~/Tools~/run-android.ps1    # on a machine with the device + adb
```

Requires Unity **6000.3.19f1**. For an **IL2CPP** Windows run, install *Windows
Build Support (IL2CPP)* via Unity Hub (this host had only the Mono module).

See `RESULTS.md` for what ran where and the measured numbers (short version:
OpenGlyph is 9–21× slower than TMP on per-object throughput but does real
BiDi/shaping TMP cannot; parallel mode is 2.3–3.1× faster than serial).
