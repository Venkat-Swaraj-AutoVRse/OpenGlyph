using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using LightSide;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>A deterministic <see cref="IGlyphOutlineSource"/> for tests.</summary>
    internal sealed class FakeOutlineSource : IGlyphOutlineSource
    {
        private readonly bool _available;
        private readonly System.Func<uint, GlyphOutline> _factory;
        public FakeOutlineSource(bool available, System.Func<uint, GlyphOutline> factory = null)
        {
            _available = available;
            _factory = factory;
        }
        public bool IsAvailable => _available;
        public GlyphOutline GetOutline(uint glyphIndex, int pixelsPerEm) => _factory?.Invoke(glyphIndex);
    }

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

        // ---- Deterministic MSDF FALLBACK (source unavailable) ----

        [Test]
        public void MSDF_FallsBackToSDF_WhenOutlineSourceUnavailable()
        {
            var font = UniTextFont.CreateFontAsset(_fontBytes, 64, 0.25f, UniTextRenderMode.Msdf, 512);
            Assert.IsNotNull(font);
            try
            {
                // Inject an unavailable source: the effective mode MUST degrade to SDF before any
                // atlas is created, so the atlas is Alpha8 and AtlasRenderMode reports SDF.
                font.SetOutlineSourceForTesting(new FakeOutlineSource(available: false));
                Assert.AreEqual(UniTextRenderMode.SDF, font.AtlasRenderMode,
                    "Unavailable outline source must degrade effective mode to SDF.");
                Assert.AreEqual(UniTextRenderMode.Msdf, font.ConfiguredRenderMode,
                    "Configured mode should still read Msdf.");

                var indices = GlyphIndicesFor(font, "Agn");
                int added = font.TryAddGlyphsBatch(indices);
                Assert.Greater(added, 0, "Fallback SDF added no glyphs.");
                Assert.AreEqual(TextureFormat.Alpha8, font.AtlasTexture.format,
                    "Fallback atlas must be Alpha8 (a clean SDF atlas), not a half-written RGB24 one.");
            }
            finally { Object.DestroyImmediate(font); }
        }

        // ---- Deterministic MSDF RGB24 path (source available, known outlines) ----

        [Test]
        public void MSDF_ProducesRGB24MultichannelAtlas_WhenOutlineSourceAvailable()
        {
            var font = UniTextFont.CreateFontAsset(_fontBytes, 64, 0.25f, UniTextRenderMode.Msdf, 512);
            Assert.IsNotNull(font);
            try
            {
                // Fake source returns a known square for every glyph index, so the RGB24 MSDF path
                // runs deterministically without the native export.
                font.SetOutlineSourceForTesting(new FakeOutlineSource(true,
                    _ => MsdfTestUtil.Square(0, 0, 40)));

                Assert.AreEqual(UniTextRenderMode.Msdf, font.AtlasRenderMode,
                    "Available outline source must keep effective mode Msdf.");

                var indices = GlyphIndicesFor(font, "AB");
                Assert.Greater(indices.Count, 0);
                int added = font.TryAddGlyphsBatch(indices);
                Assert.Greater(added, 0, "MSDF added no glyphs from fake source.");
                Assert.AreEqual(TextureFormat.RGB24, font.AtlasTexture.format,
                    "Available MSDF source must yield an RGB24 atlas.");

                // A square has 4 sharp corners -> genuine per-channel divergence must appear.
                var px = font.AtlasTexture.GetPixels32();
                bool multichannel = false;
                foreach (var p in px)
                    if (p.r != p.g || p.g != p.b) { multichannel = true; break; }
                Assert.IsTrue(multichannel, "RGB24 MSDF atlas has no per-channel variation.");
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
                // Force MSDF effective-mode to stay Msdf via an available fake source, so padding
                // is compared under the intended mode regardless of the native binary.
                msdf.SetOutlineSourceForTesting(new FakeOutlineSource(true, _ => MsdfTestUtil.Square(0, 0, 40)));
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

        [Test]
        public void MSDF_AtlasEndToEnd_RealBinary_CoherentFormat()
        {
            // With no injected source, the effective mode follows the ACTUAL native binary: RGB24
            // when ut_ft_get_outline_data exists, else a clean Alpha8 SDF fallback. Either way the
            // atlas format must be coherent with AtlasRenderMode (the bug this replaces produced a
            // RGB24 atlas while the export was absent).
            var font = UniTextFont.CreateFontAsset(_fontBytes, 64, 0.25f, UniTextRenderMode.Msdf, 512);
            Assert.IsNotNull(font);
            try
            {
                var indices = GlyphIndicesFor(font, "Agn");
                int added = font.TryAddGlyphsBatch(indices);
                Assert.Greater(added, 0);
                var fmt = font.AtlasTexture.format;
                if (font.AtlasRenderMode == UniTextRenderMode.Msdf)
                    Assert.AreEqual(TextureFormat.RGB24, fmt, "Msdf effective mode must have an RGB24 atlas.");
                else
                    Assert.AreEqual(TextureFormat.Alpha8, fmt, "SDF-fallback effective mode must have an Alpha8 atlas.");
            }
            finally { Object.DestroyImmediate(font); }
        }
    }
}
