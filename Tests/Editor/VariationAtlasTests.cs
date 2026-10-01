using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Step 4: the variation-keyed atlas facet of <see cref="UniTextFont"/>. The SAME glyph of a
    /// variable font, rasterized at wght 400 vs 700 (design coords applied to the FreeType face
    /// before rendering), is stored under distinct <see cref="VariationKey"/>s as DIFFERENT atlas
    /// entries (different bitmap extents and/or metrics). Uses RobotoFlex-VF.
    /// </summary>
    public class VariationAtlasTests
    {
        private UniTextFont _vf;
        private byte[] _bytes;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");
            _bytes = File.ReadAllBytes(vf);
            _vf = UniTextFont.CreateFontAsset(_bytes, samplingPointSize: 64);
            if (_vf == null) Assert.Ignore("RobotoFlex face failed to load.");
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            if (_vf != null) UnityEngine.Object.DestroyImmediate(_vf);
        }

        private (VariationKey key, uint[] tags, float[] coords) Instance(int weight)
        {
            var face = FT.LoadFace(_bytes, 0);
            try
            {
                var map = VariationMapper.Read(face);
                var key = map.Map(new FontStyleSpec(weight, 100, StyleAxis.Normal), 0f, out var tags, out var coords);
                return (key, tags, coords);
            }
            finally { FT.UnloadFace(face); }
        }

        [Test]
        public void SameGlyph_Weight400_vs_700_TwoDistinctAtlasEntries()
        {
            uint g = Shaper.GetGlyphIndex(_vf, 'g');
            Assert.AreNotEqual(0u, g, "'g' glyph index.");

            var a = Instance(400);
            var b = Instance(700);
            Assert.AreNotEqual(a.key, b.key, "wght 400 and 700 must be distinct VariationKeys.");

            var list = new List<uint> { g };
            int addedA = _vf.EnsureGlyphsForVariation(list, a.key, a.tags, a.coords);
            int addedB = _vf.EnsureGlyphsForVariation(list, b.key, b.tags, b.coords);
            Assert.AreEqual(1, addedA, "wght 400 glyph must be added to its own store.");
            Assert.AreEqual(1, addedB, "wght 700 glyph must be added to its own store.");

            Assert.IsTrue(_vf.HasGlyphInAtlas(g, a.key), "wght 400 'g' present under its key.");
            Assert.IsTrue(_vf.HasGlyphInAtlas(g, b.key), "wght 700 'g' present under its key.");

            Assert.IsTrue(_vf.TryGetGlyph(g, a.key, out var ga));
            Assert.IsTrue(_vf.TryGetGlyph(g, b.key, out var gb));

            // Two SEPARATE atlas cells (different rect position OR size), and the heavier weight has
            // a wider/taller bbox and/or different advance -- i.e. genuinely different fields.
            bool rectDiffers = !ga.glyphRect.Equals(gb.glyphRect);
            bool metricsDiffer = ga.metrics.width != gb.metrics.width
                                 || ga.metrics.height != gb.metrics.height
                                 || ga.metrics.horizontalAdvance != gb.metrics.horizontalAdvance;
            Assert.IsTrue(rectDiffers, $"The two instances must occupy different atlas rects (400={ga.glyphRect}, 700={gb.glyphRect}).");
            Assert.IsTrue(metricsDiffer, "The two instances must have different glyph fields (bbox/advance).");
        }

        [Test]
        public void SameInstance_IsCachedOnce()
        {
            uint g = Shaper.GetGlyphIndex(_vf, 'a');
            var a = Instance(400);
            var list = new List<uint> { g };
            Assert.AreEqual(1, _vf.EnsureGlyphsForVariation(list, a.key, a.tags, a.coords));
            Assert.AreEqual(0, _vf.EnsureGlyphsForVariation(list, a.key, a.tags, a.coords),
                "Re-requesting the same (glyph, instance) must not add a second atlas entry.");
        }

        [Test]
        public void NoneKey_IsNoOp()
        {
            uint g = Shaper.GetGlyphIndex(_vf, 'R');
            Assert.AreEqual(0, _vf.EnsureGlyphsForVariation(new List<uint> { g }, VariationKey.None, null, null),
                "VariationKey.None routes to the normal batch path, not the variation facet.");
        }
    }
}
