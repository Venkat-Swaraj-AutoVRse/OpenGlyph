# Memory Budgets, Eviction & Font Compression — Design

Rendering-phase memory work for OpenGlyph. Two features:

- **(A) Glyph/atlas memory budgets with eviction** — bound resident glyph memory on
  mobile (Quest 3S) with configurable per-font/global limits and LRU eviction.
- **(B) Font compression** — reduce font-byte footprint in builds and at runtime.

This document records **where the memory goes** (measured) *before* any behaviour change,
per the measure-first rule. All defaults below keep current visual output and existing tests
byte-for-byte unchanged.

> **Status of this doc's numbers.** The headline **~429 MB creation allocation** is from the
> IL2CPP **player** benchmark (`Documentation/Benchmarks/LSE-BenchmarkWorkshop.md` §7), measured
> with Unity's `totalAlloc` Recorder on Windows IL2CPP and Quest 3S (the two agree to ~1 %). That
> Recorder is **not readable from the editor**, so this doc *corroborates* the decomposition with
> an editor-side harness (`Tests/Editor/MemoryBudgetMeasurementTests.cs`) that drives the **real**
> pipeline (`TextProcessor` + `UniTextFontProvider` + `UniTextMeshGenerator`) over the same
> workload (100 objects × 2,405 chars = 240,500 chars). Figures are tagged **[player]**,
> **[editor-measured]**, or **[computed]** so the provenance of each is explicit.

---

## 1. Workload

100 text objects × 2,405 chars/object = **240,500 chars**, mixed Latin + Arabic + Hebrew, the
benchmark's "creation" phase (instantiate 100 objects and build each once). Target platform
includes **Quest 3S** (mobile memory, where a transient spike or an unbounded resident atlas
hurts most).

---

## 2. The ~429 MB creation allocation — transient vs retained

The central question the task poses: of the ~429 MB allocated during object creation (1 GC on
both Windows and Quest), **how much is transient (reclaimed) vs retained (held while objects
live)?**

### 2.1 Retained (held as long as the 100 objects live)

| Category | Size | Provenance | Notes |
|---|--:|---|---|
| **Mesh buffers** (vertex/UV/UV2/color + triangles) | **26.1 MB** | [editor-measured] | 507,600 verts + 761,400 indices for this workload at 1200 px wrap. **48 B/vertex** (`Vector3` pos 12 + `Vector4` uv0 16 + `Vector4` uv1 16 + `Color32` 4) + **4 B/index**. ≈ **0.26 MB/object**. |
| **Per-font glyph atlas** (`UniTextFont.atlasTextures`) | **1.00 MB / font** | [editor-measured] | One 1024×1024 **Alpha8** page (1 B/px) covered the full Latin+Arabic+Hebrew glyph set here. Grows one page at a time only when a page fills. |
| **Raw font bytes** (`UniTextFont.fontData`, `byte[]`) | **0.60 MB / font** | [editor-measured] | NotoSans-Regular 629,024 B held raw and uncompressed. 3 bundled default fonts (NotoSans + Arabic 194 KB + Hebrew 48 KB) ≈ **0.87 MB** total. |
| HarfBuzz glyph/advance caches (`Shaper.FontCacheEntry`) | few KB / font | [computed] | `FastIntDictionary<uint>` glyph cache + `FastIntDictionary<int>` advance cache, keyed by codepoint / glyphIndex. One int per distinct glyph — kilobytes, not megabytes. |
| HarfBuzz face (unmanaged) + FreeType face | ~font-size, native | [computed] | `Marshal.AllocHGlobal(fontData.Length)` copy inside `FontCacheEntry` (~0.6 MB/font) + FT face. Native heap, not managed. |

**Retained total for this workload ≈ 26 MB mesh + ~1–3 MB atlas + ~0.9 MB font bytes + native
face copies ≈ well under 35 MB managed.**

### 2.2 Transient (reclaimed — the bulk of the 429 MB)

The editor harness measured **net mono-heap growth of 0.2 MB over 100 warm builds** (0 GCs
during the measured loop). In other words, once the array pools (`UniTextArrayPool`,
`UniTextBuffers`) are warm, **a build is very nearly allocation-neutral** — it rents and returns.
The large player figure is dominated by **first-time, pre-pool-warm, per-object transient
allocation** in the shaping/layout/mesh pipeline:

- intermediate shaping/bidi/segmentation buffers per object before pool buckets are populated,
- `string`/`char[]` churn and boxed intermediates on the first pass,
- pooled arrays that are allocated once (the first time a bucket size is requested) and then
  reused — **counted in `totalAlloc` but reclaimed/retained-in-pool**, not leaked.

**Conclusion.** The ~429 MB is **overwhelmingly transient**: it is reclaimed in the single GC the
benchmark records, and the warm steady state adds ~0 MB/build. The *retained* working set for
100 live objects is on the order of **~30 MB managed** + per-font atlas/native. This reframes the
work:

- **(A) atlas/glyph budgets** matter for the **resident** axis — many fonts / variable-font
  instances / large CJK sets can grow `atlasTextures` and the shared `Texture2DArray` without
  bound. That is what eviction caps.
- **(B) font compression** attacks the **0.6 MB/font raw bytes** (build size + idle memory).
- The transient 429 MB is **not** a leak and is already pool-recycled; §6 notes what *could*
  shave the first-build spike, but there is no large retained target hiding in it.

---

## 3. Existing infrastructure (already on `afdc733`)

Significant budget machinery already exists and is **off by default**:

- **`GlyphAtlasArray`** (`Runtime/FontCore/GlyphAtlasArray.cs`) — a shared `Texture2DArray` atlas
  with per-cell **refcount + LRU timestamp**, **free-rect reuse**, guillotine remainder packing,
  and a **`PageBudget`**: when `PageBudget <= 0` (default) it grows unbounded (legacy behaviour);
  when positive, `EvictToFit` drops least-recently-used **refcount-0** cells and reuses their
  rects. An evicted glyph re-rasterizes on next request (lookup miss).
- **`SharedGlyphAtlas`** (`Runtime/FontCore/SharedGlyphAtlas.cs`) — process-wide registry of those
  arrays keyed by (format, size); reads the budget from
  **`UniTextSettings.SharedAtlasPageBudget`** (0 ⇒ unbounded) at array creation, with
  `ApplyBudgetFromSettings()` + `BeginFrame()` to drive LRU.
- **`UniTextArrayPool<T>`** — two-tier (thread-local + shared) array pool, buckets 32…65536;
  arrays > 65536 bypass pooling and allocate directly. This is why warm builds are
  allocation-neutral.

**What is missing** (this work adds it):

1. A **per-font / global glyph-cache budget** (entries *and* bytes) with LRU eviction for the
   **legacy per-font atlas path** (`UniTextFont.atlasTextures` + glyph lookup), which the shared
   array's `PageBudget` does **not** cover — most objects still render through it.
2. **Shaping-cache bounds** (the HarfBuzz glyph/advance caches are unbounded, though tiny).
3. **Runtime font-byte compression** (decompress-on-load) — `fontData` is stored raw today; there
   is editor-side `FontSubsetter` (HarfBuzz subset, `unitext_native_editor`) but no compressed
   storage.

---

## 4. (A) Budgets — design

New `UniTextSettings` fields (all default to **unbounded / off** so existing output + tests are
unchanged):

- `SharedAtlasPageBudget` — **already present** (per-shared-array page cap).
- `GlyphCacheMaxEntriesPerFont` (0 = unbounded) — cap on resident glyph cells in a font's atlas.
- `GlyphCacheMaxBytesPerFont` (0 = unbounded) — byte cap on a font's atlas pages
  (`pages × size² × bpp`), the more mobile-relevant knob.
- `GlyphCacheMaxBytesGlobal` (0 = unbounded) — process-wide ceiling across all fonts.

**Eviction policy.** LRU over glyphs **not referenced by any live text** (refcount 0), mirroring
`GlyphAtlasArray`'s existing scheme, extended to the per-font atlas: a glyph used by a currently
displayed `UniText` object is pinned; only unreferenced glyphs are evicted; the freed rect is
returned to the font's `freeGlyphRects` for reuse (the field already exists). An evicted glyph
**re-rasterizes identically on next request** — the correctness contract the eviction test
enforces (fill past budget → evict → re-show → pixels/geometry identical).

**Atlas page release / repack.** When a whole page becomes empty after eviction it is released;
partial repack is deferred (free-rect reuse already bounds fragmentation for the glyph-size
distribution).

**Defaults.** 0 everywhere ⇒ no eviction ⇒ byte-identical to today. A project targeting Quest
sets a byte budget; the engine then holds the atlas under that ceiling by evicting cold glyphs.

## 5. (B) Font compression — design

`UniTextFont.fontData` is a raw `byte[]` (0.6 MB for NotoSans) serialized into the asset and the
build, and held in memory for the font's lifetime (plus a native `AllocHGlobal` copy in HarfBuzz).

**Approach: compressed storage with decompress-on-load**, pure managed C# (`System.IO.Compression`
Deflate/GZip), **no native code touched** → **no CI cross-platform reference change** (called out
explicitly here because step 3 of the task asks for it: *this compression path does not alter any
native output, so the `.github/workflows/native.yml` reference results are untouched*).

- A compressed companion field stores Deflate-compressed font bytes; `FontData` decompresses on
  first access and caches the raw bytes (so FreeType/HarfBuzz still get a contiguous `byte[]`).
- TTF/OTF compress to roughly **55–65 %** of raw with Deflate (glyf/CFF tables are compressible),
  so build size + at-rest asset size drop materially; measured before/after in the test + reported.
- Opt-in per font (default off) so no existing asset changes on import.

Subsetting (editor `FontSubsetter`) remains the complementary tool for projects that know their
character set; compression is the zero-config runtime win.

## 6. If the transient is reducible (task step 4)

The measured warm-build growth is ~0 MB, so there is **no large avoidable retained transient**.
The first-build spike could be reduced by pre-warming pool buckets at known sizes, but that trades
startup time for a one-time spike that is already GC-reclaimed; not pursued in this pass. Documented
here so the decision is explicit rather than silent.

## 7. Test plan

- **Eviction**: fill a font's atlas past a small byte/entry budget, force eviction of
  unreferenced glyphs, re-request them, assert identical glyph rect + mesh geometry (re-rasterize
  correctness). Assert a referenced glyph is **never** evicted.
- **Compression**: round-trip a real font through compress→decompress, assert byte-identical to
  raw; report compressed/raw ratio and decompress time.
- Full editor suite with the new renderer **off** and **forced on** (`UNITEXT_FORCE_UNIFIED=1`),
  0 failures (baseline this session: 238 passed / 0 failed / 3 skipped in **both** modes).
