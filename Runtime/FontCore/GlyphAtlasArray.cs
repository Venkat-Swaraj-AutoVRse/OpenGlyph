using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, step 1: a <b>shared</b> glyph atlas backed by a
    /// <see cref="Texture2DArray"/> whose slices (array layers) are atlas pages, shared across
    /// every <see cref="UniTextFont"/> that uses the same pixel format. A glyph lookup resolves to
    /// a <see cref="GlyphCell"/> carrying the slice index (array layer) plus the normalized UV rect
    /// inside that slice, so one texture-array binding serves many fonts and many pages — the
    /// enabling piece for collapsing per-font CanvasRenderers to one draw group (decision §5.1).
    /// </summary>
    /// <remarks>
    /// <para><b>Formats (design §5.1).</b> Two arrays only: an <see cref="TextureFormat.Alpha8"/>
    /// array for SDF / coverage-bitmap / pixel glyphs (1 B/px), and an <see cref="TextureFormat.RGBA32"/>
    /// array for MSDF + COLR emoji. One instance of this class owns exactly one format; the host
    /// (<see cref="UniTextFont"/> / mesh generator) selects which shared instance a glyph lands in by
    /// its render mode. Slices in a <see cref="Texture2DArray"/> must share dimensions and format, so
    /// a single array cannot mix the two — hence two arrays, two bindings max.</para>
    ///
    /// <para><b>Keying (phase-2 ready).</b> Cells are keyed by
    /// <see cref="GlyphCellKey"/> = (fontId, glyphIndex, <see cref="VariationKey"/>). A static font
    /// uses <see cref="VariationKey.None"/>, so wght 400 vs 700 of a variable font occupy distinct
    /// cells exactly as the existing <c>variationGlyphLookup</c> facet requires.</para>
    ///
    /// <para><b>Memory management (design §3.5, decision §5.5).</b> Each cell carries a refcount
    /// (components currently displaying it) and an LRU timestamp (last frame it was touched). When a
    /// page allocation is needed and the array is at its page budget, the least-recently-used
    /// <em>refcount-0</em> cells are evicted and their rects returned to a per-page free list for
    /// reuse; an evicted glyph re-rasterizes on demand the next time it is requested. <b>Default:
    /// no eviction</b> — when <see cref="PageBudget"/> &lt;= 0 the array grows without bound, so
    /// behaviour is identical to the legacy shelf atlas until a budget is configured.</para>
    ///
    /// <para><b>Threading.</b> All mutation (page growth, packing, eviction, <c>Apply</c>) is
    /// main-thread only, like the existing atlas. Rasterization still happens off-thread upstream;
    /// only the pixel copy + bookkeeping run here.</para>
    /// </remarks>
    public sealed class GlyphAtlasArray : IDisposable
    {
        /// <summary>Identity of a glyph cell: which font, which glyph, which variation instance.</summary>
        public readonly struct GlyphCellKey : IEquatable<GlyphCellKey>
        {
            public readonly int fontId;
            public readonly uint glyphIndex;
            public readonly VariationKey variation;

            public GlyphCellKey(int fontId, uint glyphIndex, VariationKey variation)
            {
                this.fontId = fontId;
                this.glyphIndex = glyphIndex;
                this.variation = variation;
            }

            public bool Equals(GlyphCellKey other) =>
                fontId == other.fontId && glyphIndex == other.glyphIndex && variation.Equals(other.variation);

            public override bool Equals(object obj) => obj is GlyphCellKey o && Equals(o);
            public override int GetHashCode() => HashCode.Combine(fontId, glyphIndex, variation);
            public override string ToString() => $"(font={fontId}, glyph={glyphIndex}, var={variation})";
        }

        /// <summary>
        /// A packed glyph in the shared array: its slice (array layer) and pixel rect, plus the
        /// normalized UV rect the shader samples. <see cref="slice"/> is the per-vertex sliceIdx of
        /// the Round-2 vertex layout (design §3.2).
        /// </summary>
        public readonly struct GlyphCell
        {
            /// <summary>Array layer (Texture2DArray slice) this glyph lives in.</summary>
            public readonly int slice;
            /// <summary>Pixel rect inside the slice (origin + size, no padding).</summary>
            public readonly GlyphRect pixelRect;
            /// <summary>Normalized UV rect (x,y,w,h in 0..1) for the shader.</summary>
            public readonly Rect uvRect;

            public GlyphCell(int slice, GlyphRect pixelRect, Rect uvRect)
            {
                this.slice = slice;
                this.pixelRect = pixelRect;
                this.uvRect = uvRect;
            }

            public bool IsValid => pixelRect.width > 0 && pixelRect.height > 0;
        }

        // --- Per-cell residency record (internal bookkeeping) -------------------------------------
        private sealed class CellRecord
        {
            public GlyphCellKey key;
            public int slice;
            public GlyphRect packedRect; // includes spacing-padded footprint used for reuse
            public GlyphRect glyphRect;  // the tight glyph rect (what the cell exposes)
            public int refCount;
            public long lastUsedFrame;
            public int channels;
        }

        // --- A single page = one array slice, with its own shelf cursor + reuse free list ---------
        private sealed class Page
        {
            public int sliceIndex;
            public int shelfX;
            public int shelfY;
            public int shelfHeight;
            // CPU-side backing buffer for this slice (size*size*channels). Sub-rects are written here
            // on the CPU; the whole slice is pushed to the Texture2DArray via SetPixelData in Apply().
            // Keeping the pixels CPU-side makes the allocator/refcount/LRU logic fully exercisable
            // without a GPU, and the GPU upload is a single well-defined call per dirty slice.
            public byte[] cpu;
            public bool dirty;
            // Free rects available for reuse after eviction, bucketed loosely by appending; a simple
            // first-fit scan keeps fragmentation bounded for the glyph size distribution.
            public readonly List<GlyphRect> freeRects = new();
        }

        private const int PackingSpacing = 1;

        private readonly int _size;
        private readonly TextureFormat _format;
        private readonly int _channels;

        private Texture2DArray _array;
        private readonly List<Page> _pages = new();
        private readonly Dictionary<GlyphCellKey, CellRecord> _cells = new();

        private long _frame;
        private int _pageBudget; // <= 0 => unbounded (no eviction), unchanged legacy behaviour

        /// <summary>Atlas page (and slice) size in pixels (square).</summary>
        public int Size => _size;
        /// <summary>Pixel format of every slice.</summary>
        public TextureFormat Format => _format;
        /// <summary>Number of allocated pages (= array slice count).</summary>
        public int PageCount => _pages.Count;
        /// <summary>Number of resident glyph cells.</summary>
        public int CellCount => _cells.Count;
        /// <summary>The backing texture array (one binding). Null until the first page is created.</summary>
        public Texture2DArray Texture => _array;

        /// <summary>
        /// Maximum number of pages (array slices) before eviction kicks in. <c>&lt;= 0</c> means
        /// unbounded — no eviction, behaviour identical to the legacy shelf atlas (default).
        /// </summary>
        public int PageBudget
        {
            get => _pageBudget;
            set => _pageBudget = value;
        }

        /// <param name="size">Page/slice size in pixels (square), e.g. 1024.</param>
        /// <param name="format">Alpha8 (SDF/coverage) or RGBA32 (MSDF/emoji).</param>
        /// <param name="pageBudget">Max pages before eviction; &lt;= 0 = unbounded (default).</param>
        public GlyphAtlasArray(int size, TextureFormat format, int pageBudget = 0)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            _size = size;
            _format = format;
            _channels = format == TextureFormat.RGBA32 ? 4 : format == TextureFormat.RGB24 ? 3 : 1;
            _pageBudget = pageBudget;
        }

        /// <summary>Advances the LRU clock. Call once per frame before touching cells for that frame.</summary>
        public long BeginFrame() => ++_frame;

        /// <summary>Current LRU frame counter.</summary>
        public long CurrentFrame => _frame;

        /// <summary>True when the glyph is resident; refreshes its LRU timestamp if so.</summary>
        public bool TryGetCell(in GlyphCellKey key, out GlyphCell cell)
        {
            if (_cells.TryGetValue(key, out var rec))
            {
                rec.lastUsedFrame = _frame;
                cell = ToCell(rec);
                return true;
            }
            cell = default;
            return false;
        }

        /// <summary>Peek without touching the LRU timestamp (diagnostics / tests).</summary>
        public bool PeekCell(in GlyphCellKey key, out GlyphCell cell)
        {
            if (_cells.TryGetValue(key, out var rec)) { cell = ToCell(rec); return true; }
            cell = default;
            return false;
        }

        /// <summary>Increments a resident glyph's refcount (a component started displaying it).</summary>
        public void Acquire(in GlyphCellKey key)
        {
            if (_cells.TryGetValue(key, out var rec))
            {
                rec.refCount++;
                rec.lastUsedFrame = _frame;
            }
        }

        /// <summary>Decrements a resident glyph's refcount (a component stopped displaying it). Clamped at 0.</summary>
        public void Release(in GlyphCellKey key)
        {
            if (_cells.TryGetValue(key, out var rec) && rec.refCount > 0)
                rec.refCount--;
        }

        /// <summary>Current refcount for a cell (0 if not resident).</summary>
        public int RefCountOf(in GlyphCellKey key) =>
            _cells.TryGetValue(key, out var rec) ? rec.refCount : 0;

        /// <summary>
        /// Adds a rendered glyph bitmap to the shared array, returning its cell. The bitmap channel
        /// count must match the array format's channel count. Grows the array by a page when needed;
        /// when at <see cref="PageBudget"/>, evicts LRU refcount-0 cells and reuses their rects.
        /// Returns false only when the glyph cannot be placed even after eviction (too large / all
        /// pages pinned). Call <see cref="Apply"/> after a batch of adds. Main-thread only.
        /// </summary>
        public bool AddGlyph(in GlyphCellKey key, byte[] pixels, int width, int height, int channels, out GlyphCell cell)
        {
            cell = default;
            if (_cells.ContainsKey(key))
            {
                cell = ToCell(_cells[key]);
                return true;
            }
            if (pixels == null || width <= 0 || height <= 0)
                return false;
            if (channels != _channels)
            {
                Cat.MeowWarnFormat("[GlyphAtlasArray] channel mismatch: glyph {0} ch, array {1} ch ({2}).",
                    channels, _channels, _format);
                return false;
            }
            if (width + PackingSpacing > _size || height + PackingSpacing > _size)
                return false; // glyph larger than a whole page

            if (!TryReserveRect(width, height, out int slice, out GlyphRect packedRect, out GlyphRect glyphRect))
                return false;

            CopyToSlice(slice, pixels, width, height, glyphRect.x, glyphRect.y, channels);

            var rec = new CellRecord
            {
                key = key,
                slice = slice,
                packedRect = packedRect,
                glyphRect = glyphRect,
                refCount = 0,
                lastUsedFrame = _frame,
                channels = channels,
            };
            _cells[key] = rec;
            cell = ToCell(rec);
            return true;
        }

        /// <summary>
        /// Uploads pending pixel writes to the GPU. Call once after a batch of <see cref="AddGlyph"/>.
        /// Each dirty slice's CPU buffer is pushed with <see cref="Texture2DArray.SetPixelData{T}(T[],int,int,int)"/>,
        /// then a single <see cref="Texture2DArray.Apply(bool,bool)"/> commits them.
        /// </summary>
        public void Apply(bool updateMipmaps = false)
        {
            if (_array == null) return;
            bool any = false;
            for (int i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                if (!page.dirty) continue;
                _array.SetPixelData(page.cpu, 0, page.sliceIndex);
                page.dirty = false;
                any = true;
            }
            if (any)
                _array.Apply(updateMipmaps, false);
        }

        // --- Rect reservation: free-list reuse first, then shelf append, then grow, then evict -----
        private bool TryReserveRect(int w, int h, out int slice, out GlyphRect packedRect, out GlyphRect glyphRect)
        {
            int pw = w + PackingSpacing;
            int ph = h + PackingSpacing;

            // 1) Reuse a freed rect on any existing page (first-fit).
            for (int p = 0; p < _pages.Count; p++)
            {
                var page = _pages[p];
                for (int i = 0; i < page.freeRects.Count; i++)
                {
                    var fr = page.freeRects[i];
                    if (fr.width >= pw && fr.height >= ph)
                    {
                        page.freeRects.RemoveAt(i);
                        slice = page.sliceIndex;
                        glyphRect = new GlyphRect(fr.x, fr.y, w, h);
                        packedRect = new GlyphRect(fr.x, fr.y, pw, ph);
                        // Return the unused remainder of an oversized free rect so it stays reusable.
                        ReturnRemainder(page, fr, pw, ph);
                        return true;
                    }
                }
            }

            // 2) Shelf-append on an existing page.
            for (int p = 0; p < _pages.Count; p++)
            {
                if (TryShelfPack(_pages[p], w, h, out glyphRect, out packedRect))
                {
                    slice = _pages[p].sliceIndex;
                    return true;
                }
            }

            // 3) Grow by a page if under budget (or unbounded).
            if (_pageBudget <= 0 || _pages.Count < _pageBudget)
            {
                var page = GrowPage();
                if (TryShelfPack(page, w, h, out glyphRect, out packedRect))
                {
                    slice = page.sliceIndex;
                    return true;
                }
            }

            // 4) At budget: evict LRU refcount-0 cells to free rects, then retry reuse.
            if (EvictToFit(pw, ph))
            {
                for (int p = 0; p < _pages.Count; p++)
                {
                    var page = _pages[p];
                    for (int i = 0; i < page.freeRects.Count; i++)
                    {
                        var fr = page.freeRects[i];
                        if (fr.width >= pw && fr.height >= ph)
                        {
                            page.freeRects.RemoveAt(i);
                            slice = page.sliceIndex;
                            glyphRect = new GlyphRect(fr.x, fr.y, w, h);
                            packedRect = new GlyphRect(fr.x, fr.y, pw, ph);
                            ReturnRemainder(page, fr, pw, ph);
                            return true;
                        }
                    }
                }
            }

            slice = -1;
            packedRect = default;
            glyphRect = default;
            return false;
        }

        private static void ReturnRemainder(Page page, GlyphRect fr, int usedW, int usedH)
        {
            // Guillotine the leftover: a right strip and a bottom strip (whichever are non-empty).
            int rightW = fr.width - usedW;
            int bottomH = fr.height - usedH;
            if (rightW >= 2) // keep only usefully-sized remainders
                page.freeRects.Add(new GlyphRect(fr.x + usedW, fr.y, rightW, usedH));
            if (bottomH >= 2)
                page.freeRects.Add(new GlyphRect(fr.x, fr.y + usedH, fr.width, bottomH));
        }

        private bool TryShelfPack(Page page, int w, int h, out GlyphRect glyphRect, out GlyphRect packedRect)
        {
            glyphRect = default;
            packedRect = default;
            int pw = w + PackingSpacing;
            int ph = h + PackingSpacing;

            if (page.shelfX + pw > _size)
            {
                page.shelfY += page.shelfHeight + PackingSpacing;
                page.shelfX = 0;
                page.shelfHeight = 0;
            }
            if (page.shelfY + ph > _size)
                return false;

            glyphRect = new GlyphRect(page.shelfX, page.shelfY, w, h);
            packedRect = new GlyphRect(page.shelfX, page.shelfY, pw, ph);
            page.shelfX += pw;
            if (ph > page.shelfHeight) page.shelfHeight = ph;
            return true;
        }

        private Page GrowPage()
        {
            var page = new Page
            {
                sliceIndex = _pages.Count,
                cpu = new byte[_size * _size * _channels], // zero-initialized (transparent)
                dirty = true,
            };
            _pages.Add(page);
            EnsureArrayCapacity();
            return page;
        }

        /// <summary>
        /// Ensures the backing <see cref="Texture2DArray"/> has at least <see cref="PageCount"/>
        /// slices, growing it when pages were added. Slice contents are (re)uploaded from the CPU
        /// buffers in <see cref="Apply"/>, so a grow never needs a GPU-side slice copy.
        /// </summary>
        private void EnsureArrayCapacity()
        {
            int want = Mathf.Max(1, _pages.Count);
            if (_array != null && _array.depth >= want)
                return;

            if (_array != null)
                UnityEngine.Object.DestroyImmediate(_array);

            // mipChain:false, linear:true — the atlas stores DISTANCE-FIELD / coverage DATA, not sRGB
            // colour. Omitting linear makes Unity gamma-decode an RGBA32 array on sample, which
            // corrupts the MSDF median3 (glyphs render as solid blocks) and shifts SDF edges. Legacy
            // atlas textures are likewise created without sRGB.
            _array = new Texture2DArray(_size, _size, want, _format, false, true)
            {
                name = $"UniText SharedAtlas[{_format}] x{want}",
                hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            // All slices must be re-uploaded into the freshly allocated array.
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].dirty = true;
        }

        /// <summary>
        /// Evicts least-recently-used, refcount-0 cells until a <paramref name="needW"/> x
        /// <paramref name="needH"/> rect is reusable (or nothing more can be evicted). Returns true
        /// if at least one eviction happened that could satisfy the need.
        /// </summary>
        private bool EvictToFit(int needW, int needH)
        {
            // Candidates: refcount 0, oldest first.
            var candidates = new List<CellRecord>();
            foreach (var rec in _cells.Values)
                if (rec.refCount == 0)
                    candidates.Add(rec);
            if (candidates.Count == 0) return false;

            candidates.Sort((a, b) => a.lastUsedFrame.CompareTo(b.lastUsedFrame));

            bool anyFit = false;
            foreach (var rec in candidates)
            {
                EvictCell(rec);
                if (rec.packedRect.width >= needW && rec.packedRect.height >= needH)
                {
                    anyFit = true;
                    break; // enough for this allocation; stop evicting
                }
            }
            return anyFit;
        }

        private void EvictCell(CellRecord rec)
        {
            _cells.Remove(rec.key);
            var page = _pages[rec.slice];
            page.freeRects.Add(rec.packedRect);
            // Note: pixels are left in place; they are overwritten when the rect is reused, and the
            // glyph re-rasterizes on demand (the lookup now misses). No GPU clear needed.
        }

        /// <summary>
        /// Explicitly evicts a glyph (test / manual). No-op if resident with refcount &gt; 0 unless
        /// <paramref name="force"/>. Returns true if evicted.
        /// </summary>
        public bool Evict(in GlyphCellKey key, bool force = false)
        {
            if (!_cells.TryGetValue(key, out var rec)) return false;
            if (rec.refCount > 0 && !force) return false;
            EvictCell(rec);
            return true;
        }

        private GlyphCell ToCell(CellRecord rec)
        {
            float inv = 1f / _size;
            var uv = new Rect(rec.glyphRect.x * inv, rec.glyphRect.y * inv,
                              rec.glyphRect.width * inv, rec.glyphRect.height * inv);
            return new GlyphCell(rec.slice, rec.glyphRect, uv);
        }

        private void CopyToSlice(int slice, byte[] pixels, int w, int h, int dstX, int dstY, int channels)
        {
            var page = _pages[slice];
            byte[] dst = page.cpu;
            int atlasStride = _size * channels;
            int rowBytes = w * channels;
            for (int y = 0; y < h; y++)
            {
                int dstOffset = (dstY + y) * atlasStride + dstX * channels;
                Buffer.BlockCopy(pixels, y * rowBytes, dst, dstOffset, rowBytes);
            }
            page.dirty = true;
        }

        /// <summary>Drops all cells and pages (does not destroy the array; use <see cref="Dispose"/> for that).</summary>
        public void Clear()
        {
            _cells.Clear();
            _pages.Clear();
            _frame = 0;
        }

        // --- Test / diagnostics accessors (internal: visible to the test assembly) -----------------

        /// <summary>Reads one byte of a slice's CPU backing buffer (channel 0 for Alpha8).</summary>
        internal byte ReadCpuByte(int slice, int x, int y, int channel = 0)
        {
            var cpu = _pages[slice].cpu;
            return cpu[(y * _size + x) * _channels + channel];
        }

        /// <summary>Total free (reusable) rects currently held across all pages.</summary>
        internal int FreeRectCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _pages.Count; i++) n += _pages[i].freeRects.Count;
                return n;
            }
        }

        /// <summary>True when the glyph currently has a resident cell (no LRU touch).</summary>
        internal bool IsResident(in GlyphCellKey key) => _cells.ContainsKey(key);

        // --- Pages-as-slices facet (unified renderer): publish a whole legacy atlas page ----------
        // The unified single-renderer path reuses the existing per-font meshes/UVs wholesale and only
        // needs each legacy atlas PAGE to become a slice of the shared array, so a vertex's page-local
        // (u,v) + sliceIndex addresses it. This copies a source page's pixels into a dedicated slice
        // once, cached by an opaque page key (e.g. the Texture2D instance id), and returns the slice.
        private readonly Dictionary<int, int> _pageSlices = new(); // pageKey -> slice

        /// <summary>
        /// Publishes an entire legacy atlas page texture as a slice of this shared array, returning the
        /// slice index. Cached by <paramref name="pageKey"/> so a page is copied once. The source must
        /// be the array's dimensions and channel-compatible; a channel-expanding copy (Alpha8-&gt;RGBA32,
        /// RGB24-&gt;RGBA32) is performed when needed. Returns false if the source is null/unreadable or
        /// the dimensions do not match. Main-thread only; call <see cref="Apply"/> after a batch.
        /// </summary>
        public bool AddPage(int pageKey, Texture2D srcPage, out int slice)
        {
            slice = -1;
            if (_pageSlices.TryGetValue(pageKey, out slice))
                return true;
            if (srcPage == null || srcPage.width != _size || srcPage.height != _size)
                return false;

            int srcCh = srcPage.format == TextureFormat.RGBA32 ? 4 : srcPage.format == TextureFormat.RGB24 ? 3 : 1;
            Unity.Collections.NativeArray<byte> raw;
            try { raw = srcPage.GetRawTextureData<byte>(); }
            catch { return false; }
            if (!raw.IsCreated || raw.Length == 0) return false;

            var page = new Page { sliceIndex = _pages.Count, cpu = new byte[_size * _size * _channels], dirty = true };
            _pages.Add(page);
            EnsureArrayCapacity();
            slice = page.sliceIndex;

            // Copy with channel expansion matching the shared format.
            int px = _size * _size;
            for (int i = 0; i < px; i++)
            {
                int s = i * srcCh;
                int d = i * _channels;
                if (_channels == 1)
                    page.cpu[d] = srcCh == 4 ? raw[s + 3] : raw[s];
                else // RGBA32 shared
                {
                    if (srcCh == 1) { byte a = raw[s]; page.cpu[d] = a; page.cpu[d + 1] = a; page.cpu[d + 2] = a; page.cpu[d + 3] = a; }
                    else if (srcCh == 3) { page.cpu[d] = raw[s]; page.cpu[d + 1] = raw[s + 1]; page.cpu[d + 2] = raw[s + 2]; page.cpu[d + 3] = 255; }
                    else { page.cpu[d] = raw[s]; page.cpu[d + 1] = raw[s + 1]; page.cpu[d + 2] = raw[s + 2]; page.cpu[d + 3] = raw[s + 3]; }
                }
            }
            _pageSlices[pageKey] = slice;
            return true;
        }

        /// <summary>Slice a published page occupies, or -1 if that page key is not published.</summary>
        public int SliceForPage(int pageKey) => _pageSlices.TryGetValue(pageKey, out var s) ? s : -1;

        public void Dispose()
        {
            _cells.Clear();
            _pages.Clear();
            if (_array != null)
            {
                UnityEngine.Object.DestroyImmediate(_array);
                _array = null;
            }
        }
    }
}
