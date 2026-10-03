# Changelog

All notable changes to UniText will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### OpenGlyph 1.1.0-preview

Open-source (MIT) clean-room fork of UniText 1.0. This preview integrates phase 0
through phase 1c.

#### Added
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
