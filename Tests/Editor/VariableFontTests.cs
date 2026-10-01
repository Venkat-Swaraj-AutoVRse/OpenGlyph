using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Variable-font tests against RobotoFlex-VF (OFL, 13 axes, 20 named instances), using the
    /// existing native variation ABI (<see cref="FTVar"/>) + FreeType + HarfBuzz. Proves:
    /// axes are read; <c>wght</c> 400 vs 700 produce different glyph outlines/advances and different
    /// cache keys; and HarfBuzz shaping advances track the variation.
    /// </summary>
    public class VariableFontTests
    {
        private byte[] _vf;
        private GCHandle _pin;
        private IntPtr _dataPtr;
        private int _dataLen;

        private const uint WGHT = 0x77676874; // 'wght'

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable in this environment.");

            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null)
                Assert.Ignore("RobotoFlex-VF.ttf not fetched (run NativeSource~/tests/fetch_fonts.ps1).");

            _vf = File.ReadAllBytes(vf);
            _pin = GCHandle.Alloc(_vf, GCHandleType.Pinned);
            _dataPtr = _pin.AddrOfPinnedObject();
            _dataLen = _vf.Length;
        }

        [TearDown]
        public void TearDown()
        {
            if (_pin.IsAllocated) _pin.Free();
        }

        // ---- Axis read ----------------------------------------------------------

        [Test]
        public void ReadsFvarAxesAndNamedInstances()
        {
            var face = FT.LoadFace(_vf, 0);
            Assert.AreNotEqual(IntPtr.Zero, face, "RobotoFlex face failed to load.");
            try
            {
                var map = VariationMapper.Read(face);
                Assert.IsTrue(map.isVariable, "RobotoFlex must be detected as variable.");
                Assert.AreEqual(13, map.axes.Length, "RobotoFlex has 13 fvar axes.");
                Assert.AreEqual(20, map.namedInstanceCount, "RobotoFlex has 20 named instances.");
                Assert.GreaterOrEqual(map.AxisIndex(WGHT), 0, "RobotoFlex exposes a 'wght' axis.");

                // The wght axis range must straddle 400 and 700.
                var wi = map.AxisIndex(WGHT);
                Assert.LessOrEqual(map.axes[wi].min, 400f);
                Assert.GreaterOrEqual(map.axes[wi].max, 700f);
            }
            finally { FT.UnloadFace(face); }
        }

        // ---- VariationKey separation -------------------------------------------

        [Test]
        public void DifferentWeights_ProduceDifferentVariationKeys()
        {
            var face = FT.LoadFace(_vf, 0);
            try
            {
                var map = VariationMapper.Read(face);
                var k400 = map.Map(new FontStyleSpec(400, 100, StyleAxis.Normal), 0f, out _, out _);
                var k700 = map.Map(new FontStyleSpec(700, 100, StyleAxis.Normal), 0f, out _, out _);
                Assert.AreNotEqual(k400, k700, "wght 400 and 700 must yield distinct cache keys.");
                Assert.AreNotEqual(k400.GetHashCode(), k700.GetHashCode());

                // Same request -> identical key (cache hit).
                var k400b = map.Map(new FontStyleSpec(400, 100, StyleAxis.Normal), 0f, out _, out _);
                Assert.AreEqual(k400, k400b, "Identical requests must collapse to one cache entry.");
            }
            finally { FT.UnloadFace(face); }
        }

        // ---- FreeType outline + advance divergence ------------------------------

        [Test]
        public void FreeType_Weight400_vs_700_DifferentOutlineAndAdvance()
        {
            var map = ReadMap(out var face);
            try
            {
                uint g = FT.GetCharIndex(face, 'g');
                Assert.AreNotEqual(0u, g, "'g' must exist in RobotoFlex.");

                var m400 = LoadVariedMetrics(face, map, 400, g, out int pts400);
                var m700 = LoadVariedMetrics(face, map, 700, g, out int pts700);

                // A heavier weight changes the glyph bounding box and advance.
                bool bboxDiffers = m400.width != m700.width || m400.height != m700.height;
                Assert.IsTrue(bboxDiffers,
                    $"'g' bbox must differ between wght 400 ({m400.width}x{m400.height}) and 700 ({m700.width}x{m700.height}).");
                Assert.AreNotEqual(m400.advanceX, m700.advanceX,
                    "'g' advance (design units) must differ between wght 400 and 700.");
            }
            finally { FT.UnloadFace(face); }
        }

        // ---- HarfBuzz shaping advance divergence --------------------------------

        [Test]
        public void HarfBuzz_Weight400_vs_700_DifferentShapingAdvance()
        {
            var font = HB.CreateFont(IntPtr.Zero, _dataPtr, _dataLen, out var blob, out var hbFace, out _);
            if (font == IntPtr.Zero) Assert.Ignore("HarfBuzz font creation unavailable.");
            try
            {
                Assert.IsTrue(HB.TryGetGlyph(font, 'g', out uint g) && g != 0, "'g' glyph lookup failed.");

                SetHbWeight(font, 400f);
                int adv400 = HB.GetGlyphAdvance(font, g);

                SetHbWeight(font, 700f);
                int adv700 = HB.GetGlyphAdvance(font, g);

                Assert.AreNotEqual(adv400, adv700,
                    $"HarfBuzz 'g' advance must change with wght (400={adv400}, 700={adv700}).");
            }
            finally { HB.DestroyFont(font, blob, hbFace); }
        }

        // ---- helpers ------------------------------------------------------------

        private VariationMapper ReadMap(out IntPtr face)
        {
            face = FT.LoadFace(_vf, 0);
            Assert.AreNotEqual(IntPtr.Zero, face);
            return VariationMapper.Read(face);
        }

        private static FT.GlyphMetrics LoadVariedMetrics(IntPtr face, VariationMapper map, int weight, uint glyph, out int points)
        {
            map.Map(new FontStyleSpec(weight, 100, StyleAxis.Normal), 0f, out _, out float[] coords);
            Assert.IsTrue(FTVar.SetDesignCoordinates(face, coords), "SetDesignCoordinates failed.");
            Assert.IsTrue(FT.LoadGlyph(face, glyph, FT.LOAD_NO_SCALE), "LoadGlyph failed.");
            FT.GetOutlineInfo(face, out _, out points);
            return FT.GetGlyphMetrics(face);
        }

        private static void SetHbWeight(IntPtr font, float w)
        {
            Span<uint> tags = stackalloc uint[1]; tags[0] = WGHT;
            Span<float> vals = stackalloc float[1]; vals[0] = w;
            FTVar.SetHbVariations(font, tags, vals);
        }
    }
}
