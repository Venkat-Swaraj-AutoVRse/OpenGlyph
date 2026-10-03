using System;

namespace LightSide
{
    /// <summary>Parses <![CDATA[<feature=smcp,onum,tnum,liga=0>text</feature>]]> (OpenType features for a span).</summary>
    /// <seealso cref="FeatureModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class FeatureParseRule : TagParseRule
    {
        protected override string TagName => "feature";
        protected override bool HasParameter => true;
    }
}
