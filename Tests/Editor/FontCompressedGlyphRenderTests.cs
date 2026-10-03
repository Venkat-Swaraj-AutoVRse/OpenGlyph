using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace LightSide.Tests
{
    /// <summary>
    /// Item 1 regression: a COMPRESSED font must still rasterize NEW glyphs. The bug was that
    /// <see cref="UniTextFont"/> fed FreeType the stored (compressed) <c>fontData</c> field instead
    /// of the decompressing <see cref="UniTextFont.FontData"/> property, so FreeType got a UTFZ
    /// container, loaded no face, and silently added zero glyphs (blank text). These tests compress a
    /// real font, clear its atlas, request a glyph that is NOT yet in the atlas, and assert the atlas
    /// gains that glyph WITH ink (non-zero coverage) — which only holds once every FreeType face is
    /// loaded from the decompressed bytes.
    ///
    /// Each test is written to FAIL without the fix: before the fix, <see cref="UniTextFont.TryAddGlyphsBatch"/>
    /// on a compressed font returns 0 and the atlas stays empty.
    /// </summary>
    public class FontCompressedGlyphRenderTests
    {
        private UniTextFont _font;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string path = MsdfTestUtil.FindNotoSansPath();
            if (path == null) Assert.Ignore("NotoSans-Regular.ttf fixture missing.");
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: 64);
            if (_font == null) Assert.Ignore("Font backend unavailable.");
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            if (_font != null) Object.DestroyImmediate(_font);
        }

        // Sums the coverage bytes inside a glyph's atlas rect. For an SDF/Alpha8 atlas the glyph area
        // carries a distance field whose interior bytes are non-zero, so a real rasterization yields a
        // positive total; a blank (never-rasterized) atlas yields exactly 0.
        private static long InkInRect(Texture2D atlas, GlyphRect rect)
        {
            var raw = atlas.GetRawTextureData<byte>();
            int ch = atlas.format == TextureFormat.RGBA32 ? 4 : atlas.format == TextureFormat.RGB24 ? 3 : 1;
            int stride = atlas.width * ch;
            long sum = 0;
            for (int y = 0; y < rect.height; y++)
            {
                int row = (rect.y + y) * stride + rect.x * ch;
                for (int x = 0; x < rect.width * ch; x++)
                    sum += raw[row + x];
            }
            return sum;
        }

        [Test]
        public void CompressedFont_RasterizesNewGlyph_WithInk()
        {
            // Compress the font in place; FontData must now transparently decompress for FreeType.
            Assert.IsTrue(_font.CompressStoredFontData(), "font data compresses");
            Assert.IsTrue(_font.IsFontDataCompressed, "stored bytes are a UTFZ container");

            // Clear any atlas/glyph state so the requested glyph is genuinely NOT present.
            _font.ClearDynamicData();

            uint gi = Shaper.GetGlyphIndex(_font, 'A');
            Assert.AreNotEqual(0u, gi, "font has a glyph for 'A'");
            Assert.IsFalse(_font.HasGlyphInAtlas(gi), "glyph is not in the atlas before the request");

            int added = _font.TryAddGlyphsBatch(new List<uint> { gi });
            Assert.AreEqual(1, added, "the compressed font rasterized exactly the one requested glyph " +
                                      "(0 here is the bug: FreeType was handed the compressed container)");
            Assert.IsTrue(_font.HasGlyphInAtlas(gi), "the atlas now contains the glyph");

            Assert.IsTrue(_font.TryGetGlyph(gi, VariationKey.None, out var glyph), "glyph record exists");
            Assert.Greater(glyph.glyphRect.width, 0, "'A' is an inked glyph with a non-empty rect");
            Assert.Greater(glyph.glyphRect.height, 0);
            Assert.IsNotNull(_font.AtlasTexture, "an atlas texture was created");

            var atlas = _font.AtlasTextures[glyph.atlasIndex];
            long ink = InkInRect(atlas, glyph.glyphRect);
            Assert.Greater(ink, 0, "the rasterized glyph has non-zero ink/coverage in the atlas " +
                                   "(a blank atlas sums to 0 — the silent no-op the bug produced)");
        }

        [Test]
        public void CompressedFont_MatchesRawFont_GlyphCount()
        {
            // Equivalence: the same glyph request must add the same count whether the font's stored
            // bytes are raw or compressed. (Before the fix, compressed added 0.)
            string path = MsdfTestUtil.FindNotoSansPath();
            var rawFont = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: 64);
            if (rawFont == null) { Assert.Ignore("Font backend unavailable."); return; }
            try
            {
                var request = new List<uint>();
                foreach (char c in "Hello123")
                {
                    uint g = Shaper.GetGlyphIndex(rawFont, c);
                    if (g != 0 && !request.Contains(g)) request.Add(g);
                }
                Assert.Greater(request.Count, 0);

                int rawAdded = rawFont.TryAddGlyphsBatch(new List<uint>(request));

                _font.CompressStoredFontData();
                _font.ClearDynamicData();
                int compAdded = _font.TryAddGlyphsBatch(new List<uint>(request));

                Assert.AreEqual(rawAdded, compAdded,
                    "a compressed font rasterizes the same number of glyphs as the raw font");
                Assert.Greater(compAdded, 0, "glyphs actually rasterized from the compressed font");
            }
            finally
            {
                Object.DestroyImmediate(rawFont);
            }
        }

        [Test]
        public void CorruptContainer_LogsOnce_AndDoesNotThrowPerAccess()
        {
            // A UTFZ container with a corrupt codec id must not throw on EVERY FontData access: the
            // failure is latched and logged once, and FontData falls back to the stored bytes.
            var raw = new byte[8192];
            for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 17);
            var packed = (byte[])FontCompression.Compress(raw, preferBrotli: false).Clone();
            Assert.IsTrue(FontCompression.IsCompressed(packed));
            packed[6] = 0x7F; // unknown codec -> Decompress throws

            var font = ScriptableObject.CreateInstance<UniTextFont>();
            try
            {
                // The warning may be emitted during SetFontData's face load OR on first FontData
                // access; either way it must appear exactly once. Arm the expectation first.
                LogAssert.Expect(LogType.Warning, new Regex("decompression failed"));
#if UNITY_EDITOR
                font.SetFontData(packed);
#endif
                byte[] a = font.FontData;
                byte[] b = font.FontData;
                byte[] c = font.FontData;
                Assert.AreSame(packed, a, "falls back to the stored bytes on decode failure");
                Assert.AreSame(a, b);
                Assert.AreSame(b, c, "no second attempt / no per-access throw");
            }
            finally
            {
                Object.DestroyImmediate(font);
            }
        }

        [Test]
        public void FontCompression_RejectsAbsurdRawLength()
        {
            var raw = new byte[4096];
            for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 13);
            var packed = (byte[])FontCompression.Compress(raw).Clone();
            Assert.IsTrue(FontCompression.IsCompressed(packed));
            // Overwrite the LE int32 raw-length header (bytes 8..11) with 0x7FFFFFFF (~2 GB).
            packed[8] = 0xFF; packed[9] = 0xFF; packed[10] = 0xFF; packed[11] = 0x7F;
            Assert.Throws<InvalidDataException>(() => FontCompression.Decompress(packed),
                "an absurd declared raw length is rejected before a multi-GB allocation is attempted");
        }
    }
}
