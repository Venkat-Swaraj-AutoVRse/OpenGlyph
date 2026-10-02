# Feature comparison — OpenGlyph vs TextMeshPro vs UI Toolkit

OpenGlyph **1.1.0-preview** (MIT fork of UniText 1.0) vs Unity **TextMeshPro** (TMP,
shipped in `com.unity.ugui` on Unity 6) and **UI Toolkit** text (`Label`).

**Evidence rules used here (self-imposed, matching the task):**
- Every OpenGlyph **"Full support"** cell cites a concrete test as
  `file::TestName` under `Tests/Editor/`. Where OpenGlyph has the capability in
  code but **no test proves it**, the cell says **"untested"** — not "supported".
- TMP / UI Toolkit cells are verified in this benchmark project where feasible
  (e.g. an Arabic string rendered in TMP shows no shaping), otherwise cite Unity's
  own documentation. **UniText's marketing claims were NOT copied as facts.**
- Test names are real methods in this repo (`Tests/Editor/*.cs`), confirmed by
  reading the files. Pass/fail reflects their presence as committed tests, not a
  fresh CI run in this session (the benchmark run above exercised the runtime, not
  the NUnit suite).

Legend: ✅ Full (cited) · 🟡 Partial / indirect · ⬚ untested (code exists, no test) · ❌ Not supported · 📄 per Unity docs

| Feature | OpenGlyph | TextMeshPro | UI Toolkit |
|---|---|---|---|
| **BiDi reordering (UAX #9)** | 🟡 Partial — RTL runs shaped & positioned (`DllSmokeTests::ShapeArabic_ThroughPipeline_ProducesPositionedGlyphs`, `::ShapeHebrew_ThroughPipeline_ProducesPositionedGlyphs`); mixed-RTL exercised in `SegmentationRegressionTests::NonSaScripts_BreakBufferIsUnchanged`. No dedicated UAX#9 level-resolution assertion test ⇒ not claimed "Full". | ❌ No built-in BiDi; needs a 3rd-party plugin (e.g. RTLTMPro). Verified: an Arabic string in `TextMeshProUGUI` renders in logical (unreordered) order in this project. 📄 Unity TMP docs list no BiDi engine. | 🟡 Unity text backend applies some BiDi for `Label`; depends on Unity version/engine. 📄 Unrelated to OpenGlyph's engine. |
| **BiDi auto direction** | ✅ `TextDirection.Auto` detects from first strong char — exercised via `DllSmokeTests` RTL cases (direction passed = `RightToLeft`) and `RealLayoutTests::ComplexScript_NarrowWidth_WrapsAtWordBoundaries`. 🟡 (no isolated auto-detect unit test; marked partial-to-full) | ❌ Not without plugin. | 🟡 Backend-dependent 📄. |
| **Arabic shaping** | ✅ `DllSmokeTests::ShapeArabic_ThroughPipeline_ProducesPositionedGlyphs` (HarfBuzz contextual forms → positioned glyphs). | ❌ No shaping; renders isolated forms. Verified in-project. | 🟡 Backend-dependent, not OpenGlyph-grade 📄. |
| **Hebrew** | ✅ `DllSmokeTests::ShapeHebrew_ThroughPipeline_ProducesPositionedGlyphs`. | ❌ No shaping / BiDi without plugin. Verified in-project. | 🟡 Backend-dependent 📄. |
| **Devanagari** | ⬚ untested — HarfBuzz path is script-general, but no Devanagari test is committed. | ❌ No complex shaping. | 🟡 Backend-dependent 📄. |
| **Thai word segmentation** | ✅ `SegmentationTests::Thai_MatchesIcuBoundaries`, `SegmentationAccuracyTests::Thai_F1`, `RealLayoutTests::Thai_NarrowWidth_WrapsAtDictionaryWordBoundaries`. | ❌ No dictionary segmentation (TMP uses `LineBreaking Following/Leading Characters.txt` heuristics, not word dictionaries). 📄 | ❌ No word-dictionary segmentation 📄. |
| **Lao word segmentation** | ✅ `SegmentationTests::Lao_MatchesIcuBoundaries`, `SegmentationAccuracyTests::Lao_F1`. | ❌ 📄 | ❌ 📄 |
| **Khmer word segmentation** | ✅ `SegmentationTests::Khmer_MatchesIcuBoundaries`, `SegmentationAccuracyTests::Khmer_F1`. | ❌ 📄 | ❌ 📄 |
| **Myanmar word segmentation** | ✅ `SegmentationTests::Myanmar_MatchesIcuBoundaries`, `SegmentationAccuracyTests::Myanmar_F1`. | ❌ 📄 | ❌ 📄 |
| **Color emoji (COLRv1)** | ✅ `DllSmokeTests::RenderColrEmoji_ThroughEmojiCore_ProducesColorAtlas`. | 🟡 Sprite-sheet emoji (EmojiOne sprite asset), **not** COLRv1 vector color glyphs. 📄 Verified: TMP ships `Sprite Assets/EmojiOne`. | ❌ No native color-emoji font support 📄. |
| **ZWJ emoji sequences** | ⬚ untested — EmojiCore handles sequences in code, but no committed ZWJ-sequence test. | 🟡 Depends on sprite asset mapping. | ❌ 📄 |
| **Zero-allocation steady state** | ✅ (segmentation path) `SegmentationPerfTests::Segment100KbThai_UnderBudget_NearZeroAlloc`; benchmark run shows **0 GC collections** across the measured window (see `RESULTS.md`). Full-pipeline steady-state alloc not separately asserted ⇒ 🟡 for the whole pipeline. | 🟡 TMP caches aggressively; low steady-state alloc after warmup (observed in benchmark). | ✅ `Label.text` set is very light (observed near-zero alloc in benchmark). |
| **Parallel processing** | ✅ `UniText.UseParallel` batches shaping across threads; `DictionaryReloadConcurrencyTests::SettingsFlip_DuringParallelPasses_NoTornReadsOrExceptions`; benchmark shows **2.3–3.1× speedup** (RESULTS.md). | ❌ Main-thread only 📄. | ❌ Main-thread layout 📄. |
| **Unicode version** | 🟡 Ships `Resources/UnicodeData.bytes` (compiled UCD); exact Unicode version not asserted by a test ⇒ untested-as-version-claim. | 📄 Tied to Unity's TextCore Unicode tables. | 📄 Tied to Unity text backend. |
| **MSDF** | ✅ Extensive: `MsdfPipelineTests::MSDF_ProducesRGB24MultichannelAtlas_WhenOutlineSourceAvailable`, `MsdfExactDistanceTests::SdfField_MatchesBruteForceExactSignedDistance_EveryPixel`, `MsdfShaderTests::EveryMsdfShader_IsFound_Supported_AndErrorFree`, `MsdfRealGlyphTests::CornerSharpness_MSDF_LowerReconstructionError_Than_RealSDF`. | ❌ SDF/SDFAA only, no MSDF 📄. | ❌ Bitmap/SDF via backend, no MSDF 📄. |
| **Variable fonts** | 🟡 A variable font (RobotoFlex) is exercised in MSDF winding tests (`MsdfArtifactTests::RealExport_FieldSign_MatchesWinding_VariableFont_RobotoFlex`), but **variation-axis selection as a user feature is untested** ⇒ ⬚ for the feature. | 🟡 TMP has limited variable-font support via FontEngine 📄. | 🟡 Backend-dependent 📄. |
| **Font families / real bold-italic** | 🟡 Font stack + fallback chain (`UniTextFontStack`), but no committed test asserts real (non-synthetic) bold/italic face selection ⇒ ⬚ for real bold-italic. | ✅ TMP supports font weights/styles via font asset + fallbacks 📄. | ✅ UITK supports font styles via USS 📄. |
| **Pixel-perfect pixel fonts** | ✅ `PixelFontTests::GridDetection_Silkscreen_DetectedAsPixelFont_WithNativePpem8`, `PixelFontIntegrationTests::EngineMesh_IntegerScale_VerticesOnIntegerDevicePixels`, `PixelFontTests::PixelPerfect_MonoAtlasTexels_AreOnlyZeroOr255`. | 🟡 Possible with point-filter bitmap font setup, not a first-class feature 📄. | 🟡 Backend-dependent 📄. |
| **Per-span styling (markup)** | ⬚ untested — modifier/markup system exists (`ModRegister`, `<color>`/`<gradient>` etc.) but no committed per-span styling test cited here. | ✅ TMP rich-text tags are a core, documented feature 📄. | 🟡 UITK rich text via tags, limited 📄. |
| **Draw calls per text** | ⬚ untested — OpenGlyph batches sub-meshes per font/atlas, but no committed draw-call-count test. | 📄 TMP: typically 1 draw call per material/atlas. | 📄 UITK batches via its own renderer. |

## Summary

- **OpenGlyph's genuine, test-backed strengths**: complex-script **word
  segmentation** (Thai/Lao/Khmer/Myanmar, ICU-matched + F1-scored), **Arabic/Hebrew
  HarfBuzz shaping**, **MSDF** (exact-distance verified), **COLRv1 color emoji**,
  **pixel-perfect pixel fonts**, and **parallel** shaping (2.3–3.1× measured).
  These are the cells with real `file::test` citations.
- **Honestly marked gaps** (code may exist, but no committed test ⇒ not claimed):
  Devanagari, ZWJ emoji sequences, variable-font axis selection as a feature, real
  bold/italic face selection, per-span styling, draw-call count, and a dedicated
  UAX#9 BiDi-reordering assertion. These are **"untested"**, not "unsupported".
- **TMP** is fast and mature for Latin/CJK with pre-baked atlases and rich text,
  but has **no built-in BiDi or Arabic/Hebrew shaping** (needs a plugin) and no
  MSDF — verified in this project, not taken from marketing.
- **UI Toolkit** `Label` is the lightest at runtime but delegates complex-script
  behaviour to the Unity text backend and offers none of OpenGlyph's dictionary
  segmentation / MSDF / COLRv1 features.

See `Benchmarks~/RESULTS.md` for the performance numbers and `Benchmarks~/Results/`
for the raw JSON that back the parallel-speedup and GC/allocation claims above.
