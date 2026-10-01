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

        public static void ColorSimple(Shape shape, double angleThreshold = DefaultAngleThreshold, ulong seed = 0)
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

                    // No corner to anchor on; msdfgen lays the three colours with the same symmetric
                    // map as the teardrop, anchored at edge 0. raw in {-1,0,1} -> colors[1+raw].
                    int m0 = edgeCount;
                    for (int i = 0; i < m0; i++)
                    {
                        int raw = (int)(3 + 2.875 * i / (m0 - 1) - 1.4375 + 0.5) - 3;
                        int seg = Math.Max(0, Math.Min(2, 1 + raw));
                        contour.Edges[i].Color = colors[seg];
                    }
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
                        // msdfgen's symmetric three-way map, anchored at the corner. The raw index is
                        // in {-1,0,1} and msdfgen reads it as (colors+1)[raw] == colors[1+raw], so the
                        // three parts receive colors[0], colors[1] (WHITE) and colors[2] respectively.
                        // (A previous port dropped the +1 offset and clamped the -1 to 0, which left
                        // colors[2] unused — that produced the all-CYAN inner ring of '@' whose pinned
                        // G,B pair collapsed the median into the vertical streak.)
                        int raw = (int)(3 + 2.875 * i / (m - 1) - 1.4375 + 0.5) - 3; // -1, 0, or 1
                        int seg = 1 + raw;
                        seg = Math.Max(0, Math.Min(2, seg));
                        contour.Edges[idx].Color = colors[seg];
                    }
                }
                else
                {
                    // MULTIPLE corners. msdfgen edgeColoringSimple: one switching colour per spline
                    // (the run of edges between two consecutive corners), chosen so the two splines
                    // meeting at each corner share exactly one channel and differ in another — that
                    // difference is what the median turns into a sharp corner. No post-pass: the
                    // artifact clean-up is now msdfgen's own MSDFErrorCorrection on the generated
                    // field (see MsdfErrorCorrection.cs), not an edge-colouring heuristic.
                    int cornerCount = corners.Count;
                    int spline = 0;
                    int start = corners[0];
                    EdgeColor color = EdgeColor.White;
                    SwitchColor(ref color, ref seed);
                    EdgeColor initialColor = color;

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
                    }
                }
            }
        }

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
