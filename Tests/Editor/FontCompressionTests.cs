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

        [Test]
        public void LargeCjkFont_Compression_VsUniText20Bar()
        {
            // The UniText 2.0 bar: a ~12 MB font -> 4.4 MB in build (36.7%), sub-ms decompress.
            // Measure our codec on the ~11 MB NotoSerifSC OTF if it is reachable (benchmark asset),
            // so the doc compares against the bar on a comparable font. Skipped when not present.
            string big = FindBigFont();
            if (big == null) { Assert.Ignore("No large (>5 MB) font reachable for the 2.0-bar comparison."); return; }
            byte[] raw = File.ReadAllBytes(big);

            var sw = Stopwatch.StartNew();
            var packed = FontCompression.Compress(raw);
            sw.Stop();
            var sw2 = Stopwatch.StartNew();
            var back = FontCompression.Decompress(packed);
            sw2.Stop();

            Assert.AreEqual(raw, back, "large font round-trips byte-exact");
            double ratio = packed.Length / (double)raw.Length;
            double perMb = sw2.ElapsedMilliseconds / (raw.Length / (1024.0 * 1024.0));
            Debug.Log($"[FontCompress] {Path.GetFileName(big)} raw={raw.Length / (1024.0 * 1024.0):F1} MB " +
                      $"-> {packed.Length / (1024.0 * 1024.0):F1} MB ({ratio:P1} of raw); " +
                      $"compress={sw.ElapsedMilliseconds} ms decompress={sw2.ElapsedMilliseconds} ms ({perMb:F1} ms/MB). " +
                      $"2.0 bar: 36.7% + sub-ms. NOTE: whole-font byte decompress cannot be sub-ms for 12 MB; " +
                      $"the sub-ms bar implies subsetting (ship a tiny font) — see FontSubsetter.");
            Assert.Less(packed.Length, raw.Length, "large font still compresses below raw");
        }

        [Test]
        public void DefaultCodec_IsDeflate_DecodableEverywhere()
        {
            // The DEFAULT (no preferBrotli) must emit a Deflate container (codec 0), because Deflate
            // is in the BCL on every Unity backend — a shipped asset must decode on every target.
            var raw = new byte[8192];
            for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 11);
            var packed = FontCompression.Compress(raw); // default: preferBrotli = false
            Assert.IsTrue(FontCompression.IsCompressed(packed), "default compression still produces a container");
            Assert.AreEqual(0, FontCompression.CodecId(packed), "default codec is Deflate (0), decodable on every platform");
            Assert.AreEqual(raw, FontCompression.Decompress(packed), "default round-trips byte-exact");
        }

        [Test]
        public void BrotliOptIn_RoundTrips_WhenAvailable_ElseFallsBackToDeflate()
        {
            var raw = new byte[16384];
            var rnd = new System.Random(99);
            for (int i = 0; i < raw.Length; i++) raw[i] = (byte)((i % 64 < 40) ? (i % 7) : rnd.Next(256));

            var packed = FontCompression.Compress(raw, System.IO.Compression.CompressionLevel.Optimal, preferBrotli: true);
            Assert.IsTrue(FontCompression.IsCompressed(packed), "opt-in Brotli still produces a container");
            int codec = FontCompression.CodecId(packed);
            if (FontCompression.BrotliAvailable)
                Assert.AreEqual(1, codec, "when Brotli round-trips on this runtime, the opt-in container is Brotli (1)");
            else
                Assert.AreEqual(0, codec, "when Brotli is unavailable, Compress NEVER emits a Brotli container — it falls back to Deflate (0)");
            Assert.AreEqual(raw, FontCompression.Decompress(packed), "opt-in path round-trips byte-exact regardless of codec");
        }

        [Test]
        public void Decompress_NeverCrashes_OnUnknownCodec()
        {
            // Hand-craft a UTFZ container with a bogus codec id: decode must throw a controlled
            // InvalidDataException, not an unhandled native/type fault.
            var raw = new byte[256];
            new System.Random(3).NextBytes(raw);
            var packed = (byte[])FontCompression.Compress(raw, preferBrotli: false).Clone();
            Assume.That(FontCompression.IsCompressed(packed), "need a real container to corrupt");
            packed[6] = 0x7F; // unknown codec id
            Assert.Throws<System.IO.InvalidDataException>(() => FontCompression.Decompress(packed),
                "an unknown codec id is rejected cleanly, never crashing the player");
        }

        [Test]
        public void BrotliAvailable_ProbeIsStableAndReportsCapability()
        {
            // The probe must be deterministic across calls (cached) and must gate codec choice.
            bool a = FontCompression.BrotliAvailable;
            bool b = FontCompression.BrotliAvailable;
            Assert.AreEqual(a, b, "BrotliAvailable is a stable, cached capability probe");
            UnityEngine.Debug.Log($"[FontCompress] BrotliAvailable (editor runtime) = {a}");
        }

        private static string FindBigFont()
        {
            foreach (var root in new[]
            {
                "D:/OpenGlyphWork/scratch/lse-bench/repo/Assets/UniText.Test/BenchmarkWorkshop/TMPFonts/Languages/Raw",
            })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var ext in new[] { "*.otf", "*.ttf", "*.ttc" })
                    foreach (var f in Directory.GetFiles(root, ext))
                        if (new FileInfo(f).Length > 5 * 1024 * 1024) return f;
            }
            return null;
        }
    }
}
