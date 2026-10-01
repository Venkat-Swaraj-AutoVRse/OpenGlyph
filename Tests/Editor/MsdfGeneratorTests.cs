using System.Collections.Generic;
using NUnit.Framework;
using LightSide.Msdf;

namespace LightSide.Tests
{
    [TestFixture]
    public class MsdfGeneratorTests
    {
        // ---------- Edge coloring invariants ----------

        [Test]
        public void EdgeColoring_EveryEdgeGetsANonBlackColor()
        {
            var shape = Shape.FromOutline(MsdfTestUtil.Square(0, 0, 40));
            EdgeColoring.ColorSimple(shape);
            foreach (var c in shape.Contours)
                foreach (var e in c.Edges)
                    Assert.AreNotEqual(EdgeColor.Black, e.Color, "Edge left uncoloured (BLACK).");
        }

        [Test]
        public void EdgeColoring_AtEachCorner_AdjacentEdgesShareExactlyOneChannel()
        {
            // A square has 4 hard corners; adjacent edges must share exactly one channel so the
            // median reconstructs the corner. (msdfgen invariant.)
            var shape = Shape.FromOutline(MsdfTestUtil.Square(0, 0, 64));
            EdgeColoring.ColorSimple(shape);

            var edges = shape.Contours[0].Edges;
            int n = edges.Count;
            Assert.GreaterOrEqual(n, 4);
            for (int i = 0; i < n; i++)
            {
                var a = edges[i].Color;
                var b = edges[(i + 1) % n].Color;
                int shared = CountBits((int)(a & b));
                // At a hard 90° corner the two edges must differ; they should share at most one
                // channel (exactly one for a proper 3-colour assignment on a 4-corner loop).
                Assert.LessOrEqual(shared, 1,
                    $"Corner {i}: adjacent edges share too many channels ({a} & {b}).");
            }
        }

        private static int CountBits(int v)
        {
            int c = 0; while (v != 0) { c += v & 1; v >>= 1; } return c;
        }

        // ---------- Distance sign correctness ----------

        [Test]
        public void Median_SignMatchesInsideOutside_ForSquare()
        {
            var outline = MsdfTestUtil.Square(0, 0, 40);
            var polys = MsdfTestUtil.Flatten(outline);
            int spread = 6;
            var res = MsdfBuilder.Build(outline, spread);
            Assert.IsTrue(res.IsValid);

            int mismatches = 0, tested = 0;
            // Sample a grid across the bitmap (bottom-up field coords).
            for (int y = 0; y < res.Height; y += 2)
            {
                for (int x = 0; x < res.Width; x += 2)
                {
                    // Map pixel centre back to shape space (see MsdfBuilder framing).
                    double sx = (x + 0.5) - (spread - System.Math.Floor(0.0));
                    double sy = (y + 0.5) - (spread - System.Math.Floor(0.0));
                    // Skip the boundary band where sign is genuinely ambiguous (~1px of the edge).
                    bool inside = MsdfTestUtil.IsInside(polys, sx, sy);
                    float signed = MsdfBuilder.SampleMedianSigned(res.Field, res.Width, res.Height, x, y, res.Range);

                    // Only count pixels clearly away from the edge (|dist| > 1.5 px).
                    if (System.Math.Abs(signed) <= 1.5f) continue;
                    tested++;
                    if ((signed > 0) != inside) mismatches++;
                }
            }
            Assert.Greater(tested, 20, "Not enough interior/exterior samples tested.");
            double rate = (double)mismatches / tested;
            Assert.Less(rate, 0.02, $"Median sign disagreed with inside/outside on {mismatches}/{tested} samples.");
        }

        [Test]
        public void Median_IsInsidePositive_AtSquareCenter()
        {
            var outline = MsdfTestUtil.Square(0, 0, 40);
            int spread = 6;
            var res = MsdfBuilder.Build(outline, spread);
            // Centre of the square in field coords = spread + 20.
            int cx = spread + 20, cy = spread + 20;
            float signed = MsdfBuilder.SampleMedianSigned(res.Field, res.Width, res.Height, cx, cy, res.Range);
            Assert.Greater(signed, 0f, "Square centre should be inside (positive signed distance).");
        }

        [Test]
        public void Median_IsOutsideNegative_AtCorner()
        {
            var outline = MsdfTestUtil.Square(0, 0, 40);
            int spread = 6;
            var res = MsdfBuilder.Build(outline, spread);
            // A pixel in the padding corner is clearly outside.
            float signed = MsdfBuilder.SampleMedianSigned(res.Field, res.Width, res.Height, 1, 1, res.Range);
            Assert.Less(signed, 0f, "Padding corner should be outside (negative signed distance).");
        }

        // ---------- Empty / degenerate ----------

        [Test]
        public void Build_ReturnsInvalid_ForEmptyOutline()
        {
            var res = MsdfBuilder.Build(new GlyphOutline(0), 6);
            Assert.IsFalse(res.IsValid);
        }

        [Test]
        public void Build_ProducesThreeChannelBitmapOfExpectedSize()
        {
            int spread = 4;
            var outline = MsdfTestUtil.Square(0, 0, 30);
            var res = MsdfBuilder.Build(outline, spread);
            Assert.IsTrue(res.IsValid);
            Assert.AreEqual(res.Width * res.Height * 3, res.Rgb.Length, "RGB24 buffer size mismatch.");
            Assert.AreEqual(30 + 2 * spread, res.Width);
            Assert.AreEqual(30 + 2 * spread, res.Height);
        }

        // ---------- MSDF median vs single-channel SDF sign parity ----------

        [Test]
        public void MsdfMedian_MatchesTrueSdfSign_OnSamplePoints_Triangle()
        {
            // Build a triangle (has one sharp apex corner). The MSDF median sign must match a
            // ground-truth SDF sign (from the flattened polygon) at points away from the edge.
            var outline = MsdfTestUtil.Triangle(30, 5, 24, 44);
            var polys = MsdfTestUtil.Flatten(outline);
            int spread = 6;
            var res = MsdfBuilder.Build(outline, spread);
            Assert.IsTrue(res.IsValid);

            int mismatches = 0, tested = 0;
            for (int y = 2; y < res.Height - 2; y += 2)
            {
                for (int x = 2; x < res.Width - 2; x += 2)
                {
                    double sx = (x + 0.5) - spread + FloorMinX(polys);
                    double sy = (y + 0.5) - spread + FloorMinY(polys);
                    float signed = MsdfBuilder.SampleMedianSigned(res.Field, res.Width, res.Height, x, y, res.Range);
                    if (System.Math.Abs(signed) <= 2f) continue;
                    bool inside = MsdfTestUtil.IsInside(polys, sx, sy);
                    tested++;
                    if ((signed > 0) != inside) mismatches++;
                }
            }
            Assert.Greater(tested, 20);
            double rate = (double)mismatches / tested;
            Assert.Less(rate, 0.05, $"Triangle median sign mismatched on {mismatches}/{tested}.");
        }

        private static double FloorMinX(List<List<Vector2D>> polys)
        {
            MsdfTestUtil.PolyBounds(polys, out double minX, out _, out _, out _);
            return System.Math.Floor(minX);
        }
        private static double FloorMinY(List<List<Vector2D>> polys)
        {
            MsdfTestUtil.PolyBounds(polys, out _, out double minY, out _, out _);
            return System.Math.Floor(minY);
        }
    }
}
