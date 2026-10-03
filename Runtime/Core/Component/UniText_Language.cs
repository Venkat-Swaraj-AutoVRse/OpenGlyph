using System;
using System.Collections.Generic;

namespace LightSide
{
    // BCP 47 language of the text (UniText.Language). <lang> spans: LanguageModifier.
    public partial class UniText
    {
        [NonSerialized] private readonly List<CjkLanguage> prepareLanguages = new(4);

        /// <summary>
        /// BCP 47 language of the text, e.g. <c>"ja"</c>, <c>"ko"</c>, <c>"zh-Hans"</c>, <c>"zh-Hant"</c>,
        /// <c>"zh-HK"</c>, <c>"sr"</c>, <c>"tr"</c>. Empty/null = unset (the previous, language-less behaviour).
        /// Passed to HarfBuzz for language-specific glyph forms (<c>locl</c>), and used to pick the CJK system
        /// font face for this component when the font stack does not cover a character.
        /// <c>&lt;lang=…&gt;</c> spans override it. Setting it reshapes the text.
        /// </summary>
        public string Language
        {
            get => language ?? "";
            set
            {
                var v = LanguageTags.Normalize(value) ?? "";
                if (string.Equals(language ?? "", v, StringComparison.Ordinal)) return;
                language = v;
                SetDirty(DirtyFlags.Text);
            }
        }

        partial void ConfigureLanguage(TextProcessor processor) => processor.Language = language;

        /// <summary>
        /// CJK languages this text uses (the component language and every <c>&lt;lang=…&gt;</c> tag in the
        /// source), so the main-thread prepare step loads the matching system faces before workers run.
        /// </summary>
        private List<CjkLanguage> CollectCjkLanguages(ReadOnlySpan<char> source)
        {
            prepareLanguages.Clear();
            prepareLanguages.Add(LanguageTags.ClassifyCjk(language));
            const string open = "<lang=";
            var i = 0;
            while (i < source.Length)
            {
                var at = source.Slice(i).IndexOf(open.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                var s = i + at + open.Length;
                var e = s;
                while (e < source.Length && source[e] != '>') e++;
                var tag = source.Slice(s, e - s).Trim().Trim('"').Trim('\'').ToString();
                var cjk = LanguageTags.ClassifyCjk(tag);
                if (!prepareLanguages.Contains(cjk)) prepareLanguages.Add(cjk);
                i = e;
            }
            return prepareLanguages;
        }
    }
}
