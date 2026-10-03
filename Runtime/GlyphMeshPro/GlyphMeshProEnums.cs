// OpenGlyph GlyphMeshPro — TMP-parity enums and value types.
//
// Clean-room: these mirror the PUBLIC NAMES and member sets of TextMeshPro's
// enums so that migrating from TMP is a mechanical rename. No TMP source,
// shader, or asset is copied; semantics are implemented against OpenGlyph's
// own engine (LightSide.UniText). See Documentation/GlyphMeshPro-Parity.md.

using System;
using UnityEngine;

namespace OpenGlyph
{
    /// <summary>
    /// Font style flags. Mirrors TextMeshPro's <c>FontStyles</c> flag names and bit
    /// values so bitmasks migrate unchanged. Mapped onto OpenGlyph's engine
    /// (<see cref="LightSide.StyleAxis"/>, font weight, and span-style tags).
    /// </summary>
    [Flags]
    public enum FontStyles
    {
        Normal = 0x0,
        Bold = 0x1,
        Italic = 0x2,
        Underline = 0x4,
        LowerCase = 0x8,
        UpperCase = 0x10,
        SmallCaps = 0x20,
        Strikethrough = 0x40,
        Superscript = 0x80,
        Subscript = 0x100,
        Highlight = 0x200,
    }

    /// <summary>
    /// Named font weights. Mirrors TextMeshPro's <c>FontWeight</c> (OpenType weight
    /// classes). Forwarded to the engine's integer <c>FontWeight</c>.
    /// </summary>
    public enum FontWeight
    {
        Thin = 100,
        ExtraLight = 200,
        Light = 300,
        Regular = 400,
        Medium = 500,
        SemiBold = 600,
        Bold = 700,
        Heavy = 800,
        Black = 900,
    }

    /// <summary>
    /// Horizontal alignment options. Mirrors TextMeshPro's
    /// <c>HorizontalAlignmentOptions</c> (same names and bit values).
    /// <see cref="Justified"/>/<see cref="Flush"/>/<see cref="Geometry"/> have no
    /// engine equivalent in Round 1 — see the parity doc.
    /// </summary>
    [Flags]
    public enum HorizontalAlignmentOptions
    {
        Left = 0x1,
        Center = 0x2,
        Right = 0x4,
        Justified = 0x8,
        Flush = 0x10,
        Geometry = 0x20,
    }

    /// <summary>
    /// Vertical alignment options. Mirrors TextMeshPro's
    /// <c>VerticalAlignmentOptions</c> (same names and bit values).
    /// </summary>
    [Flags]
    public enum VerticalAlignmentOptions
    {
        Top = 0x100,
        Middle = 0x200,
        Bottom = 0x400,
        Baseline = 0x800,
        Geometry = 0x1000,
        Capline = 0x2000,
    }

    /// <summary>
    /// Combined text alignment. Mirrors TextMeshPro's <c>TextAlignmentOptions</c>:
    /// each value is a horizontal | vertical bit combination, so the integer
    /// values match TMP exactly and migrate unchanged.
    /// </summary>
    public enum TextAlignmentOptions
    {
        TopLeft = HorizontalAlignmentOptions.Left | VerticalAlignmentOptions.Top,
        Top = HorizontalAlignmentOptions.Center | VerticalAlignmentOptions.Top,
        TopRight = HorizontalAlignmentOptions.Right | VerticalAlignmentOptions.Top,
        TopJustified = HorizontalAlignmentOptions.Justified | VerticalAlignmentOptions.Top,
        TopFlush = HorizontalAlignmentOptions.Flush | VerticalAlignmentOptions.Top,
        TopGeoAligned = HorizontalAlignmentOptions.Geometry | VerticalAlignmentOptions.Top,

        Left = HorizontalAlignmentOptions.Left | VerticalAlignmentOptions.Middle,
        Center = HorizontalAlignmentOptions.Center | VerticalAlignmentOptions.Middle,
        Right = HorizontalAlignmentOptions.Right | VerticalAlignmentOptions.Middle,
        Justified = HorizontalAlignmentOptions.Justified | VerticalAlignmentOptions.Middle,
        Flush = HorizontalAlignmentOptions.Flush | VerticalAlignmentOptions.Middle,
        CenterGeoAligned = HorizontalAlignmentOptions.Geometry | VerticalAlignmentOptions.Middle,

        BottomLeft = HorizontalAlignmentOptions.Left | VerticalAlignmentOptions.Bottom,
        Bottom = HorizontalAlignmentOptions.Center | VerticalAlignmentOptions.Bottom,
        BottomRight = HorizontalAlignmentOptions.Right | VerticalAlignmentOptions.Bottom,
        BottomJustified = HorizontalAlignmentOptions.Justified | VerticalAlignmentOptions.Bottom,
        BottomFlush = HorizontalAlignmentOptions.Flush | VerticalAlignmentOptions.Bottom,
        BottomGeoAligned = HorizontalAlignmentOptions.Geometry | VerticalAlignmentOptions.Bottom,

        BaselineLeft = HorizontalAlignmentOptions.Left | VerticalAlignmentOptions.Baseline,
        Baseline = HorizontalAlignmentOptions.Center | VerticalAlignmentOptions.Baseline,
        BaselineRight = HorizontalAlignmentOptions.Right | VerticalAlignmentOptions.Baseline,
        BaselineJustified = HorizontalAlignmentOptions.Justified | VerticalAlignmentOptions.Baseline,
        BaselineFlush = HorizontalAlignmentOptions.Flush | VerticalAlignmentOptions.Baseline,
        BaselineGeoAligned = HorizontalAlignmentOptions.Geometry | VerticalAlignmentOptions.Baseline,

        MidlineLeft = HorizontalAlignmentOptions.Left | VerticalAlignmentOptions.Geometry,
        Midline = HorizontalAlignmentOptions.Center | VerticalAlignmentOptions.Geometry,
        MidlineRight = HorizontalAlignmentOptions.Right | VerticalAlignmentOptions.Geometry,
        MidlineJustified = HorizontalAlignmentOptions.Justified | VerticalAlignmentOptions.Geometry,
        MidlineFlush = HorizontalAlignmentOptions.Flush | VerticalAlignmentOptions.Geometry,
        MidlineGeoAligned = HorizontalAlignmentOptions.Geometry | VerticalAlignmentOptions.Geometry,

        CaplineLeft = HorizontalAlignmentOptions.Left | VerticalAlignmentOptions.Capline,
        Capline = HorizontalAlignmentOptions.Center | VerticalAlignmentOptions.Capline,
        CaplineRight = HorizontalAlignmentOptions.Right | VerticalAlignmentOptions.Capline,
        CaplineJustified = HorizontalAlignmentOptions.Justified | VerticalAlignmentOptions.Capline,
        CaplineFlush = HorizontalAlignmentOptions.Flush | VerticalAlignmentOptions.Capline,
        CaplineGeoAligned = HorizontalAlignmentOptions.Geometry | VerticalAlignmentOptions.Capline,
    }

    /// <summary>
    /// Text overflow handling. Mirrors TextMeshPro's <c>TextOverflowModes</c>.
    /// All modes are implemented: ScrollRect behaves as Overflow (as in TMP), Page shows
    /// <c>pageToDisplay</c>, Linked hands the overflow to <c>linkedTextComponent</c>.
    /// </summary>
    public enum TextOverflowModes
    {
        Overflow = 0,
        Ellipsis = 1,
        Masking = 2,
        Truncate = 3,
        ScrollRect = 4,
        Page = 5,
        Linked = 6,
    }

    /// <summary>
    /// Text wrapping modes. Mirrors TextMeshPro's <c>TextWrappingModes</c>.
    /// NoWrap/Normal are implemented; PreserveWhitespace variants fall back
    /// (see parity doc).
    /// </summary>
    public enum TextWrappingModes
    {
        NoWrap = 0,
        Normal = 1,
        PreserveWhitespace = 2,
        PreserveWhitespaceNoWrap = 3,
    }

    /// <summary>
    /// Four-corner vertex color gradient. Mirrors TextMeshPro's
    /// <c>VertexGradient</c> field layout so assignments migrate unchanged.
    /// </summary>
    [Serializable]
    public struct VertexGradient
    {
        public Color topLeft;
        public Color topRight;
        public Color bottomLeft;
        public Color bottomRight;

        public VertexGradient(Color color)
        {
            topLeft = topRight = bottomLeft = bottomRight = color;
        }

        public VertexGradient(Color color0, Color color1, Color color2, Color color3)
        {
            topLeft = color0;
            topRight = color1;
            bottomLeft = color2;
            bottomRight = color3;
        }
    }
}
