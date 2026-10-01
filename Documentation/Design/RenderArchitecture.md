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

| Scenario | Legacy draws | Unified draws (per-component mat) | Unified draws (shared mat) |
|----------|-------------:|----------------------------------:|---------------------------:|
| (a) one text | 2 | 3 | **3** |
| (b) 50 components | 2 | **102** | **3** |

**Fixed.** The first cut created a separate uber `Material` instance per component;
distinct material instances do not batch, so 50 identical components cost ~2×50 ≈
**102** GPU draws and lost to the legacy path's cross-component UI batching. The
fix (shipped): ONE process-wide uber material + ONE shared `Texture2DArray` + ONE
shared, content-deduped `StyleTable` per draw-group format, so every component
binds identical GPU state and UGUI batches them. Re-measured: scenario (b) ON
drops **102 → 3** GPU draws — on par with legacy's **2–3** — while using **50**
CanvasRenderers instead of legacy's **150** (the structural CPU-side win). The
merged mesh stays per component (its own geometry); only the batched GPU state is
shared. Absolute counts are from a 320×240 dev player with trivial geometry —
*relative* evidence of batching, not absolute budgets; atlas-budget defaults
(decision §5.5) still want numbers from representative scenes.


### 8.4 Draw-event attribution + CPU (standalone dev player, 320×240)

A per-renderer dump and an empty-canvas baseline make the "scenario (a) 2→3 draws"
unambiguous — it is **not** a regression:

| Row | GPU draws | setpass | CanvasRenderers | first-renderer dump | CPU main-thread median (ms) |
|-----|----------:|--------:|----------------:|---------------------|-----------------------------:|
| baseline (empty canvas) | 2 | 2 | 0 | — | — |
| (a) legacy | 2 | 2 | 3 | — | 0.31 |
| (a) unified | 3 | 3 | **1** | `[matCount=1 shader=UniText/Uber submeshes=1]` | 0.31 |
| (b) legacy | 2 | 2 | 150 | — | 1.78 |
| (b) unified | 3 | 3 | **50** | `[matCount=1 shader=UniText/Uber submeshes=1]` | 2.09 |

- **The empty canvas already costs 2 draws.** Legacy text folds into those existing
  UI draws via dynamic batching (same atlas+material), so its *total* stays 2.
  Unified adds **exactly one** uber draw (its material differs from the baseline
  UI, so it cannot fold in) → 3. One marginal draw for one text is the floor; the
  "3rd draw" is that single irreducible uber draw, not an extra pass (the dump
  shows `matCount=1`, one submesh, `UniText/Uber`).
- **Build fix found by this measurement:** `UniText/Uber` has no referencing
  material asset, so it was **stripped from the player** and the unified path fell
  back to `UI/Default` (the first dump showed `shader=UI/Default`).
  `UniTextBuildProcessor` now pins it into Always-Included Shaders; the dump then
  shows `UniText/Uber`.
- **CPU:** the unified path is currently a *slight CPU regression* under
  text-changing-every-frame (a: 0.31≈0.31; b: 1.78→2.09 ms median over 120
  frames), because the per-frame segment→merged-mesh repack + page publish adds
  work. The renderer-count drop (150→50) does **not** yet convert to a CPU win in
  this micro-benchmark; a real win needs the repack cached across frames when the
  text is unchanged (future step). Reported straight, flat-to-slightly-worse.

### 8.5 Pixel equivalence — status (honest)

- **Geometry equivalence: PROVEN.** `UnifiedRendererEquivalenceTests` (SDF + MSDF)
  shows the unified path submits a **vertex-identical** mesh to the legacy path
  (max positional delta < 1e-3; the merge copies positions verbatim) and draws
  through one `UniText/Uber` renderer. The only variable left is the fragment
  shader, and the uber shader's SDF (`.a`) and MSDF (`median3`) reconstruction
  mirrors the legacy display shaders.
- **Camera PIXEL equivalence: NOT YET SUBSTANTIATED.** Rendering the **legacy**
  multi-CanvasRenderer path to an offscreen `RenderTexture` (both via a batchmode
  CommandBuffer replay and via a real `cam.Render()` in the standalone player)
  produces a **black** legacy image (`nonBg_off = 0`) while the unified path
  renders (`nonBg_on > 0`). The legacy per-child-CanvasRenderer UI submission does
  not reproduce under offscreen capture here, so a legacy-vs-unified pixel diff
  would compare unified against black — misleading, so it is **not** asserted and
  no evidence PNG is published. The EditMode pixel cases are `Assert.Ignore`d with
  this reason. Substantiating true pixel equivalence needs an on-screen capture
  path (or a neutral array-sampling reference shader applied to BOTH paths' meshes
  over the same atlas) — a follow-up. The geometry proof above plus the shared
  SDF/MSDF reconstruction math is the evidence available this round.