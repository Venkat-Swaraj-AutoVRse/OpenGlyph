using System;

namespace LightSide
{
    /// <summary>
    /// <![CDATA[<lang=ja>]]>: sets the BCP 47 language of the span (overriding <see cref="UniText.Language"/>).
    /// The language is passed to HarfBuzz (language-specific glyph forms through <c>locl</c>), splits the
    /// span into its own shaping runs, and picks the matching CJK system font face (Japanese, Korean,
    /// Simplified / Traditional / Hong Kong Chinese) when no font in the stack covers a character.
    /// </summary>
    [Serializable]
    [TypeGroup("Text Style", 0)]
    public class LanguageModifier : BaseModifier
    {
        protected override void OnEnable() { }
        protected override void OnDisable() { }
        protected override void OnDestroy() { }

        protected override void OnApply(int start, int end, string parameter)
        {
            if (end <= start) return;
            var tp = uniText.TextProcessor;
            if (tp == null) return;
            tp.AddLanguageSpan(new TextProcessor.LanguageSpan { start = start, end = end, language = parameter });
        }
    }
}
