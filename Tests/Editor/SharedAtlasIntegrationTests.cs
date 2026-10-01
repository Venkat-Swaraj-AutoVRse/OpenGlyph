using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, step 1 integration: two DISTINCT <see cref="UniTextFont"/>s
    /// publish their packed glyphs into ONE shared <see cref="GlyphAtlasArray"/> (same pixel format),
    /// via <see cref="UniTextFont.TryPublishToSharedAtlas"/> and the <see cref="SharedGlyphAtlas"/>
    /// registry. This is the "two fonts share one array" property the single draw-group renderer
    /// relies on. The legacy per-font atlas path is unchanged (asserted by the full suite staying
    /// green); here we only check the additive shared facet.
    /// </summary>
    public class SharedAtlasIntegrationTests
    {
        private UniTextFont _a, _b;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();

            string notoSans = MsdfTestUtil.FindNotoSansPath();
            if (notoSans == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            // A second DISTINCT font in the same Defaults/ folder (SDF mode, same atlas size).
            string arabic = Path.Combine(Path.GetDirectoryName(notoSans)!, "NotoSansArabic-Regular.ttf");
            if (!File.Exists(arabic)) Assert.Ignore("NotoSansArabic-Regular.ttf not found.");

            _a = UniTextFont.CreateFontAsset(File.ReadAllBytes(notoSans), samplingPointSize: 48);
            _b = UniTextFont.CreateFontAsset(File.ReadAllBytes(arabic), samplingPointSize: 48);
            if (_a == null || _b == null) Assert.Ignore("font asset creation failed.");
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            SharedGlyphAtlas.Clear();
            if (_a != null) Object.DestroyImmediate(_a);
            if (_b != null) Object.DestroyImmediate(_b);
        }

        private static uint RasterizeOne(UniTextFont f, char c)
        {
            uint gi = Shaper.GetGlyphIndex(f, c);
            if (gi == 0) return 0;
            f.TryAddGlyphsBatch(new System.Collections.Generic.List<uint> { gi });
            return gi;
        }

        [Test]
        public void TwoFonts_SameFormat_ShareOneSharedArray()
        {
            // Both are SDF (Alpha8) -> ONE shared array for the pair.
            uint ga = RasterizeOne(_a, 'A');
            uint gb = RasterizeOne(_b, '\u0628'); // Arabic beh
            Assert.AreNotEqual(0u, ga, "latin 'A' rasterized in font A");
            Assert.AreNotEqual(0u, gb, "arabic glyph rasterized in font B");

            Assert.IsTrue(_a.TryPublishToSharedAtlas(ga, VariationKey.None, out var cellA),
                "font A glyph published to shared array");
            Assert.IsTrue(_b.TryPublishToSharedAtlas(gb, VariationKey.None, out var cellB),
                "font B glyph published to shared array");

            // Exactly ONE shared array exists for the Alpha8 format, and it holds both fonts' glyphs.
            Assert.AreEqual(1, SharedGlyphAtlas.ArrayCount, "both SDF fonts use ONE shared array");
            var arr = SharedGlyphAtlas.Get(TextureFormat.Alpha8, _a.AtlasSize);
            Assert.IsNotNull(arr.Texture, "the shared Texture2DArray is allocated (one binding)");
            Assert.GreaterOrEqual(arr.CellCount, 2, "both fonts' glyphs are resident in the one array");

            // The two glyphs resolve to cells on the SAME shared array (same binding), distinct rects.
            Assert.IsTrue(cellA.IsValid && cellB.IsValid);
            Assert.AreEqual(cellA.slice, cellB.slice, "both fit on slice 0 of the shared array");

            // Keys are per-font distinct, so a lookup for A's glyph under B's font id must miss.
            var keyA = new GlyphAtlasArray.GlyphCellKey(_a.GetCachedInstanceId(), ga, VariationKey.None);
            var keyBWrongFont = new GlyphAtlasArray.GlyphCellKey(_a.GetCachedInstanceId(), gb, VariationKey.None);
            Assert.IsTrue(arr.PeekCell(keyA, out _), "font A's glyph resident under font A's id");
            if (ga != gb)
                Assert.IsFalse(arr.PeekCell(keyBWrongFont, out _), "font B's glyph is NOT under font A's id");
        }

        [Test]
        public void Republish_IsIdempotent()
        {
            uint ga = RasterizeOne(_a, 'Q');
            Assert.AreNotEqual(0u, ga);
            Assert.IsTrue(_a.TryPublishToSharedAtlas(ga, VariationKey.None, out _));
            var arr = SharedGlyphAtlas.Get(TextureFormat.Alpha8, _a.AtlasSize);
            int before = arr.CellCount;
            Assert.IsTrue(_a.TryPublishToSharedAtlas(ga, VariationKey.None, out _), "re-publish succeeds");
            Assert.AreEqual(before, arr.CellCount, "re-publishing the same glyph adds no new cell");
        }
    }
}
