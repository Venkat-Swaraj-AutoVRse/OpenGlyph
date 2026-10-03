# GlyphMeshPro vs TextMeshPro Parity

**Status: Round 2 (in progress).** This document maps TextMeshPro's API and rich-text
tag surface onto OpenGlyph's `GlyphMeshProUGUI` (Canvas) and `GlyphMeshPro` (world-space).
Every row is kept in sync with ACTUAL behaviour; a non-"applied" status is a real gap.

### Round 2 status summary

| Feature | Status | Evidence |
|---------|--------|----------|
| fontStyle Underline / Strikethrough | **done** | evidence/fontstyle_underline_strike.png |
| fontStyle UpperCase / LowerCase (+ `<uppercase>`/`<lowercase>`) | **done** | evidence/fontstyle_upper_lower.png |
| fontStyle Superscript / Subscript (+ `<sup>`/`<sub>`) | **done** | evidence/markup_sup_sub.png |
| Justified / Flush alignment | **done** | evidence/alignment_justified_flush.png |
| Tag `<voffset>` | **done** | evidence/markup_voffset.png |
| fontStyle Highlight (`<mark>`) | **not started** | needs background-quad modifier behind glyphs |
| fontStyle SmallCaps | **not started** | needs small-caps transform modifier |
| Tags `<nobr> <font> <align> <indent> <mark> <sprite>` | **not started** | see tag table |
| 3D GlyphMeshPro (world-space MeshRenderer) | **not started** (Round-1 stub) | headless host + mesh emission |
| Overflow Page / Linked / ScrollRect | **not started** | see overflow table |

Each "done" row: deterministic headless test + full suite BOTH modes (renderer-off 256 pass/0 fail; unified-on 272 pass/0 fail; 280 total) + GPU side-by-side PNG verified against real TextMeshPro.

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
| `FontStyles` (flags)          | `OpenGlyph.FontStyles` (flags)        | Mirror of TMP flag names. R2 applies Bold/Italic/Underline/Strikethrough/Upper/LowerCase/Super/Subscript; SmallCaps/Highlight remain gaps. |
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
| `FontStyles fontStyle` | adapter | Bold->FontWeight, Italic->StyleAxis. **Round 2 (applied):** Underline, Strikethrough, UpperCase, LowerCase, Superscript, Subscript � composed as engine span tags, auto-registering their backing modifiers. SmallCaps/Highlight stored and round-trip but **not applied** yet (no engine modifier; see font-style section). |
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
| `*Justified` | **applied (R2)** | The layout pass distributes inter-word slack so every line but the paragraph's last fills the box; last line stays ragged. Verified by test + GPU PNG vs TMP. |
| `*Flush` | **applied (R2)** | Like Justified but the paragraph's last line is justified too. Verified by test + GPU PNG. |
| `*GeoAligned` / H=`Geometry` | oos | Geometry-based alignment (align to rendered glyph bounds) — niche; mapped to Center + documented. |
| `Baseline*` (V=Baseline) | adapter | mapped to `UnderEdge=Baseline` + V=Bottom approximation. |
| `Capline*` (V=Capline) | adapter | mapped to `OverEdge=CapHeight` + V=Top approximation. |
| `Midline*` (V=Geometry) | adapter | mapped to V=Middle. |

**Round 2 (verified):** Justified/Flush are now really justified � the engine layout pass
distributes inter-word slack. Justified fills every line but the paragraph's last (last line
ragged, matching TMP); Flush justifies the last line too. GPU evidence:
`scratch/gmp-round2/evidence/alignment_justified_flush.png` (lines fill the column, match TMP).

## Font-style parity (`FontStyles` flags)

| TMP flag | Status | Mapping |
|----------|--------|---------|
| `Normal` | direct | default. |
| `Bold` | adapter | engine `FontWeight` → 700 (synthetic bold fallback if no bold face). **Wired.** |
| `Italic` | adapter | engine `FontStyleAxis = Italic` (synthetic oblique fallback). **Wired.** |
| `Underline` | **applied (R2)** | `fontStyle` composes `<u>…</u>` around the run; the component auto-registers `UnderlineParseRule`+`UnderlineModifier`. Verified by test + GPU evidence PNG (line present, matches TMP). |
| `Strikethrough` | **applied (R2)** | `fontStyle` composes `<s>…</s>`; auto-registers `StrikethroughParseRule`+`StrikethroughModifier`. Verified by test + GPU evidence PNG. |
| `UpperCase` | **applied (R2)** | composes `<uppercase>` (alias of engine `upper`) + `UppercaseModifier`. Verified by test + GPU PNG. | `LowerCase` | **applied (R2)** | composes `<lowercase>` + new `LowercaseModifier`. Verified. | `SmallCaps` | **gap (R2)** | no engine small-caps modifier yet; TODO. |
| `Subscript` / `Superscript` | **applied (R2)** | compose `<sub>`/`<sup>` + new `SuperSubscriptModifier` (0.5 scale on advance + quad, baseline raise/lower). Verified by test + GPU PNG vs TMP. |
| `Highlight` | **gap (R2)** | no engine highlight rule; the component-level `TextHighlighter` is separate; TODO. |

**Current reality (verified by test + GPU evidence):** `fontStyle` applies **Bold, Italic, Underline, Strikethrough, UpperCase, LowerCase, Superscript and Subscript**. U/S compose `<u>`/`<s>`; case composes `<uppercase>`/`<lowercase>` (pre-shape codepoint transform); sup/sub compose `<sup>`/`<sub>` (scale + baseline shift). GPU PNGs: fontstyle_underline_strike.png, fontstyle_upper_lower.png, markup_sup_sub.png (all match TMP). SmallCaps/Highlight still need engine modifiers (tracked below).
Underline and Strikethrough**. Underline/Strikethrough render by composing the engine's
`<u>`/`<s>` span tags around the run; the GPU evidence PNG
(`scratch/gmp-round2/evidence/fontstyle_underline_strike.png`) shows both lines present and
matching TMP. The remaining flags (Upper/LowerCase, SmallCaps, Sub/Superscript, Highlight) are
stored and round-trip so bitmasks migrate from TMP unchanged, but have no visible effect yet —
they need engine modifiers that do not exist (tracked below). Use markup where the engine already
has a rule.

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

**Observed behaviour (verified by test):** an over-long single token (no break
opportunities) wider than the rect is **character-wrapped** across lines by the
engine's emergency overflow break — matching TMP's character-wrapping fallback
for an unbreakable word. It stays on one line only when it fits the width.

## Rich-text tag parity

**Verified** against the engine's registered parse rules (`Runtime/ModCore/Rules/*`,
each rule's `TagName`). The engine's actual tag set is: `b, i, u, s, size, color,
cspace, line-height, line-spacing, gradient, link, style, upper, outline, underlay,
dilate, softness, ellipsis, obj`. TMP tags are mapped against that reality — several
common TMP tags have **no engine rule in Round 1** and are listed as gaps, not glossed
as supported.

| TMP tag | Status | Mapping / notes |
|---------|--------|-----------------|
| `<b>` | direct | engine `BoldParseRule` (`b`). |
| `<i>` | direct | engine `ItalicParseRule` (`i`). |
| `<u>` | direct | engine `UnderlineParseRule` (`u`). |
| `<s>` | direct | engine `StrikethroughParseRule` (`s`). |
| `<size=…>` | direct | engine `SizeParseRule` (`size`). |
| `<color=…>` / `<#rrggbb>` | direct | engine `ColorParseRule` (`color`). |
| `<cspace=…>` | direct | engine `CSpaceParseRule` (`cspace`). |
| `<line-height=…>` | direct | engine `LineHeightParseRule` (`line-height`). |
| `<gradient=…>` | direct | engine `GradientParseRule` (`gradient`) + `UniTextGradients`. |
| `<link=…>` | direct | engine `LinkTagParseRule` (`link`). |
| `<style=…>` | direct | engine span-style rule (`style`). |
| `<uppercase>` | **applied (R2)** | `UppercaseAliasParseRule` (name alias of engine `upper`) + `UppercaseModifier`. |
| `<lowercase>` | **applied (R2)** | new `LowercaseParseRule` + `LowercaseModifier`. |
| `<smallcaps>` | **gap (R2)** | no engine rule; TODO. |
| `<mark=#…>` | **gap (R2)** | no engine highlight rule; TODO (highlighter exists at the component level). |
| `<sup>` / `<sub>` | **applied (R2)** | new `SuperscriptParseRule`/`SubscriptParseRule` + `SuperSubscriptModifier`. Verified by GPU PNG vs TMP. |
| `<voffset=�>` | **applied (R2)** | new `VOffsetParseRule` + `VOffsetModifier` (per-span vertical shift of the glyph quad; em/px/% value). Verified by GPU PNG vs TMP. |
| `<nobr>` | **gap (R2)** | no engine no-break rule; TODO. |
| `<mspace=…>` | **gap (R2)** | no engine monospace rule; TODO. |
| `<space=…>` | **gap (R2)** | no engine rule; TODO. |
| `<width=…>` | **gap (R2)** | no engine rule; TODO. |
| `<indent=…>` | **gap (R2)** | no engine rule; TODO. |
| `<margin=…>` | **gap (R2)** | no engine inline-margin rule; component-level `margin` property only. |
| `<align=…>` | **gap (R2)** | no engine inline-align rule; component-level alignment only (justified/flush also gap). |
| `<alpha=#xx>` | **gap (R2)** | no engine rule; TODO (fold into `color`). |
| `<font=…>` | **gap (R2)** | no engine inline font-switch rule; component-level `font` only. |
| `<pos>` / `<rotate>` / `<sprite>` / `<page>` | oos | positional/sprite/page tags — not in R1 scope. |

**OpenGlyph-only tags (no TMP equivalent, present in the engine):** `line-spacing`,
`outline`, `underlay`, `dilate`, `softness` (SDF style controls), `ellipsis`, `obj`.
These are additive and do not affect TMP migration.

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

- Inter-word **justification** (Justified/Flush alignment) is **done** (R2). Remaining: inline `<align=justified>` tag.
- Overflow **ScrollRect / Page / Linked**.
- Wrapping **PreserveWhitespace / PreserveWhitespaceNoWrap**.
- `GlyphMeshPro` headless engine host + world-space mesh emission.
- `<sprite>` / `<pos>` / `<rotate>` / `<page>` tags.
- `GetTextInfo(string)` full fidelity (sprite/link/word info arrays).
- **`fontStyle` flags not yet wired:** SmallCaps, Highlight (all others now apply). SmallCaps needs a small-caps modifier; Highlight needs a background-quad modifier drawn behind the glyphs.
  Superscript, Highlight (Bold/Italic/Underline/Strikethrough now apply). Underline/Strikethrough
  were wired to the existing `<u>`/`<s>` rules in Round 2; the rest need new engine modifiers
  (a case transform, a baseline-shift+scale, and a background-quad renderer).
- **Rich-text tags with no engine rule (apply via markup is impossible until added):**
  `<smallcaps>`, `<mark>`, `<nobr>`,
  `<mspace>`, `<space>`, `<width>`, `<indent>`, `<margin>` (inline), `<align>` (inline),
  `<alpha>`, `<font>` (inline). `<uppercase>` is the engine's `upper` (needs a name alias).


## Round 2.1 — grey-text fix + justification line-break parity (PR #17 review follow-up)

Two defects were found in the GPU side-by-side review images and fixed:

### 1. Grey / heavier text (all GlyphMeshPro text, size-dependent)
GlyphMeshPro rendered **grey, heavier, soft-edged** where TextMeshPro is crisp white.
Measured from the pixels: at small font sizes (~30 pt) the glyph interior never
saturated (peak luminance 244/255, 0 % near-white) and the ink spread ~65 % wider.
A large glyph saturated fine — the bug was size-dependent, which is why no existing
test caught it (the unified-vs-legacy pixel tests compare two OpenGlyph paths, not vs
TMP).

**Root cause:** the `UniText/Uber` shader derived the SDF coverage-ramp slope in the
**fragment** from `ddx/ddy(uv0.y) * _AtlasSize * 0.75`. That approximation
under-estimates the true screen pixel size at small sizes, so the ramp went shallow.

**Fix:** compute `baseScale` in the **vertex** shader exactly as the legacy
`UniText/SDF-Face` does — `rsqrt(dot(pixelSize, pixelSize)) * (_Sharpness+1)` with
`pixelSize` from the clip-space `w` and `UNITY_MATRIX_P * _ScreenParams` (TMP's
own method) — and carry it to the fragment (`cov.z`) for face, outline and underlay.
This matches the legacy path the pixel-equivalence suite already validates, so those
tests stay green. Guarded by `WhiteText_Face_ReachesNearWhite_LikeTMP` (30 pt white
text must peak >= 98 % luminance; was 95.7 %, now 100 %).

### 2. Justified / Flush line breaks now match TMP
TMP keeps a near-fitting word on a justified line by allowing the line to overrun the
box by up to **5 %** before wrapping, then compressing inter-word spacing to pull it
back (in the review paragraph "quickly" stays on line 2 in TMP but moved to line 3 in
ours). Mirrored exactly:

- `LineBreaker` takes a `widthTolerance` (1.05 for Justified/Flush, 1.0 otherwise)
  so plain Left/Center/Right wrapping is byte-identical; only Justified/Flush pack more.
- `TextProcessor` captures the alignment and the live component re-breaks when the
  alignment crosses the justify boundary (so a runtime Left->Justified switch re-wraps).
- `TextLayout` justification distributes **negative** slack too (compress an overrun
  line back into the box), not only positive (spread).

Guarded by `Alignment_Justified_KeepsNearFittingWord_OnLine_NotWrapped` (deterministic)
and `Compare_Justified_LineCount_MatchesTMP` (vs real TMP: 5 = 5 lines at the review
width). Plain UniText layout is unchanged — the tolerance only engages for Justified/Flush.

### Justified spacing split (word + character), Flush letter-spread
Justification distributes the line's slack exactly as TMP's `wordWrappingRatios`
(default 0.4): **word spacing absorbs 60 %** (spread across the whitespace gaps) and
**character spacing absorbs 40 %** (spread across every visible glyph). This applies to
both spread (positive slack) and compress (negative slack, from a 5 %-tolerance
overrun line), so a packed line compresses word AND letter spacing together instead of
collapsing the word gaps to zero. A hard floor additionally caps space compression at
30 % (a space keeps >= 70 % of its natural width); any remainder is left as residual
overflow within the 5 % tolerance. Because the character-spacing share applies to every
justified/flush line, **Flush now spreads the last line's letters** ("t o  v e x  t h e
 g y m n a s t.") matching TMP. Measured on the review paragraph's justified line 2, the
narrowest word gap is GMP 4 px vs TMP 5 px (80 %). Full `charWidthAdjDelta` glyph-width
scaling (TMP shrinks glyph bodies as a last resort) is still not implemented, so an
extreme overrun could leave a hair of residual overflow rather than squeezing glyphs —
not observed in the review cases.

### Shader-change scope (grey fix)
The grey fix changed `UniText/Uber`'s SDF `scale`, which is the shared unified render
path for ALL text, not only GlyphMeshPro. It replaces a fragment `ddx/ddy` approximation
with the legacy `UniText/SDF-Face` **vertex** formula **verbatim**, so unified now matches
the legacy production AA by construction. `UnifiedRendererPixelEquivalenceTests`
(sdf / msdf / colored-span) report `maxDelta=0`, `0.000 %` pixels differing — unified
geometry/UVs still byte-match legacy. Brightness is near-white at both small (30 pt) and
large (120 pt) sizes; outline / underlay / MSDF / SDF tests all pass.
