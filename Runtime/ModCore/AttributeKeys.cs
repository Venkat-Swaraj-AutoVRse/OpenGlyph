
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
    }
}
