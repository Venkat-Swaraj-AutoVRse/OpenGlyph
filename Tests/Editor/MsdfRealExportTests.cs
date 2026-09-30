using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using LightSide;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// Phase-0 END-TO-END tests that exercise the REAL rebuilt native binary through the real C#
    /// APIs (FT + FreeTypeOutlineSource + the atlas pipeline), NOT a fake or a managed TrueType
    /// parser. Guarded to <see cref="RuntimePlatform.WindowsEditor"/> because only the Windows x64
    /// Plugins ship the rebuilt DLL with <c>ut_ft_get_outline_data</c>; the other platforms still
    /// carry the old binaries and would (correctly) take the SDF fallback, so a positive
    /// export-present assertion must not run there.
    /// </summary>
    [TestFixture]
    public class MsdfRealExportTests
    {
        private byte[] _fontBytes;
        private string _fontPath;

        [OneTimeSetUp]
        public void Setup()
        {
            _fontPath = MsdfTestUtil.FindNotoSansPath();
            if (_fontPath == null)
                Assert.Ignore("NotoSans-Regular.ttf not found; skipping real-export tests.");
            _fontBytes = File.ReadAllBytes(_fontPath);
        }

        /// <summary>
        /// On Windows editor x64 the rebuilt DLL MUST expose ut_ft_get_outline_data, and a real
        /// 'A' glyph loaded through FreeType must produce an RGB24 MSDF with genuine per-channel
        /// variation whose outline point count matches FT.GetOutlineInfo exactly. This is the first
        /// test to drive the export end to end (prior MSDF tests used synthetic/managed outlines).
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void RealExport_A_ProducesRGB24Msdf_WithChannelVariation_AndMatchingPointCount()
        {
            Assert.IsTrue(FT.IsInitialized || FT.Initialize(), "FreeType failed to initialize.");

            IntPtr face = FT.LoadFace(_fontBytes);
            Assert.AreNotEqual(IntPtr.Zero, face, "FT.LoadFace returned a null face for NotoSans.");
            try
            {
                // 1) The rebuilt Windows x64 binary MUST carry the phase-0 export.
                Assert.IsTrue(FT.OutlineExportAvailable(face),
                    "ut_ft_get_outline_data must be present in the rebuilt Windows x64 binary.");
                Assert.IsTrue(MsdfNative.Probe(face),
                    "MsdfNative.Probe must confirm the export on the real binary.");

                const int ppem = 48;
                uint gi = FT.GetCharIndex(face, 'A');
                Assert.AreNotEqual(0u, gi, "Glyph index for 'A' must be non-zero.");

                // 2) Ground-truth point/contour count straight from FreeType, using the same
                //    load path the outline source uses (scaling changes coordinates, not counts).
                Assert.IsTrue(FT.SetPixelSize(face, ppem), "SetPixelSize failed.");
                Assert.IsTrue(FT.LoadGlyph(face, gi, FT.LOAD_DEFAULT | FT.LOAD_NO_HINTING), "LoadGlyph failed.");
                Assert.IsTrue(FT.GetOutlineInfo(face, out int ftContours, out int ftPoints),
                    "FT.GetOutlineInfo failed for 'A'.");
                Assert.Greater(ftPoints, 0, "'A' must have outline points.");
                Assert.Greater(ftContours, 0, "'A' must have at least one contour.");

                // 3) Drive the REAL FreeTypeOutlineSource against the same face.
                var source = new FreeTypeOutlineSource(face,
                    (glyph, pel) => FT.SetPixelSize(face, pel) && FT.LoadGlyph(face, glyph, FT.LOAD_DEFAULT | FT.LOAD_NO_HINTING));
                Assert.IsTrue(source.IsAvailable, "FreeTypeOutlineSource reports unavailable on the real binary.");

                GlyphOutline outline = source.GetOutline(gi, ppem);
                Assert.IsNotNull(outline, "Real outline for 'A' was null (export path failed).");

                int sourcePoints = 0, sourceContours = 0;
                foreach (var c in outline.Contours) { sourcePoints += c.Count; sourceContours++; }
                Assert.AreEqual(ftContours, sourceContours,
                    "Contour count from FreeTypeOutlineSource must match FT.GetOutlineInfo.");
                Assert.AreEqual(ftPoints, sourcePoints,
                    $"Outline point count from FreeTypeOutlineSource ({sourcePoints}) must match FT.GetOutlineInfo ({ftPoints}).");

                // 4) Build the MSDF from the real outline and assert genuine multichannel content.
                MsdfGlyphResult msdf = MsdfBuilder.Build(outline, spread: 6);
                Assert.IsTrue(msdf.IsValid, "MSDF build failed for the real 'A' outline.");
                Assert.AreEqual(msdf.Width * msdf.Height * 3, msdf.Rgb.Length, "RGB24 buffer size mismatch.");

                bool channelsDiverge = false;
                for (int i = 0; i < msdf.Rgb.Length; i += 3)
                    if (msdf.Rgb[i] != msdf.Rgb[i + 1] || msdf.Rgb[i + 1] != msdf.Rgb[i + 2])
                    { channelsDiverge = true; break; }
                Assert.IsTrue(channelsDiverge,
                    "Real-export MSDF for 'A' has no per-channel variation — it collapsed to a single-channel SDF.");
            }
            finally
            {
                FT.UnloadFace(face);
            }
        }

        /// <summary>
        /// The full atlas pipeline, no injected source: on Windows editor x64 the real export must
        /// drive the MSDF path, yielding an RGB24 atlas (NOT the Alpha8 SDF fallback). This is the
        /// positive-platform counterpart to MSDF_AtlasEndToEnd_RealBinary_CoherentFormat, which
        /// accepts either mode on platforms that ship the old binary.
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void RealExport_AtlasEndToEnd_TakesMsdfPath_RGB24()
        {
            var font = UniTextFont.CreateFontAsset(_fontBytes, 64, 0.25f, UniTextRenderMode.Msdf, 512);
            Assert.IsNotNull(font, "MSDF font asset creation failed.");
            try
            {
                Assert.AreEqual(UniTextRenderMode.Msdf, font.AtlasRenderMode,
                    "On Windows editor x64 the real export must keep the effective mode Msdf, not fall back to SDF.");

                var indices = new System.Collections.Generic.List<uint>();
                foreach (char c in "AMW&")
                {
                    uint gi = font.GetGlyphIndexForUnicode((uint)c);
                    if (gi != 0) indices.Add(gi);
                }
                Assert.Greater(indices.Count, 0, "No glyph indices resolved for 'AMW&'.");

                int added = font.TryAddGlyphsBatch(indices);
                Assert.Greater(added, 0, "MSDF added no glyphs from the real export.");
                Assert.AreEqual(TextureFormat.RGB24, font.AtlasTexture.format,
                    "Real-export MSDF atlas must be RGB24.");

                var px = font.AtlasTexture.GetPixels32();
                bool multichannel = false;
                foreach (var p in px)
                    if (p.r != p.g || p.g != p.b) { multichannel = true; break; }
                Assert.IsTrue(multichannel, "Real-export RGB24 atlas has no per-channel variation.");
            }
            finally { UnityEngine.Object.DestroyImmediate(font); }
        }
    }
}
