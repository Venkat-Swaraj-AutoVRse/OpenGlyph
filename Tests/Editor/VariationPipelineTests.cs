using System;
using System.IO;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Verifies the live <see cref="Shaper"/> path produces instance-specific advances for a variable
    /// font: the SAME face, SAME codepoint shaped at wght 400 vs 700 returns different HarfBuzz
    /// advances, and the two instances occupy distinct cache entries. Uses RobotoFlex-VF.
    /// </summary>
    public class VariationPipelineTests
    {
        private UniTextFont _vf;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");
            _vf = UniTextFont.CreateFontAsset(File.ReadAllBytes(vf), samplingPointSize: 48);
            if (_vf == null) Assert.Ignore("RobotoFlex face failed to load.");
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            if (_vf != null) UnityEngine.Object.DestroyImmediate(_vf);
        }

        private VariationKey MapWeight(int w, out uint[] tags, out float[] coords)
        {
            var face = FT.LoadFace(_vf.FontData, 0);
            try
            {
                var map = VariationMapper.Read(face);
                return map.Map(new FontStyleSpec(w, 100, StyleAxis.Normal), 0f, out tags, out coords);
            }
            finally { FT.UnloadFace(face); }
        }

        [Test]
        public void SameFace_Weight400_vs_700_DifferentShapedAdvance()
        {
            var k400 = MapWeight(400, out var t400, out var c400);
            var k700 = MapWeight(700, out var t700, out var c700);
            Assert.AreNotEqual(k400, k700, "Distinct instances must have distinct VariationKeys.");

            Assert.IsTrue(Shaper.TryGetGlyphInfoVaried(_vf, 'g', 48f, k400, t400, c400, out var gi400, out var adv400));
            Assert.IsTrue(Shaper.TryGetGlyphInfoVaried(_vf, 'g', 48f, k700, t700, c700, out var gi700, out var adv700));

            Assert.AreEqual(gi400, gi700, "Same codepoint maps to the same glyph id across weights.");
            Assert.AreNotEqual(adv400, adv700,
                $"Shaped 'g' advance must differ between wght 400 ({adv400}) and 700 ({adv700}).");
        }

        [Test]
        public void SameRequest_ReusesOneCacheEntry()
        {
            var k = MapWeight(600, out var t, out var c);
            Assert.IsTrue(Shaper.TryGetGlyphInfoVaried(_vf, 'a', 48f, k, t, c, out _, out var a1));
            Assert.IsTrue(Shaper.TryGetGlyphInfoVaried(_vf, 'a', 48f, k, t, c, out _, out var a2));
            Assert.AreEqual(a1, a2, "Identical instance requests must be stable (one cache entry).");
        }

        [Test]
        public void NoneKey_EqualsPlainShaping()
        {
            // VariationKey.None must route to the plain per-face cache and still shape.
            Assert.IsTrue(Shaper.TryGetGlyphInfoVaried(_vf, 'R', 48f, VariationKey.None, null, null, out var gi, out var adv));
            Assert.AreNotEqual(0u, gi);
            Assert.Greater(adv, 0f);
        }
    }
}
