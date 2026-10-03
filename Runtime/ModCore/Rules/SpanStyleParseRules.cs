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

    /// <summary>Parses <![CDATA[<glow=#RRGGBBAA,size,softness,intensity>text</glow>]]> per-span soft outer glow.</summary>
    /// <seealso cref="SpanStyleModifier"/>
    [Serializable]
    [TypeGroup("Tags", 1)]
    public sealed class GlowParseRule : TagParseRule
    {
        protected override string TagName => "glow";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<innershadow=#RRGGBBAA,x,y,dilate,softness>text</innershadow>]]> per-span inner shadow.</summary>
    /// <seealso cref="SpanStyleModifier"/>
    [Serializable]
    [TypeGroup("Tags", 1)]
    public sealed class InnerShadowParseRule : TagParseRule
    {
        protected override string TagName => "innershadow";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<outline2=#RRGGBBAA,width,softness>text</outline2>]]>: a second stroke band outside the outline.</summary>
    /// <seealso cref="SpanStyleModifier"/>
    [Serializable]
    [TypeGroup("Tags", 1)]
    public sealed class Outline2ParseRule : TagParseRule
    {
        protected override string TagName => "outline2";
        protected override bool HasParameter => true;
    }
}
