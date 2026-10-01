using System;
using System.Collections.Generic;

namespace LightSide.Msdf
{
    /// <summary>A closed sequence of edges. Ported from msdfgen's <c>Contour</c> (MIT).</summary>
    public sealed class Contour
    {
        public readonly List<EdgeSegment> Edges = new List<EdgeSegment>();

        public void Add(EdgeSegment e) => Edges.Add(e);

        /// <summary>
        /// Reverses the contour's direction in place: each edge is replaced by its reverse and the
        /// edge order is inverted, so a counter-clockwise loop becomes clockwise and vice versa.
        /// This flips the sign of <see cref="Winding"/> and of the directed-edge distance sign the
        /// MSDF generator reads, without changing the geometry traced.
        /// </summary>
        public void Reverse()
        {
            int n = Edges.Count;
            if (n == 0) return;
            var rev = new List<EdgeSegment>(n);
            for (int i = n - 1; i >= 0; i--)
                rev.Add(Edges[i].Reversed());
            Edges.Clear();
            Edges.AddRange(rev);
        }

        /// <summary>
        /// Signed winding of the contour: +1 counter-clockwise, -1 clockwise, 0 empty.
        /// Uses the shoelace signed area over control points (msdfgen Contour::winding).
        /// </summary>
        public int Winding()
        {
            if (Edges.Count == 0) return 0;
            double total = 0;
            if (Edges.Count == 1)
            {
                Vector2D a = Edges[0].Point(0), b = Edges[0].Point(1.0 / 3.0), c = Edges[0].Point(2.0 / 3.0);
                total += Shoelace(a, b);
                total += Shoelace(b, c);
                total += Shoelace(c, a);
            }
            else if (Edges.Count == 2)
            {
                Vector2D a = Edges[0].Point(0), b = Edges[0].Point(0.5),
                         c = Edges[1].Point(0), d = Edges[1].Point(0.5);
                total += Shoelace(a, b);
                total += Shoelace(b, c);
                total += Shoelace(c, d);
                total += Shoelace(d, a);
            }
            else
            {
                Vector2D prev = Edges[Edges.Count - 1].Point(0);
                foreach (var e in Edges)
                {
                    Vector2D cur = e.Point(0);
                    total += Shoelace(prev, cur);
                    prev = cur;
                }
            }
            return Sign(total);
        }

        public void Bound(ref double l, ref double b, ref double r, ref double t)
        {
            foreach (var e in Edges)
            {
                // Sample endpoints and a few interior points to bound curves conservatively.
                for (int i = 0; i <= 8; i++)
                {
                    Vector2D p = e.Point(i / 8.0);
                    if (p.X < l) l = p.X;
                    if (p.Y < b) b = p.Y;
                    if (p.X > r) r = p.X;
                    if (p.Y > t) t = p.Y;
                }
            }
        }

        private static double Shoelace(Vector2D a, Vector2D b) => (b.X - a.X) * (a.Y + b.Y);
        private static int Sign(double n) => n > 0 ? 1 : (n < 0 ? -1 : 0);
    }

    /// <summary>
    /// A glyph shape: a set of closed <see cref="Contour"/>s. Built from a <see cref="GlyphOutline"/>,
    /// resolving FreeType's implicit on-curve midpoint rule between consecutive off-curve points.
    /// Ported from msdfgen's <c>Shape</c> (MIT).
    /// </summary>
    public sealed class Shape
    {
        public readonly List<Contour> Contours = new List<Contour>();

        /// <summary>
        /// True when the winding fill rule is "positive-is-inside". For FreeType TrueType glyphs
        /// (clockwise-outer) we normalise to msdfgen's convention during generation, so this stays
        /// informational.
        /// </summary>
        public bool InverseYAxis;

        /// <summary>Total edge count across all contours.</summary>
        public int EdgeCount
        {
            get
            {
                int n = 0;
                foreach (var c in Contours) n += c.Edges.Count;
                return n;
            }
        }

        public void Bounds(out double l, out double b, out double r, out double t)
        {
            l = double.MaxValue; b = double.MaxValue; r = double.MinValue; t = double.MinValue;
            foreach (var c in Contours) c.Bound(ref l, ref b, ref r, ref t);
            if (Contours.Count == 0 || l > r) { l = b = r = t = 0; }
        }

        /// <summary>
        /// True when point <paramref name="p"/> is inside the shape under the NON-ZERO winding
        /// fill rule, evaluated over ALL contours together. This is the authoritative inside/outside
        /// test that a distance-field sign must agree with. Unlike summing per-contour windings, it
        /// is correct for glyphs with holes (counter-oriented inner contours) AND for overlapping
        /// contours (common in Noto and variable-font instances), because it accumulates the signed
        /// crossing count of a ray from <paramref name="p"/> against every edge.
        /// </summary>
        /// <remarks>
        /// Casts a horizontal ray to +X and sums the signed crossings of every edge (each edge
        /// flattened into short spans for curves). Winding != 0 ⇒ inside. Ported in spirit from
        /// msdfgen's Shape::scanline / nonZeroWinding.
        /// </remarks>
        public bool Contains(Vector2D p)
        {
            return Winding(p) != 0;
        }

        /// <summary>Non-zero winding number of the shape at <paramref name="p"/> (all contours).</summary>
        public int Winding(Vector2D p)
        {
            int winding = 0;
            foreach (var contour in Contours)
            {
                foreach (var edge in contour.Edges)
                {
                    // Flatten each edge into line spans; a line contributes one span, curves several.
                    int steps = (edge is LinearSegment) ? 1 : CurveSteps;
                    Vector2D a = edge.Point(0);
                    for (int s = 1; s <= steps; s++)
                    {
                        Vector2D b = edge.Point((double)s / steps);
                        winding += RayCrossing(p, a, b);
                        a = b;
                    }
                }
            }
            return winding;
        }

        private const int CurveSteps = 64;

        /// <summary>
        /// Signed crossing contribution of segment a→b for a horizontal ray from p toward +X.
        /// +1 for an upward crossing, -1 for a downward crossing, 0 otherwise. Half-open in y to
        /// avoid double-counting shared vertices.
        /// </summary>
        private static int RayCrossing(Vector2D p, Vector2D a, Vector2D b)
        {
            bool aAbove = a.Y > p.Y;
            bool bAbove = b.Y > p.Y;
            if (aAbove == bAbove) return 0; // no y-crossing
            // X of the intersection at y = p.Y.
            double t = (p.Y - a.Y) / (b.Y - a.Y);
            double xCross = a.X + t * (b.X - a.X);
            if (xCross <= p.X) return 0; // crossing is behind the ray origin
            return bAbove ? 1 : -1;      // upward edge => +1, downward => -1
        }


        /// <summary>
        /// Makes contour orientation consistent so that "inside" (by the non-zero winding fill of
        /// the WHOLE shape) always corresponds to the positive side of each directed edge — the
        /// convention the MSDF/SDF generator's signed-distance math assumes. This is the ROOT fix
        /// for inside-out glyphs: TrueType ships outer contours clockwise (PostScript ships them
        /// counter-clockwise), and glyphs like '&', 'g', '@' add overlapping or nested contours, so
        /// raw outline direction cannot be trusted. We reverse exactly the contours whose own
        /// direction disagrees with the role (fill vs hole) that the overall fill assigns them.
        /// </summary>
        /// <remarks>
        /// Faithful in spirit to msdfgen's <c>Shape::orientContours</c>: for each contour we take a
        /// point just OUTSIDE it (stepped off a representative edge point along the outward normal
        /// implied by the contour's own winding) and a point just INSIDE it, then ask the whole
        /// shape's non-zero winding whether that inside point is actually filled. A contour whose
        /// interior the fill rule treats as solid must wind counter-clockwise (+1); one whose
        /// interior is a hole must wind clockwise (−1). Any contour violating that is reversed.
        /// Degenerate/zero-winding contours are left untouched.
        /// </remarks>
        public void OrientContours()
        {
            if (Contours.Count == 0) return;

            // A representative point strictly inside each contour's OWN outline (hole-independent).
            int n = Contours.Count;
            var rep = new Vector2D[n];
            var hasRep = new bool[n];
            var selfW = new int[n];
            for (int i = 0; i < n; i++)
            {
                selfW[i] = Contours[i].Winding();
                hasRep[i] = InteriorPoint(Contours[i], out rep[i]);
            }

            // Decide each contour's role by ENCLOSURE PARITY: how many OTHER contours contain its
            // representative point. Even depth (0, 2, …) ⇒ a FILLED region (outermost ring, or an
            // island inside a hole); odd depth ⇒ a HOLE. This is correct for nested glyphs ('O','B',
            // '&','g') where a single whole-shape fill sample would land in a hole and mislabel the
            // enclosing contour. Overlaps are handled too: an overlapping duplicate sits at depth 0
            // like the contour it overlaps, so both stay fills.
            var reverse = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (selfW[i] == 0 || !hasRep[i] || Contours[i].Edges.Count == 0) continue;

                int depth = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i || !hasRep[j]) continue;
                    if (ContourContains(Contours[j], rep[i])) depth++;
                }

                // This port's directed-edge distance is POSITIVE-inside when a contour's own
                // Contour.Winding() == +1 (verified against a known square and the real-glyph suite),
                // so a fill contour must settle at +1 and a hole at −1.
                int wanted = (depth % 2 == 0) ? +1 : -1;
                if (selfW[i] != wanted)
                    reverse[i] = true;
            }

            for (int i = 0; i < n; i++)
                if (reverse[i]) Contours[i].Reverse();
        }

        /// <summary>Non-zero crossing test of a SINGLE contour against a horizontal ray to +X.</summary>
        private static bool ContourContains(Contour contour, Vector2D p)
        {
            int winding = 0;
            foreach (var edge in contour.Edges)
            {
                int steps = (edge is LinearSegment) ? 1 : CurveSteps;
                Vector2D a = edge.Point(0);
                for (int s = 1; s <= steps; s++)
                {
                    Vector2D c = edge.Point((double)s / steps);
                    winding += RayCrossing(p, a, c);
                    a = c;
                }
            }
            return winding != 0;
        }

        /// <summary>
        /// Finds a point just inside <paramref name="contour"/>'s OWN outline, in its material band
        /// next to an edge (NOT at the bbox centre, which for a ring would land in the hole). Offsets
        /// an edge midpoint a tiny step along the normal, choosing the side the contour's own
        /// crossing test reports as inside. Returns false if no usable edge is found.
        /// </summary>
        private static bool InteriorPoint(Contour contour, out Vector2D inside)
        {
            inside = default;
            if (contour.Edges.Count == 0) return false;

            double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
            contour.Bound(ref l, ref b, ref r, ref t);
            double diag = Math.Sqrt((r - l) * (r - l) + (t - b) * (t - b));
            if (!(diag > 0)) return false;
            double step = Math.Max(1e-4, diag * 5e-3);

            // Try edges longest-first for a stable normal.
            var edges = new List<EdgeSegment>(contour.Edges);
            edges.Sort((x, y) => (y.Point(1) - y.Point(0)).SquaredLength()
                                 .CompareTo((x.Point(1) - x.Point(0)).SquaredLength()));

            foreach (var e in edges)
            {
                Vector2D mid = e.Point(0.5);
                Vector2D dir = e.Direction(0.5).Normalize();
                if (dir.X == 0 && dir.Y == 0) continue;
                Vector2D nrm = new Vector2D(-dir.Y, dir.X);

                Vector2D plus = mid + nrm * step;
                Vector2D minus = mid - nrm * step;
                bool pIn = ContourContains(contour, plus);
                bool mIn = ContourContains(contour, minus);
                if (pIn && !mIn) { inside = plus; return true; }
                if (mIn && !pIn) { inside = minus; return true; }
                // Ambiguous at this edge (step crossed another part of the contour); try the next.
            }
            return false;
        }


        /// <summary>
        /// Builds a <see cref="Shape"/> from a decomposed <see cref="GlyphOutline"/>.
        /// Handles lines, quadratics (with FreeType implicit on-curve midpoints between two
        /// consecutive conic control points) and cubics (paired off-curve points).
        /// </summary>
        public static Shape FromOutline(GlyphOutline outline)
        {
            var shape = new Shape();
            if (outline == null) return shape;

            foreach (var oc in outline.Contours)
            {
                int n = oc.Count;
                if (n < 2) continue;

                var contour = new Contour();

                // Find a starting on-curve point. If none exists (all-conic contour, legal in
                // TrueType), synthesise a start at the midpoint of the first two off-curve points.
                int startIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    if (oc.Tags[i] == OutlinePointTag.OnCurve) { startIdx = i; break; }
                }

                Vector2D start;
                int begin;
                var pts = oc.Points;
                var tags = oc.Tags;

                if (startIdx >= 0)
                {
                    start = V(pts[startIdx]);
                    begin = startIdx;
                }
                else
                {
                    // All off-curve: start at midpoint of last & first.
                    start = 0.5 * (V(pts[n - 1]) + V(pts[0]));
                    begin = 0; // we will treat 'start' as a virtual on-curve before index 0
                    startIdx = -1;
                }

                Vector2D cur = start;
                // Walk the contour once, wrapping around.
                // Collect pending off-curve control points.
                var pending = new List<Vector2D>(2);
                bool pendingCubic = false;

                int total = n;
                for (int k = 1; k <= total; k++)
                {
                    int idx = startIdx >= 0 ? (begin + k) % n : (begin + k - 1) % n;
                    // When we started on a real on-curve point, index runs begin+1..begin+n.
                    // When synthesised, we still need to visit all n points (0..n-1) then close.
                    if (startIdx < 0 && k > total) break;

                    Vector2D p = V(pts[idx]);
                    OutlinePointTag tag = tags[idx];

                    if (tag == OutlinePointTag.OnCurve)
                    {
                        cur = EmitSegment(contour, cur, pending, pendingCubic, p);
                        pending.Clear();
                        pendingCubic = false;
                    }
                    else if (tag == OutlinePointTag.CubicControl)
                    {
                        pending.Add(p);
                        pendingCubic = true;
                    }
                    else // QuadraticControl
                    {
                        if (pending.Count > 0 && !pendingCubic)
                        {
                            // Two consecutive conic controls: implicit on-curve midpoint.
                            Vector2D prevCtrl = pending[pending.Count - 1];
                            Vector2D mid = 0.5 * (prevCtrl + p);
                            contour.Add(new QuadraticSegment(cur, prevCtrl, mid));
                            cur = mid;
                            pending.Clear();
                        }
                        pending.Add(p);
                        pendingCubic = false;
                    }
                }

                // Close back to start.
                if (pending.Count > 0)
                    cur = EmitSegment(contour, cur, pending, pendingCubic, start);
                if (!Approximately(cur, start))
                    contour.Add(new LinearSegment(cur, start));

                PruneDegenerate(contour);

                if (contour.Edges.Count > 0)
                    shape.Contours.Add(contour);
            }

            return shape;
        }

        /// <summary>
        /// Removes zero-length segments and merges consecutive collinear line segments. Font
        /// outlines routinely contain duplicate points and collinear on-curve points; left in,
        /// each produces a spurious "corner" that edge-colouring then splits into different
        /// channels, which shows up as a spur/notch at that joint in the reconstructed glyph.
        /// </summary>
        private static void PruneDegenerate(Contour contour)
        {
            var edges = contour.Edges;

            // 1) Drop zero-length (or sub-pixel-epsilon) edges.
            for (int i = edges.Count - 1; i >= 0; i--)
            {
                var e = edges[i];
                if ((e.Point1 - e.Point0).SquaredLength() < 1e-12)
                    edges.RemoveAt(i);
            }
            if (edges.Count < 2) return;

            // 2) Merge consecutive collinear LINEAR segments (a→b→c with b on segment a→c).
            const double collinearSin = 1e-4; // |sin(angle)| threshold between the two directions
            for (int i = edges.Count - 1; i >= 0; i--)
            {
                int j = (i + 1) % edges.Count;
                if (i == j) break;
                if (edges[i] is LinearSegment a && edges[j] is LinearSegment b)
                {
                    Vector2D da = (a.Point1 - a.Point0).Normalize(true);
                    Vector2D db = (b.Point1 - b.Point0).Normalize(true);
                    // Same direction (collinear, not a reversal) => merge into a→b.Point1.
                    if (Math.Abs(Vector2D.Cross(da, db)) < collinearSin && Vector2D.Dot(da, db) > 0)
                    {
                        edges[i] = new LinearSegment(a.Point0, b.Point1, a.Color);
                        edges.RemoveAt(j > i ? j : i); // remove the merged-away edge
                        // Re-examine this position against the new successor.
                        if (i >= edges.Count) i = edges.Count;
                    }
                }
            }
        }

        private static Vector2D EmitSegment(Contour contour, Vector2D from, List<Vector2D> ctrls, bool cubic, Vector2D to)
        {
            if (ctrls.Count == 0)
            {
                contour.Add(new LinearSegment(from, to));
            }
            else if (cubic && ctrls.Count >= 2)
            {
                contour.Add(new CubicSegment(from, ctrls[0], ctrls[1], to));
            }
            else if (cubic && ctrls.Count == 1)
            {
                // Single cubic control is malformed; approximate with a quadratic.
                contour.Add(new QuadraticSegment(from, ctrls[0], to));
            }
            else
            {
                // One (or leftover) conic control.
                contour.Add(new QuadraticSegment(from, ctrls[ctrls.Count - 1], to));
            }
            return to;
        }

        private static Vector2D V(OutlinePoint p) => new Vector2D(p.X, p.Y);
        private static bool Approximately(Vector2D a, Vector2D b) =>
            Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
    }
}
