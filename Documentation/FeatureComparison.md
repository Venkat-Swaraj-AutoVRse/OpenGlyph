# Feature comparison — OpenGlyph vs TextMeshPro vs UI Toolkit

OpenGlyph (`com.openglyph.text`, MIT fork of UniText 1.0) vs Unity **TextMeshPro** (TMP,
shipped in `com.unity.ugui` on Unity 6) and **UI Toolkit** text (`Label`).

**Evidence rules:**
- Every OpenGlyph **✅** cell cites a committed test as `file::TestName` under `Tests/Editor/`
  (conformance runners under `Tests/Editor/Conformance/`). Where OpenGlyph has the capability in
  code but **no test proves it**, the cell is **⬚ untested** — not "supported".
- TMP / UI Toolkit cells come only from in-project observation (named) or Unity's own
  documentation (📄). UniText / competitor marketing is not used as evidence.
- Benchmark-backed statements refer to the Quest 3S VR run in
  [`Benchmarks/quest3s/`](Benchmarks/quest3s/SUMMARY_main_2c71ef6.md) (main `2c71ef6`).

Legend: ✅ Full (cited) · 🟡 Partial / indirect · ⬚ untested (code exists, no test) · ❌ Not supported · 📄 per Unity docs

| Feature | OpenGlyph | TextMeshPro | UI Toolkit |
|---|---|---|---|
| **BiDi reordering (UAX #9)** | ✅ 100 % of the Unicode 17.0.0 BiDi conformance data, 0 exclusions: `Conformance/BidiConformanceTests::BidiTest_Unicode17` (770,241 cases), `::BidiCharacterTest_Unicode17` (91,707 cases). Results: `Conformance~/results/UAX9-*.json`. | ❌ No built-in BiDi; needs a 3rd-party plugin (e.g. RTLTMPro). Verified: an Arabic string in `TextMeshProUGUI` renders in logical (unreordered) order in this project. 📄 TMP docs list no BiDi engine. | 🟡 Unity text backend applies some BiDi for `Label`; depends on Unity version/engine. 📄 |
| **BiDi auto direction** | 🟡 `TextDirection.Auto` detects from the first strong character; exercised indirectly by the UAX #9 suite (paragraph level "auto" cases in `BidiTest.txt`) and `RealLayoutTests::ComplexScript_NarrowWidth_WrapsAtWordBoundaries`. No isolated component-level auto-detect test. | ❌ Not without plugin. | 🟡 Backend-dependent 📄. |
| **Arabic shaping** | ✅ `DllSmokeTests::ShapeArabic_ThroughPipeline_ProducesPositionedGlyphs` (HarfBuzz contextual forms → positioned glyphs). | ❌ No shaping; renders isolated forms. Verified in-project. | 🟡 Backend-dependent 📄. |
| **Hebrew** | ✅ `DllSmokeTests::ShapeHebrew_ThroughPipeline_ProducesPositionedGlyphs`. | ❌ No shaping / BiDi without plugin. Verified in-project. | 🟡 Backend-dependent 📄. |
| **Devanagari** | ✅ Conjunct shaping through the real HarfBuzz pipeline: `ComplexScriptClusterTests::Devanagari_KshaConjunct_ShapesToConjunctGlyph` (क्ष → fewer glyphs than codepoints, GSUB conjunct glyph, no standalone virama), `::Devanagari_SteConjunct_ShapesToHalfFormAndMatra` (स्ते). | ❌ No complex shaping. | 🟡 Backend-dependent 📄. |
| **Thai word segmentation** | ✅ `SegmentationTests::Thai_MatchesIcuBoundaries`, `SegmentationAccuracyTests::Thai_F1`, `RealLayoutTests::Thai_NarrowWidth_WrapsAtDictionaryWordBoundaries`. | ❌ No dictionary segmentation (TMP uses `LineBreaking Following/Leading Characters.txt` heuristics, not word dictionaries). 📄 | ❌ No word-dictionary segmentation 📄. |
| **Lao word segmentation** | ✅ `SegmentationTests::Lao_MatchesIcuBoundaries`, `SegmentationAccuracyTests::Lao_F1`. | ❌ 📄 | ❌ 📄 |
| **Khmer word segmentation** | ✅ `SegmentationTests::Khmer_MatchesIcuBoundaries`, `SegmentationAccuracyTests::Khmer_F1`. | ❌ 📄 | ❌ 📄 |
| **Myanmar word segmentation** | ✅ `SegmentationTests::Myanmar_MatchesIcuBoundaries`, `SegmentationAccuracyTests::Myanmar_F1`. | ❌ 📄 | ❌ 📄 |
| **System font fallback (any script)** | ✅ Any script the font stack does not cover uses an installed system font, with one warning per script: `SystemFontFallbackTests::UncoveredScripts_UseSystemFont_NoNotdef_OneWarningPerScript` (Tamil → Nirmala UI, Georgian → Sylfaen, Ethiopic → Ebrima on Windows; no .notdef; exactly one warning per script across components and rebuilds), `::ScriptWithNoSystemFont_WarnsOnceAsMissing_CachesNegative`, `::ParallelPath_MatchesSerial`, `::UncoveredScript_SettingOff_TofuAndNoSystemFont`, `::CoveredLatinText_NoSystemFont_NoWarning`, `::AndroidFontsXml_MapsScriptTagsToFiles`. CJK: `::Cjk_UsesSystemFont_NoNotdef`, `::SettingOff_StaysTofu`, `::NonCjkText_NeverLoadsSystemFont`; CJK also observed on Quest 3S (`.github/assets/quest3s_stereo_scripts.png`). Non-CJK scripts are tested on Windows only. See [SystemFontFallback.md](SystemFontFallback.md). | 🟡 Requires authoring fallback font assets; a TMP font asset without coverage for a script renders tofu (observed for CJK in the Quest 3S capture, TMP column). 📄 | 📄 Not verified in this project. |
| **Color emoji (COLRv1)** | ✅ `DllSmokeTests::RenderColrEmoji_ThroughEmojiCore_ProducesColorAtlas`. | 🟡 Sprite-sheet emoji (EmojiOne sprite asset), **not** COLRv1 vector color glyphs. 📄 Verified: TMP ships `Sprite Assets/EmojiOne`. | ❌ No native color-emoji font support 📄. |
| **ZWJ emoji sequences** | ✅ A ZWJ family sequence is one grapheme cluster: `ComplexScriptClusterTests::ZwjFamilyEmoji_IsSingleGraphemeCluster`, `::ZwjFamilyEmoji_BetweenLetters_IsThreeClusters`; UAX #29 conformance `Conformance/GraphemeBreakConformanceTests::GraphemeBreakTest_Unicode17` (766 cases, 100 %). | 🟡 Depends on sprite asset mapping. | ❌ 📄 |
| **Unicode version** | ✅ **17.0.0**: `UnicodeDataVersionTests::ShippedUnicodeData_ReportsVersion_17_0_0` (shipped `UnicodeData.bytes` header), and all conformance suites run against the 17.0.0 UCD test files (`Conformance~/ucd-17.0.0/`). | 📄 Tied to Unity's TextCore Unicode tables. | 📄 Tied to Unity text backend. |
| **Per-span styling (markup)** | ✅ `SpanStyleTests::EndToEnd_PerSpanOutline_OneRenderer_DistinctStyleIdxPerGlyph`, `::Nesting_InnerLayersOverOuter_PerField`, `::StyleSheet_WholeStyle_ThenFieldOverride`. Per-span outline/underlay styles require the unified renderer (`::LegacyRenderer_SpanStyles_WarnsExactlyOncePerComponent`). | ✅ TMP rich-text tags are a core, documented feature 📄. | 🟡 UITK rich text via tags, limited 📄. |
| **Overflow: Truncate / Ellipsis / Clip** | ✅ `OverflowModeTests::Truncate_KeepsExactlyTheFittingLines`, `::Ellipsis_LastGlyphIsEllipsis_AndLineFitsWidth`, `::Ellipsis_NeverSplitsGraphemeCluster`, `::Ellipsis_Rtl_GoesAtLogicalEnd_VisualLeft`, `::Clip_EnablesRectClipping_WithTheComponentWorldRect`. | ✅ Overflow modes incl. Ellipsis, Truncate, Masking, Page, Linked 📄. | ✅ `text-overflow: ellipsis` / clip via USS 📄. |
| **Masks (Mask / RectMask2D)** | ✅ `UnifiedMaskStencilTests::UnifiedRenderer_TextInsideStencilMask_SurvivesSharedBindingChange` (stencil Mask), `OverflowModeTests::Clip_WithParentRectMask2D_UsesTheIntersection` (RectMask2D). | ✅ Works with uGUI Mask / RectMask2D 📄. | ✅ `overflow: hidden` clipping 📄. |
| **VR single-pass instanced stereo** | ✅ `ShaderStereoLintTests::EveryVertexFragmentShader_DeclaresStereoMacros` + on-device capture on Meta Quest 3S (OpenXR, single-pass instanced, Vulkan), both eyes: `.github/assets/quest3s_stereo_scripts.png`, `Documentation/Design/evidence/quest/quest3s_stereo_page{0,4,5}.png`. | ✅ Observed rendering in both eyes in the same Quest 3S capture (TMP column). | 📄 Not verified in this project. |
| **Zero-allocation steady state** | 🟡 Low, not zero. Quest 3S raw runs: layout and mesh-rebuild phases allocate ≈ 5–7 KB per 10-iteration phase with **0 GC collections**; full rebuild ≈ 41 MB, 0 GCs (`Benchmarks/quest3s/ua_91a39f0_vr_run{1,2,3}.json`). Segmentation path: `SegmentationPerfTests::Segment100KbThai_UnderBudget_NearZeroAlloc`. | 🟡 Layout/mesh phases < 1.2 KB, but full rebuild ≈ 14.8 MB with 3–4 GCs (same raw runs). | 🟡 Layout/mesh phases measured at 0–120 bytes, 0 GCs (same raw runs), but the harness cannot verify UITK actually re-laid-out, so not claimed as full. |
| **Parallel processing** | ✅ `ParallelPipelineThreadingTests::ParallelMatchesSerial_ByteIdentical_NoErrors`, `DictionaryReloadConcurrencyTests::SettingsFlip_DuringParallelPasses_NoTornReadsOrExceptions`. Quest 3S: creation 307.4 → 168.2 ms, full rebuild 247.6 → 142.9 ms (1-thread → parallel). | ❌ Main-thread only 📄. | ❌ Main-thread layout 📄. |
| **MSDF** | ✅ `MsdfPipelineTests::MSDF_ProducesRGB24MultichannelAtlas_WhenOutlineSourceAvailable`, `MsdfExactDistanceTests::SdfField_MatchesBruteForceExactSignedDistance_EveryPixel`, `MsdfShaderTests::EveryMsdfShader_IsFound_Supported_AndErrorFree`, `MsdfRealGlyphTests::CornerSharpness_MSDF_LowerReconstructionError_Than_RealSDF`. | ❌ SDF/SDFAA only, no MSDF 📄. | ❌ Bitmap/SDF via backend, no MSDF 📄. |
| **Variable fonts** | ✅ `VariableFontTests::ReadsFvarAxesAndNamedInstances`, `::FreeType_Weight400_vs_700_DifferentOutlineAndAdvance`, `::HarfBuzz_Weight400_vs_700_DifferentShapingAdvance`. | 🟡 Limited variable-font support via FontEngine 📄. | 🟡 Backend-dependent 📄. |
| **Font families / real bold-italic** | ✅ `MarkupRealFaceTests::Markup_Bold_SelectsRealBoldFace_NoSynthesis`, `::Markup_BoldItalic_SelectsRealBoldItalicFace_NoSynthesis`, `FontStackFamilyTests::Stack_WithFamily_BoldItalic_SelectsRealBoldItalic_NoSynthesis`. | ✅ Font weights/styles via font asset + fallbacks 📄. | ✅ Font styles via USS 📄. |
| **Pixel-perfect pixel fonts** | ✅ `PixelFontTests::GridDetection_Silkscreen_DetectedAsPixelFont_WithNativePpem8`, `PixelFontIntegrationTests::EngineMesh_IntegerScale_VerticesOnIntegerDevicePixels`, `PixelFontTests::PixelPerfect_MonoAtlasTexels_AreOnlyZeroOr255`. | 🟡 Possible with point-filter bitmap font setup, not a first-class feature 📄. | 🟡 Backend-dependent 📄. |
| **Font compression** | ✅ `FontCompressionTests::RoundTrip_IsByteExact`, `::RealFont_Compresses_And_UniTextFont_DecompressesTransparently`, `::DefaultCodec_IsDeflate_DecodableEverywhere`. Measured: NotoSans 629,024 → 296,411 bytes (−52.9 %). | ❌ No font-data compression 📄. | ❌ 📄 |
| **Draw groups per component (unified renderer)** | ✅ `UberDrawGroupTests::TextOnly_ThreeSdfFonts_OneDrawGroup`, `::NeverExceedsTwo_ForAnyMix` (SDF text = 1 draw group; mixing MSDF/emoji = at most 2). | 📄 Typically 1 draw call per material/atlas (fallback atlases add more). | 📄 Batched by the UITK renderer. |

## Summary

- **Test-backed OpenGlyph strengths:** 100 % Unicode 17.0.0 conformance (BiDi, line breaking,
  scripts, grapheme clusters — 1,042,587 cases), HarfBuzz shaping (Arabic, Hebrew, Devanagari),
  dictionary word segmentation (Thai/Lao/Khmer/Myanmar), system CJK fallback, COLRv1 emoji,
  per-span styles, overflow modes, MSDF, variable fonts, real font families, pixel fonts,
  parallel processing, font compression, and VR single-pass-instanced stereo verified on Quest 3S.
- **Honest gaps:** steady-state allocation is low but not zero (TMP and UI Toolkit allocate less in
  the layout/mesh phases; TMP destroys slightly faster, UI Toolkit allocates far less at creation);
  BiDi auto-direction has no isolated component-level test.
- **TMP** is mature for Latin/CJK with pre-baked atlases and rich text, but has no built-in BiDi or
  Arabic/Hebrew shaping and no MSDF — verified in this project.
- **UI Toolkit** `Label` is the lightest at runtime but delegates complex-script behaviour to the
  Unity text backend.
