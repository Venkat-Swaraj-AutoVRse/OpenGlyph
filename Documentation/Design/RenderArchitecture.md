# OpenGlyph Rendering Architecture — Design & Migration Plan (Round 1)

Status: **DRAFT for review.** Branch `openglyph/render-arch`, based on
`openglyph/phase2-families` (f8a6a41) merged up to `openglyph/main` (6b9c3f1,
static-CRT native DLLs).

Clean-room note: this document is derived solely from reading the OpenGlyph
(UniText 1.0 MIT) source in this repository and from public graphics/Unity
knowledge. Nothing here is taken from UniText 2.0/Platinum or any unlicensed
upstream.

> Scope of Round 1: **design + migration plan + a feasibility spike.** No
> production code is changed. All source references below are to files in this
> repository as read on 2026-10-01.

---

## 1. Current rendering path (end to end), with numbers

### 1.1 Pipeline summary

```
text + markup
  -> TextProcessor / Shaper (HarfBuzz via native)      Runtime/Core/TextProcessor.cs, Shaper.cs
  -> TextLayout (line breaking, bidi, segmentation)     Runtime/Core/TextLayout.cs, Unicode/*
  -> PositionedGlyph[]  (glyphId, fontId, x, y, variationKey, realBold/Italic)
  -> UniTextMeshGenerator.GenerateMeshDataOnly()        Runtime/Core/UniTextMeshGenerator.cs
       group glyphs BY fontId, then BY atlasIndex
       -> one GeneratedMeshSegment per (fontId, atlasIndex)
          each segment carries { materials[], texture }
  -> UniTextMeshGenerator.ApplyMeshesToUnity()
       -> one UniTextRenderData(mesh, materials[], texture) per segment
  -> UniText.UpdateSubMeshes()                          Runtime/Core/Component/UniText.cs
       -> one child GameObject "-_UTSM_-" with its own CanvasRenderer PER segment
          CanvasRenderer.SetMesh(mesh)
          CanvasRenderer.materialCount = materials.Length   // 1, or 2 for outline+face
          CanvasRenderer.SetMaterial(mat, i) per pass
          CanvasRenderer.SetTexture(atlasTexture)
```

### 1.2 Where the draw calls come from (confirmed from source)

The `[RequireComponent(typeof(CanvasRenderer))]` host (`UniText.cs:32`) keeps a
`List<SubMeshRenderer>` (`UniText.cs:183`), one entry per render segment, each
entry being a **child GameObject with its own `CanvasRenderer`**
(`CreateSubMeshRenderer`, `UniText.cs:1244`+). Each `CanvasRenderer` is given
**one atlas texture** (`SetTexture`, `UniText.cs` `SetSubMeshRendererData`) and a
material array of length 1 (single pass) or 2 (2-pass outline-then-face;
`UniTextAppearance.materials[0]=outline, [1]=face`).

Segments are produced in `UniTextMeshGenerator.GenerateMeshDataOnly()`:

- Glyphs are grouped **by `fontId`** (`glyphsByFont`), and within a font **by
  `atlasIndex`** when the font has more than one atlas page
  (`hasMultipleAtlases`). Each group emits one `GeneratedMeshSegment` carrying
  that font's `materials` (from `fontProvider.GetMaterials(fontId)` →
  `UniTextAppearance.GetMaterials`) and that page's `texture`.
- Emoji (`EmojiFont`) is its own `fontId` with its own `EmojiFont.Material` and
  its own RGBA atlas.

Because Unity UI batches only CanvasRenderers that share **the same material AND
the same texture**, and every segment here has a distinct texture (different
font atlas / different atlas page / emoji atlas) and often a distinct material
(SDF vs MSDF vs emoji; outline pass vs face pass), **no two segments batch
together**. Therefore:

```
draw calls per UniText component  ≈  Σ over fonts used ( atlasPages(font) × passes(font) )
                                     + emoji atlas groups
```

where `passes = 2` when the font's appearance supplies an outline+face material
pair, else `1`.

### 1.3 Worked example (the Round-2 spike target)

A single piece of text that uses **3 SDF fonts + emoji + 1 MSDF font**, each font
currently fitting in one 1024² atlas page, outline enabled on the SDF fonts
(2-pass):

| Font group        | atlas pages | passes | CanvasRenderers (= draw calls) |
|-------------------|-------------|--------|--------------------------------|
| SDF font A        | 1           | 2      | 2                              |
| SDF font B        | 1           | 2      | 2                              |
| SDF font C        | 1           | 2      | 2                              |
| MSDF font         | 1           | 1      | 1                              |
| Emoji (COLR→RGBA) | 1           | 1      | 1                              |
| **Total**         |             |        | **8 draw calls for one text**  |

With `k` such components on a canvas and no cross-component batching possible
(distinct textures), the canvas pays **≈ 8 × k** text draw calls. Even with
outline off (single pass) it is **5 × k**. A second atlas page on any font adds
one more per page. This is the "n draw calls (one per font texture)" the project
wants to collapse to **1**.

> These are structural counts read from the code. Round 2's spike will replace
> them with Frame Debugger / `UnityStats.drawCalls` + `ProfilerRecorder("Draw
> Calls Count","Batches Count")` measurements on real scenes (overlay + world
> space), before and after.

### 1.4 Current atlas / glyph memory model

`UniTextFont` (`Runtime/FontCore/UniTextFont.cs`) holds
`List<Texture2D> atlasTextures` (`:69`), `atlasSize = 1024` (`:73`),
`atlasRenderMode` (SDF/MSDF/Smooth/Mono/color; `:82`). Glyphs are placed by a
**shelf packer** (`TryPackGlyphShelf`, `:1355`): advance `shelfX` along a row,
drop to a new shelf row (`shelfY += shelfHeight`) when the row is full, and when
the page is full (`shelfY + ph > atlasSize`) a **new page is allocated**
(`CreateNewAtlasTexture`, `:1378`).

Atlas texture format by mode (`CreateNewAtlasTexture`):

| Mode              | TextureFormat | Bytes/px | 1024² page size |
|-------------------|---------------|----------|-----------------|
| SDF               | Alpha8        | 1        | 1.0 MB          |
| MSDF              | RGB24         | 3        | 3.0 MB          |
| Smooth/Mono (cov) | Alpha8        | 1        | 1.0 MB          |
| Color (emoji)     | RGBA32        | 4        | 4.0 MB          |

**There is no eviction, no LRU, and no compaction.** `freeGlyphRects` is seeded
but the live path is pure shelf-append; glyphs are only cleared wholesale when
the font is reset (`:354`). Variable-font instances make this worse: Phase 2
keys varied glyphs per `VariationKey` (wght 400 vs 700 occupy their own cells —
`GenerateMeshDataForFont` resolves `font.TryGetGlyph(glyphId, variationKey)`), so
every used instance permanently consumes atlas area until the font is reset.
Memory grows monotonically with the set of (glyph × variation) ever displayed.

### 1.5 Shaders (current)

`Shaders/` contains a large matrix of per-mode, per-canvas, per-platform shader
variants: `UniText_SDF*`, `UniText_MSDF*`, `UniText_Bitmap*`, each with
`-Overlay` (Screen Space Overlay), `-SSD` (Screen Space Camera / World),
`-Mobile`, `-Masking`, `-2-Pass`, `-Surface` forms. The shading model
(`UniText_Properties.cginc`) is TMP-style: `_FaceColor/_FaceDilate`,
`_OutlineColor/_OutlineWidth/_OutlineDilate/_OutlineSoftness`,
`_UnderlayColor/_UnderlayOffset*/_UnderlayDilate/_UnderlaySoftness`,
`_GlowColor/_GlowOffset/_GlowOuter/_GlowInner/_GlowPower`, bevel, bump, env/cube.
All of these are **per-material uniforms** — i.e. one style set per material, so
differing styles force differing materials, which breaks batching.

MSDF differs from SDF only in the distance *sample*: `UniText_MSDF.cginc`'s
`UniTextMedian3`/`UniTextSampleMSDF` replace `tex2D(_MainTex, uv).a` with the
median of RGB; everything downstream (bias/scale, outline, glow, underlay) is
identical. **This is the key enabler for a single uber-shader**: the modes
diverge at one line.

---

## 2. Goals this architecture must satisfy

From the approved goal list:

1. Remove `Material` assets and `UniTextAppearance`; style configured **on the
   component**.
2. Styles (face colour, outline, underlay/shadow, glow, softness, dilation,
   gradient…) applied to the **whole text OR specific ranges** (per-span: two
   different outlines/shadows in one text).
3. Draw calls from n → **1** per text where format allows.
4. Glyph memory management: refcount/LRU eviction, page reuse, compaction;
   bounded under variable fonts.
5. Font compression: smaller builds (target example 12 MB → 4.4 MB, sub-ms
   decompression).
6. **Migration path (hard requirement):** editor tool converting existing
   `UniTextAppearance`/material settings on scenes & prefabs to the new
   component styles, plus a deprecation period where old assets still load and
   render identically.

---

## 3. Proposed architecture (recommended design)

### 3.1 One shader, one texture, one draw call

**One CanvasRenderer per component.** Collapse `renderData`'s n segments into a
single mesh drawn by a single `CanvasRenderer` with a single material and a
single texture binding. The three things that currently force a split — texture,
shader mode, and style — are each moved off the material and into data the one
shader can read per-glyph.

- **Texture → `Texture2DArray` (atlas pages as array slices).** All atlas pages
  of all fonts used by the component become slices of one `Texture2DArray`; the
  per-vertex data carries a `sliceIndex`. One texture binding, so one draw call.
  Emoji (RGBA) and SDF/MSDF (Alpha8/RGB24) have different formats and cannot
  share one array — see §3.3 for the layered-array resolution.
- **Shader mode → per-glyph selector in vertex data.** A `glyphMode` field
  (SDF=0, MSDF=1, bitmap/coverage=2, COLR/premult=3) selects the sampling +
  reconstruction branch inside one uber-shader. MSDF/SDF already differ by one
  line (§1.5); bitmap and COLR are additional small branches. Branch is on data
  that is uniform across a glyph's 4 verts, so it is coherent per primitive.
- **Style → per-glyph/per-span style index into a style buffer.** The whole TMP
  style set (face/outline/underlay/glow/softness/dilate/gradient) is packed into
  a **style table** uploaded once per component (a small `StructuredBuffer`, or a
  float texture on platforms without SRV in UI — §3.4). Each vertex carries a
  `styleIndex`; a span with its own outline/shadow is just a different index.
  Per-span multi-layer effects (outline + shadow in one pass) are shaded in a
  single pass by evaluating the layers in the fragment shader from the style
  record (§3.4), not by emitting extra quads.

Result: `3 fonts + emoji + MSDF + per-span outline` → **1 mesh, 1 material, 1
texture-array binding, 1 draw call** (modulo the emoji-format caveat in §3.3,
which costs at most +1).

### 3.2 Per-glyph vertex layout (proposed)

CanvasRenderer meshes expose POSITION, COLOR, UV0..UV3 (and normal/tangent).
Current code uses UV0 (`uv.xy` atlas UV, `uv.z` gradientScale, `uv.w` xScale) and
UV1 (`spreadRatio`). Proposed packing:

| Channel | x            | y            | z              | w            |
|---------|--------------|--------------|----------------|--------------|
| UV0     | atlasU       | atlasV       | gradientScale  | xScale       |
| UV1     | spreadRatio  | **sliceIdx** | **glyphMode**  | **styleIdx** |
| COLOR   | per-vertex tint (face colour / gradient stop) — unchanged                 |

`sliceIdx`, `glyphMode`, `styleIdx` are small non-negative integers packed as
floats (exactly representable to 2^24). This keeps the existing two UV channels
and adds no new vertex streams. Alternative packing (style data in vertex
attributes rather than a buffer) is evaluated and rejected in §3.4.

### 3.3 Single-texture strategy — decision

Three candidates (Unity UI constraint: a `CanvasRenderer` binds exactly one
texture via `SetTexture`, and UI batching requires identical material+texture):

- **(A) `Texture2DArray` with atlas pages as slices — RECOMMENDED.** One binding,
  per-vertex slice index. Supported on all current OpenGlyph targets: GLES3 /
  Vulkan (Quest), D3D11, Metal, and **WebGL2** (`sampler2DArray` is core in WebGL
  2.0 / GLES3). All slices must share dimensions and format. Mixed formats
  (Alpha8 SDF/MSDF-as-RGB24 vs RGBA32 emoji) cannot live in one array, so use
  **two arrays**: one coverage/distance array (promote SDF Alpha8 and MSDF RGB24
  to a common RGBA32 or keep two sub-arrays) and one color array for emoji. In
  the common text-only case there is exactly one array → one draw call; text +
  emoji costs **2** (still a massive win over n). A UI mesh can bind only one
  texture per CanvasRenderer, so a true 1-call text+emoji needs the color glyphs
  packed into the same array (promote all to RGBA32) — viable but 4× the SDF
  memory; offered as an option, not the default.
- **(B) One large shared atlas (e.g. 4096²/8192²).** One plain `Texture2D`, no
  array feature needed → widest compatibility, simplest shader. But a single
  8192² RGBA is 256 MB and 8192 is not guaranteed on older GLES3/WebGL2
  (max-texture-size varies; Quest is 8192 but low-end WebGL2 can be 4096).
  Repacking many fonts into one atlas also complicates eviction. Good fallback
  for a "max compatibility" quality tier.
- **(C) Bindless textures.** Not available in Unity UI / CanvasRenderer and not
  on GLES3/WebGL2. Rejected.

**Decision: (A) Texture2DArray** as the primary path, with **(B) single large
atlas** as an automatic fallback quality tier for devices that report no/limited
array support. Emoji handled as a second array/binding (text+emoji = 2 calls) by
default; an opt-in "unified RGBA array" setting reaches a literal single call at
a memory cost.

### 3.4 Per-span style data — decision

- **Style buffer (RECOMMENDED): `StructuredBuffer<GlyphStyle>` indexed by
  `styleIdx`.** One upload per component of the distinct styles in the text
  (typically 1–8; a reasonable cap of 64/text). The fragment shader reads the
  face/outline/underlay/glow/softness/dilate/gradient record for the glyph and
  shades **all layers in one pass**: distance `d` is sampled once, then face,
  outline (`d` thresholded at outline width), and underlay/shadow (offset sample
  of the same slice) are composited in-shader. No extra quads. StructuredBuffer
  in a UI shader is supported on GLES3.1+/Vulkan/D3D11/Metal.
- **Fallback: style data in a float texture** (`styleIdx` → row), sampled with
  `tex2Dlod`. Needed on **WebGL2/GLES3.0** where compute-style SRV buffers in the
  UI shader are not guaranteed. Same shader math, different fetch. This is the
  portable default; StructuredBuffer is the fast path where available.
- **Rejected: packing full style into vertex attributes.** The TMP style set is
  far larger than the free UV/tangent channels; it would bloat every vertex 4×
  and still cap the style richness. Only the small *index* goes in the vertex.

Per-span outline/shadow therefore costs **no extra geometry and no extra pass** —
it is a different `styleIdx` on that span's vertices, resolved to a different
record composited in the same fragment program.

### 3.5 Glyph memory management (goal 4)

Replace the append-only shelf with a **managed atlas**:

- **Refcounted glyph residency.** Each atlas cell carries a refcount =
  components currently displaying it. A per-frame "glyph used" set (the pipeline
  already groups glyphs per font per frame) feeds an LRU timestamp.
- **LRU eviction + free-list.** `freeGlyphRects` (already present but unused on
  the hot path) becomes the real free list. When a page is full and new glyphs
  are needed, evict least-recently-used, refcount-0 cells and reuse their rects
  (bucketed by size class to limit fragmentation).
- **Compaction.** When fragmentation passes a threshold, repack live cells into a
  fresh page and retire the old one; the generation counter invalidates stale
  UVs so meshes rebuild. Runs incrementally / off the hot frame.
- **Budget settings** on `UniTextSettings` (per-mode page count / MB cap). Over
  budget → force eviction before new allocation.
- **Variable fonts:** `VariationKey`-keyed cells are the biggest consumer;
  refcount + LRU bounds them to the working set actually on screen, instead of
  "every instance ever shown."

### 3.6 Font compression (goal 5)

Store a compressed font blob in the asset; decompress once at load into the
in-memory face FreeType already consumes.

- **Options & estimates** (target: 12 MB → ~4.4 MB, sub-ms decompress):
  - **Subsetting** (drop unused glyphs/tables): by far the biggest win for app
    fonts with known coverage; combine with the below. Editor-time.
  - **Brotli of glyf/loca (WOFF2-style)** or **zstd/LZ4 of the whole file.**
    zstd level ~19 on a TTF commonly reaches ~0.35–0.40× (12 MB → ~4.3–4.8 MB),
    matching the published figure; zstd decompresses at GB/s so a 12 MB font is
    well under a millisecond on desktop and a few ms worst case on mobile — load
    time, not per-frame. LZ4 is faster to decompress but compresses less (~0.6×).
  - **Recommendation:** subset where coverage is known + **zstd** whole-file for
    the general case (best ratio/robustness, sub-ms decompress). WOFF2/Brotli is
    an alternative if an existing Brotli dependency is preferred. Decompression
    runs off-thread on the existing `UniTextWorkerPool`.

### 3.7 Migration (hard requirement, goal 6)

Data model mapping (every source property → component style field):

| Source (`UniTextAppearance` / `UniTextMaterial_*.mat`) | New (component style) |
|--------------------------------------------------------|------------------------|
| `defaultMaterials[0]` face props (`_FaceColor`, `_FaceDilate`, `_OutlineSoftness`) | component default style: face colour, softness, dilate |
| `_OutlineColor/_OutlineWidth/_OutlineDilate`           | style.outline {colour,width,dilate} |
| `_UnderlayColor/_UnderlayOffsetX/Y/_UnderlayDilate/_UnderlaySoftness` | style.underlay (shadow) |
| `_GlowColor/_GlowOffset/_GlowOuter/_GlowInner/_GlowPower` | style.glow |
| gradient (`UniTextGradients`)                          | style.gradient |
| 2-pass pair `materials[0]=outline,[1]=face`            | single-pass multi-layer style (outline+face layers) |
| per-font material override (`fontMaterials`)           | per-font default style on the font family |
| emoji `EmojiFont.Material`                             | built-in COLR glyphMode, no material |

Mechanism:

1. **Deprecation period.** Keep `UniTextAppearance` and the material path alive
   behind `[Obsolete]`. A compatibility shim reads an assigned appearance/material
   at load and *synthesises* the equivalent component style at runtime, so **old
   assets load and render identically** with zero scene edits. Both paths ship in
   the same release window.
2. **Editor migration tool.** Menu `Tools/OpenGlyph/Migrate Appearances…` plus an
   automatic `AssetPostprocessor`/`OnPostprocessAllAssets` hook that, on import or
   on demand, walks scenes & prefabs, finds `UniText` components referencing an
   appearance/material, writes the mapped component style inline, and clears the
   obsolete reference. Idempotent; dry-run report first; backs up via version
   control (never touches user assets without a preview list).
3. **VRseBuilder assets.** VRseBuilder consumes OpenGlyph as a package; its scenes
   & prefabs are migrated by the same tool run against its project (by the
   VRseBuilder team, not from here — this worktree must never touch
   `D:\Unity\VRseBuilder-Starter-Project`). The deprecation shim means VRseBuilder
   keeps rendering before it runs the migration.
4. **Removal** of `UniTextAppearance` + material assets only after the deprecation
   window, in a later phase.

---

## 4. Alternatives summary (trade-offs)

| Axis | Recommended | Alternatives (rejected / fallback) |
|------|-------------|--------------------------------------|
| Shader | One uber-shader, per-glyph `glyphMode` branch | Shader variants/keywords: more variants to strip, re-breaks batching across modes → rejected as primary |
| Texture | Texture2DArray (pages=slices) | Large shared atlas (compat fallback tier); bindless (unsupported → rejected) |
| Per-glyph style | StructuredBuffer style table + per-vertex index | Float-texture style table (WebGL2/GLES3.0 fallback); full style in vertex attrs (bloat → rejected) |
| Emoji | 2nd array binding (text+emoji=2 calls) default | Unified RGBA array for literal 1 call (4× SDF memory → opt-in) |
| Memory | Refcount + LRU + compaction + budget | Keep append-only (unbounded under variable fonts → rejected) |
| Compression | Subset + zstd whole-file, off-thread | WOFF2/Brotli (alt); LZ4 (faster, weaker ratio) |
| Migration | Obsolete shim (identical render) + editor rewrite tool | Hard break (violates user condition → rejected) |

---

## 5. Decisions (RESOLVED by the conductor, Round 2) — these are now binding

The Round-1 open questions were decided by the conductor for the Round-2
implementation. They supersede the "options" framing above; the alternatives are
retained only as rejected-path rationale.

1. **Two draw groups max per canvas batch (text + emoji).** A canvas batch uses at
   most **two** array bindings: an **Alpha8 `Texture2DArray`** for SDF / coverage
   bitmap / pixel (1 B/px), and an **RGBA32 `Texture2DArray`** for **MSDF + COLR
   emoji**. We do **not** promote everything to one RGBA32 array (that would be 4×
   memory for SDF on Quest). So text-only = 1 draw call; text+emoji (or text+MSDF)
   = 2. The "unified single RGBA array" idea is dropped.
   - Consequence: MSDF, previously RGB24, now lives in the shared **RGBA32** emoji
     array (median-of-three reads .rgb; .a unused for MSDF). This keeps array
     slice formats to exactly two.
2. **Style table = a float `Texture2D` style table.** Works on GLES3.0 / WebGL2
   with no compute-buffer requirement. `StructuredBuffer` is **not** used — one
   portable path, not two. Each glyph vertex carries a `styleIdx`; the fragment
   shader samples the style record from the float texture.
3. **Platform floor = GLES3.0 / WebGL2.** Quest runs via Vulkan / GLES3. This is
   what forces decision 2 (float-texture, no SRV buffers in the UI shader) and
   permits `sampler2DArray` (core in GLES3 / WebGL2).
4. **Font compression = zstd**, implemented in a **later step** (not this round).
5. **Atlas budget defaults = decided later from the Round-2 benchmark numbers.**
   This round ships the budget *mechanism* (refcount + LRU + page reuse) with the
   default of **no eviction** (budget unset ⇒ behaviour unchanged), and the
   benchmark in step 4 produces the numbers the defaults will be set from.

---

## 6. Recommended design (one line)

**One CanvasRenderer / one uber-shader / one `Texture2DArray` per text, with
per-glyph `(sliceIdx, glyphMode, styleIdx)` packed in UV1 and a per-component
style table; refcounted-LRU managed atlas with compaction and a budget; subset +
zstd font compression; and a `[Obsolete]` compatibility shim plus an editor
rewrite tool so existing `UniTextAppearance`/material assets keep rendering
identically through a deprecation window.** Draw calls drop from
`Σ(atlasPages × passes)` per text to **1** (text-only) or **2** (text+emoji,
default), independent of font count, span count, or outline/shadow use.

---

## 7. Feasibility spike results (Round 1, 2026-10-01)

The riskiest claim — collapsing a mixed 3-fonts + emoji + MSDF + per-span-outline
text to **one draw call** — was prototyped (`Spike~/RenderArch/`, throwaway) and
measured on Unity 6000.3.19f1:

- One `Texture2DArray` (5 slices: 3 SDF + 1 MSDF + 1 emoji/COLR), one uber-shader
  with a per-glyph `glyphMode` branch, and a per-glyph/per-span `styleIdx` into a
  `StructuredBuffer<GlyphStyle>` composited face + outline + underlay in one pass.
- Measured structural CanvasRenderer count (= UI draw-call groups):
  **Overlay 5 → 1, World 5 → 1.** The uber-shader compiled with no errors; the
  array + buffer material bound and rendered through a single CanvasRenderer
  across all five mixed-mode runs including the per-span outline. VERDICT: **PASS**.
- `supports2DArrayTextures = True`, `maxTextureSize = 8192` on the test editor.
- GPU-submit counters (`UnityStats.drawCalls`) are 0 under `-nographics`; the
  structural count is what determines UI draw calls. Round 2 confirms on a real
  GPU with Frame Debugger + `ProfilerRecorder("Draw Calls Count","Batches Count")`
  and CPU frame time.
- **Android/GLES3: PARTIAL.** The IL2CPP arm64 build (GLES3+Vulkan) compiled all
  scripts, resolved the bundled SDK/NDK/JDK/Gradle, and ran IL2CPP producing the
  native player **with no spike-shader compile errors**, then failed at the LLVM
  native-strip stage with `No space left on device` — the host C: drive was at 0
  bytes free. Environmental, not a design/toolchain defect. Re-run on a host with
  disk headroom to finish. WebGL2 has `sampler2DArray` but not UI
  `StructuredBuffer`, so it uses the float-texture style-table fallback (§3.4).

See `Spike~/RenderArch/README.md` for the full result dump and reproduction.

### Next (Round 2 implementation)

Wire the proposed path into the real pipeline behind the obsolete shim: replace
the per-segment CanvasRenderer split in `UniText.UpdateSubMeshes` with a single
renderer + array-atlas, add the `(sliceIdx, glyphMode, styleIdx)` vertex packing
in `UniTextMeshGenerator`, build the component style model + migration tool, and
measure before/after on the real sample scenes with a GPU present.


---

## 8. Round-2 measured results (2026-10-01) — production path behind `UseUnifiedRenderer`

The unified single-renderer path is now wired into `UniText` behind
`UniTextSettings.UseUnifiedRenderer` (+ per-component `UnifiedRenderer`
override), default **off**. Verified on Unity 6000.3.19f1 (batchmode EditMode +
a Development standalone Windows player for GPU counters).

### 8.1 Correctness
- Full EditMode suite: **188 passed / 0 failed / 1 skipped** with the flag **off**
  (default), and **identically 188 / 0 / 1** with the flag forced **on** for the
  whole suite (`UNITEXT_FORCE_UNIFIED=1` → `[SetUpFixture]`). No existing text
  test asserts legacy per-segment-renderer structure that the unified path
  breaks, so it is a faithful drop-in. (The 1 skip is a pre-existing COLR
  `DllSmoke` ignore in both modes.)
- Off-vs-on **geometry equivalence** (default appearance, "Reading 123"): the
  unified path submits a vertex-identical mesh (tol 1e-3; the merge copies
  positions verbatim) and draws through **1** `UniText/Uber` CanvasRenderer where
  legacy used several.

### 8.2 Draw calls — structural CanvasRenderer count (deterministic)
| Scenario | Legacy (off) | Unified (on) |
|----------|-------------:|-------------:|
| (a) one text, 3 SDF + MSDF | **3 renderers** | **1 renderer** |
| (b) 50 components, same mix | **150 renderers** | **50 renderers** |

The per-component renderer collapse is exactly as designed: n → 1 (text-only /
same Alpha8 group here) per component.

### 8.3 Draw calls — GPU `ProfilerRecorder` (standalone dev player, 320×240)

Initial per-component-material build (regression) then the shared-material fix:

### 8.3 / 8.4 Draw calls + CPU — CORRECTED (world-space canvas → camera → 1280×720 RT, dev player)

An earlier version of this section reported "(a) 2→3, (b) 2" with legacy allegedly
adding **zero** draws over an empty canvas. That was wrong, for two compounding
reasons the reviewer caught: (1) the harness captured a **Screen-Space-Overlay**
canvas, which a camera does **not** draw into its `targetTexture`; and (2) a **bare
`UniTextAppearance` has no material**, so the LEGACY path drew nothing at all. Both
are fixed here: a **World-Space** canvas rendered by a camera into a 1280×720 RT,
and a face material assigned to the appearance (as any real scene has). Legacy now
renders (`nonBg > 0` in every row), so the numbers compare like-for-like.

| Scenario | Legacy draws / renderers / CPU static·changing ms | Unified draws / renderers / CPU static·changing ms |
|----------|---------------------------------------------------:|----------------------------------------------------:|
| (a) one text, 3 SDF + MSDF | **18** / 3 / 0.32 · 0.48 | **5** / **1** / 0.34 · 0.53 |
| (b) 50 components | **455** / 150 / 0.62 · 2.76 | **5** / **50** / **0.44 · 2.45** |
| baseline (empty canvas) | 3 draws | — |

- **Draw calls — the real win.** Once legacy actually draws, the unified path is a
  large reduction: **(a) 18 → 5**, **(b) 455 → 5** GPU draws, and 3→1 / 150→50
  CanvasRenderers. The 50-component canvas collapses to **5 draws total** (one
  shared uber material + shared array batches all 50) vs legacy's 455.
- **CPU regression FIXED (not deferred).** The repack now reads meshes with the
  non-allocating `Mesh.GetVertices/GetColors/GetTriangles/GetUVs(list)` into reused
  scratch lists (the `.vertices/.colors32/.triangles` *properties* allocated a fresh
  array every frame — the GC the +17 % came from). Result: scenario **(b) unified
  now BEATS legacy on both static (0.44 < 0.62 ms) and changing (2.45 < 2.76 ms)**.
  Scenario (a), a single trivial component, is within noise and a hair higher
  (0.34 vs 0.32 static) — the per-component repack overhead marginally exceeds a
  3-renderer legacy path at n=1, but the at-scale case (b), which is the point of
  the renderer-count drop, is a clear win. A `ProfilerMarker("UniText.Unified.Build")`
  wraps the repack for attribution.
- **Build fix (shipped):** `UniText/Uber` had no referencing material and was
  **stripped from the player** (first dump showed `UI/Default`); `UniTextBuildProcessor`
  now pins it into Always-Included Shaders, and every `Shader.Find` null logs the
  name + FAILs instead of `new Material(null)` throwing.
- Absolute counts are from a 1280×720 dev player with simple content — treat as
  *relative* evidence; atlas-budget defaults (§5.5) still want representative scenes.

### 8.5 Pixel equivalence — two layers, one honest gap

Rendered on a **World-Space canvas → camera → 1280×720 RenderTexture** in a standalone
dev player (0 exceptions). A key correction: a **bare `UniTextAppearance` has no
material**, so the LEGACY path draws nothing (its earlier "black offscreen" and
"0 marginal draws" were this, not an offscreen-capture bug). With a face material
assigned (as any real scene has) legacy renders — `nonBg_off > 0` for every case
below — so the comparison is now valid.

**Layer 1 — atlas/geometry indirection: BIT-IDENTICAL.** Both paths' meshes through
MATCHED neutral shaders (EditMode `UnifiedRendererPixelEquivalenceTests`): maxDelta
= 0 for SDF, MSDF, `<color>` span. The pages-as-slices array indirection, UVs and
per-vertex colour are exact.

**Layer 2 — PRODUCTION shaders (`UniText/Uber` vs legacy SDF/MSDF), real UGUI path:**

| case | legacy nonBg | unified nonBg | maxΔ | %px > 2/255 |
|------|-------------:|--------------:|-----:|------------:|
| SDF        | 4065 | **4065** | **0**  | **0.00 %** |
| MSDF       | 4368 | **4368** | **0**  | **0.00 %** |
| color span | 8636 | **8636** | **0**  | **0.00 %** |
| outline    | 5555 | 3690 | 255 | 0.42 % |
| underlay   | 2737 | 2397 | 64  | 0.19 % |

PNGs: `Documentation/Design/evidence/prod_unified_vs_legacy_{sdf,msdf,color_span,outline,underlay}.png`.

**SDF, MSDF and `<color>` are now BIT-IDENTICAL to legacy** (maxΔ 0, 0.00 % of pixels
differ, nonBg exactly equal) through the production uber shader. Three fixes got
there, each with a found root cause:
1. **In-fragment SSD scale.** The combined legacy shader `UniText/SDF-SSD` derives
   `baseScale` IN THE FRAGMENT from the atlas-UV screen-space derivative
   (`1/((|ddx(uv.y)|+|ddy(uv.y)|)·texelW·0.75)·(_Sharpness+1)`), not the
   `vPosition.w/_ScreenParams` form of SDF-Face. Porting the SSD formula in-frag
   (with a shared `_AtlasSize` for the texel term) made the face edge exact.
2. **Shim keyword-gating.** The SSD material defaults `_UnderlayColor` to
   `(0,0,0,0.5)`; legacy only renders underlay/glow when the `UNDERLAY_ON`/`GLOW_ON`
   shader_feature is enabled. The shim was synthesizing those disabled defaults → a
   black drop-shadow darkened every glyph (the systematic ~10 % deficit). Now the
   shim gates underlay/glow on the material keyword.
3. **Real spreadRatio in UV1.x.** The merge packed `gradientScale` (~10) where the
   shader expects `spreadRatio` (~0.1); `normFactor = 0.1/spreadRatio` was ~100× off,
   collapsing the outline/underlay offset. Now carries the source mesh's TEXCOORD1.x.

Plus the double-premultiply removal and the premultiplied `BlendOver` composite.

**Remaining residual — outline + underlay (effect layers only; face is exact).**
Outline: nonBg 3690 vs 5555, maxΔ 255 (red ring at the edge). Underlay: 2397 vs
2737, maxΔ 64.

**Empirical bisection (the deliverable, not a reading).** With a test hook scaling
the synthesized outline width by k and the underlay offset by a factor, both swept
against a fixed legacy render (world-space 1280×720; spreadRatio = 0.25,
gradientScale = 8, normFactor = 0.4):

- **Outline width sweep** (legacy lit 5555 / red 2925): `k=2.4 → 5174/2554`,
  `2.5 → 5270/2688`, `2.667 → 5411/2852`, `2.8 → 5524/3038`, `2.9 → 5573/3120`,
  `3.0 → 5647/3213`. The lit count matches legacy at **k ≈ 2.85**, the red count at
  **k ≈ 2.73** — i.e. **no single width factor hits both**, and 2.73–2.85 is not a
  clean quantity of normFactor/gradientScale/spreadRatio. Conclusion: the outline
  gap is **not one missing scalar**; the merged single-pass outline ring is both
  slightly narrow AND slightly differently anti-aliased vs legacy `UniText/SDF-SSD`'s
  layered outline. A single k would be a magic constant that still misses the bar on
  one of lit/red, so it was NOT applied. A faithful fix needs the ring profile
  reproduced (width + edge), likely by matching SSD's `scaleSoftness`/`SDFLayer`
  outline exactly as its own layer — a bounded next task.
- **Underlay offset sweep** (legacy lit 2737): offset ×1→2397, ×2→2397, ×3→2358,
  ×4→2300 — scaling the OFFSET does not grow the lit area, so the underlay gap is an
  **EXTENT (dilate/scale)** difference, not an offset error. Next: port SSD's
  underlay `layerScale`/`layerBias` extent exactly (it already uses the shared
  `_AtlasSize` texel term).

So: **SDF, MSDF and `<color>` are a bit-exact drop-in**; the outline/underlay effect
layers draw correctly but are the remaining items, now characterised empirically
(width+edge for outline, extent for underlay) rather than guessed.

**Legacy coverage ported (batching-safe).** `UniText/SDF-Face` + `SDF-Base` coverage
is now ported into `UniText/Uber` WITHOUT per-component material state (so the 5/5
draw batching is preserved): per-vertex `scale = baseScale(vPosition.w,_ScreenParams,
_Sharpness)·xScaleVal(UV0.w)·gradientScale(UV0.z)`; shared-constant uniforms
`_WeightNormal/_WeightBold/_ScaleX/_ScaleY/_Sharpness` at legacy defaults; per-style
dilate/outline/underlay from the StyleTable. Face uses the legacy linear ramp
`saturate(dist·scale − faceBias)`; the composite matches legacy `BlendOver` with
PREMULTIPLIED alpha (outline fills the full extent, face over), and the stray final
double-premultiply was removed. Normals are copied in the merge and the canvas
enables `TexCoord1|Normal` for world-space perspective. **Result: face, MSDF,
`<color>`, and underlay now match legacy to maxΔ = 36 (from 107–111) with nonBg
within ~10 %** and 0.16–0.52 % of pixels over 2/255. Draws remain 5/5 (a/b); CPU (b)
unified 0.86/2.31 ms still beats legacy 1.30/2.43 (static/changing).

**Outline — still diverges (the one remaining case).** nonBg 2262 vs legacy 5555,
maxΔ 255: the unified single-pass outline under-draws the red ring, where legacy
renders the outline via a **2-pass** SDF-Base(behind)+SDF-Face(over) material. The
outline data reaches the style table correctly (`AppearanceStyleShim` maps
`_OutlineColor/_OutlineWidth`; StyleTable packs them at col 1 / col 4.z, which the
shader reads), so this is a **compositing** mismatch, not a data gap: a single-pass
`saturate(dist·scaleSoft − outlineBias)` ring does not reproduce the 2-pass legacy
outline extent/blend. Bisected to the outline term (face/MSDF/underlay all match at
maxΔ 36 with the same scale/ramp; only outline's extent is short). Next: reproduce
the 2-pass outline weight exactly (the SDF-Base `scaleSoftness`/`_OutlineSoftness`
path and the premultiplied `SDFLayer`+`BlendOver` order for the outline layer
specifically). The other four cases are within target; outline is the exact residual.

**MSDF solid-white bug — root cause found & fixed.** Earlier the unified MSDF panel
was solid white (17188 lit px, maxΔ 255). A Unity **Canvas only uploads the vertex
channels in `additionalShaderChannels`, and UV1 (TEXCOORD1) is NOT included by
default** — so the uber shader read UV1 = 0 → `glyphMode = 0 (SDF)` for every glyph,
and an MSDF glyph on the RGBA32 array then sampled `.a` (= 255 from the RGB24→RGBA32
copy) → a solid block. (`MsdfSharedArrayDiagTests` proved the atlas copy is exact,
the draw-group routing is correct, and the uber shader given correct data + UV1
renders a real glyph — the bug was purely the stripped UV1.) Fix: `UniText` enables
`AdditionalCanvasShaderChannels.TexCoord1` on the canvas in the unified path. MSDF
now renders correctly (17188 → 3279, maxΔ 255 → 111). The linear atlas array +
`fwidth` screen-space AA fixes also landed.

**Exact residual (the only remaining gap).** All five cases now render correctly
(no blocks). The residual is a **thin AA edge ring**: the uber shader renders
~15–20 % **fewer lit pixels** than legacy (unified nonBg below legacy in every row),
i.e. slightly *thinner* glyphs, with maxΔ localized to that one-pixel edge ring
(outline's 255 is the red outline colour at that ring). %px > 2/255 is 0.19–0.64 %
(so 99.4–99.8 % of pixels match within 2/255) — just over the ≤2/255-on-≥99.5 %
bar for color/outline, under it for the rest. Cause: the uber `smoothstep(0.5±band)`
coverage sits a fraction inside legacy's edge contour (legacy biases coverage via
`_FaceDilate`/`scale`/`sharpness` and outputs straight — not premultiplied — alpha).
Closing it fully means matching legacy's dilate/sharpness bias and alpha-output mode
in the uber fragment program; it is a sub-pixel edge-weight tune, not broken
rendering. (Layer 1 remains bit-identical.)

**Legacy-formula port — attempted, reverted, exact remaining work.** Rather than
blind-tune, the legacy `UniText/SDF-Base` coverage was ported into the uber shader:
the per-vertex `scale = baseScale·xScaleVal·gradientScale` (from `vPosition.w`,
`_ScaleX/_ScaleY`, `_ScreenParams`, `_Sharpness`, and UV0.z/.w), `normFactor`,
`baseWeight` (`_WeightNormal/_WeightBold/_ScaleRatioA`), and the **linear ramp**
`saturate(dist·scaleSoft − bias)` with `bias = (0.5 − (baseWeight +
dilate·_ScaleRatioA·0.5)·normFactor)·scaleSoft − 0.5` — replacing the `fwidth`
smoothstep. Result: it **tightened the edge** (maxΔ 127→**98** on face/color/
underlay) but did **not** close the nonBg gap (SDF 3207→3282, still ~20 % below
legacy) and **regressed the outline** (nonBg 4858→2088 — the ported outline bias is
wrong without the real per-component uniforms). Root reason it can't close here:
legacy's `scale` consumes **`_ScaleX`, `_ScaleY`, `_ScaleRatioA`** which the legacy
pipeline sets on the material **per component from the canvas**; the unified path
leaves them at 1, so the screen-space scale (hence the ramp slope and weight)
differs. The port was **reverted** (it was net-negative: no gain + an outline
regression). Closing the residual to ≤2/255 on ≥99.5 % requires the `UniText`
component to propagate `_ScaleX/_ScaleY/_ScaleRatioA` (and `_Sharpness`,
`_WeightNormal/_Bold`) onto the shared uber material each rebuild — the same values
the legacy path computes — then the ported ramp matches. That propagation is the
concrete, bounded next task; it was out of this session's remaining budget after the
MSDF root-cause fix. The current shipped uber (fwidth) renders all cases correctly
with 99.4–99.8 % within 2/255.


### 8.6 Outline/underlay — CPU reference + runtime capture (R3)

Per the "build a CPU reference, don't re-derive by reading" approach, the two
fragment programs were transcribed to CPU and the actual GPU inputs/outputs were
captured through the player harness (`BenchmarkBoot`, world-space 1280×720).

**CPU model — the per-pixel math is IDENTICAL.** `UniText/SDF SSD` PixShader and
`UniText/Uber` frag were modelled in isolation and fed the SAME `(dist, scale)` over
`dist∈[0,1]` at scales {6,8,10,12,16}. For the harness style (white face, red
outline width 0.25, softness 0, dilate 0, `_ScaleRatioA=1`, normFactor 0.4) the two
models are **bit-identical (maxΔ 0.00000)** at every dist and scale — composited
premultiplied RGBA included. So the divergence is NOT in the fragment arithmetic as
written.

**Runtime input dump — every input is IDENTICAL.** For the outline glyph, both paths
feed the fragment the same data: UV0 `(u,v,gradientScale=8,xScaleVal=1)`, UV1.x
`spreadRatio=0.25` (→ normFactor 0.4), `styleIdx=0`; the StyleTable row carries
`outline=(1,0,0,1)`, `scal0=(faceDilate 0, softness 0, outlineWidth 0.25, 0)`;
`_AtlasSize=1024` = the legacy atlas page width; `_ScaleRatioA/B/C=1`,
`_ScaleX/Y=1`, `_WeightNormal/_Bold=0/1`, `_Sharpness=0` (no code sets the ratios at
render time — the serialized `=1` holds). First-quad `uv0` bounds and local vertex
positions are **identical** between the legacy per-glyph mesh and the unified merged
mesh (`[(0,0)..(0.05,0.06)]`, pos `[-649,296.69]..[-593,355.69]`).

**GPU capture (`_UberDebug`, a temporary batching-safe diagnostic uniform, since
reverted) — `outAlpha` under-saturates.** On an ALIGNED scanline (same glyph, same
stage), legacy renders a **wide saturated red plateau** (`(255,0,0)` across the ring)
while unified's outline alpha is a **narrow triangular hump peaking at ≈0.43** and
never reaching 1.0 — the composite then shows only a thin, faint ring (the measured
nonBg 3690 vs 5555). The composited unified pixels equal the debug `outAlpha`
exactly, so the premultiplied `BlendOver` composite is faithful; the deficit is in
`outAlpha` itself.

**Differing term.** With identical inputs AND identical arithmetic, the only quantity
that can make `outAlpha = saturate(dist·scale − outlineBias)` peak at ≈0.43 instead
of saturating — while the FACE (`alpha`, a near-step at dist 0.5) stays bit-exact —
is the in-fragment **`scale`** at the glyph FRINGE, where the outline band lives
(dist≈0.45, out on the flat part of the SDF ramp). `scale` is derived in-frag from
`(|ddx(uv0.y)|+|ddy(uv0.y)|)·_AtlasSize·0.75`. `_AtlasSize`, the atlas page size, and
the quad's uv0.y span are all identical, yet the empirical outline sweep needs the
width ×2.73–2.85 to match legacy's lit/red — i.e. the unified ring behaves ~2.8×
narrower, consistent with the merged single-draw mesh yielding a different
screen-space `uv0.y` derivative at glyph-boundary fragments than legacy's
per-glyph draws do. The face edge, being a near-step at dist 0.5, is insensitive to
this; the outline extent, living out on the ramp, is not — which is exactly why four
cases are bit-exact and only outline/underlay diverge.

**Status / bounded next step.** Characterised to the term (`scale` at the fringe via
the merged-mesh screen-space derivative), not closed. A magic width constant was NOT
applied (no single k fits both lit and red; the brief forbids it). The derivation
fix is to make the outline/underlay extent independent of the fragile merged-mesh
`ddx/ddy(uv0.y)` — e.g. carry a per-vertex `scale` (clean per glyph in the vertex
stage) for the OUTLINE/UNDERLAY layers only, leaving the face's in-frag derivative
untouched so SDF/MSDF/`<color>` stay bit-exact (those cases have no visible
outline/underlay, so an outline-only change cannot regress them). CPU model + capture
harness live in the host `BenchmarkBoot` (`DumpInputs`, scanline capture); the
`_UberDebug` uniform was reverted out of the production shader.

### 8.7 Outline/underlay — §8.6 hypothesis KILLED by direct measurement (STEP 1)

Per the brief's STEP 1 ("prove or kill §8.6 by direct measurement, not inference"),
both production fragment programs were instrumented with a temporary, batching-safe
global `_UberDebug` uniform (reverted before commit) that returns the fragment's own
`(dist, scale, outAlpha, outlineBias, scaleSoftness, faceBias, spreadRatio,
normFactor)` encoded in RGB. Each path was rendered through ITS OWN real shader
(`UniText/Uber` forced on vs `UniText/SDF SSD` forced off) into the world-space
1280×720 RT, and an ALIGNED scanline through a large isolated `O` was dumped per-x
(`BenchmarkBoot.DumpBothPaths`). Harness style: white face, red outline width 0.25,
softness 0, dilate 0; font NotoSans 48pt, atlasPadding 12, atlasSize 1024.

**Measured, FontSize 48, aligned scanline (the acceptance condition):**

| term | UNIFIED (Uber) | LEGACY (SSD) |
|------|---------------:|-------------:|
| `scale`         | 10.54 | 10.54  — **IDENTICAL** |
| `dist` (sample) | 0.020–0.035 | 0.020–0.035 — **IDENTICAL** |
| `scaleSoftness` | 10.54 | 10.54 — **IDENTICAL** |
| `spreadRatio` (UV1.x) | **0.251** | **0.024–0.027** |
| `normFactor`    | **0.408** | **~3.6–4.5** |
| `outlineBias`   | **+2.0** (→ outAlpha 0) | **−1.2 → +0.04** (real ring) |
| `outAlpha`      | **0.000** everywhere | **1.0 → 0.3** across the ring |

**§8.6 is KILLED.** `scale` and `dist` are bit-identical between the two paths on the
aligned scanline. The merged single-draw mesh does NOT yield a different
`ddx/ddy(uv0.y)` derivative — exactly the suspicion the brief flagged ("identical uv0
and positions should give identical derivatives"). The §8.6 ≈0.43 reading came from
`DumpUberIntermediates` drawing the LEGACY mesh through the UNIFIED material (whose
UV1 the legacy mesh does not populate the uber way), not from a real derivative gap.

**Actual root cause (→ STEP 2b).** The one differing intermediate is `spreadRatio`
carried in **UV1.x**: unified feeds **0.251** (the generator's real
`atlasPadding/pointSize = 12/48 = 0.25`, carried faithfully by `UnifiedRenderBuilder`
from the source mesh TEXCOORD1.x), while the legacy `UniText/SDF SSD` fragment
effectively uses **~0.024 ≈ 0.25 × REFERENCE_SPREAD_RATIO(0.1)**. Hence
`normFactor = 0.1/spreadRatio` is **0.4 unified vs ~4.0 legacy** — a ~10× gap. That
gap flows straight into `outlineBias = (0.5 − (baseWeight +
(faceDilate+outlineWidth)·0.5)·normFactor)·scaleSoftness − 0.5`: unified's small
normFactor leaves `outlineBias ≈ +2`, so `outAlpha = saturate(dist·scaleSoftness −
outlineBias) = 0` for the whole ring (the outline never lights — measured nonBg 3690
vs legacy 5555). Legacy's larger normFactor drives `outlineBias` negative at the
inner ring, saturating `outAlpha` to 1.0 → the wide red ring. The face edge (a near
step at dist≈0.5) is insensitive to normFactor, which is why SDF/MSDF/`<color>`
stay bit-exact (maxΔ 0) and only outline/underlay diverge. The generator always
produces `spreadRatio=0.25` (`[GEN-SPREAD]` logged it for every generate); the ~10×
is NOT a per-vertex scale difference and NOT the derivative — it is the normFactor
the two effect-layer paths resolve.

**Fix direction.** Correct the OUTLINE and UNDERLAY `normFactor` in `UniText/Uber`
to match legacy's effective value (≈ `1/spreadRatio`, i.e. ×1 rather than
×REFERENCE_SPREAD_RATIO for those layers), leaving the FACE ramp and the in-frag
`scale`/`dist` untouched so SDF/MSDF/`<color>` remain bit-identical. Validated by
re-dumping the aligned scanline and the five-case pixel harness, not by a swept magic
width constant. All `_UberDebug` uniforms and the `[GEN-SPREAD]` log are reverted
before commit.

---

## 9. Component styles, per-span markup, migration & deprecation (Round-2 steps 1–4)

This section documents the user-facing surface that removes `Material` assets and
`UniTextAppearance` in favour of styles configured **on the component**, with a
migration tool and a deprecation window where old assets still load and render
identically. All behaviour is behind `UniTextSettings.UseUnifiedRenderer`
(default **off**) plus the per-component `UniText.UnifiedRenderer` override; the
legacy per-segment path is byte-for-byte unchanged when the flag is off.

### 9.1 Component style API (`UniTextStyle`)

`UniTextStyle` is a serializable struct authored **on the `UniText` component**,
with the same fields as a `StyleTable` row / `GlyphStyle`:

| Field | Meaning |
|-------|---------|
| `faceColor` | fill colour |
| `faceDilate` (−1..1) | edge-weight bias |
| `softness` (0..1) | edge AA / outline softness |
| `outlineColor`, `outlineWidth` (0..1), `outlineDilate` (−1..1) | outline layer |
| `underlayColor`, `underlayOffsetX/Y`, `underlayDilate`, `underlaySoftness` | drop-shadow layer |
| `glowColor`, `glowOffset`, `glowOuter`, `glowInner`, `glowPower` | glow layer |

- `UniText.OverrideStyle` (bool) + `UniText.Style` (`UniTextStyle`). When
  `OverrideStyle` is **on**, the unified path shades every glyph from `Style`
  instead of synthesising one from a legacy appearance/material via
  `AppearanceStyleShim`. When **off**, the shim remains the fallback so a
  component still referencing a `UniTextAppearance` keeps rendering unchanged.
- `UniTextStyle.ToGlyphStyle()` / `FromGlyphStyle()` convert losslessly to and
  from the runtime `GlyphStyle` that the per-component `StyleTable` packs.
- Tests: `UniTextStyleTests` (round-trip, field-count parity with `GlyphStyle`,
  `StyleTable` dedup, `OverrideStyle`/`Style` wiring).

### 9.2 Per-span markup grammar

The following tags set a style for a *range* of text. Each distinct composed
style becomes **one deduped `StyleTable` row**, chosen per glyph via the per-vertex
`styleIdx` (UV1.w) — so a text with several span styles is still **one renderer**
(or two, counting a separate MSDF/emoji draw group).

```
<outline=#RRGGBB,w>…</outline>                     outline colour (+ optional alpha #RRGGBBAA) and width w∈[0,1]
<underlay=#RRGGBBAA,x,y,dilate,softness>…</underlay>  drop-shadow: colour, offset x,y, dilate, softness (trailing params optional, default 0)
<dilate=v>…</dilate>                               face dilation v∈[-1,1]
<softness=v>…</softness>                            edge softness v∈[0,1]
<style=Name>…</style>                              a whole named UniTextStyle from the component's UniTextStyleSheet (UniText.StyleSheet)
```

- Tags **nest and compose**: an inner tag layers only the field(s) it sets over
  the style the outer tags produced (inner wins per field). `<style=Name>`
  replaces the whole style; an inner `<outline=…>` then overrides just the
  outline on top of the named style.
- Colours are `#RGB` / `#RRGGBB` / `#RRGGBBAA` (hex). Follows the existing
  `<tag=params>…</tag>` convention (`TagParseRule` + a `GlyphModifier`), the same
  machinery as `<color>`.
- Implementation: five `*ParseRule`s map to one `SpanStyleModifier` (Kind-driven)
  that accumulates partial `SpanStyleOverride`s into one shared per-cluster buffer
  (so nesting merges); the component's unified path owns a single `OnGlyph` that
  composes the cluster's override over the base style, dedups it to a local id,
  and stamps it into UV1.w; `UnifiedRenderBuilder` maps each local id to a shared
  `StyleTable` row.
- Register the tags per component with `UniText.RegisterModifier` (as any markup
  tag), e.g. `new ModRegister { Modifier = new SpanStyleModifier(SpanStyleModifier.Kind.Outline), Rule = new OutlineParseRule() }`.
- Tests: `SpanStyleTests` — grammar parsing (`TryParseOutline`/`TryParseUnderlay`/
  `TryParseScalar`, malformed rejected), nesting/composition (`MergeOver`+
  `ComposeOnto`, inner-wins), stylesheet whole-style + field override, collector
  dedup (**a row per distinct span style**), and end-to-end
  `AB<outline=#FF0000,0.2>CD</outline>EF` → **1 renderer**, ≥2 distinct styles,
  distinct per-glyph `styleIdx`. Draw-group count ≤2 for any mix:
  `UberDrawGroupTests`.

### 9.3 Migration tool — `Tools/OpenGlyph/Migrate Appearance to Styles`

Converts each `UniText` component's appearance/material settings into a component
`UniTextStyle` (via the same `AppearanceStyleShim` mapping that keeps old assets
rendering) and switches the component to the unified path.

- **Dry-run report first.** `AppearanceMigration.DryRun(includeOpenScenes)` lists
  counts (scanned / to-migrate / already-migrated / no-appearance) and a per-object
  entry list, mutating nothing. The window (`AppearanceMigrationWindow`) shows this
  before any apply.
- **Apply.** `AppearanceMigration.Migrate(options, includeOpenScenes)` walks
  project prefabs (`AssetDatabase` + `LoadPrefabContents`/`SaveAsPrefabAsset`) and
  the open scenes (`Undo.RecordObject` + `RecordPrefabInstancePropertyModifications`).
- **Undo** restores the pre-migration state for scene objects; prefab assets are
  modified in place and are reversible via version control.
- **Idempotent.** A component already using a component style (`OverrideStyle`)
  is skipped (`reason="already migrated"`); a second run changes nothing.
- **Mapping.** Face/outline/underlay/glow are read from the appearance's
  materials (per-font override, else default materials); 2-pass outline+face
  collapses to one multi-layer style. `UniTextStyleSheet` holds named styles for
  `<style=Name>`.
- **Auto-migrate on import — OFF by default**, behind the project setting
  `UniTextSettings.AutoMigrateAppearanceOnImport`. When enabled, an
  `AssetPostprocessor` migrates imported prefabs; otherwise migration is manual.
- Tests: `AppearanceMigrationTests` — migrate → style equals the material values;
  second run no-op (idempotent); Undo restores; temp prefab round-trips; dry run
  never mutates.

### 9.4 Deprecation timeline

1. **Now (this round).** `UniTextAppearance` and its material API are marked
   `[Obsolete(…, false)]` — a **warning, not an error**. Existing assets still
   compile, load, and render **identically** through the compatibility shim with
   the flag off. The package's own code stays warning-clean via file-scoped
   `#pragma warning disable 618` in the deprecation-bridge files.
2. **Deprecation window.** Both paths ship together. Projects migrate at their own
   pace using the tool in §9.3; VRseBuilder (which consumes OpenGlyph as a package)
   runs the same tool against its own project — this worktree never touches it.
3. **Removal (later phase).** After the window, `UniTextAppearance` and the
   material assets are removed; by then all assets are on component styles.
- Tests: `AppearanceDeprecationTests` — asserts `[Obsolete]` is present with
  `IsError == false` and the message points at the migration tool.

### 9.5 Draw-call / renderer evidence (recap)

Per §8.2/§8.4, the unified path collapses the per-segment renderers (and GPU draw
calls) with no CPU regression at scale:

| Scenario | Legacy draws / renderers | Unified draws / renderers |
|----------|-------------------------:|--------------------------:|
| one text, 3 SDF + MSDF | 18 / 3 | **5 / 1** |
| 50 components, same mix | 455 / 150 | **5 / 50** |

Structural renderer counts are asserted deterministically in EditMode
(`UnifiedRendererEquivalenceTests`, `UberDrawGroupTests`); GPU draw calls + CPU are
measured by the PlayMode `DrawCallBenchmarkPlayTests`. Per-span styles add
`StyleTable` rows, **not** draw groups, so they never increase the draw count.
