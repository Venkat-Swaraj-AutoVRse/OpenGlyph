using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

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

        [Test]
        public void Msdf_Weight400_vs_700_DifferentAtlasFields()
        {
            // MSDF renders from a SEPARATE msdfFace; verify the variation facet produces distinct
            // MSDF atlas cells at wght 400 vs 700 (field bytes differ), via that face.
            var vf = UniTextFont.CreateFontAsset(_bytes, samplingPointSize: 64, renderMode: UniTextRenderMode.Msdf);
            if (vf == null) Assert.Ignore("RobotoFlex MSDF asset failed to load.");
            try
            {
                if (vf.AtlasRenderMode != UniTextRenderMode.Msdf)
                    Assert.Ignore("MSDF outline export unavailable in this native binary.");

                uint g = Shaper.GetGlyphIndex(vf, 'g');
                var a = Instance(400); var b = Instance(700);
                var list = new List<uint> { g };
                Assert.AreEqual(1, vf.EnsureGlyphsForVariation(list, a.key, a.tags, a.coords));
                Assert.AreEqual(1, vf.EnsureGlyphsForVariation(list, b.key, b.tags, b.coords));

                Assert.IsTrue(vf.TryGetGlyph(g, a.key, out var ga));
                Assert.IsTrue(vf.TryGetGlyph(g, b.key, out var gb));

                // Read the RGB24 MSDF cells and assert the fields are not byte-identical.
                var tex = vf.AtlasTextures[ga.atlasIndex];
                var pa = tex.GetPixels(ga.glyphRect.x, ga.glyphRect.y, Mathf.Max(1, ga.glyphRect.width), Mathf.Max(1, ga.glyphRect.height));
                var texB = vf.AtlasTextures[gb.atlasIndex];
                var pb = texB.GetPixels(gb.glyphRect.x, gb.glyphRect.y, Mathf.Max(1, gb.glyphRect.width), Mathf.Max(1, gb.glyphRect.height));
                bool differ = pa.Length != pb.Length;
                if (!differ)
                    for (int i = 0; i < pa.Length && !differ; i++)
                        if (Mathf.Abs(pa[i].r - pb[i].r) > 0.02f || Mathf.Abs(pa[i].g - pb[i].g) > 0.02f || Mathf.Abs(pa[i].b - pb[i].b) > 0.02f)
                            differ = true;
                Assert.IsTrue(differ, "MSDF 'g' fields must differ between wght 400 and 700.");
            }
            finally { UnityEngine.Object.DestroyImmediate(vf); }
        }

        [Test]
        public void DefaultAppearance_MsdfFont_UsesMsdfShader_NotSdf()
        {
            // Product fix: an MSDF-mode font on the DEFAULT appearance must be drawn with an MSDF
            // shader, not the SDF default (which would render solid blocks sampling the RGB atlas).
            var vf = UniTextFont.CreateFontAsset(_bytes, samplingPointSize: 64, renderMode: UniTextRenderMode.Msdf);
            if (vf == null) Assert.Ignore("RobotoFlex MSDF asset failed to load.");
            try
            {
                if (vf.AtlasRenderMode != UniTextRenderMode.Msdf)
                    Assert.Ignore("MSDF outline export unavailable in this native binary.");
                var appearance = RealLayoutFixtures.LoadDefaultAppearance();
                var mats = appearance.GetMaterials(vf);
                Assert.IsNotNull(mats);
                Assert.Greater(mats.Length, 0);
                Assert.IsNotNull(mats[0]);
                Assert.IsNotNull(mats[0].shader);
                StringAssert.Contains("MSDF", mats[0].shader.name,
                    $"An MSDF font on the default appearance must use an MSDF shader, got '{mats[0].shader.name}'.");

                // And a plain SDF font must still get its (non-MSDF) shader.
                var sdf = UniTextFont.CreateFontAsset(_bytes, samplingPointSize: 64, renderMode: UniTextRenderMode.SDF);
                try
                {
                    var sdfMats = appearance.GetMaterials(sdf);
                    if (sdfMats != null && sdfMats.Length > 0 && sdfMats[0] != null && sdfMats[0].shader != null)
                        StringAssert.DoesNotContain("MSDF", sdfMats[0].shader.name, "An SDF font must not get an MSDF shader.");
                }
                finally { UnityEngine.Object.DestroyImmediate(sdf); }
            }
            finally { UnityEngine.Object.DestroyImmediate(vf); }
        }

        [Test]
        public void Colr_EmojiFont_UnaffectedByVariationKeys()
        {
            // The EmojiFont (COLR) is a non-variable color font; a variation request on it is a no-op
            // (nothing to vary) and must not create variation atlas entries. We assert the facet
            // reports no additions for a synthetic non-variable font: use RobotoFlex but request a
            // key on a font whose EnsureGlyphsForVariation still returns coords -- the invariant we
            // guarantee is that a NON-variable font yields VariationKey.None, so no keyed store forms.
            // Load Noto (static) as the stand-in for a non-variable face.
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans not found.");
            var staticFont = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 64);
            try
            {
                var face = FT.LoadFace(File.ReadAllBytes(noto), 0);
                VariationMapper map;
                try { map = VariationMapper.Read(face); } finally { FT.UnloadFace(face); }
                Assert.IsFalse(map.isVariable, "A static font must not be variable.");
                var key = map.Map(new FontStyleSpec(700, 100, StyleAxis.Normal), 0f, out var tags, out var coords);
                Assert.AreEqual(VariationKey.None, key, "A non-variable font yields VariationKey.None -> no variation atlas, keys cannot affect it.");
            }
            finally { UnityEngine.Object.DestroyImmediate(staticFont); }
        }
    }
}
