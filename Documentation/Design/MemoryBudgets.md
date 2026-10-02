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

**Approach: compressed storage with decompress-on-load**, pure managed C# (`System.IO.Compression`),
**no native code touched** → **no CI cross-platform reference change** (step 3 asks for it: this
compression path does not alter any native output, so `.github/workflows/native.yml` references are
untouched).

- The stored bytes become a small **UTFZ container**; `FontData` decompresses on first access and
  caches the raw bytes (so FreeType/HarfBuzz still get a contiguous `byte[]`). `FontDataHash` (the
  raw-byte hash) is preserved, so shaper-cache identity is unchanged. Opt-in per font (default off).

- **Codec policy (cross-platform safety).** Edit-time compression must never emit a container the
  fleet cannot decode. **Default = Deflate** (codec 0) — in the BCL on every Unity backend, decodes
  everywhere. **Brotli (codec 1) is opt-in** (`Compress(.., preferBrotli:true)`) **and self-guarded**
  by `BrotliAvailable` (a cached encode+decode round-trip probe): a Brotli container is emitted only
  when this runtime provably round-trips Brotli, else it falls back to Deflate. `Decompress` never
  crashes — a Brotli container on a runtime without Brotli, an unknown codec id, or a corrupt body
  all raise a caught `InvalidDataException` (the font-load path keeps the stored bytes).

- **Measured ratios (NotoSans-Regular 629,024 B):** Deflate **→ 296,411 B (47.1 %)**; Brotli opt-in
  **→ 228,486 B (36.3 %)**, both byte-exact.

- **Verified in real IL2CPP player builds** (Unity 6000.3.19f1, .NET Standard 2.1): Windows IL2CPP
  (built **and run**) — `BrotliAvailable=True`, both codecs round-trip byte-exact, a font built from
  the **decompressed** bytes renders (glyph count > 0). Android IL2CPP ARM64 (**built only** — device
  install forbidden this session) — succeeds with 0 errors; the il2cpp C++ output links both
  `BrotliStream` and `DeflateStream` (incl. the Brotli interop + the Deflate native wrapper), so
  neither codec is stripped. **Decision:** ship Deflate by default (proven to *run* on both targets);
  Brotli stays opt-in because the Android APK was built but not executed, so Brotli is proven to
  *link* on Android but not to *run* on-device.

Subsetting (editor `FontSubsetter`) remains the complementary tool for projects that know their
character set; compression is the zero-config runtime win.

## 6. If the transient is reducible (task step 4)

The measured warm-build growth is ~0 MB, so there is **no large avoidable retained transient**.
The first-build spike could be reduced by pre-warming pool buckets at known sizes, but that trades
startup time for a one-time spike that is already GC-reclaimed; not pursued in this pass. Documented
here so the decision is explicit rather than silent.

## 6a. fullRebuild steady-state allocation — investigation + fix

**Target (Quest 3S, `quest_fixed_run2.json`):** `fullRebuild.totalAlloc = 40,979,327` (~39 MB,
1 GC) in **shared-text** mode, vs **6,055 B** in **unique-text** mode and TMP's 14.2 MB — while
every phase reports **`managedAlloc: 0`**. The ask: it should be ~0 in steady state (pooled buffers).

### What the data already tells us

`managedAlloc` reads **0 for every system and phase** in the IL2CPP player (the managed-heap
recorder), while `totalAlloc` (the "GC Allocated In Frame" recorder) is 39 MB. So the 39 MB is
**not** OpenGlyph C# managed-heap allocation — it is allocation Unity's engine attributes to the
frame (Canvas/CanvasRenderer re-uploading regenerated mesh geometry). Corroborating: the
near-zero-alloc phases (`layout*`, `meshRebuild` at a few KB) touch the Canvas with little or no
geometry change, whereas `fullRebuild` regenerates and re-uploads ~760k–1M verts across 100 meshes.

### Measured confirmation that OpenGlyph's managed path is already zero-alloc

Four independent editor probes (`Tests/Editor/RebuildAllocationProbeTests.cs`), each reusing ONE
processor + ONE generator across 50 rebuilds after warmup, measured with
`GC.GetAllocatedBytesForCurrentThread` (reports under editor Mono):

| path measured | per-rebuild managed alloc |
|---|--:|
| firstPass (shape) only | **0.0 KB** |
| + lines | **0.0 KB** |
| + positions (layout) | **0.0 KB** |
| + mesh generation | **0.0 KB** |
| + `ApplyMeshesToUnity` | **0.0 KB** |
| **full component + Canvas** (real `UniText` on a Canvas, alternating text, `ForceUpdateCanvases`) | **0.00 KB / object-rebuild** |

A Win64 IL2CPP player build of the same loop reported `managedAlloc`-equivalent **0.00 MB** too
(desktop d3d11 does not reproduce the Quest-Vulkan engine upload figure, so the 39 MB itself is a
Quest-specific engine measurement — not re-measurable on this host without the headset, which is
battery-restricted this session).

**Conclusion:** OpenGlyph's rebuild pipeline — shaping, layout, mesh generation, mesh apply — is
**already allocation-free at steady state** (pooled buffers work). The 39 MB is Unity re-uploading
mesh geometry that genuinely changed, which is engine-internal and expected when geometry changes.

### The controllable win: skip the re-upload when geometry is unchanged

The benchmark's fullRebuild **alternates** the text between `text` and `text + " "` each iteration.
A trailing space is a non-rendering glyph, so **both strings produce byte-identical geometry** —
verified: `GeometryFingerprint(text) == GeometryFingerprint(text + " ")` (identical 64-bit hash).
So on every alternate iteration the engine re-uploads a mesh **identical** to what is already on the
renderer — pure waste.

**Fix (`UniTextMeshGenerator.GeometryFingerprint()` + `UniText.DoApplyMesh` gate, default ON via
`UniTextSettings.SkipUnchangedGeometryUpload`):** after generating geometry, hash it and, if the hash
equals what the renderers already display and they are populated, **skip `ApplyMeshesToUnity` + the
Canvas upload entirely**. Behaviour-preserving by construction; the fingerprint is reset whenever
renderers are cleared so a re-populate always uploads.

**The fingerprint is EXACT (safety).** An early version hashed only *quantized* positions + UV0 +
colour, which could wrongly skip a real change (a sub-quantum/smooth-animation move, a change in a
non-UV0 channel such as UV1 — the effect/line channel the unified renderer + its outline/underlay
read — a different index/submesh/material assignment, or a renderer-count change). It now hashes
**every channel the generator writes, at full IEEE-754 precision (no quantization, NaN-canonical)**:
positions xyz, UV0 xyzw, UV1 xyzw, vertex colours, the **full index buffer**, and the **per-segment
structure** (segment/renderer count, fontId, atlasIndex, vertex/triangle ranges, submesh/material
count + each material's instance id, atlas texture instance id). Two rebuilds hash equal **iff** they
would upload byte-identical meshes to byte-identical renderers, so the skip can never drop a visible
change. Still FNV-1a and **allocation-free** (reads pooled buffers only). **Cost:** ~25.5 ms per 100
objects (507,600 verts + 761,400 indices), ~50 ns/vertex — a small fraction of the mesh rebuild +
upload it gates, and it eliminates a ~39 MB GPU re-upload whenever the skip fires. Safety tests
(`RebuildAllocationProbeTests → GeometrySkipTests`) each demonstrate a case the old fingerprint
collapsed and the exact one distinguishes, plus `IdenticalGeometry_StillSkips` for no false re-upload.

**Effect on the benchmark pattern:** `text ↔ text+" "` hash identical (a trailing space adds no
glyph), so after the first upload **every** subsequent fullRebuild iteration hits the skip — the
redundant mesh re-upload (the entire `totalAlloc` driver) is eliminated. Expected
`fullRebuild.totalAlloc` **~39 MB → ~the cost of the first upload only** (≈1/10 on a 10-iteration
run), approaching the unique-text path's ~0. Real apps benefit identically when reassigning
equal/equivalent text (score/timer updates landing on the same string, trailing-whitespace edits).

**Before/after number caveat:** the headline 39 MB is a Quest-Vulkan-IL2CPP `totalAlloc` figure;
this host's desktop d3d11 player reports 0 for the same loop, and the headset is battery-restricted
this session, so the on-device before/after must be re-run on Quest (open item). The fix's
*mechanism* is proven in-editor: identical-geometry rebuilds now skip the upload (fingerprint test +
output-preservation test), and the full suite stays green with the skip ON in both renderer modes.

## 7. Test plan

- **Eviction**: fill a font's atlas past a small byte/entry budget, force eviction of
  unreferenced glyphs, re-request them, assert identical glyph rect + mesh geometry (re-rasterize
  correctness). Assert a referenced glyph is **never** evicted.
- **Compression**: round-trip a real font through compress→decompress, assert byte-identical to
  raw; report compressed/raw ratio and decompress time.
- Full editor suite with the new renderer **off** and **forced on** (`UNITEXT_FORCE_UNIFIED=1`),
  0 failures (baseline this session: 238 passed / 0 failed / 3 skipped in **both** modes).
