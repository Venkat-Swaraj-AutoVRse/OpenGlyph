using System;
using System.Collections.Generic;

namespace LightSide.Msdf
{
    /// <summary>
    /// Assigns <see cref="EdgeColor"/>s to a shape's edges so that at every sharp corner the
    /// two incident edges share exactly one channel and differ in another, which is what lets
    /// the median-of-three reconstruction preserve corners. Port of msdfgen's
    /// <c>edgeColoringSimple</c> (Viktor Chlumsky, MIT). See Third-Party Notices.txt.
    /// </summary>
    public static class EdgeColoring
    {
        /// <summary>Default corner threshold angle (radians). Corners sharper than this split colours.</summary>
        public const double DefaultAngleThreshold = 3.0;

        private static bool IsCorner(Vector2D aDir, Vector2D bDir, double crossThreshold)
        {
            return Vector2D.Dot(aDir, bDir) <= 0 ||
                   Math.Abs(Vector2D.Cross(aDir, bDir)) > crossThreshold;
        }

        private static void SwitchColor(ref EdgeColor color, ref ulong seed, EdgeColor banned = EdgeColor.Black)
        {
            EdgeColor combined = color & banned;
            if (combined == EdgeColor.Red || combined == EdgeColor.Green || combined == EdgeColor.Blue)
            {
                color = combined ^ EdgeColor.White;
                return;
            }
            if (color == EdgeColor.Black || color == EdgeColor.White)
            {
                // Start colours (msdfgen START_COLORS = { CYAN, MAGENTA, YELLOW }).
                EdgeColor[] start = { EdgeColor.Cyan, EdgeColor.Magenta, EdgeColor.Yellow };
                color = start[seed % 3];
                seed /= 3;
                return;
            }
            int shifted = (int)color << (int)(1 + (seed & 1));
            color = (EdgeColor)((shifted | (shifted >> 3)) & (int)EdgeColor.White);
            seed >>= 1;
        }

        public static void ColorSimple(Shape shape, double angleThreshold = DefaultAngleThreshold, ulong seed = 0, double maxEdgeRun = 0)
        {
            double crossThreshold = Math.Sin(angleThreshold);

            foreach (var contour in shape.Contours)
            {
                int edgeCount = contour.Edges.Count;
                if (edgeCount == 0) continue;

                // Detect corners.
                var corners = new List<int>();
                {
                    Vector2D prevDir = contour.Edges[edgeCount - 1].Direction(1).Normalize();
                    for (int i = 0; i < edgeCount; i++)
                    {
                        var e = contour.Edges[i];
                        if (IsCorner(prevDir, e.Direction(0).Normalize(), crossThreshold))
                            corners.Add(i);
                        prevDir = e.Direction(1).Normalize();
                    }
                }

                if (corners.Count == 0)
                {
                    // SMOOTH contour (no corners): e.g. 'O', the inner/outer walls of '@', 'o', 'e'.
                    // msdfgen does NOT leave this a single colour — a lone WHITE loop reconstructs
                    // like a plain SDF, and a lone TWO-channel loop pins its two channels to ~0.5
                    // wherever the wall runs tangent to a sample column, collapsing the median along
                    // an interior line (the '@' artifact this round targets). The faithful fix is
                    // msdfgen's "teardrop" treatment of a corner-free contour: SPLIT it into three
                    // parts and give them three colours drawn so that EVERY channel is represented
                    // and no single two-channel colour spans a long tangent run. We split each edge
                    // into thirds first when the contour has fewer than three edges, so there are
                    // always at least three parts to colour (msdfgen splitInThirds). Port of the
                    // zero-corner branch of edgeColoringSimple (Viktor Chlumsky, MIT).
                    EnsureAtLeastThreeEdges(contour);
                    edgeCount = contour.Edges.Count;

                    EdgeColor[] colors = new EdgeColor[3];
                    EdgeColor c0 = EdgeColor.White;
                    SwitchColor(ref c0, ref seed);
                    colors[0] = c0;
                    colors[1] = EdgeColor.White;
                    SwitchColor(ref c0, ref seed);
                    colors[2] = c0;

                    // No corner to anchor on, so lay the three colours evenly around the loop.
                    for (int i = 0; i < edgeCount; i++)
                        contour.Edges[i].Color = colors[ThreeWaySegment(i, edgeCount)];
                }
                else if (corners.Count == 1)
                {
                    // TEARDROP: a single sharp point on an otherwise smooth loop. msdfgen splits the
                    // loop into three parts fanning out from the corner and colours them so the two
                    // edges meeting AT the corner differ in a channel (preserving the point) while
                    // the smooth span opposite the corner still carries three colours between it.
                    // Split to >=3 edges first so the three-way division has edges to land on.
                    EnsureAtLeastThreeEdges(contour);
                    edgeCount = contour.Edges.Count;

                    // The split above may have shifted the corner index; re-find it (there is still
                    // exactly one, splitting an edge in thirds introduces no new corners).
                    int corner = FindFirstCorner(contour, crossThreshold);
                    if (corner < 0) corner = 0;

                    EdgeColor[] colors = new EdgeColor[3];
                    EdgeColor c = EdgeColor.White;
                    SwitchColor(ref c, ref seed);
                    colors[0] = c;
                    colors[1] = EdgeColor.White;
                    SwitchColor(ref c, ref seed);
                    colors[2] = c;

                    int m = edgeCount;
                    for (int i = 0; i < m; i++)
                    {
                        int idx = (corner + i) % m;
                        // msdfgen's symmetric three-way map, anchored at the corner.
                        int seg = (int)(3 + 2.875 * i / (m - 1) - 1.4375 + 0.5) - 3;
                        seg = Math.Max(0, Math.Min(2, seg));
                        contour.Edges[idx].Color = colors[seg];
                    }
                }
                else
                {
                    // MULTIPLE corners. Base colouring is msdfgen edgeColoringSimple: one switching
                    // colour per spline (the run of edges between two consecutive corners), chosen so
                    // the two splines meeting at each corner share exactly one channel and differ in
                    // another — that difference is what the median turns into a sharp corner.
                    int cornerCount = corners.Count;
                    int spline = 0;
                    int start = corners[0];
                    EdgeColor color = EdgeColor.White;
                    SwitchColor(ref color, ref seed);
                    EdgeColor initialColor = color;

                    // Track, per edge, which spline it belongs to and its base colour, so a second
                    // pass can refine LONG splines without disturbing corners.
                    var splineOf = new int[edgeCount];
                    var baseColor = new EdgeColor[edgeCount];

                    for (int i = 0; i < edgeCount; i++)
                    {
                        int idx = (start + i) % edgeCount;
                        if (spline + 1 < cornerCount && corners[spline + 1] == idx)
                        {
                            spline++;
                            EdgeColor banned = (spline == cornerCount - 1) ? initialColor : EdgeColor.Black;
                            SwitchColor(ref color, ref seed, banned);
                        }
                        contour.Edges[idx].Color = color;
                        splineOf[idx] = spline;
                        baseColor[idx] = color;
                    }

                    // LONG-SPLINE refinement (the '@' inner-wall fix). A long SMOOTH spline carrying a
                    // single TWO-channel colour pins those two channels to ~0.5 wherever it runs
                    // tangent to a sample column; the third channel comes only from the far wall, so
                    // the median collapses to the contour value along an interior line. msdfgen avoids
                    // this by reintroducing the third channel along the spline. We do it with a
                    // SHARED-CHANNEL HANDOFF: partway along the spline we switch to a colour that
                    // shares ONE channel with the base and swaps the other for the missing third
                    // channel (e.g. YELLOW=RG -> CYAN=GB keeps G, swaps R for B). Because the two
                    // colours share a channel, the median is continuous across the seam (no spurious
                    // corner), yet all three channels are now represented along the run, so the median
                    // can no longer collapse. Handoffs repeat every ~maxRun output pixels of arc.
                    if (maxEdgeRun > 0)
                        RefineLongSplines(contour, splineOf, baseColor, cornerCount, maxEdgeRun);
                }
            }
        }

        /// <summary>
        /// For each spline whose arc length exceeds <paramref name="maxRun"/>, recolours its INTERIOR
        /// edges WHITE while leaving the first and last edge at the spline's two-channel base colour.
        /// A long two-channel spline running tangent to a sample column pins BOTH its channels to the
        /// edge value there, collapsing the median to the contour along an interior line (the '@'
        /// inner-wall artifact). A WHITE edge contributes to all three channels equally, so its median
        /// is simply that edge's honest distance — exactly like a single-channel SDF along a smooth
        /// wall, with no pair to pin. Keeping the first/last edge coloured preserves the channel
        /// difference each bounding corner needs for a sharp median corner. Short splines, WHITE
        /// splines and single-channel splines are left untouched. This is msdfgen's strategy of
        /// reserving two-channel colouring for the corner neighbourhoods and letting smooth spans
        /// reconstruct like an SDF.
        /// </summary>
        private static void RefineLongSplines(Contour contour, int[] splineOf, EdgeColor[] baseColor, int splineCount, double maxRun)
        {
            int n = contour.Edges.Count;
            for (int s = 0; s < splineCount; s++)
            {
                // Collect this spline's edge indices in traversal order and measure total arc length.
                var idxs = new List<int>();
                double total = 0;
                for (int i = 0; i < n; i++)
                    if (splineOf[i] == s) { idxs.Add(i); total += (contour.Edges[i].Point(1) - contour.Edges[i].Point(0)).Length(); }
                if (idxs.Count < 3) continue; // need an interior to whiten while keeping both ends

                EdgeColor baseC = baseColor[idxs[0]];
                // Only a TWO-channel colour can pin a channel pair; WHITE/single-channel never collapse.
                if (!IsTwoChannel(baseC) || total <= maxRun) continue;

                // Whiten everything except the first and last edge (which anchor the two end corners).
                for (int k = 1; k < idxs.Count - 1; k++)
                    contour.Edges[idxs[k]].Color = EdgeColor.White;
            }
        }

        /// <summary>True for the three two-channel edge colours (YELLOW=RG, MAGENTA=RB, CYAN=GB).</summary>
        private static bool IsTwoChannel(EdgeColor c) =>
            c == EdgeColor.Yellow || c == EdgeColor.Magenta || c == EdgeColor.Cyan;

        /// <summary>
        /// Ensures a contour carries at least three edges by splitting each edge into thirds
        /// (msdfgen <c>EdgeSegment::splitInThirds</c>) until there are three or more. The geometry
        /// is unchanged — a split edge traces the identical curve — so this only gives the colouring
        /// pass enough separate pieces to assign three distinct channel colours to a smooth or very
        /// short contour. A one-edge contour becomes three; a two-edge contour becomes six.
        /// </summary>
        private static void EnsureAtLeastThreeEdges(Contour contour)
        {
            if (contour.Edges.Count >= 3) return;
            var split = new List<EdgeSegment>(contour.Edges.Count * 3);
            foreach (var e in contour.Edges)
            {
                e.SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c);
                split.Add(a); split.Add(b); split.Add(c);
            }
            contour.Edges.Clear();
            contour.Edges.AddRange(split);
        }

        /// <summary>
        /// Maps edge position <paramref name="i"/> of <paramref name="count"/> onto one of three
        /// evenly-sized arcs (0,1,2) around a corner-free loop, so the three teardrop colours are
        /// laid down in equal thirds.
        /// </summary>
        private static int ThreeWaySegment(int i, int count)
        {
            if (count <= 0) return 0;
            int seg = (3 * i) / count;
            return seg < 0 ? 0 : (seg > 2 ? 2 : seg);
        }

        /// <summary>Index of the first corner in a contour, or -1 if the contour is smooth.</summary>
        private static int FindFirstCorner(Contour contour, double crossThreshold)
        {
            int n = contour.Edges.Count;
            if (n == 0) return -1;
            Vector2D prevDir = contour.Edges[n - 1].Direction(1).Normalize();
            for (int i = 0; i < n; i++)
            {
                var e = contour.Edges[i];
                if (IsCorner(prevDir, e.Direction(0).Normalize(), crossThreshold))
                    return i;
                prevDir = e.Direction(1).Normalize();
            }
            return -1;
        }
    }
}
