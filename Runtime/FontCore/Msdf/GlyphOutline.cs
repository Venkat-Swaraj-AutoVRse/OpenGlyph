using System;
using System.Collections.Generic;

namespace LightSide.Msdf
{
    /// <summary>
    /// A single point of a glyph contour, expressed in floating-point font units
    /// (typically 26.6 fixed-point converted to <see cref="double"/>).
    /// </summary>
    public readonly struct OutlinePoint
    {
        public readonly double X;
        public readonly double Y;

        public OutlinePoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// FreeType curve-tag classification for an outline point. Mirrors the low bits of
    /// FreeType's <c>FT_CURVE_TAG_*</c> flags so a <see cref="FreeTypeOutlineSource"/> can
    /// forward tags verbatim, while the managed decomposer applies FreeType's implicit
    /// on-curve-midpoint rule.
    /// </summary>
    public enum OutlinePointTag : byte
    {
        /// <summary>On-curve point (FT_CURVE_TAG_ON).</summary>
        OnCurve = 1,
        /// <summary>Off-curve quadratic control point (FT_CURVE_TAG_CONIC / tag == 0).</summary>
        QuadraticControl = 0,
        /// <summary>Off-curve cubic control point (FT_CURVE_TAG_CUBIC).</summary>
        CubicControl = 2,
    }

    /// <summary>
    /// A closed contour: an ordered list of points plus their FreeType curve tags.
    /// The number of points equals the number of tags.
    /// </summary>
    public sealed class OutlineContour
    {
        public readonly List<OutlinePoint> Points;
        public readonly List<OutlinePointTag> Tags;

        public OutlineContour(int capacity = 0)
        {
            Points = new List<OutlinePoint>(capacity);
            Tags = new List<OutlinePointTag>(capacity);
        }

        public int Count => Points.Count;

        public void Add(OutlinePoint p, OutlinePointTag tag)
        {
            Points.Add(p);
            Tags.Add(tag);
        }
    }

    /// <summary>
    /// A decomposed glyph outline: a set of closed contours in font-unit coordinates,
    /// with the winding orientation FreeType reports (used for signed distance sign).
    /// </summary>
    /// <remarks>
    /// This is the shared model the MSDF generator consumes. It is deliberately free of
    /// any FreeType or Unity dependency so it can be produced by a native outline export,
    /// a test-only TrueType parser, or synthesised programmatically.
    /// </remarks>
    public sealed class GlyphOutline
    {
        public readonly List<OutlineContour> Contours;

        /// <summary>
        /// True when the font uses FreeType's default (PostScript / CFF-style) fill rule
        /// where outer contours are counter-clockwise. FreeType reports this via
        /// <c>FT_OUTLINE_REVERSE_FILL</c>; when set the fill orientation is reversed
        /// (TrueType-style, clockwise-outer). The MSDF generator uses it only for a global
        /// sign convention and is robust to it via the shoelace-based orientation test.
        /// </summary>
        public bool ReverseFill;

        public GlyphOutline(int contourCapacity = 0)
        {
            Contours = new List<OutlineContour>(contourCapacity);
        }

        public bool IsEmpty
        {
            get
            {
                if (Contours.Count == 0) return true;
                foreach (var c in Contours)
                    if (c.Count > 0) return false;
                return true;
            }
        }

        /// <summary>Computes the tight bounding box of all contour points, in font units.</summary>
        public bool TryGetBounds(out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            bool any = false;
            foreach (var c in Contours)
            {
                foreach (var p in c.Points)
                {
                    any = true;
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
            }
            if (!any) { minX = minY = maxX = maxY = 0; }
            return any;
        }
    }

    /// <summary>
    /// Supplies decomposed <see cref="GlyphOutline"/>s for glyph indices at a given
    /// sampling size. Implementations may be backed by FreeType (native) or, in tests,
    /// by a managed TrueType parser or synthetic shapes.
    /// </summary>
    public interface IGlyphOutlineSource
    {
        /// <summary>True when this source can actually produce outlines in this environment.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Retrieves the outline for <paramref name="glyphIndex"/> in font units
        /// (the source is responsible for any scaling to <paramref name="pixelsPerEm"/>-space
        /// if it applies one; the default FreeType source returns 26.6 pixel units at the
        /// requested size).
        /// </summary>
        /// <returns>The outline, or null if the glyph is empty (e.g. whitespace) or unavailable.</returns>
        GlyphOutline GetOutline(uint glyphIndex, int pixelsPerEm);
    }
}
