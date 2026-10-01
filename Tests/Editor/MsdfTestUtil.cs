using System;
using System.Collections.Generic;
using System.IO;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>Shared helpers for MSDF tests: ground-truth inside/outside via flattened outlines.</summary>
    internal static class MsdfTestUtil
    {
        /// <summary>
        /// Locates the bundled NotoSans-Regular.ttf inside the package (works whether the package
        /// is embedded or resolved from cache) by walking up from the test assembly location and
        /// probing the known Defaults path.
        /// </summary>
        public static string FindNotoSansPath()
        {
            // Try Packages virtual path first (Unity resolves this for embedded/local packages).
            string[] candidates =
            {
                "Packages/com.openglyph.text/Defaults/NotoSans-Regular.ttf",
                Path.Combine(UnityEngine.Application.dataPath ?? "", "..", "Packages", "com.openglyph.text", "Defaults", "NotoSans-Regular.ttf"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            // Resolve relative to THIS package's physical on-disk root (works for a file:-referenced
            // package whose source tree is outside the host project): Defaults/ ships inside the
            // imported package.
            string pkgRoot = PackagePhysicalRoot();
            if (pkgRoot != null)
            {
                string cand = Path.Combine(pkgRoot, "Defaults", "NotoSans-Regular.ttf");
                if (File.Exists(cand)) return cand;
            }

            // Fallback: search the Library/PackageCache and the project tree.
            string root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath ?? ".", ".."));
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "NotoSans-Regular.ttf", SearchOption.AllDirectories))
                    return f;
            }
            catch { /* ignore */ }
            return null;
        }

        /// <summary>
        /// Physical on-disk root of THIS package, resolved from the test assembly via the Package
        /// Manager (<c>PackageInfo.FindForAssembly</c>). For a <c>file:</c>-referenced package this is
        /// the real source worktree, NOT a path under the host project's <c>dataPath</c> — which is
        /// why walking up from <c>Application.dataPath</c> cannot find assets that live only in the
        /// source tree (e.g. the <c>NativeSource~/</c> test fonts, which Unity never imports because
        /// of the trailing <c>~</c>). Returns null if the Package Manager cannot locate the assembly.
        /// </summary>
        private static string PackagePhysicalRoot()
        {
            try
            {
                var asm = typeof(MsdfTestUtil).Assembly;
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(asm);
                // resolvedPath is the physical directory of the package on disk (the worktree root
                // for a file: dependency); assetPath is the virtual Packages/<name> path.
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath) && Directory.Exists(info.resolvedPath))
                    return info.resolvedPath;
            }
            catch { /* Package Manager unavailable — fall back to the dataPath walk-up. */ }
            return null;
        }

        /// <summary>Flattens each contour into a closed polyline (line/quad/cubic sampled).</summary>
        public static List<List<Vector2D>> Flatten(GlyphOutline outline, int stepsPerCurve = 16)
        {
            var shape = Shape.FromOutline(outline);
            var polys = new List<List<Vector2D>>();
            foreach (var contour in shape.Contours)
            {
                var poly = new List<Vector2D>();
                foreach (var e in contour.Edges)
                {
                    int steps = (e is LinearSegment) ? 1 : stepsPerCurve;
                    for (int i = 0; i < steps; i++)
                        poly.Add(e.Point((double)i / steps));
                }
                if (poly.Count >= 3) polys.Add(poly);
            }
            return polys;
        }

        /// <summary>Even-odd point-in-polygons test on flattened contours. True == inside the fill.</summary>
        public static bool IsInside(List<List<Vector2D>> polys, double px, double py)
        {
            bool inside = false;
            foreach (var poly in polys)
            {
                int n = poly.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double yi = poly[i].Y, yj = poly[j].Y;
                    double xi = poly[i].X, xj = poly[j].X;
                    bool cross = ((yi > py) != (yj > py)) &&
                                 (px < (xj - xi) * (py - yi) / (yj - yi) + xi);
                    if (cross) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>Bounds of a flattened outline.</summary>
        public static void PolyBounds(List<List<Vector2D>> polys, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue; maxX = maxY = double.MinValue;
            foreach (var poly in polys)
                foreach (var p in poly)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
            if (minX > maxX) { minX = minY = maxX = maxY = 0; }
        }

        /// <summary>Builds a simple programmatic square outline (CCW) of the given size, y-up.</summary>
        public static GlyphOutline Square(double x0, double y0, double size)
        {
            var o = new GlyphOutline(1);
            var c = new OutlineContour(4);
            c.Add(new OutlinePoint(x0, y0), OutlinePointTag.OnCurve);
            c.Add(new OutlinePoint(x0 + size, y0), OutlinePointTag.OnCurve);
            c.Add(new OutlinePoint(x0 + size, y0 + size), OutlinePointTag.OnCurve);
            c.Add(new OutlinePoint(x0, y0 + size), OutlinePointTag.OnCurve);
            o.Contours.Add(c);
            return o;
        }

        /// <summary>Builds an axis-aligned triangle outline (CCW), y-up, with a sharp apex at top.</summary>
        public static GlyphOutline Triangle(double cx, double baseY, double halfWidth, double height)
        {
            var o = new GlyphOutline(1);
            var c = new OutlineContour(3);
            c.Add(new OutlinePoint(cx - halfWidth, baseY), OutlinePointTag.OnCurve);
            c.Add(new OutlinePoint(cx + halfWidth, baseY), OutlinePointTag.OnCurve);
            c.Add(new OutlinePoint(cx, baseY + height), OutlinePointTag.OnCurve);
            o.Contours.Add(c);
            return o;
        }

        /// <summary>Locates the fetched RobotoFlex-VF.ttf (variable font) under NativeSource~/tests/fonts.</summary>
        public static string FindRobotoFlexPath()
        {
            // PRIMARY: resolve relative to THIS package's physical root. The font lives at
            // <packageRoot>/NativeSource~/tests/fonts/RobotoFlex-VF.ttf. The NativeSource~ folder is
            // NOT imported by Unity (trailing ~), so it is never under the host project's dataPath;
            // walking up from dataPath (the old-only strategy) therefore found nothing in a
            // file:-referenced host and the test skipped. PackageInfo.resolvedPath IS the worktree.
            string pkgRoot = PackagePhysicalRoot();
            if (pkgRoot != null)
            {
                string cand = Path.Combine(pkgRoot, "NativeSource~", "tests", "fonts", "RobotoFlex-VF.ttf");
                if (File.Exists(cand)) return cand;
            }

            // FALLBACK 1: walk up from the host project (covers an embedded package laid out under
            // the project directory).
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath ?? ".", ".."));
            var dir = new DirectoryInfo(root);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string cand = Path.Combine(dir.FullName, "NativeSource~", "tests", "fonts", "RobotoFlex-VF.ttf");
                if (File.Exists(cand)) return cand;
                dir = dir.Parent;
            }
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "RobotoFlex-VF.ttf", SearchOption.AllDirectories))
                    return f;
            }
            catch { /* ignore */ }
            return null;
        }

        /// <summary>
        /// Non-zero-winding ground-truth fill of a Shape at a supersampled cell centre. Correct for
        /// glyphs with holes AND overlapping contours (unlike the even-odd <see cref="IsInside"/>).
        /// </summary>
        public static bool WindingInside(Shape shape, double x, double y) => shape.Contains(new Vector2D(x, y));

        /// <summary>
        /// Analyses a boolean error mask (true = reconstructed silhouette disagrees with ground
        /// truth) and returns the sizes of connected error components (4-connectivity) that lie
        /// MORE than <paramref name="edgeBandPx"/> output pixels from any true-edge pixel. Isolated
        /// specks/spurs away from the real edge are exactly what these components capture.
        /// </summary>
        public static List<int> IsolatedErrorBlobSizes(bool[] error, bool[] nearEdge, int w, int h)
        {
            var sizes = new List<int>();
            var seen = new bool[w * h];
            var stack = new Stack<int>();
            for (int i = 0; i < w * h; i++)
            {
                if (!error[i] || seen[i] || nearEdge[i]) continue;
                int size = 0; stack.Clear(); stack.Push(i); seen[i] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop(); size++;
                    int px = p % w, py = p / w;
                    void Try(int q) { if (q >= 0 && q < w * h && error[q] && !seen[q] && !nearEdge[q]) { seen[q] = true; stack.Push(q); } }
                    if (px > 0) Try(p - 1);
                    if (px + 1 < w) Try(p + 1);
                    if (py > 0) Try(p - w);
                    if (py + 1 < h) Try(p + w);
                }
                sizes.Add(size);
            }
            return sizes;
        }
    }
}
