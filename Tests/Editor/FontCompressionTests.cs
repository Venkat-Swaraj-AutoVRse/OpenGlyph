using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LightSide.Tests
{
    /// <summary>
    /// Font-compression tests (feature B): managed Deflate compression of font bytes with
    /// decompress-on-load. Verifies byte-exact round-trip, measurable size reduction + decompress
    /// time on a real font, transparent <see cref="UniTextFont.FontData"/> access, hash preservation
    /// (shaper-cache identity unaffected), and the container-recognition guards. No native code is
    /// involved, so CI cross-platform reference results are untouched.
    /// </summary>
    public class FontCompressionTests
    {
        [Test]
        public void RoundTrip_IsByteExact()
        {
            var raw = new byte[20000];
            var rnd = new System.Random(1234);
            // Semi-compressible payload (runs + noise), like real font tables.
            for (int i = 0; i < raw.Length; i++) raw[i] = (byte)((i % 97 < 60) ? (i % 13) : rnd.Next(256));

            var packed = FontCompression.Compress(raw);
            Assert.IsTrue(FontCompression.IsCompressed(packed), "compressed output carries the UTFZ header");
            Assert.AreEqual(raw.Length, FontCompression.RawLength(packed), "header records the raw length");

            var back = FontCompression.Decompress(packed);
            Assert.AreEqual(raw.Length, back.Length, "decompressed length matches raw");
            Assert.AreEqual(raw, back, "decompressed bytes are byte-for-byte identical to raw");
        }

        [Test]
        public void PlainFontBytes_AreNotMistakenForContainer_AndDecompressPassesThrough()
        {
            // A TTF starts with 0x00010000; an OTF with 'OTTO'. Neither is the UTFZ magic.
            byte[] ttfHead = { 0x00, 0x01, 0x00, 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            Assert.IsFalse(FontCompression.IsCompressed(ttfHead), "raw TTF header is not a UTFZ container");
            Assert.AreSame(ttfHead, FontCompression.Decompress(ttfHead), "decompress passes raw bytes through unchanged");
        }

        [Test]
        public void Compress_NeverGrows_And_IsIdempotent()
        {
            var incompressible = new byte[4096];
            new System.Random(7).NextBytes(incompressible);
            var packed = FontCompression.Compress(incompressible);
            Assert.LessOrEqual(packed.Length, incompressible.Length + 12,
                "incompressible data is kept raw rather than inflated");

            var once = FontCompression.Compress(new byte[2048]); // zeros -> compresses well
            var twice = FontCompression.Compress(once);
            Assert.AreSame(once, twice, "compressing an already-compressed container is a no-op");
        }

        [Test]
        public void RealFont_Compresses_And_UniTextFont_DecompressesTransparently()
        {
            string fontPath = MsdfTestUtil.FindNotoSansPath();
            if (fontPath == null) { Assert.Ignore("NotoSans fixture missing."); return; }
            byte[] raw = File.ReadAllBytes(fontPath);

            var sw = Stopwatch.StartNew();
            var packed = FontCompression.Compress(raw, System.IO.Compression.CompressionLevel.Optimal);
            sw.Stop();
            double ratio = packed.Length / (double)raw.Length;

            var sw2 = Stopwatch.StartNew();
            var back = FontCompression.Decompress(packed);
            sw2.Stop();

            Assert.AreEqual(raw, back, "real font round-trips byte-exact");
            Assert.Less(packed.Length, raw.Length, "a real TTF compresses smaller than raw");
            Debug.Log($"[FontCompress] NotoSans raw={raw.Length:N0} B -> compressed={packed.Length:N0} B " +
                      $"({ratio:P1} of raw); compress={sw.ElapsedMilliseconds} ms decompress={sw2.ElapsedMilliseconds} ms");

            // Transparent decompress-on-load through UniTextFont, with hash preserved.
            var font = UniTextFont.CreateFontAsset(raw, 90, 0.10f, UniTextRenderMode.SDF, 1024);
            if (font == null) { Assert.Ignore("Font backend unavailable."); return; }
            try
            {
                int hashBefore = font.FontDataHash;
                int storedRaw = font.StoredFontDataLength;
                Assert.IsFalse(font.IsFontDataCompressed, "starts raw");

                Assert.IsTrue(font.CompressStoredFontData(), "font data compresses");
                Assert.IsTrue(font.IsFontDataCompressed, "stored bytes are now a UTFZ container");
                Assert.Less(font.StoredFontDataLength, storedRaw, "stored (at-rest) size shrank");
                Assert.AreEqual(hashBefore, font.FontDataHash, "FontDataHash is preserved across compression");

                // FontData transparently returns the ORIGINAL raw bytes.
                Assert.AreEqual(raw, font.FontData, "FontData decompresses transparently to the raw bytes");

                // And it can be decompressed back in place.
                Assert.IsTrue(font.DecompressStoredFontData());
                Assert.IsFalse(font.IsFontDataCompressed, "stored bytes are raw again");
                Assert.AreEqual(raw, font.FontData, "still the raw bytes after in-place decompress");
            }
            finally
            {
                Object.DestroyImmediate(font);
            }
        }
    }
}
