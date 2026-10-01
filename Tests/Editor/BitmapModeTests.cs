using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Phase 1c bitmap-mode tests: <see cref="UniTextRenderMode.Smooth"/> (grayscale AA) and
    /// <see cref="UniTextRenderMode.Mono"/> (1-bit) must render glyphs for REGULAR (non-emoji) fonts,
    /// not only pixel-perfect ones. Before Phase 1c the base engine had no bitmap raster path for
    /// these modes on regular fonts, so the channel-coherence guard dropped every glyph and Smooth
    /// added zero glyphs. These tests assert glyphs are packed, the Smooth atlas holds intermediate
    /// alpha (real anti-aliasing) with bilinear filtering, and the Mono atlas holds only 0/255 with
    /// point filtering.
    /// </summary>
    [TestFixture]
    public class BitmapModeTests
    {
        private UniTextFont _font;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable in this environment.");
        }

        [TearDown]
        public void TearDown()
        {
            if (_font != null) UnityEngine.Object.DestroyImmediate(_font);
            _font = null;
        }

        private UniTextFont MakeNoto(UniTextRenderMode mode, int ppem)
        {
            string path = MsdfTestUtil.FindNotoSansPath();
            if (path == null) Assert.Ignore("NotoSans-Regular.ttf not found in package.");
            var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: ppem, renderMode: mode);
            Assert.IsNotNull(f, "Font asset creation failed.");
            return f;
        }

        private static List<uint> Glyphs(UniTextFont font, string text)
        {
            var list = new List<uint>();
            foreach (char c in text)
            {
                uint gi = font.GetGlyphIndexForUnicode(c);
                if (gi != 0) list.Add(gi);
            }
            return list;
        }

        [Test]
        public void Smooth_RegularFont_AddsGlyphs()
        {
            _font = MakeNoto(UniTextRenderMode.Smooth, 48);
            var added = _font.TryAddGlyphsBatch(Glyphs(_font, "Ago"));
            Assert.Greater(added, 0, "Smooth regular font must add glyphs (regression: previously added 0).");
            Assert.Greater(_font.AtlasTextures.Count, 0);
        }

        [Test]
        public void Mono_RegularFont_AddsGlyphs()
        {
            _font = MakeNoto(UniTextRenderMode.Mono, 48);
            var added = _font.TryAddGlyphsBatch(Glyphs(_font, "Ago"));
            Assert.Greater(added, 0, "Mono regular font must add glyphs.");
            Assert.Greater(_font.AtlasTextures.Count, 0);
        }

        [Test]
        public void Smooth_Atlas_IsAlpha8_Bilinear_WithIntermediateAlpha()
        {
            _font = MakeNoto(UniTextRenderMode.Smooth, 48);
            int added = _font.TryAddGlyphsBatch(Glyphs(_font, "Ago"));
            Assert.Greater(added, 0);

            var tex = _font.AtlasTextures[0];
            Assert.AreEqual(TextureFormat.Alpha8, tex.format, "Regular Smooth atlas must be Alpha8 coverage.");
            Assert.AreEqual(FilterMode.Bilinear, tex.filterMode, "Smooth atlas must use bilinear filtering.");

            var raw = tex.GetRawTextureData<byte>();
            int intermediate = 0, opaque = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                byte v = raw[i];
                if (v == 255) opaque++;
                else if (v != 0) intermediate++;
            }
            Assert.Greater(opaque, 0, "Smooth atlas should have some fully-covered texels.");
            Assert.Greater(intermediate, 0, "Smooth atlas must contain intermediate alpha (anti-aliasing).");
        }

        [Test]
        public void Mono_Atlas_IsAlpha8_Point_OnlyZeroOr255()
        {
            _font = MakeNoto(UniTextRenderMode.Mono, 48);
            int added = _font.TryAddGlyphsBatch(Glyphs(_font, "Ago"));
            Assert.Greater(added, 0);

            var tex = _font.AtlasTextures[0];
            Assert.AreEqual(TextureFormat.Alpha8, tex.format, "Mono atlas must be Alpha8 coverage.");
            Assert.AreEqual(FilterMode.Point, tex.filterMode, "Mono atlas must use point filtering.");

            var raw = tex.GetRawTextureData<byte>();
            int on = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                byte v = raw[i];
                if (v != 0 && v != 255)
                    Assert.Fail($"Mono atlas texel {i}={v}: must be 0 or 255 only (no anti-aliasing).");
                if (v == 255) on++;
            }
            Assert.Greater(on, 0, "Mono atlas should have some set (255) texels.");
        }

        [Test]
        public void Regression_Sdf_AtlasStillAlpha8_AndAddsGlyphs()
        {
            _font = MakeNoto(UniTextRenderMode.SDF, 48);
            int added = _font.TryAddGlyphsBatch(Glyphs(_font, "Ago"));
            Assert.Greater(added, 0, "SDF must still add glyphs.");
            Assert.AreEqual(TextureFormat.Alpha8, _font.AtlasTextures[0].format, "SDF atlas remains Alpha8.");
            Assert.AreEqual(FilterMode.Bilinear, _font.AtlasTextures[0].filterMode, "SDF atlas keeps bilinear filtering.");
        }
    }
}
