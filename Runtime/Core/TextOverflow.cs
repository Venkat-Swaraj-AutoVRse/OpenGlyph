namespace LightSide
{
    /// <summary>
    /// How text that does not fit its container VERTICALLY is handled. Applied after line breaking;
    /// word wrapping is unaffected.
    /// </summary>
    public enum TextOverflow
    {
        /// <summary>Text spills out of the rect (the historical behaviour, and the default).</summary>
        Overflow = 0,

        /// <summary>
        /// Lines whose bottom would exceed the rect height are dropped. The first line is always kept,
        /// even when it alone does not fit.
        /// </summary>
        Truncate = 1,

        /// <summary>
        /// Like <see cref="Truncate"/>, but the last kept line ends with an ellipsis (U+2026) placed at the
        /// logical end of the text. Trailing grapheme clusters are removed until the line plus the ellipsis
        /// fits the rect width. Text that already fits gets no ellipsis.
        /// </summary>
        Ellipsis = 2,

        /// <summary>
        /// Layout is unchanged but pixels outside the RectTransform are not drawn (CanvasRenderer rect
        /// clipping, intersected with any parent RectMask2D).
        /// </summary>
        Clip = 3,
    }
}
