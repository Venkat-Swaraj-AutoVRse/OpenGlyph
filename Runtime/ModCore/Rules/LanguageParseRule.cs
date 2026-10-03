using System;

namespace LightSide
{
    /// <summary>Parses <![CDATA[<lang=ja>text</lang>]]> (BCP 47 language for a span).</summary>
    /// <seealso cref="LanguageModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class LanguageParseRule : TagParseRule
    {
        protected override string TagName => "lang";
        protected override bool HasParameter => true;
    }
}
