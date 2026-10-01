# OpenGlyph Feature Comparison

How OpenGlyph's rendering behaves on the **legacy per-segment path** (default) vs
the **unified single-renderer path** (`UniTextSettings.UseUnifiedRenderer` on, or
per-component `UniText.UnifiedRenderer = ForceOn`). Behaviour is identical with
the flag off; the unified path adds the rows below. See
`Documentation/Design/RenderArchitecture.md` §9 for the full design.

Each row cites the test(s) that assert it so the claim is reproducible in the
EditMode/PlayMode suite.

| Feature | Legacy path (flag off) | Unified path (flag on) | Tests |
|---------|------------------------|------------------------|-------|
| **Draw calls per text** | `Σ(atlasPages × passes)` — one CanvasRenderer per font / atlas page / pass (3 SDF+MSDF ⇒ 18 draws / 3 renderers; 50 components ⇒ 455 / 150) | **1** draw group (text-only) or **2** (text + MSDF/emoji), independent of font/span count — 3 SDF+MSDF ⇒ **5 draws / 1 renderer**; 50 components ⇒ **5 / 50** | `UberDrawGroupTests` (≤2 groups for any mix), `UnifiedRendererEquivalenceTests` (n→1 renderers, same geometry), PlayMode `DrawCallBenchmarkPlayTests` (GPU draws + CPU) |
| **Per-span styles** (two different outlines/shadows in one text) | not available on one renderer — a different material/appearance means a different CanvasRenderer, so distinct styles split the draw | `<outline>`, `<underlay>`, `<dilate>`, `<softness>`, `<style=Name>` markup; each **distinct** composed style is one deduped `StyleTable` row chosen **per glyph** (UV1.w), still **one renderer**; tags nest and compose | `SpanStyleTests` (grammar, nesting/compose, dedup = a row per distinct style, end-to-end 1 renderer + distinct per-glyph styleIdx) |
| **Style configured on the component** | style lives in a `UniTextAppearance` + `Material` asset | serializable `UniTextStyle` on the `UniText` component (`OverrideStyle` + `Style`); no asset, no material | `UniTextStyleTests` (round-trip, field parity with the StyleTable row, dedup, component wiring) |
| **Named styles** | — | `UniTextStyleSheet` asset of named `UniTextStyle`s, addressable via `<style=Name>` | `SpanStyleTests.StyleSheet_WholeStyle_ThenFieldOverride` |
| **Appearance → style migration** | manual re-authoring | editor tool `Tools/OpenGlyph/Migrate Appearance to Styles`: dry-run report, Undo + prefab recording, idempotent; optional auto-migrate-on-import (OFF by default) | `AppearanceMigrationTests` (style equals material values, idempotent second run, Undo restores, prefab round-trip, dry-run no-mutate) |
| **Old assets during deprecation** | — | `UniTextAppearance` + material API are `[Obsolete]` **warnings** (not errors); old assets still load and render identically via `AppearanceStyleShim` with the flag off | `AppearanceDeprecationTests` (Obsolete present, `IsError == false`, message → migration tool); full EditMode suite green flag-off |

## Pixel fidelity (unified vs legacy)

Per `RenderArchitecture.md` §8.5: SDF, MSDF and `<color>` spans are **bit-identical**
to legacy through the production `UniText/Uber` shader (maxΔ 0). Outline/underlay
effect-layer parity is owned on a separate branch (`openglyph/uber-outline`) and is
tracked there, not in this feature set.

- Atlas/geometry indirection (pages-as-slices) bit-identical: `UnifiedRendererPixelEquivalenceTests`.
