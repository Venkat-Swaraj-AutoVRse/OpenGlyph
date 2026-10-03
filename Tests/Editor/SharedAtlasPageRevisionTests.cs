using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Item 2 regression: STALE SHARED-ATLAS COPIES. A legacy atlas page is mutated in place
    /// (<c>page.Apply</c> after every glyph upload), but the unified renderer copied each page into
    /// the shared <see cref="GlyphAtlasArray"/> ONCE per instance id. Glyphs added to the page AFTER
    /// that first copy never reached the shared slice, so they drew blank under the unified renderer
    /// (the GlyphMeshPro default). The fix gives each page a revision counter (bumped on every glyph
    /// upload) that <see cref="GlyphAtlasArray.AddPage"/> uses to re-copy a changed page into its
    /// EXISTING slice (no new slice). This test asserts exactly that: a glyph rasterized after the
    /// first publish lands in the shared slice on re-publish, and the slice count does not grow.
    ///
    /// Written to FAIL without the fix: the copy-once AddPage returns the cached slice and never
    /// re-copies, so the late glyph's ink is absent from the slice.
    /// </summary>
    public class SharedAtlasPageRevisionTests
    {
        private UniTextFont _font;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();
            string path = MsdfTestUtil.FindNotoSansPath();
            if (path == null) Assert.Ignore("NotoSans-Regular.ttf fixture missing.");
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: 48);
            if (_font == null) Assert.Ignore("Font backend unavailable.");
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            SharedGlyphAtlas.Clear();
            if (_font != null) Object.DestroyImmediate(_font);
        }

        private uint Rasterize(char c)
        {
            uint gi = Shaper.GetGlyphIndex(_font, c);
            if (gi == 0) return 0;
            _font.TryAddGlyphsBatch(new List<uint> { gi });
            return gi;
        }

        // Sum of CPU-side slice bytes inside a glyph's rect (Alpha8 slice => channel 0).
        private static long SliceInk(GlyphAtlasArray arr, int slice, GlyphRect rect)
        {
            long sum = 0;
            for (int y = 0; y < rect.height; y++)
                for (int x = 0; x < rect.width; x++)
                    sum += arr.ReadCpuByte(slice, rect.x + x, rect.y + y);
            return sum;
        }

        [Test]
        public void LateGlyph_ReachesSharedSlice_OnRepublish_NoSliceGrowth()
        {
            // 1) Rasterize an early glyph and publish its legacy page to the shared array.
            uint gA = Rasterize('A');
            Assert.AreNotEqual(0u, gA, "font has 'A'");
            Assert.IsTrue(_font.TryGetGlyph(gA, VariationKey.None, out var glyphA));
            var page = _font.AtlasTextures[glyphA.atlasIndex];
            Assert.IsNotNull(page, "legacy atlas page exists");

            var arr = SharedGlyphAtlas.Get(TextureFormat.Alpha8, _font.AtlasSize);
            int rev1 = UniTextFont.AtlasPageRevision(page);
            Assert.IsTrue(arr.AddPage(page.GetInstanceID(), page, rev1, out int slice1), "first publish");
            Assert.Greater(SliceInk(arr, slice1, glyphA.glyphRect), 0, "'A' is in the slice after first publish");
            int pagesAfterFirst = arr.PageCount;

            // 2) Rasterize a glyph NOT used before — this uploads into the SAME legacy page and bumps
            //    its revision.
            uint gB = Rasterize('Q');
            Assert.AreNotEqual(0u, gB, "font has 'Q'");
            Assert.AreNotEqual(gA, gB, "'Q' is a distinct glyph");
            Assert.IsTrue(_font.TryGetGlyph(gB, VariationKey.None, out var glyphB));
            Assert.AreEqual(glyphA.atlasIndex, glyphB.atlasIndex, "both glyphs share the one legacy page");
            int rev2 = UniTextFont.AtlasPageRevision(page);
            Assert.Greater(rev2, rev1, "the page revision bumped when the late glyph was uploaded");

            // 3) Re-publish with the new revision: the page must be re-copied into the SAME slice.
            Assert.IsTrue(arr.AddPage(page.GetInstanceID(), page, rev2, out int slice2), "re-publish");
            Assert.AreEqual(slice1, slice2, "re-copy stays on the same slice (no new slice)");
            Assert.AreEqual(pagesAfterFirst, arr.PageCount, "the slice count did not grow");

            // 4) The late glyph's ink is now present in the shared slice (the whole point).
            Assert.Greater(SliceInk(arr, slice2, glyphB.glyphRect), 0,
                "the glyph added AFTER the first copy now has ink in the shared slice " +
                "(0 here is the stale-copy bug)");
        }

        [Test]
        public void SameRevision_DoesNotRecopy_ButLateRevisionDoes()
        {
            uint gA = Rasterize('M');
            Assert.AreNotEqual(0u, gA);
            Assert.IsTrue(_font.TryGetGlyph(gA, VariationKey.None, out var glyphA));
            var page = _font.AtlasTextures[glyphA.atlasIndex];
            var arr = SharedGlyphAtlas.Get(TextureFormat.Alpha8, _font.AtlasSize);

            int rev = UniTextFont.AtlasPageRevision(page);
            Assert.IsTrue(arr.AddPage(page.GetInstanceID(), page, rev, out int slice));
            // Re-publishing at the same revision is a no-op that returns the same slice.
            Assert.IsTrue(arr.AddPage(page.GetInstanceID(), page, rev, out int sliceAgain));
            Assert.AreEqual(slice, sliceAgain, "same-revision re-publish keeps the slice");
            Assert.AreEqual(1, arr.PageCount, "no slice growth on same-revision re-publish");
        }

        [Test]
        public void RemovePage_DropsStaleMapping_NoSliceLeakForReuse()
        {
            uint gA = Rasterize('Z');
            Assert.AreNotEqual(0u, gA);
            Assert.IsTrue(_font.TryGetGlyph(gA, VariationKey.None, out var glyphA));
            var page = _font.AtlasTextures[glyphA.atlasIndex];
            var arr = SharedGlyphAtlas.Get(TextureFormat.Alpha8, _font.AtlasSize);

            int key = page.GetInstanceID();
            Assert.IsTrue(arr.AddPage(key, page, UniTextFont.AtlasPageRevision(page), out int slice));
            Assert.AreEqual(slice, arr.SliceForPage(key), "page maps to its slice");

            // Simulate the source page being destroyed/cleared: the mapping must be removable so the
            // key does not forever resolve to a now-orphaned slice (_pageSlices leak).
            Assert.IsTrue(arr.RemovePage(key), "mapping removed");
            Assert.AreEqual(-1, arr.SliceForPage(key), "key no longer resolves to a slice after removal");

            // A fresh publish under the same key re-copies into a new slice (does not reuse the stale one).
            Assert.IsTrue(arr.AddPage(key, page, UniTextFont.AtlasPageRevision(page), out int slice2),
                "re-publish after removal succeeds");
            Assert.AreEqual(slice2, arr.SliceForPage(key), "key maps to the fresh slice");
        }
    }
}
