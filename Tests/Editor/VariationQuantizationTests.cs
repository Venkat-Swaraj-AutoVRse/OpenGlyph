using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Verifies the round-2 variation decisions on RobotoFlex-VF: free coordinates quantize to ~1%
    /// of each axis range (so a continuous sweep yields a BOUNDED set of distinct cache keys), and
    /// optical size auto-drives <c>opsz</c> from the rendered size unless set explicitly.
    /// </summary>
    public class VariationQuantizationTests
    {
        private byte[] _vf;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");
            _vf = File.ReadAllBytes(vf);
        }

        private VariationMapper Map(out IntPtr face)
        {
            face = FT.LoadFace(_vf, 0);
            return VariationMapper.Read(face);
        }

        [Test]
        public void WeightSweep_1to1000_Step1_IsCappedByQuantization()
        {
            var map = Map(out var face);
            try
            {
                var wi = map.AxisIndex(VariationMapper.WGHT);
                Assert.GreaterOrEqual(wi, 0);
                float min = map.axes[wi].min, max = map.axes[wi].max;

                var keys = new HashSet<VariationKey>();
                for (int w = (int)min; w <= (int)max; w++)
                {
                    var k = map.Map(new FontStyleSpec(w, 100, StyleAxis.Normal), 0f, out _, out _);
                    keys.Add(k);
                }

                // With 100 buckets across the wght range, distinct keys cannot exceed ~101 regardless
                // of how many single-unit steps we take. This is the unbounded-atlas guard.
                Assert.LessOrEqual(keys.Count, VariationMapper.DefaultBuckets + 1,
                    $"Sweeping wght {min}->{max} in steps of 1 produced {keys.Count} keys; must be capped at {VariationMapper.DefaultBuckets + 1}.");
                Assert.Greater(keys.Count, 1, "The sweep must still produce several distinct buckets.");
            }
            finally { FT.UnloadFace(face); }
        }

        [Test]
        public void CloseWeights_WithinOneBucket_ShareKey()
        {
            var map = Map(out var face);
            try
            {
                var wi = map.AxisIndex(VariationMapper.WGHT);
                float min = map.axes[wi].min, max = map.axes[wi].max;
                float step = (max - min) / VariationMapper.DefaultBuckets;

                // The Slot function rounds (coord-min)/range*buckets, so slot k is centered at
                // coordinate (min + k*step) and spans +/- 0.5*step. Sample a slot center and nudge
                // by a small fraction of a step so both samples round to the SAME slot.
                int bucket = VariationMapper.DefaultBuckets / 2;        // middle slot
                float center = min + bucket * step;                    // center of slot `bucket`
                int w1 = Mathf.RoundToInt(center - step * 0.2f);
                int w2 = Mathf.RoundToInt(center + step * 0.2f);
                if (w1 == w2) Assert.Ignore("Axis range too small for a sub-bucket integer delta.");

                var a = map.Map(new FontStyleSpec(w1, 100, StyleAxis.Normal), 0f, out _, out _);
                var b = map.Map(new FontStyleSpec(w2, 100, StyleAxis.Normal), 0f, out _, out _);
                Assert.AreEqual(a, b, $"Weights {w1} and {w2} are within one bucket (step~{step:0.##}) and must share a key.");
            }
            finally { FT.UnloadFace(face); }
        }

        [Test]
        public void Opsz_Auto_ChangesCoordsWithFontSize()
        {
            var map = Map(out var face);
            try
            {
                int oi = map.AxisIndex(VariationMapper.OPSZ);
                if (oi < 0) Assert.Ignore("RobotoFlex has no opsz axis in this build.");

                map.Map(new FontStyleSpec(400, 100, StyleAxis.Normal), 12f, out _, out float[] small);
                map.Map(new FontStyleSpec(400, 100, StyleAxis.Normal), 72f, out _, out float[] large);

                Assert.AreNotEqual(small[oi], large[oi], "opsz must auto-track the rendered font size.");
                // And it must be clamped into the axis range.
                Assert.GreaterOrEqual(large[oi], map.axes[oi].min);
                Assert.LessOrEqual(large[oi], map.axes[oi].max);
            }
            finally { FT.UnloadFace(face); }
        }

        [Test]
        public void Opsz_Explicit_WinsOverAutoSize()
        {
            var map = Map(out var face);
            try
            {
                int oi = map.AxisIndex(VariationMapper.OPSZ);
                if (oi < 0) Assert.Ignore("RobotoFlex has no opsz axis in this build.");

                // Auto would give ~72, but explicit 14 must win.
                map.Map(new FontStyleSpec(400, 100, StyleAxis.Normal), 72f, out _, out float[] c,
                    opszExplicit: true, explicitOpsz: 14f);
                float expected = Mathf.Clamp(14f, map.axes[oi].min, map.axes[oi].max);
                Assert.AreEqual(expected, c[oi], 0.001f, "Explicit opsz must override the auto size.");
            }
            finally { FT.UnloadFace(face); }
        }
    }
}
