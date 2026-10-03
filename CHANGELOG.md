# Changelog

All notable changes to UniText will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### OpenGlyph 1.1.0-preview

Open-source (MIT) clean-room fork of UniText 1.0. This preview integrates phase 0
through phase 1c.

#### Changed
- **Unified renderer is now the project default** (`UniTextSettings.useUnifiedRenderer = true`, shipped asset and runtime default). Opt out per component with `UnifiedRenderer = ForceOff` or project-wide in settings. Test runs: `UNITEXT_FORCE_UNIFIED=1` / `UNITEXT_FORCE_LEGACY=1` pin each mode.

#### Added
- **Wave 1: typography and integration.**
  - **OpenType features:** `UniText.FontFeatures` (e.g. `tnum`, `onum`, `smcp`, `ss01`, `cv01`, `liga=0`, `-kern`) and the `<feature=…>` tag (`FeatureParseRule` + `FeatureModifier`), passed to HarfBuzz with the span's cluster range; spans override the component list; parser `OpenTypeFeatures`. Docs: `Documentation/OpenTypeFeatures.md`.
  - **Language-aware shaping:** `UniText.Language` (BCP 47) and the `<lang=…>` tag (`LanguageParseRule` + `LanguageModifier`) set the HarfBuzz buffer language (`locl` and other language systems) and pick the CJK system font face per component and per span (ja / ko / zh-Hans / zh-Hant / zh-HK; new Traditional-Chinese face group, `msjh.ttc` on Windows). Unset = previous behaviour. Native: new export `ut_hb_buffer_set_language`; every runtime binary in `Plugins/` rebuilt by CI (118 `ut_*` symbols; provenance in `NativeSource~/tests/BINARIES.md`). `Shaper.SupportsLanguage` reports an older native library. Docs: `Documentation/LanguageShaping.md`.
  - **Content measurement:** `GetMinContentWidth()`, `GetMaxContentWidth()`, `GetHeightForWidth(float)` on UniText (padding/margins and Auto Size min/max included; no change to the rect or the current layout); opt-in `ContentMinWidth` reports min-content as `ILayoutElement.minWidth`. Docs: `Documentation/ContentMeasurement.md`.
  - **Auto Size fit steps:** `UniText.AutoSizeStep` (0 = continuous, the default): the fitted size is the largest multiple of the step that fits. Docs: `Documentation/AutoSizeSteps.md`.
  - **Padding:** `UniText.Padding` (left, top, right, bottom) insets layout and wrapping, adds to the preferred size, insets the Clip rect and link hit testing; on GlyphMeshProUGUI it adds to `margin`. Docs: `Documentation/Padding.md`.
  - **Unity Localization:** optional assembly `OpenGlyph.Localization` (compiles only with `com.unity.localization` ≥ 1.0.0) with `LocalizeUniText`: a `LocalizedString` drives `Text`, the selected locale drives `Language`. Docs: `Documentation/Localization.md`.
  - Tests: `Tests/Editor/Wave1{Feature,Language,Measurement,AutoSizeStep,Padding}Tests.cs`, `Tests/Editor/Localization/LocalizeUniTextTests.cs`.
- **GlyphMeshProUGUI TMP parity, round 3.** The properties that were only stored now work with TMP units and semantics, update at runtime and serialize: `characterSpacing`, `wordSpacing`, `lineSpacing` (negative allowed), `paragraphSpacing` (em/100), `margin`, `richText` (off shows tags literally), `enableVertexGradient`/`colorGradient` (4-corner, multiplied with the colour), `maxVisibleCharacters`/`Words`/`Lines` (hidden glyphs emit no geometry; changing them never reshapes or re-lays out). New: `FontStyles.SmallCaps` (OpenType `smcp` or synthetic 0.8x capitals), `FontStyles.Highlight` + `<mark=#RRGGBBAA>` (box behind the run, both renderers), tags `<nobr> <noparse> <align> <indent> <line-indent> <font="Name">` (`UniTextFontRegistry` lookup), overflow ScrollRect (= Overflow, no warning), Page (`pageToDisplay`) and Linked (`linkedTextComponent`, `firstVisibleCharacter`). Engine: TMP spacing inputs on `TextProcessor`, `UniText.LayoutMargins`, `UniTextMeshGenerator.ClusterHidden`, ranged HarfBuzz features, per-line alignment and indent jumps; components dirtied during mesh apply are processed in the same frame. Tests: `GlyphMeshProParity3Tests`. Fixed during the demo render: a component created with `maxVisibleCharacters = 0` could reveal stale glyph geometry from the pooled glyph cache (`MaxVisibleCharacters_StartingAtZero_RevealsTheRightGlyphs`). Visibility now counts characters exactly like TMP (spaces, newlines and combining marks; Latin `liga`/`clig` off as with TMP's default font features; whitespace is never `isVisible`), and a space at a line end no longer forces a wrap in GlyphMeshPro.
- **Physical alignment for GlyphMeshPro (B23)**: `GlyphMeshProUGUI` Left/Right now hug the rect's physical left/right edge for RTL paragraphs, like TMP (`LayoutSettings.physicalAlignment` / `TextProcessSettings.PhysicalAlignment`). Plain `UniText` is unchanged: `HorizontalAlignment.Left/Right` are the paragraph's start/end edge (now documented as such).
- **System font fallback for any script**: code points of any script the font stack does not cover (Tamil, Bengali, Georgian, Ethiopic, Tibetan, ...) now use an installed system font found by script (Android `fonts.xml`, per-platform name hints, `NotoSans<Script>` files, then a bounded cmap scan). It logs one warning per script per session, naming the font used or saying it will render as missing glyphs. See `Documentation/SystemFontFallback.md`.
- **System CJK font fallback** (`SystemFontFallback`, `UniTextSettings.useSystemFontFallback`, default on): CJK code points not covered by the font stack lazily use the OS CJK font. TTC face index is now honoured by `CreateFontAsset(..., faceIndex)` and shaping. See `Documentation/SystemFontFallback.md`.
- **Phase 0 — Native MSDF pipeline.** Clean-room native font core
  (`unitext_native`) built from source for all platforms (Windows, macOS, Linux,
  Android, iOS, tvOS, WebGL), FreeType outline extraction, and a from-scratch
  multi-channel signed distance field (MSDF) generator (`Runtime/FontCore/Msdf/*`)
  with edge colouring and error correction, validated against msdfgen for parity.
- **Phase 1a — MSDF integration & artifact tests.** MSDF glyph generation wired
  into the atlas pipeline with exact-distance, real-glyph, real-export and visual
  artifact test coverage, plus the DLL smoke tests.
- **Phase 1b — Dictionary word segmentation.** Opt-in dictionary segmenter
  (`DictionarySegmenter`, `DictionaryTrie`, `SegmentationScript`) for the
  space-less complex-context scripts Thai, Lao, Khmer and Myanmar — a faithful
  re-implementation of ICU dictionary break iteration, with every emitted boundary
  filtered to a UAX #29 grapheme-cluster boundary. Line breaking wraps such runs at
  real word boundaries. Dictionaries ship as `.bytes` assets outside any `Resources`
  folder, so a project that assigns none pays zero cost. Includes `DictGen` tooling
  and natural-text F1 accuracy fixtures.
- **Phase 1c — Pixel fonts + Smooth/Mono rendering.** Pixel/bitmap-strike font
  detection (`PixelFontDetection`, native pixel-grid and EBDT/CBDT/sbix strike
  analysis) and a Pixel-Perfect rendering path (`MonoGlyphRenderer`,
  `SmoothGlyphRenderer`, `FreeTypeOutlineGridProvider`) that rasterises detected
  pixel fonts in 1-bit Mono at integer multiples of the native em into a
  point-filtered, mip-free atlas, snapping glyph quads and the text origin to the
  device pixel grid. Adds **Smooth** (anti-aliased grayscale bitmap) and **Mono**
  (1-bit) render modes for ordinary vector fonts, selectable per font asset.

#### Changed
- **Shared glyph-atlas memory budgets are disabled at runtime (temporary).** The per-array
  page/byte budgets and the global byte budget on `UniTextSettings`
  (`SharedAtlasPageBudget`, `SharedAtlasByteBudgetPerArray`, `SharedAtlasByteBudgetGlobal`)
  are **no longer enforced**: `SharedGlyphAtlas` reads every budget as `0` (unbounded)
  regardless of the configured values, so the atlas never evicts through the runtime.
  They were disabled because the eviction machinery only evicts refcount-0 cells on an
  LRU clock, but the render path never calls `Acquire`/`Release` or `BeginFrame` — so a
  non-zero budget could evict **on-screen** glyphs and draw wrong text. The eviction code
  and its unit tests are retained; the serialized settings fields are kept (existing assets
  load unchanged) and marked *"not active yet"* in their tooltips, and a single warning is
  logged once per session if a non-zero budget is set. See
  `Documentation/Design/MemoryBudgets.md` for the full rationale and the conditions to
  re-enable.

#### Known limitations
- **MSDF 'W' junction residual.** A small corner-rounding residual remains at the
  'W' apex. This is inherent to the algorithm, not a bug: msdfgen v1.12 (85e8b3d)
  reproduces it identically (20 offenders / 0.356 @ 280,368). 'W' is gated against
  the msdfgen baseline; the other eight reference glyphs keep an absolute 0.25 bound.
- **Lao segmentation F1 target is 0.90**, deliberately lower than the other scripts.
  This reflects a disagreement between ICU's current `laodict` and ICU's own word
  `BreakIterator` (which caps any faithful re-implementation at ~F1 0.92), not an
  OpenGlyph quality regression; against the ICU-60-vintage `laodict` the same code
  reaches F1 0.9985.
- **Real-layout wrap tests** cover Thai, Khmer and Myanmar with bundled OFL Noto Sans
  fixtures (test data only, not shipped). Lao has no real-layout test yet.

## [1.0.0] - TBD

### Fixed
- Restore Unity 6.6 compilation by implementing unlimited UI layout maximum sizes and using the current object identity API on Unity 6.2+, while retaining Unity 2021.3 compatibility.

### Added
- Initial release
