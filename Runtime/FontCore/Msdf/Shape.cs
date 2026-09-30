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

                if (contour.Edges.Count > 0)
                    shape.Contours.Add(contour);
            }

            return shape;
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
