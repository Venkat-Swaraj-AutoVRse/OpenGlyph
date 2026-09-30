using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// Phase 1c pixel-font tests: grid detection on a real pixel font (Silkscreen), rejection of a
    /// non-pixel font (NotoSans), the 0/255-only atlas-texel guarantee for Mono coverage, and
    /// device-pixel snapping of mesh quad corners at canvas scale factors 1, 2 and 1.5.
    /// </summary>
    /// <remarks>
    /// Grid detection is exercised through <see cref="PixelFontDetection"/> using a managed
    /// TrueType outline reader (<see cref="TtfGlyfReader"/>), so these tests do NOT depend on the
    /// native <c>ut_ft_get_outline_data</c> export being present in the shipped binary — matching the
    /// production graceful-degradation path.
    /// </remarks>
    [TestFixture]
    public class PixelFontTests
    {
        // Established empirically from Silkscreen-Regular.ttf: unitsPerEm=1000, coarsest grid = 8
        // cells/em (cell = 125 units), ~97% of outline points on-grid. See fetch_fonts_pixel.ps1.
        private const int SilkscreenExpectedPpem = 8;

        private static string FindFont(string fileName)
        {
            string[] candidates =
            {
                Path.Combine(Path.GetDirectoryName(new System.Diagnostics.StackTrace(true).GetFrame(0).GetFileName()) ?? "", "Fonts", fileName),
                "Packages/com.openglyph.text/Tests/Editor/Fonts/" + fileName,
                Path.Combine(UnityEngine.Application.dataPath ?? "", "..", "Packages", "com.openglyph.text", "Tests", "Editor", "Fonts", fileName),
            };
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;

            string root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath ?? ".", ".."));
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                    return f;
            }
            catch { /* ignore */ }
            return null;
        }

        // ---------------------------------------------------------------------------------------
        // Grid detection: real pixel font
        // ---------------------------------------------------------------------------------------

        [Test]
        public void GridDetection_Silkscreen_DetectedAsPixelFont_WithNativePpem8()
        {
            string path = FindFont("Silkscreen-Regular.ttf");
            if (path == null)
                Assert.Ignore("Silkscreen-Regular.ttf not found; run Tests/Editor/fetch_fonts_pixel.ps1 first.");

            var reader = TtfGlyfReader.FromFile(path);
            var provider = new GlyfReaderGridProvider(reader);
            var info = PixelFontDetection.Analyze(provider, Array.Empty<int>());

            Assert.IsTrue(info.outlineDataAvailable, "Managed reader should have produced outline data.");
            Assert.IsTrue(info.isPixelGrid, $"Silkscreen should be detected as a pixel grid (onGrid={info.onGridFraction:0.###}).");
            Assert.AreEqual(SilkscreenExpectedPpem, info.nativePixelsPerEm,
                $"Expected native {SilkscreenExpectedPpem} px/em; got {info.nativePixelsPerEm} (onGrid={info.onGridFraction:0.###}).");
            Assert.IsTrue(info.IsPixelFont);
        }

        // ---------------------------------------------------------------------------------------
        // Grid detection: non-pixel (vector) font must NOT be classified as a pixel font
        // ---------------------------------------------------------------------------------------

        [Test]
        public void GridDetection_NotoSans_NotDetectedAsPixelFont()
        {
            string path = MsdfTestUtil.FindNotoSansPath();
            if (path == null)
                Assert.Ignore("NotoSans-Regular.ttf not found in package.");

            var reader = TtfGlyfReader.FromFile(path);
            var provider = new GlyfReaderGridProvider(reader);
            var info = PixelFontDetection.Analyze(provider, Array.Empty<int>());

            Assert.IsTrue(info.outlineDataAvailable, "Managed reader should have produced outline data for NotoSans.");
            Assert.IsFalse(info.isPixelGrid,
                $"NotoSans must NOT be detected as a pixel grid (best onGrid={info.onGridFraction:0.###} must stay below threshold {PixelFontDetection.OnGridThreshold}).");
            Assert.Less(info.onGridFraction, PixelFontDetection.OnGridThreshold);
        }

        // ---------------------------------------------------------------------------------------
        // Pure detector unit tests (no font files) — synthetic on-grid vs off-grid coordinates
        // ---------------------------------------------------------------------------------------

        [Test]
        public void DetectGrid_PerfectGrid_ReturnsCoarsestPpem()
        {
            // upem 1000, points on a 125-unit lattice => 8 px/em (like Silkscreen's ideal grid).
            var coords = new List<int>();
            for (int k = 0; k <= 8; k++)
                for (int rep = 0; rep < 16; rep++)
                    coords.Add(k * 125);
            var arr = coords.ToArray();

            bool ok = PixelFontDetection.DetectGrid(arr, arr.Length, 1000, out int ppem, out float frac);
            Assert.IsTrue(ok);
            Assert.AreEqual(8, ppem, "Should pick the coarsest (smallest N) grid, not a harmonic.");
            Assert.GreaterOrEqual(frac, PixelFontDetection.OnGridThreshold);
        }

        [Test]
        public void DetectGrid_RandomOffGrid_Rejected()
        {
            var rng = new System.Random(1234);
            var coords = new int[512];
            for (int i = 0; i < coords.Length; i++)
                coords[i] = rng.Next(0, 1000); // dense, no lattice
            bool ok = PixelFontDetection.DetectGrid(coords, coords.Length, 1000, out int ppem, out _);
            Assert.IsFalse(ok, "Random coordinates must not register as a pixel grid.");
            Assert.AreEqual(0, ppem);
        }

        [Test]
        public void DetectGrid_TooFewSamples_Rejected()
        {
            var coords = new[] { 0, 125, 250, 375 };
            bool ok = PixelFontDetection.DetectGrid(coords, coords.Length, 1000, out _, out _);
            Assert.IsFalse(ok, "Below MinSampleCoords the detector must refuse to classify.");
        }

        // ---------------------------------------------------------------------------------------
        // Bitmap strikes reported even without an outline grid
        // ---------------------------------------------------------------------------------------

        [Test]
        public void Analyze_BitmapStrikes_ReportedAsPixelFont()
        {
            var info = PixelFontDetection.Analyze(outlineProvider: null, bitmapStrikeSizes: new[] { 16, 32 });
            Assert.IsTrue(info.hasBitmapStrikes);
            Assert.IsTrue(info.IsPixelFont, "A font with embedded bitmap strikes is pixel-perfect capable.");
            Assert.IsFalse(info.isPixelGrid, "No outline provider => no grid claim.");
            CollectionAssert.AreEqual(new[] { 16, 32 }, info.bitmapStrikeSizes);
        }

        // ---------------------------------------------------------------------------------------
        // Atlas texels for pixel-perfect Mono coverage are ONLY 0 or 255 (no anti-aliasing)
        // ---------------------------------------------------------------------------------------

        [Test]
        public void PixelPerfect_MonoAtlasTexels_AreOnlyZeroOr255()
        {
            string path = FindFont("Silkscreen-Regular.ttf");
            if (path == null)
                Assert.Ignore("Silkscreen-Regular.ttf not found; run fetch_fonts_pixel.ps1.");
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized)
                Assert.Ignore("FreeType native library unavailable in this environment; skipping atlas texel test.");

            var bytes = File.ReadAllBytes(path);
            var font = UniTextFont.CreateFontAsset(bytes, samplingPointSize: 16,
                renderMode: UniTextRenderMode.Mono);
            Assert.IsNotNull(font, "Font asset creation failed.");

            try
            {
                font.PixelPerfect = true;
                var pf = font.DetectPixelFont();
                if (!pf.IsPixelFont)
                    Assert.Ignore($"Native outline export unavailable so grid not detected here ({pf}); atlas texel test needs a pixel font. Covered by managed detection test.");

                Assert.IsTrue(font.PixelPerfectActive);

                var glyphs = new List<uint>();
                foreach (char c in "ABCabc012")
                {
                    uint gi = font.GetGlyphIndexForUnicode(c);
                    if (gi != 0) glyphs.Add(gi);
                }
                Assert.Greater(glyphs.Count, 0, "No glyphs resolved for the probe string.");

                int added = font.TryAddGlyphsBatch(glyphs);
                Assert.Greater(added, 0, "No glyphs were packed into the atlas.");

                var atlases = font.AtlasTextures;
                Assert.IsNotNull(atlases);
                Assert.Greater(atlases.Count, 0, "No atlas texture produced.");

                foreach (var tex in atlases)
                {
                    Assert.AreEqual(FilterMode.Point, tex.filterMode, "Pixel-perfect atlas must be point-filtered.");
                    Assert.AreEqual(TextureFormat.Alpha8, tex.format, "Pixel-perfect Mono atlas must be Alpha8.");
                    var raw = tex.GetRawTextureData<byte>();
                    for (int i = 0; i < raw.Length; i++)
                    {
                        byte v = raw[i];
                        if (v != 0 && v != 255)
                            Assert.Fail($"Atlas texel {i} = {v}; pixel-perfect Mono coverage must be 0 or 255 only (no AA).");
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(font);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Mesh vertex snapping: quad corners land on integer device pixels at scale 1, 2, and 1.5
        // ---------------------------------------------------------------------------------------

        [TestCase(1.0f)]
        [TestCase(2.0f)]
        public void SnapToDevicePixel_IntegerScale_LandsOnIntegerDevicePixel(float scaleFactor)
        {
            // A spread of arbitrary local-unit positions must all map to integer device pixels.
            foreach (var local in new[] { 0f, 0.13f, 0.5f, 1.49f, 3.7f, -2.28f, 10.001f })
            {
                float snapped = UniTextMeshGenerator.SnapToDevicePixel(local, scaleFactor);
                float devicePx = snapped * scaleFactor;
                Assert.AreEqual(Mathf.Round(devicePx), devicePx, 1e-4f,
                    $"At scale {scaleFactor}, local {local} snapped to {snapped} => device {devicePx} is not integral.");
            }
        }

        [Test]
        public void SnapToDevicePixel_Scale1p5_DocumentedBehavior_HalfDevicePixelStep()
        {
            // DOCUMENTED behaviour for a non-integer canvas scale factor (1.5): we still snap each
            // corner to the nearest DEVICE pixel (round(local*1.5)/1.5). The result therefore lands
            // on integer device pixels, at the cost of a snap step of 1/1.5 ≈ 0.667 local units.
            // This is the deliberate fallback: crispness on the physical grid is preserved; only the
            // local-space quantisation is coarser than at integer scales. We assert the invariant
            // that matters — device pixels are integral — rather than any local-space value.
            const float sf = 1.5f;
            foreach (var local in new[] { 0f, 0.3f, 0.7f, 1.1f, 2.9f, 5.2f })
            {
                float snapped = UniTextMeshGenerator.SnapToDevicePixel(local, sf);
                float devicePx = snapped * sf;
                Assert.AreEqual(Mathf.Round(devicePx), devicePx, 1e-4f,
                    $"At scale 1.5, device pixel {devicePx} must be integral (snap to nearest device pixel).");
            }
        }

        [Test]
        public void SnapToDevicePixel_NonPositiveScale_IsNoOp()
        {
            Assert.AreEqual(3.14159f, UniTextMeshGenerator.SnapToDevicePixel(3.14159f, 0f), 0f);
            Assert.AreEqual(3.14159f, UniTextMeshGenerator.SnapToDevicePixel(3.14159f, -1f), 0f);
            Assert.AreEqual(3.14159f, UniTextMeshGenerator.SnapToDevicePixel(3.14159f, float.NaN), 0f);
        }

        // ---------------------------------------------------------------------------------------
        // Regression: SDF/Smooth modes remain distance-field / non-pixel-perfect and unaffected
        // ---------------------------------------------------------------------------------------

        [Test]
        public void Regression_NonPixelPerfect_DoesNotSnap()
        {
            // With pixel-perfect OFF (or a non-pixel font), the snap helper is bypassed by the
            // generator; here we assert the helper itself is identity when device scale signals "off".
            Assert.AreEqual(1.2345f, UniTextMeshGenerator.SnapToDevicePixel(1.2345f, 0f), 0f,
                "Snapping must be a no-op when disabled, so SDF/Smooth geometry is unchanged.");
        }

        [Test]
        public void Regression_SdfFont_IsNotPixelPerfectActive()
        {
            string path = FindFont("Silkscreen-Regular.ttf") ?? MsdfTestUtil.FindNotoSansPath();
            if (path == null) Assert.Ignore("No font available for regression check.");
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");

            var bytes = File.ReadAllBytes(path);
            var font = UniTextFont.CreateFontAsset(bytes, samplingPointSize: 48, renderMode: UniTextRenderMode.SDF);
            try
            {
                // pixelPerfect defaults false → never active regardless of grid.
                Assert.IsFalse(font.PixelPerfectActive, "SDF font with pixelPerfect off must not be pixel-perfect active.");
                Assert.AreEqual(UniTextRenderMode.SDF, font.ConfiguredRenderMode);
            }
            finally { UnityEngine.Object.DestroyImmediate(font); }
        }
    }

    /// <summary>
    /// Adapts <see cref="TtfGlyfReader"/> to <see cref="IPixelGridOutlineProvider"/> by reading a
    /// representative set of glyph outlines in RAW design units (pixelsPerEm == unitsPerEm so the
    /// reader's scale is 1). Test-only.
    /// </summary>
    internal sealed class GlyfReaderGridProvider : IPixelGridOutlineProvider
    {
        private readonly TtfGlyfReader _reader;
        public GlyfReaderGridProvider(TtfGlyfReader reader) => _reader = reader;

        public int UnitsPerEm => _reader.UnitsPerEm;

        public bool TryCollectOutlineCoords(List<int> coords, int maxGlyphs)
        {
            const string probe = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            int sampled = 0;
            foreach (char c in probe)
            {
                if (sampled >= maxGlyphs) break;
                int gid = _reader.GetGlyphId(c);
                if (gid == 0) continue;
                var o = _reader.ReadOutline(gid, _reader.UnitsPerEm); // scale == 1 => raw design units
                if (o == null) continue;
                foreach (var contour in o.Contours)
                    foreach (var p in contour.Points)
                    {
                        coords.Add((int)Math.Round(p.X));
                        coords.Add((int)Math.Round(p.Y));
                    }
                sampled++;
            }
            return coords.Count > 0;
        }
    }
}
