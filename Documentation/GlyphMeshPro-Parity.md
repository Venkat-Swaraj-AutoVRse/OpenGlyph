# GlyphMeshPro ↔ TextMeshPro Parity

**Status: Round 1 (in progress).** This document maps the public API and rich-text
tag surface of Unity's TextMeshPro (`TMP_Text` / `TextMeshProUGUI`) onto the
OpenGlyph components `GlyphMeshProUGUI` (Canvas, implemented in Round 1) and
`GlyphMeshPro` (world-space `MeshRenderer`, design + stub in Round 1).

## Legal / clean-room note

TextMeshPro is distributed under the **Unity Companion License**; OpenGlyph is
**MIT**. Nothing in GlyphMeshPro copies or ports TMP source, shaders, or assets.
The parity surface below is derived by **reflecting TMP's public API names, enum
members, and documented tag syntax**, and by **observing TMP's rendered behavior
in tests**. All implementation is written independently against OpenGlyph's own
engine (`LightSide.UniText` shaping/layout/renderer). TMP member *names* are
reproduced only so that migration is a mechanical rename; the semantics are
re-implemented, not transcribed.

## Architecture decision: subclass `UniText` (composition rejected)

`GlyphMeshProUGUI : LightSide.UniText` — it **inherits** the existing engine
component rather than composing a reference to one. Rationale:

- **No second engine (hard requirement).** `UniText : MaskableGraphic` owns the
  `CanvasRenderer`, the `TextProcessor`, the `UniTextMeshGenerator`, the unified
  renderer path, and the dirty-flag rebuild loop. The engine is *inseparable*
  from its `MaskableGraphic` host — it cannot be driven as a plain field without
  a second `MaskableGraphic` on the GameObject (which **is** a second renderer).
  Subclassing reuses exactly one engine instance and one `CanvasRenderer`.
- **Renderer-on and renderer-off come free.** `UniText.UseUnifiedRenderer` and
  the `UnifiedRenderer` (`UseProjectSetting`/`ForceOn`/`ForceOff`) property are
  inherited unchanged, so GlyphMeshProUGUI works in both modes with no extra code.
- **`ILayoutElement` / `ILayoutController` come free.** `UniText` already
  implements both (`UniText_Layout.cs`); `preferredWidth`/`preferredHeight` and
  auto-size are inherited. Composition would force a re-implementation or a
  manual forward of every layout member.
- **Engine state is private but fully exposed via public properties** (`Text`,
  `FontSize`, `WordWrap`, `HorizontalAlignment`, `VerticalAlignment`,
  `AutoSize`, `MinFontSize`/`MaxFontSize`, `color`, `SetDirty(DirtyFlags)`, …).
  The TMP-named adapter forwards to these public members only — it does **not**
  touch private fields — so it is robust to engine refactors.

The adapter adds **no** serialized state of its own that duplicates the engine;
TMP-named properties are thin façades over the inherited `UniText` properties.
Where TMP exposes a concept the engine lacks (overflow modes, justification,
`textInfo`), the adapter adds the minimum new state and a documented behavior.

### Type mappings (for migration)

| TextMeshPro type              | OpenGlyph / GlyphMeshPro type         | Notes |
|-------------------------------|---------------------------------------|-------|
| `TextMeshProUGUI`             | `OpenGlyph.GlyphMeshProUGUI`          | Canvas component. |
| `TextMeshPro`                 | `OpenGlyph.GlyphMeshPro`              | World-space; design + stub (Round 1). |
| `TMP_FontAsset`               | `LightSide.UniTextFont`               | Font asset. |
| `TMP_Text`                    | `GlyphMeshProUGUI` base surface       | Abstract base in TMP; folded into the component here. |
| `FontStyles` (flags)          | `OpenGlyph.FontStyles` (flags)        | Mirror of TMP flag names; mapped to engine `StyleAxis` + span tags. |
| `TextAlignmentOptions`        | `OpenGlyph.TextAlignmentOptions`      | Mirror of TMP names; see Alignment section for gaps. |
| `TextOverflowModes`           | `OpenGlyph.TextOverflowModes`         | Mirror of TMP names; Overflow/Ellipsis/Truncate/Masking live, rest stubbed. |
| `TextWrappingModes`           | `OpenGlyph.TextWrappingModes`         | Mapped onto engine `WordWrap` bool. |
| `VertexGradient`              | `OpenGlyph.VertexGradient`            | 4-corner color struct mirroring TMP. |
| `TMP_TextInfo`                | `OpenGlyph.GlyphTextInfo`             | characterCount/lineCount/characterInfo/lineInfo subset. |
| `TMP_CharacterInfo`           | `OpenGlyph.GlyphCharacterInfo`        | Subset of fields. |
| `TMP_LineInfo`                | `OpenGlyph.GlyphLineInfo`             | Subset of fields. |

Status legend: **direct** = inherited/forwarded 1:1 · **adapter** = re-expressed
over the engine with matching semantics · **new** = engine feature added for
parity · **stub** = present with TMP signature, TODO body (documented) ·
**oos** = out of scope for Round 1 (reason given).

## Core API parity (`TMP_Text` / `TextMeshProUGUI` → `GlyphMeshProUGUI`)

| TMP member | Status | Mapping / notes |
|------------|--------|-----------------|
| `string text` | direct | → `UniText.Text`. |
| `SetText(string)` | adapter | → `Text` (no re-parse flag needed). |
| `SetText(string, bool syncTextInputBox)` | adapter | sync flag ignored (no input field in R1). |
| `SetText(char[])`, `SetText(char[],start,length)` | direct | → `UniText.SetText(char[],…)`. |
| `SetText(StringBuilder)` | adapter | copies into pooled char buffer, no per-call string alloc. |
| `SetText(string, float arg0 … arg7)` | new | **No-alloc** numeric formatting into a reused `char[]`; mirrors TMP's `{0}`–`{7}` placeholder overloads. |
| `TMP_FontAsset font` | adapter | → `UniText.FontStack`/`MainFont` via `UniTextFont`. |
| `float fontSize` | direct | → `UniText.FontSize`. |
| `bool enableAutoSizing` | direct | → `UniText.AutoSize`. |
| `float fontSizeMin` / `fontSizeMax` | direct | → `UniText.MinFontSize` / `MaxFontSize`. |
| `FontStyles fontStyle` | adapter | Bold/Italic → `StyleAxis`+`FontWeight`; Underline/Strikethrough/Sub/Super/LowerCase/UpperCase/SmallCaps → span-style injection. Highlight → `<mark>`. |
| `Color color` | direct | → `UniText.color` (override). |
| `bool enableVertexGradient` + `VertexGradient colorGradient` | adapter | 4-corner gradient applied as a per-span vertex-color modifier over the engine's gradient system. Feasible; see Gradient section. |
| `TextAlignmentOptions alignment` | adapter | split into `HorizontalAlignment`+`VerticalAlignment`; Justified/Flush/Geometry/Baseline/Capline/Midline gaps documented below. |
| `TextWrappingModes textWrappingMode` | adapter | NoWrap→`WordWrap=false`; Normal→`WordWrap=true`; PreserveWhitespace(NoWrap) stubbed to Normal/NoWrap + TODO. |
| `bool enableWordWrapping` (obsolete) | adapter | legacy bool → `WordWrap`. |
| `TextOverflowModes overflowMode` | adapter/new | Overflow, Ellipsis, Truncate, Masking implemented; ScrollRect, Page, Linked **stub + TODO**. |
| `float characterSpacing` | adapter | → engine char-spacing. |
| `float wordSpacing` | adapter | → engine word-spacing. |
| `float lineSpacing` | adapter | → engine line-spacing. |
| `float paragraphSpacing` | adapter | → engine paragraph-spacing. |
| `Vector4 margin` | adapter | → engine margin (L,T,R,B). |
| `bool richText` | direct | → engine rich-text toggle. |
| `int maxVisibleCharacters` | adapter | clamps visible glyph count post-layout. |
| `int maxVisibleWords` | adapter | clamps to word boundary. |
| `int maxVisibleLines` | adapter | clamps to line boundary. |
| `bool isRightToLeftText` | adapter | → `UniText.BaseDirection = RightToLeft`. |
| `float preferredWidth` / `preferredHeight` | direct | inherited `ILayoutElement`. |
| `GetPreferredValues()` + 3 overloads | adapter | → `TextProcessor.GetPreferredWidth/Height`. |
| `GetRenderedValues()` / `(bool)` | adapter | → engine `ResultSize`. |
| `ForceMeshUpdate(bool,bool)` | adapter | → `UniText.SetDirty(DirtyFlags.Text)` (full rebuild) + synchronous layout flush. |
| `TMP_TextInfo textInfo` | adapter | `GlyphTextInfo`: characterCount, lineCount, characterInfo[], lineInfo[] from engine `ResultGlyphs`/lines. |
| `bool raycastTarget` | direct | inherited from `Graphic`. |
| `TMP_TextInfo GetTextInfo(string)` | stub | returns populated `GlyphTextInfo` for the given text; TODO full fidelity. |
| `uint[] Text` (parsed), `TextRenderFlags`, `TextureMappingOptions`, `MaskingTypes`, `FontWeight` enum, sprite/link DBs | oos | advanced/renderer-internal surface not required by R1 scope. |

## Alignment parity (`TextAlignmentOptions`)

TMP alignment = `HorizontalAlignmentOptions` (`Left/Center/Right/Justified/Flush/Geometry`)
× `VerticalAlignmentOptions` (`Top/Middle/Bottom/Baseline/Geometry/Capline`).
OpenGlyph engine supports `HorizontalAlignment {Left,Center,Right}` ×
`VerticalAlignment {Top,Middle,Bottom}`.

| TMP alignment family | Status | Mapping |
|----------------------|--------|---------|
| `TopLeft/Top/TopRight` | direct | H∈{L,C,R}, V=Top. |
| `Left/Center/Right` | direct | H∈{L,C,R}, V=Middle. |
| `BottomLeft/Bottom/BottomRight` | direct | H∈{L,C,R}, V=Bottom. |
| `*Justified` | new (R2) | Engine has no inter-word justification. R1 maps to the row's Left and **records the gap**; R2 adds a justification pass in layout. |
| `*Flush` | new (R2) | Flush (justify incl. last line) → same as Justified gap. |
| `*GeoAligned` / H=`Geometry` | oos | Geometry-based alignment (align to rendered glyph bounds) — niche; mapped to Center + documented. |
| `Baseline*` (V=Baseline) | adapter | mapped to `UnderEdge=Baseline` + V=Bottom approximation. |
| `Capline*` (V=Capline) | adapter | mapped to `OverEdge=CapHeight` + V=Top approximation. |
| `Midline*` (V=Geometry) | adapter | mapped to V=Middle. |

**Intentional difference:** Justified/Flush are not visually justified in Round 1;
GlyphMeshProUGUI exposes the enum values (so code compiles and migrates) but
renders them as left-aligned until the R2 justification pass lands. Tests assert
this explicitly rather than hiding it.

## Font-style parity (`FontStyles` flags)

| TMP flag | Status | Mapping |
|----------|--------|---------|
| `Normal` | direct | default. |
| `Bold` | adapter | engine `FontWeight` → 700 (synthetic bold fallback if no bold face). |
| `Italic` | adapter | engine `FontStyleAxis = Italic` (synthetic oblique fallback). |
| `Underline` | adapter | span style `<u>`. |
| `Strikethrough` | adapter | span style `<s>`. |
| `LowerCase` / `UpperCase` / `SmallCaps` | adapter | span text transform `<lowercase>`/`<uppercase>`/`<smallcaps>`. |
| `Subscript` / `Superscript` | adapter | span `<sub>`/`<sup>`. |
| `Highlight` | adapter | span `<mark>`. |

## Overflow parity (`TextOverflowModes`)

| TMP mode | Status | Behavior in R1 |
|----------|--------|----------------|
| `Overflow` | direct | engine default — content overflows the rect. |
| `Ellipsis` | adapter | truncate at last fitting break and append `…`. |
| `Truncate` | adapter | hard clip at last fully-fitting glyph, no marker. |
| `Masking` | adapter | rely on inherited `MaskableGraphic` clip rect (`SetClipRect`). |
| `ScrollRect` | stub | TODO — needs ScrollRect integration. Falls back to Overflow. |
| `Page` | stub | TODO — needs page model. Falls back to Overflow. |
| `Linked` | stub | TODO — needs linked-text chain. Falls back to Overflow. |

## Wrapping parity (`TextWrappingModes`)

| TMP mode | Status | Mapping |
|----------|--------|---------|
| `NoWrap` | direct | `WordWrap=false`. |
| `Normal` | direct | `WordWrap=true`. |
| `PreserveWhitespace` | stub | TODO — maps to Normal; whitespace-preservation flag not yet wired. |
| `PreserveWhitespaceNoWrap` | stub | TODO — maps to NoWrap. |

## Rich-text tag parity

TMP tag set observed from documentation + behavior. OpenGlyph already parses span
style tags (see `Runtime/ModCore/Rules`, `attributeParser`). Status is per tag.

| Tag | Status | Mapping / notes |
|-----|--------|-----------------|
| `<b>` | direct | bold span. |
| `<i>` | direct | italic span. |
| `<u>` | direct | underline span. |
| `<s>` | direct | strikethrough span. |
| `<size=…>` (px/%/em) | adapter | per-span size; units px/%/em. |
| `<color=…>` / `<#rrggbb>` | direct | per-span color. |
| `<alpha=#xx>` | adapter | per-span alpha. |
| `<align=left/center/right>` | adapter | per-line alignment; justified/flush = gap (left). |
| `<cspace=…>` | adapter | character spacing span. |
| `<line-height=…>` | adapter | line-height span. |
| `<indent=…>` | adapter | indent span. |
| `<margin=…>` / `<margin-left/right>` | adapter | margin span. |
| `<mark=#…>` | adapter | highlight span. |
| `<sup>` / `<sub>` | adapter | super/subscript span. |
| `<voffset=…>` | adapter | vertical offset span. |
| `<nobr>` | adapter | no-break span. |
| `<lowercase>`/`<uppercase>`/`<smallcaps>` | adapter | text-transform spans. |
| `<mspace=…>` | adapter | monospace span. |
| `<space=…>` | adapter | horizontal space insertion. |
| `<width=…>` | adapter | line width span. |
| `<link=…>` | adapter | link span (engine `ModCore/Rules/Link`). |
| `<font=…>` | adapter | font switch span → `UniTextFont`. |
| `<style=…>` | adapter | named style span → `UniTextStyle`/`UniTextStyleSheet`. |
| `<gradient=…>` | direct | engine `UniTextGradients` named gradient. |
| `<pos>`, `<rotate>`, `<sprite>`, `<page>` | oos | positional/sprite/page tags — not required by R1 scope; `<sprite>` deferred to emoji/sprite work. |

## `GlyphMeshPro` (world-space `MeshRenderer`) — design (Round 1 = design + stub)

Target: Quest / world-space text without a Canvas. Design:

- `GlyphMeshPro : MonoBehaviour` requiring `MeshRenderer` + `MeshFilter`
  (not a `Graphic`), reusing the **same** `TextProcessor`/shaping/layout engine
  via a thin headless engine host (no `CanvasRenderer`).
- Shares the TMP-named adapter surface with `GlyphMeshProUGUI` through a common
  `IGlyphMeshText` interface so property code is written once.
- Mesh pushed to `MeshFilter.sharedMesh`; material = OpenGlyph SDF material
  (unified renderer shader), enabling world-space SDF with the same quality.
- Round 1 ships the component **stub** (serialized fields + `IGlyphMeshText`
  surface, `// TODO R2` bodies) + this design. Full headless engine host and
  mesh emission are Round 2.

## Round 2 gaps (tracked)

- Inter-word **justification** (Justified/Flush alignment, `<align=justified>`).
- Overflow **ScrollRect / Page / Linked**.
- Wrapping **PreserveWhitespace / PreserveWhitespaceNoWrap**.
- `GlyphMeshPro` headless engine host + world-space mesh emission.
- `<sprite>` / `<pos>` / `<rotate>` / `<page>` tags.
- `GetTextInfo(string)` full fidelity (sprite/link/word info arrays).
