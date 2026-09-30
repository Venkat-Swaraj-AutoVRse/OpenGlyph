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
                    // Smooth contour: single colour, all white.
                    foreach (var e in contour.Edges) e.Color = EdgeColor.White;
                }
                else if (corners.Count == 1)
                {
                    // Teardrop: three colours across the loop.
                    EdgeColor[] colors = new EdgeColor[3];
                    EdgeColor c = EdgeColor.White;
                    SwitchColor(ref c, ref seed);
                    colors[0] = c;
                    colors[1] = EdgeColor.White;
                    SwitchColor(ref c, ref seed);
                    colors[2] = c;

                    int corner = corners[0];
                    if (edgeCount >= 3)
                    {
                        int m = edgeCount;
                        for (int i = 0; i < m; i++)
                        {
                            int idx = (corner + i) % m;
                            int seg = (int)(3 + 2.875 * i / (m - 1) - 1.4375 + 0.5) - 3;
                            seg = Math.Max(0, Math.Min(2, seg));
                            contour.Edges[idx].Color = colors[seg];
                        }
                    }
                    else if (edgeCount == 2)
                    {
                        // Split both edges in thirds and recolour (msdfgen does full split; we
                        // approximate by colouring the two edges with the two non-white colours).
                        contour.Edges[0].Color = colors[0];
                        contour.Edges[1].Color = colors[2];
                    }
                    else
                    {
                        contour.Edges[0].Color = colors[0];
                    }
                }
                else
                {
                    // Multiple corners: alternate colour at each corner.
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
    }
}
