using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using LightSide;
using LightSide.Msdf;

namespace LightSide.Tests
{
    [TestFixture]
    public class MsdfPipelineTests
    {
        private byte[] _fontBytes;

        [OneTimeSetUp]
        public void Setup()
        {
            string path = MsdfTestUtil.FindNotoSansPath();
            if (path == null)
                Assert.Ignore("NotoSans-Regular.ttf not found; skipping pipeline tests.");
            _fontBytes = File.ReadAllBytes(path);
        }

        private static List<uint> GlyphIndicesFor(UniTextFont font, string s)
        {
            var list = new List<uint>();
            foreach (char c in s)
            {
                uint gi = font.GetGlyphIndexForUnicode((uint)c);
                if (gi != 0) list.Add(gi);
            }
            return list;
        }

        [Test]
        public void SDF_NoRegression_ProducesAlpha8AtlasAndGlyphs()
        {
            var font = UniTextFont.CreateFontAsset(_fontBytes, 64, 0.25f, UniTextRenderMode.SDF, 512);
            Assert.IsNotNull(font, "SDF font asset creation failed.");
            try
            {
                var indices = GlyphIndicesFor(font, "Hello");
                Assert.Greater(indices.Count, 0, "No glyph indices resolved.");
                int added = font.TryAddGlyphsBatch(indices);
                Assert.Greater(added, 0, "SDF added no glyphs.");
                Assert.IsNotNull(font.AtlasTexture, "SDF atlas texture missing.");
                Assert.AreEqual(TextureFormat.Alpha8, font.AtlasTexture.format, "SDF atlas must remain Alpha8.");
                Assert.AreEqual(UniTextRenderMode.SDF, font.AtlasRenderMode);
            }
            finally { Object.DestroyImmediate(font); }
        }

        [Test]
        public void MSDF_AtlasEndToEnd_ForString()
        {
            var font = UniTextFont.CreateFontAsset(_fontBytes, 64, 0.25f, UniTextRenderMode.Msdf, 512);
            Assert.IsNotNull(font, "MSDF font asset creation failed.");
            try
            {
                Assert.AreEqual(UniTextRenderMode.Msdf, font.AtlasRenderMode, "Render mode should be Msdf.");
                var indices = GlyphIndicesFor(font, "Agn");
                Assert.Greater(indices.Count, 0);
                int added = font.TryAddGlyphsBatch(indices);
                Assert.Greater(added, 0, "MSDF added no glyphs.");
                Assert.IsNotNull(font.AtlasTexture, "MSDF atlas texture missing.");

                var fmt = font.AtlasTexture.format;
                if (fmt == TextureFormat.RGB24)
                {
                    // Native outline export present: verify the atlas actually carries multi-channel
                    // data (not a greyscale copy) somewhere — i.e. R != G or G != B on some texel.
                    var px = font.AtlasTexture.GetPixels32();
                    bool multichannel = false;
                    foreach (var p in px)
                        if (p.r != p.g || p.g != p.b) { multichannel = true; break; }
                    Assert.IsTrue(multichannel, "RGB24 MSDF atlas has no per-channel variation.");
                }
                else
                {
                    // Export absent -> documented SDF fallback (Alpha8). Still a valid outcome.
                    Assert.AreEqual(TextureFormat.Alpha8, fmt,
                        "MSDF should be RGB24 when the outline export exists, else fall back to Alpha8 SDF.");
                    Assert.Ignore("Native outline export 'ut_ft_get_outline_data' absent in this binary; " +
                                  "MSDF correctly fell back to SDF (Alpha8). Re-run against the phase-0 native build for full MSDF.");
                }
            }
            finally { Object.DestroyImmediate(font); }
        }

        [Test]
        public void AtlasPadding_MSDF_UsesSpreadBasedPadding_LikeSDF()
        {
            var sdf = UniTextFont.CreateFontAsset(_fontBytes, 64, 0.25f, UniTextRenderMode.SDF, 256);
            var msdf = UniTextFont.CreateFontAsset(_fontBytes, 64, 0.25f, UniTextRenderMode.Msdf, 256);
            try
            {
                Assert.AreEqual(sdf.AtlasPadding, msdf.AtlasPadding,
                    "MSDF padding must match SDF (spread-based), not the minimal bitmap padding.");
                Assert.Greater(msdf.AtlasPadding, 1, "MSDF padding should be spread-based (>1).");
            }
            finally
            {
                Object.DestroyImmediate(sdf);
                Object.DestroyImmediate(msdf);
            }
        }
    }
}
