using System;

namespace LightSide
{
    /// <summary>Parses <![CDATA[<outline=#RRGGBB,w>text</outline>]]> per-span outline markup.</summary>
    /// <seealso cref="SpanStyleModifier"/>
    [Serializable]
    [TypeGroup("Tags", 1)]
    public sealed class OutlineParseRule : TagParseRule
    {
        protected override string TagName => "outline";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<underlay=#RRGGBBAA,x,y,dilate,softness>text</underlay>]]> per-span drop-shadow markup.</summary>
    /// <seealso cref="SpanStyleModifier"/>
    [Serializable]
    [TypeGroup("Tags", 1)]
    public sealed class UnderlayParseRule : TagParseRule
    {
        protected override string TagName => "underlay";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<dilate=v>text</dilate>]]> per-span face-dilation markup.</summary>
    /// <seealso cref="SpanStyleModifier"/>
    [Serializable]
    [TypeGroup("Tags", 1)]
    public sealed class DilateParseRule : TagParseRule
    {
        protected override string TagName => "dilate";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<softness=v>text</softness>]]> per-span edge-softness markup.</summary>
    /// <seealso cref="SpanStyleModifier"/>
    [Serializable]
    [TypeGroup("Tags", 1)]
    public sealed class SoftnessParseRule : TagParseRule
    {
        protected override string TagName => "softness";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<style=Name>text</style>]]>, resolving a named UniTextStyle from the component StyleSheet.</summary>
    /// <seealso cref="SpanStyleModifier"/>
    /// <seealso cref="UniTextStyleSheet"/>
    [Serializable]
    [TypeGroup("Tags", 1)]
    public sealed class StyleSheetParseRule : TagParseRule
    {
        protected override string TagName => "style";
        protected override bool HasParameter => true;
    }
}
