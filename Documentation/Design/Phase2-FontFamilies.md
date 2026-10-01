# OpenGlyph Phase 2 — Font families, real bold/italic, variable fonts

Design doc. Clean-room (see `NOTICE.md`): grounded only in OpenGlyph/UniText 1.0's own
MIT code and public specs (OpenType `fvar`/`avar`/`STAT`/`OS/2`/`head.macStyle`, the CSS
Fonts font-matching algorithm, FreeType `FT_Set_Var_Design_Coordinates`, HarfBuzz
`hb_font_set_variations`). Nothing here is taken from UniText 2.0/Platinum or the
PolyForm-licensed branches.

## 1. How 1.0 represents fonts today (observed, not assumed)

### 1.1 `UniTextFont` (`Runtime/FontCore/UniTextFont.cs`)
- A `ScriptableObject`. One asset == **one face** == one TTF/OTF blob (`fontData`),
  identified by `FontDataHash` (`ComputeFontDataHash`, a sampled FNV over the bytes).
- Carries `FaceInfo` (family/style name, metrics), `UnitsPerEm`, `FontScale`, and a single
  serialized `atlasRenderMode` (`UniTextRenderMode` = SDF | Msdf | Smooth | Mono), resolved
  at runtime to `EffectiveRenderMode` (Msdf degrades to SDF when the native outline export is
  absent). Pixel/bitmap handling via `pixelFontInfo` + `pixelPerfect`.
- **Glyph atlas cache**: `glyphLookupDictionary : Dictionary<uint, Glyph>` keyed by **glyph
  index only**; `characterLookupDictionary : Dictionary<uint, UniTextCharacter>` keyed by
  **codepoint only**. Atlas pixels live in `atlasTextures`. There is **no style or variation
  component in either key** — a face asset holds exactly one appearance.
- `ItalicStyle` (default 30° serialized; 12° used by the live modifier path) is the **synthetic
  shear** angle — a property of the asset, not evidence of a real italic face.

### 1.2 Fallback chains (`UniTextFontStack`, `UniTextFontProvider`)
- `UniTextFontStack` is an **ordered list of faces** (`fonts[0]` = main) plus an optional
  `fallbackStack`. `FindFontForCodepoint(unicode)` walks the resolved flat list (emoji font
  first for pictographics) and returns the first face whose `Shaper.GetGlyphIndex != 0`.
- The chain is **purely codepoint-coverage based**. There is no notion of "the bold member of
  this family" or "the italic member" — weight/width/style do not exist as selectable axes.
- `UniTextFontProvider` maps `fontId (= FontDataHash)` → face, caches codepoint→fontId in
  `SharedFontCache`, and owns per-face materials via `UniTextAppearance`.

### 1.3 Shaping / glyph-index cache (`Runtime/Core/Shaper.cs`, `Runtime/Native/HB.cs`)
- `Shaper.FontCacheEntry` wraps **one `hbFont` per `FontDataHash`**, with `glyphCache` and
  `advanceCache` both keyed by glyph index. One HarfBuzz font object per face, no variation
  state attached.

### 1.4 Bold / italic today — confirmed synthetic / markup-only
- `<b>` → `BoldParseRule` → `BoldModifier`: SDF **dilation in the shader** (`_WeightBold` vs
  `_WeightNormal`) plus an advance correction (`em/24 * weightDelta`). SDF/MSDF only; no effect
  on bitmap. **It never swaps to a real bold face** — there is no bold face to swap to.
- `<i>` → `ItalicParseRule` → `ItalicModifier`: **vertex shear** by `font.ItalicStyle`. Skipped
  for color glyphs. Again, **no real italic face** is ever selected.
- Markup tags are matched by `TagParseRule` (case-insensitive, nestable, open/close pairs).

### 1.5 Variable-font native layer — already present (Phase 0/1)
`Runtime/Native/FT.Variations.cs` (`FTVar`) already binds the full VF ABI, and the native DLL
exports it (confirmed by `MsdfArtifactTests` reading RobotoFlex-VF):
- `TryGetVariationInfo(face) → axisCount, namedInstanceCount`
- `TryGetAxis(face, i) → VarAxis {tag, min, def, max, nameId}`
- `TryGetNamedInstance(face, i) → nameId, coords[]`
- `SetDesignCoordinates(face, coords)` (FreeType design space; **avar is applied inside
  FreeType**), `SetNamedInstance(face, idx)`
- `SetHbVariations(hbFont, tags, values)` (HarfBuzz user space, for shaping)
- `Tag("wght")` 4CC helper.

**Gap this phase fills:** nothing above connects (a) a *family* of faces by weight/width/style,
(b) `<b>`/`<i>` to a *real* face, or (c) a *variation instance* to the atlas/shaper caches. The
substrate exists; the family/selection/variation-keying layer does not.

---

## 2. Design

### 2.0 New type: `FontStyleSpec` (the matching key)
A small value type that captures what CSS matches on, used everywhere a face is requested:
```
readonly struct FontStyleSpec {
    int   weight;    // 1..1000, CSS usWeightClass scale (400 normal, 700 bold)
    float width;     // % of normal, OS/2 usWidthClass mapped (100 normal, 75 condensed…)
    StyleAxis style; // Normal | Italic | Oblique
    float slant;     // oblique angle in degrees (0 for Normal/Italic); CSS 'oblique <deg>'
}
enum StyleAxis { Normal, Italic, Oblique }
```
Default = `{400, 100, Normal, 0}`.

### 2.a `FontFamily` asset — faces grouped by weight/width/style, CSS nearest-match
New `ScriptableObject` `FontFamily` (`Runtime/FontCore/FontFamily.cs`):
```
class FontFamily : ScriptableObject {
    string familyName;
    List<FamilyFace> faces;   // each: UniTextFont face + its FaceStyle metadata
    // selection:
    UniTextFont Match(FontStyleSpec spec, out FaceMatch how);
}
struct FaceStyle { int weight; float width; StyleAxis style; float slant; }
```
- **Where face metadata comes from** (auto-populated on import, overridable in the inspector),
  all from OpenType tables already reachable via FreeType:
  - weight ← `OS/2.usWeightClass`;
  - width ← `OS/2.usWidthClass` (1..9 → %), 
  - italic ← `OS/2.fsSelection` ITALIC bit / `head.macStyle` italic bit;
  - oblique/slant ← `STAT` or `post.italicAngle` when present.
  - For a **variable** member, the family stores the axis ranges so one VF face can satisfy a
    whole region of the matrix (see 2.c).
- **Selection = the CSS Fonts font-matching algorithm**, applied in this fixed order
  (width → style → weight), each stage narrowing the candidate set:
  1. **Width** (`font-stretch`): exact; else if desired ≤ 100% choose nearest narrower, then
     nearest wider; if desired > 100% choose nearest wider, then nearest narrower.
  2. **Style** (`font-style`): prefer exact (`Normal`/`Italic`/`Oblique`); the documented CSS
     substitution order is italic↔oblique before falling back to normal. For `oblique <angle>`
     pick the oblique face with the closest angle in range.
  3. **Weight** — the exact CSS ladder:
     - **400** → try 400, then 500, then <400 descending, then >500 ascending.
     - **500** → 500, 400, 300…, then 600 ascending.
     - **weight < 400** → that weight descending, then ascending.
     - **weight > 500** → that weight ascending, then descending.
     - (600 missing → 700; 300 missing → 200 → 100 — the brief's examples, which this ladder
       produces.)
- `FaceMatch`/`how` reports whether the result was exact or substituted, and whether synthesis
  will be applied — surfaced for the warning in 2.b and for tests.
- **Back-compat:** `UniTextFontStack` is unchanged and still works. A `FontFamily` is a *member*
  usable inside a stack; a stack entry may be a plain face (today) or a family (new). Codepoint
  fallback still runs across stack entries exactly as now; family selection happens *within* an
  entry before codepoint fallback consults the next entry.

### 2.b Real bold/italic with synthetic fallback (only when no real face)
- A per-run **requested `FontStyleSpec`** flows from (i) the component's style settings and
  (ii) `<b>`/`<i>` markup. `<b>` sets `weight = max(current, 700)`; `<i>` sets
  `style = Italic`. These compose (`<b><i>` → 700 + Italic). Markup parsing is unchanged
  (`BoldParseRule`/`ItalicParseRule` already emit the ranges); what changes is the *consumer*.
- **Resolution per run**: ask the active `FontFamily` to `Match(spec)`.
  - If a **real face** exists for the requested weight/style → shape and rasterize that face.
    The synthetic `BoldModifier`/`ItalicModifier` effect is **suppressed** for that run (no
    double-bolding, no shear on an already-italic face).
  - If **no real face** exists → fall back to the existing synthetic path (SDF dilation /
    vertex shear) and **emit a one-time warning per (family, missingStyle)** so it is visible
    but not spammy, e.g. `[Family] "Noto Sans" has no 700 Italic face; synthesising from 400.`
  - A plain face with no family behaves exactly as 1.0 (always synthetic) — no regression.
- **Advance/metrics**: a real bold face brings its own advances through shaping (no `em/24`
  correction applied); synthetic bold keeps the existing correction. This is the only subtle
  layout difference and is intended.

### 2.c Variable fonts — axes, instances, per-run variation, variation-keyed caches
- **Read** `fvar` axes + named instances through the existing `FTVar` exports at import
  (store on the face: axis tags/min/def/max, named-instance coord sets). No new native code.
- **Map** the requested `FontStyleSpec` → axis coordinates:
  - weight → `wght` (clamped to axis range), width → `wdth`, italic → `ital` (0/1) or `slnt`
    (negative degrees) per which axis the font exposes, optical size → `opsz` driven by the
    render point size. `avar` is honoured automatically because we set **design coordinates via
    FreeType** (`FTVar.SetDesignCoordinates`), which applies the `avar` segment map; HarfBuzz
    gets the **user-space** values via `FTVar.SetHbVariations` so shaping advances match.
  - Prefer a **named instance** when the request lands exactly on one (`SetNamedInstance`),
    else set explicit design coords.
- **The cache-key change (core of 2.c).** Introduce a `GlyphVariation` key = quantized axis
  coordinates (fixed-point, e.g. 16.16 rounded, same quantization used to set FT coords so
  "equal key ⇒ identical raster"). Then:
  - Atlas glyph cache key becomes **`(glyphIndex, variationKey)`** instead of `glyphIndex`.
    Implementation: a `variationKey`-indexed set of `glyphLookupDictionary`s (or a composite
    `long`/struct key) so **wght 400 and wght 700 never share an atlas entry**.
  - Shaper `FontCacheEntry` gains a **per-variation `hbFont`** (clone + `hb_font_set_variations`)
    with its own `glyphCache`/`advanceCache`, keyed by `variationKey`; and the FreeType face
    used for rasterization has `SetDesignCoordinates` applied for that key before rendering.
  - `fontId` for provider/material purposes stays `FontDataHash` (one VF asset = one material),
    but the **render/atlas identity** is `(FontDataHash, variationKey)`.
- **Render modes**: the variation coordinates are applied to the FreeType face *before*
  rasterization, so SDF, MSDF (outline export reads the varied outline), Smooth, Mono and pixel
  all produce the varied shape with no per-mode special-casing. COLR/emoji (`EmojiFont`) are
  color and generally non-variable here; variation is a no-op for them and italic shear is
  already skipped for color glyphs (`ItalicModifier` guards `font.IsColor`).
- **Fallback chain** is unchanged in structure; a variation request simply travels with the run
  and is applied to whichever face the codepoint resolves to (non-variable faces ignore it).

---

## 3. Files to touch (identified)

New:
- `Runtime/FontCore/FontFamily.cs` — the family asset + CSS `Match`.
- `Runtime/FontCore/FontStyleSpec.cs` — `FontStyleSpec`, `StyleAxis`, `FaceStyle`, `FaceMatch`.
- `Runtime/FontCore/VariationKey.cs` — quantized axis-coordinate key + hashing.
- Tests: `Tests/Editor/FontFamilyMatchTests.cs`, `MarkupRealFaceTests.cs`,
  `VariableFontAxisTests.cs`, `VariationCacheKeyTests.cs`, `VariationShapingTests.cs`,
  and `Phase2EvidenceTests.cs` (Step 3 render sheet).

Modified (minimal, additive):
- `Runtime/FontCore/UniTextFont.cs` — variation-keyed glyph/atlas lookup; apply
  `SetDesignCoordinates` before raster; expose variation metadata read via `FTVar`.
- `Runtime/Core/Shaper.cs` — per-variation `hbFont` + caches keyed by `variationKey`.
- `Runtime/FontCore/UniTextFontStack.cs` / `UniTextFontProvider.cs` — allow a family as a stack
  member and thread the requested `FontStyleSpec`/variation through resolution.
- `Runtime/Core/TextProcessor.cs` (+ `BoldModifier`/`ItalicModifier`) — resolve `<b>`/`<i>` to a
  real face when available and suppress synthesis for that run; one-time warning otherwise.
- `Editor/UniTextFontEditor.cs` / a new family editor — inspector for family faces + auto-fill
  from OpenType tables (editor-only, not required for the EditMode tests).

Test fixtures:
- Variable: `NativeSource~/tests/fonts/RobotoFlex-VF.ttf` (13 axes, 20 named instances),
  resolved via `MsdfTestUtil`'s `PackageInfo.FindForAssembly` pattern.
- Static family (only if needed): OFL Noto Sans Regular/Bold/Italic/BoldItalic, license file
  recorded next to them.

---

## 4. Open decisions for the user
- **Optical size (`opsz`) policy**: auto-drive from render point size (proposed) vs
  explicit-only. Auto changes glyph shape with size, which some users will not expect.
- **Synthetic-when-real-exists**: proposed to *suppress* synthetic bold/italic once a real face
  is chosen. Alternative: allow stacking (faux-bold a real italic) behind an opt-in flag.
- **Variation cache memory**: each distinct variation key is a distinct atlas region. A naive
  animation sweeping `wght` would explode the atlas. Proposed: quantize keys (coarse by default)
  and document the cost; optionally cap/evict. Needs a default quantization step decision.
