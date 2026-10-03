
namespace LightSide
{
    public static class AttributeKeys
    {
        public const string Color = "color";
        public const string Bold = "bold";
        public const string Italic = "italic";
        public const string Underline = "underline";
        public const string Strikethrough = "strikethrough";
        public const string Ellipsis = "ellipsis";
        public const string Size = "size";
        public const string LetterSpacing = "cspace";
        public const string InteractiveRanges = "interactiveRanges";
        public const string Gradient = "gradient";
        // Render-Architecture R2 sub-task 2: per-span style overrides (outline/underlay/dilate/softness/style)
        // all accumulate into ONE shared per-cluster buffer under this key so nested spans merge.
        public const string SpanStyle = "spanstyle";
        // TMP-parity round 3.
        /// <summary>Per-codepoint byte: 1 = shape with the OpenType <c>smcp</c> feature (real small caps).</summary>
        public const string SmallCapsFeature = "smallcapsfeature";
        /// <summary>Per-codepoint float: synthetic small-caps scale (0 = none) for lowercase letters shown as capitals.</summary>
        public const string SmallCapsScale = "smallcapsscale";
        /// <summary>Per-codepoint int: 1-based index into <see cref="TextProcessor"/>'s span-font table (<c>&lt;font&gt;</c>).</summary>
        public const string FontOverride = "fontoverride";
        /// <summary>Per-codepoint byte: <c>(byte)HorizontalAlignment + 1</c> per-paragraph override (<c>&lt;align&gt;</c>); 0 = none.</summary>
        public const string LineAlignment = "linealign";
        /// <summary>Per-codepoint packed RGBA highlight colour (<c>&lt;mark&gt;</c>); 0 = none.</summary>
        public const string Mark = "mark";
    }
}
