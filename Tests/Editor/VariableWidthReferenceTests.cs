using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Pins OpenGlyph's variable-font width behaviour to an INDEPENDENT fontTools reference
    /// (RobotoFlex-VF instanced at wght 400, opsz 14, wdth {25,100,151}; fonttools 4.55.0 hmtx):
    ///   total advance of "Reading" — wdth 25 = 6595, wdth 100 = 7422, wdth 151 = 8507 design units
    ///   (ratios 25/100 = 0.8886, 151/100 = 1.1462), and glyph bbox HEIGHTS are width-invariant.
    /// Asserts (a) OpenGlyph's live TextProcessor advance ratios match the reference, and (b) the
    /// atlas glyph heights do NOT change with wdth. Guards against axis mix-ups / avar / scale bugs.
    /// </summary>
    public class VariableWidthReferenceTests
    {
        // fontTools reference totals (design units, upem 2048), opsz pinned to the axis default 14.
        private const float RefTotal25 = 6595f, RefTotal100 = 7422f, RefTotal151 = 8507f;
        private const float Tol = 0.03f; // 3% — covers 1% axis quantization + rounding.

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
            if (_vf == null) Assert.Ignore("RobotoFlex failed to load.");
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            if (_vf != null) UnityEngine.Object.DestroyImmediate(_vf);
        }

        private (VariationKey key, uint[] tags, float[] coords) Inst(float wdth)
        {
            var face = FT.LoadFace(_bytes, 0);
            try
            {
                var map = VariationMapper.Read(face);
                // opsz left at default (opticalSize 0 => mapper keeps the axis default = 14).
                var key = map.Map(new FontStyleSpec(400, wdth, StyleAxis.Normal), 0f, out var t, out var c);
                return (key, t, c);
            }
            finally { FT.UnloadFace(face); }
        }

        // Sum the HarfBuzz advances (design units) of "Reading" for a given instance.
        private float TotalAdvanceDesignUnits(float wdth)
        {
            var (key, tags, coords) = Inst(wdth);
            float totalPts = 0f;
            const float size = 64f;
            foreach (char ch in "Reading")
            {
                Assert.IsTrue(Shaper.TryGetGlyphInfoVaried(_vf, ch, size, key, tags, coords, out _, out var advPts));
                totalPts += advPts;
            }
            // advPts is in points at `size`; convert back to design units: du = pts * upem / size / fontScale.
            return totalPts * _vf.UnitsPerEm / size / _vf.FontScale;
        }

        [Test]
        public void Advances_MatchFontToolsReference_Ratios()
        {
            float a25 = TotalAdvanceDesignUnits(25);
            float a100 = TotalAdvanceDesignUnits(100);
            float a151 = TotalAdvanceDesignUnits(151);
            UnityEngine.Debug.Log($"[WidthRef] OpenGlyph totals du: wdth25={a25:0} wdth100={a100:0} wdth151={a151:0}; ratios 25/100={a25/a100:0.0000} 151/100={a151/a100:0.0000}");

            // Absolute totals match the reference within tolerance.
            Assert.AreEqual(RefTotal25, a25, RefTotal25 * Tol, $"wdth 25 total advance (got {a25}, ref {RefTotal25}).");
            Assert.AreEqual(RefTotal100, a100, RefTotal100 * Tol, $"wdth 100 total advance (got {a100}, ref {RefTotal100}).");
            Assert.AreEqual(RefTotal151, a151, RefTotal151 * Tol, $"wdth 151 total advance (got {a151}, ref {RefTotal151}).");

            // And the width ratios match (this is what the eye reads on the sheet).
            Assert.AreEqual(RefTotal25 / RefTotal100, a25 / a100, 0.02f, "wdth 25/100 advance ratio.");
            Assert.AreEqual(RefTotal151 / RefTotal100, a151 / a100, 0.02f, "wdth 151/100 advance ratio.");
        }

        [Test]
        public void GlyphHeights_DoNotChangeWithWidth()
        {
            uint[] Gis()
            {
                var l = new List<uint>();
                foreach (char ch in "Reading") l.Add(Shaper.GetGlyphIndex(_vf, ch));
                return l.ToArray();
            }
            var gis = Gis();

            var i25 = Inst(25); var i100 = Inst(100); var i151 = Inst(151);
            _vf.EnsureGlyphsForVariation(new List<uint>(gis), i25.key, i25.tags, i25.coords);
            _vf.EnsureGlyphsForVariation(new List<uint>(gis), i100.key, i100.tags, i100.coords);
            _vf.EnsureGlyphsForVariation(new List<uint>(gis), i151.key, i151.tags, i151.coords);

            foreach (var g in gis)
            {
                if (g == 0) continue;
                Assert.IsTrue(_vf.TryGetGlyph(g, i25.key, out var a));
                Assert.IsTrue(_vf.TryGetGlyph(g, i100.key, out var b));
                Assert.IsTrue(_vf.TryGetGlyph(g, i151.key, out var c));
                // Height is a vertical metric; the wdth axis must not change it (reference: constant).
                float refH = b.metrics.height;
                if (refH <= 0) continue;
                Assert.AreEqual(refH, a.metrics.height, refH * 0.05f, $"glyph {g} height changed at wdth 25 ({a.metrics.height} vs {refH}).");
                Assert.AreEqual(refH, c.metrics.height, refH * 0.05f, $"glyph {g} height changed at wdth 151 ({c.metrics.height} vs {refH}).");
            }
        }
    }
}
