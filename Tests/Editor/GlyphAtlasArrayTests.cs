using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Key = LightSide.GlyphAtlasArray.GlyphCellKey;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, step 1: the shared <see cref="GlyphAtlasArray"/>. Covers the
    /// three required behaviours:
    ///   (1) two fonts share one Texture2DArray;
    ///   (2) eviction frees and reuses a page/rect when over budget;
    ///   (3) an evicted glyph re-rasterizes on demand.
    /// Plus refcount pinning and the default (unbounded, no-eviction) invariant.
    ///
    /// The allocator/refcount/LRU logic is pure CPU; assertions read the CPU-side backing buffer and
    /// cell records, so the atlas's bookkeeping is exercised deterministically. Constructing the
    /// backing <see cref="Texture2DArray"/> needs the editor (EditMode), not a GPU/display.
    /// </summary>
    public class GlyphAtlasArrayTests
    {
        private const int Size = 64; // small page so a few glyphs fill it — keeps budget tests fast
        private GlyphAtlasArray _atlas;

        [TearDown]
        public void TearDown() => _atlas?.Dispose();

        // A solid Alpha8 glyph bitmap of the given value, so re-rasterization is observable per-byte.
        private static byte[] Alpha(int w, int h, byte v)
        {
            var b = new byte[w * h];
            for (int i = 0; i < b.Length; i++) b[i] = v;
            return b;
        }

        private static Key K(int font, uint glyph) => new(font, glyph, VariationKey.None);

        [Test]
        public void TwoFonts_ShareOneArray_OneBinding()
        {
            _atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8); // unbounded

            // Font A, glyph 1 and Font B, glyph 1 are DISTINCT keys (different fontId) but land in the
            // SAME shared array — the enabling property for one draw group across fonts.
            Assert.IsTrue(_atlas.AddGlyph(K(10, 1), Alpha(8, 10, 200), 8, 10, 1, out var cellA));
            Assert.IsTrue(_atlas.AddGlyph(K(20, 1), Alpha(9, 11, 180), 9, 11, 1, out var cellB));

            Assert.AreEqual(2, _atlas.CellCount, "both fonts' glyphs resident");
            Assert.AreEqual(1, _atlas.PageCount, "both fit on one page -> one array slice");
            Assert.AreSame(_atlas.Texture, _atlas.Texture);
            Assert.IsNotNull(_atlas.Texture, "a single Texture2DArray backs both fonts");
            Assert.AreEqual(cellA.slice, cellB.slice, "both glyphs are on the same slice (one binding)");

            // Lookups resolve per font to the correct cell.
            Assert.IsTrue(_atlas.TryGetCell(K(10, 1), out var gotA));
            Assert.IsTrue(_atlas.TryGetCell(K(20, 1), out var gotB));
            Assert.AreEqual(cellA.pixelRect, gotA.pixelRect);
            Assert.AreEqual(cellB.pixelRect, gotB.pixelRect);
            Assert.AreNotEqual(gotA.pixelRect, gotB.pixelRect,
                "the two glyphs occupy different rects on the shared slice");

            // Normalized UVs are inside [0,1].
            Assert.That(gotA.uvRect.xMax, Is.LessThanOrEqualTo(1f));
            Assert.That(gotB.uvRect.yMax, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void OverBudget_Eviction_FreesAndReusesPage()
        {
            // Budget = 1 page. Fill it until the FIRST add that can only be satisfied by eviction;
            // that proves the page was full. The evicted (refcount-0, LRU) cell's rect must be freed
            // and REUSED (no second page is ever created).
            _atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8, pageBudget: 1);

            // Pack glyphs; detect the first eviction as the add whose CellCount does not grow by one
            // (an older cell was dropped to make room). That add is the "over-budget" event itself.
            var added = new List<Key>();
            Key overBudget = default;
            bool evictionHappened = false;
            for (int i = 0; i < 1000; i++)
            {
                var k = K(1, (uint)(100 + i));
                int before = _atlas.CellCount;
                Assert.IsTrue(_atlas.AddGlyph(k, Alpha(14, 14, (byte)(1 + (i % 254))), 14, 14, 1, out var cell),
                    "add should always succeed (grow or evict)");
                if (_atlas.CellCount == before + 1)
                {
                    added.Add(k);
                }
                else
                {
                    // The page was full: this add evicted an LRU cell and reused its rect.
                    overBudget = k;
                    evictionHappened = true;
                    Assert.AreEqual(0, cell.slice, "over-budget glyph reused a rect on the only page");
                    break;
                }
            }

            Assert.IsTrue(evictionHappened, "the single-page budget must eventually force an eviction");
            Assert.AreEqual(1, _atlas.PageCount, "still ONE page — a freed rect was reused, not grown");
            Assert.IsTrue(_atlas.IsResident(overBudget), "the over-budget glyph is resident (it took the reused rect)");

            // Exactly one of the previously-added glyphs was evicted to make room (net resident count
            // is unchanged across the over-budget add: one out, one in).
            int stillResident = 0;
            Key evicted = default;
            foreach (var k in added)
                if (_atlas.IsResident(k)) stillResident++; else evicted = k;
            Assert.AreEqual(added.Count - 1, stillResident, "exactly one earlier glyph was evicted");
            Assert.IsFalse(_atlas.IsResident(evicted), "the evicted glyph is no longer resident");

            // And the evicted glyph re-rasterizes on demand later (lookup currently misses).
            Assert.IsFalse(_atlas.TryGetCell(evicted, out _), "evicted glyph lookup misses until re-added");
        }

        [Test]
        public void RefcountedGlyph_IsNotEvicted()
        {
            _atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8, pageBudget: 1);

            var pinned = K(1, 1);
            Assert.IsTrue(_atlas.AddGlyph(pinned, Alpha(14, 14, 50), 14, 14, 1, out _));
            _atlas.Acquire(pinned); // a component is displaying it -> refcount 1

            // Fill the rest of the page (stop one before the first eviction).
            int expected = 1;
            for (int i = 0; i < 500; i++)
            {
                var k = K(1, (uint)(100 + i));
                Assert.IsTrue(_atlas.AddGlyph(k, Alpha(14, 14, (byte)(1 + i % 254)), 14, 14, 1, out _));
                if (_atlas.CellCount != expected + 1) { _atlas.Evict(k, force: true); break; }
                expected++;
            }

            // Over-budget add: eviction must skip the pinned glyph and take an unpinned LRU one.
            Assert.IsTrue(_atlas.AddGlyph(K(1, 9999), Alpha(14, 14, 200), 14, 14, 1, out _),
                "add succeeds by evicting an UNPINNED cell");
            Assert.IsTrue(_atlas.IsResident(pinned), "a refcount>0 glyph is never evicted");
        }

        [Test]
        public void EvictedGlyph_ReRasterizesOnDemand()
        {
            // (3) After eviction, the lookup misses; re-adding re-rasterizes the glyph (writes its
            // bitmap again) and makes it resident with a valid cell.
            _atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8, pageBudget: 1);

            var g = K(7, 42);
            Assert.IsTrue(_atlas.AddGlyph(g, Alpha(10, 10, 111), 10, 10, 1, out var first));
            Assert.IsTrue(_atlas.TryGetCell(g, out _), "resident after first add");

            // Force eviction via explicit API (refcount 0).
            Assert.IsTrue(_atlas.Evict(g), "unpinned glyph evicts");
            Assert.IsFalse(_atlas.TryGetCell(g, out _), "lookup MISSES after eviction (so the caller re-rasterizes)");

            // Re-rasterize on demand: a different bitmap value proves the pixels were written again.
            Assert.IsTrue(_atlas.AddGlyph(g, Alpha(10, 10, 222), 10, 10, 1, out var second));
            Assert.IsTrue(_atlas.TryGetCell(g, out var got), "resident again after re-rasterize");
            Assert.IsTrue(got.IsValid);
            Assert.AreEqual((byte)222, _atlas.ReadCpuByte(second.slice, second.pixelRect.x, second.pixelRect.y),
                "the re-rasterized bitmap (222) was written into the slice");
        }

        [Test]
        public void DefaultUnbounded_NeverEvicts_GrowsPages()
        {
            // Default (pageBudget <= 0): behaviour matches the legacy append-only atlas — it grows
            // pages and never evicts, so nothing a component already shows can vanish.
            _atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8); // budget 0 => unbounded

            for (int i = 0; i < 60; i++)
                Assert.IsTrue(_atlas.AddGlyph(K(1, (uint)i), Alpha(14, 14, (byte)(1 + i % 254)), 14, 14, 1, out _));

            Assert.AreEqual(60, _atlas.CellCount, "every glyph is retained");
            Assert.Greater(_atlas.PageCount, 1, "the array grew past one page rather than evicting");
            Assert.AreEqual(0, _atlas.FreeRectCount, "no eviction occurred, so no freed rects");
        }

        [Test]
        public void Rgba32Array_AcceptsFourChannelGlyphs_RejectsMismatch()
        {
            // The RGBA32 array (MSDF + COLR emoji, decision §5.1) takes 4-channel glyphs and rejects
            // a 1-channel (Alpha8) payload, upholding the channel-coherence guard.
            _atlas = new GlyphAtlasArray(Size, TextureFormat.RGBA32);
            var rgba = new byte[6 * 6 * 4];
            for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i % 256);
            Assert.IsTrue(_atlas.AddGlyph(K(1, 1), rgba, 6, 6, 4, out _), "4-ch glyph accepted by RGBA32 array");
            Assert.IsFalse(_atlas.AddGlyph(K(1, 2), Alpha(6, 6, 10), 6, 6, 1, out _), "1-ch glyph rejected by RGBA32 array");
        }
    }
}
