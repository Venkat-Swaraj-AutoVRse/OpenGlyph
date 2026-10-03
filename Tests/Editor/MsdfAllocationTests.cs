using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// MSDF generation must not allocate managed memory inside the per-texel loops. The only
    /// legitimate allocations are per glyph: the output field (w*h*3 floats), the error-correction
    /// stencil (w*h bytes) and the contour combiner (O(edges)). Before the fix,
    /// QuadraticSegment.MinSignedDistance allocated a root array per edge sample and the
    /// error-correction diagonal check allocated five arrays per diagonal per texel, so garbage grew
    /// with pixel count (megabytes per glyph at atlas sizes).
    /// </summary>
    public class MsdfAllocationTests
    {
        private const double Kappa = 0.5522847498;

        // Outer circle of 8 quadratics (CCW) + inner hole of 4 cubics (CW): exercises both curve
        // distance paths, two contours (the overlapping combiner) and error correction.
        private static Shape BuildShape()
        {
            var shape = new Shape();
            var outer = new Contour();
            const double cx = 50, cy = 50, r = 40;
            double ctrlR = r / Math.Cos(Math.PI / 8);
            for (int k = 0; k < 8; k++)
            {
                double a0 = k * Math.PI / 4, a1 = (k + 1) * Math.PI / 4, am = (a0 + a1) / 2;
                outer.Add(new QuadraticSegment(
                    new Vector2D(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)),
                    new Vector2D(cx + ctrlR * Math.Cos(am), cy + ctrlR * Math.Sin(am)),
                    new Vector2D(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1))));
            }
            shape.Contours.Add(outer);

            var inner = new Contour();
            const double ir = 15;
            double kk = Kappa * ir;
            // Clockwise: right -> bottom -> left -> top -> right.
            var p = new[]
            {
                new Vector2D(cx + ir, cy), new Vector2D(cx, cy - ir), new Vector2D(cx - ir, cy), new Vector2D(cx, cy + ir),
            };
            inner.Add(new CubicSegment(p[0], new Vector2D(cx + ir, cy - kk), new Vector2D(cx + kk, cy - ir), p[1]));
            inner.Add(new CubicSegment(p[1], new Vector2D(cx - kk, cy - ir), new Vector2D(cx - ir, cy - kk), p[2]));
            inner.Add(new CubicSegment(p[2], new Vector2D(cx - ir, cy + kk), new Vector2D(cx - kk, cy + ir), p[3]));
            inner.Add(new CubicSegment(p[3], new Vector2D(cx + kk, cy + ir), new Vector2D(cx + ir, cy + kk), p[0]));
            shape.Contours.Add(inner);

            shape.OrientContours();
            EdgeColoring.ColorSimple(shape, EdgeColoring.DefaultAngleThreshold, 0);
            return shape;
        }

        private static MsdfConfig Config(double scale, out int pixels)
        {
            const int pad = 4;
            int size = (int)Math.Ceiling(100 * scale) + 2 * pad;
            pixels = size * size;
            return new MsdfConfig
            {
                Width = size, Height = size, Range = 2 * pad,
                ScaleX = scale, ScaleY = scale,
                TranslateX = pad / scale, TranslateY = pad / scale,
                ErrorCorrection = true,
            };
        }

        // GC.GetAllocatedBytesForCurrentThread reports 0 under the Editor's Mono, so allocations are
        // counted with the profiler's GC.Alloc marker on this thread (the same mechanism as the Test
        // Framework's Is.Not.AllocatingGCMemory constraint). Each count is one managed allocation.
        private static int CountAllocations(Action action)
        {
            var recorder = Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try { action(); }
            finally
            {
                recorder.enabled = false;
                recorder.CollectFromAllThreads();
            }
            return recorder.sampleBlockCount;
        }

        private static int MeasureGenerate(Shape shape, double scale, out int pixels)
        {
            var cfg = Config(scale, out pixels);
            float[] field = null;
            int count = CountAllocations(() => field = MsdfGenerator.Generate(shape, in cfg));
            Assert.AreEqual(pixels * 3, field.Length);
            return count;
        }

        [Test]
        public void Generate_DoesNotAllocatePerTexel()
        {
            var shape = BuildShape();
            MeasureGenerate(shape, 0.25, out _); // JIT / static init warm-up

            int smallAllocs = MeasureGenerate(shape, 1.0, out int smallPx);
            int largeAllocs = MeasureGenerate(shape, 3.0, out int largePx);

            Debug.Log($"[PERF2][MSDF] managed allocations per Generate: {smallPx}px -> {smallAllocs}, " +
                      $"{largePx}px -> {largeAllocs}");

            // Per glyph: output field, stencil, combiner (a few arrays per contour). Anything that
            // grows with the pixel count is per-texel garbage.
            Assert.Less(largeAllocs, 64,
                $"MSDF generation of {largePx} texels made {largeAllocs} managed allocations â€” the per-texel loop is allocating");
            Assert.AreEqual(smallAllocs, largeAllocs, "allocation count must not depend on pixel count");
        }
    }
}
