using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace LightSide
{
    // BCP 47 language: the component language and <lang> spans. Passed to HarfBuzz per run and used to pick
    // the CJK system font face. Unset by default, so plain shaping and itemization are unchanged.
    public sealed partial class TextProcessor
    {
        /// <summary>A language applied to a codepoint range (<c>&lt;lang&gt;</c> span).</summary>
        public struct LanguageSpan
        {
            public int start;
            public int end;
            public string language;
        }

        private string language;
        private readonly List<LanguageSpan> languageSpans = new();

        // Language table for the current first pass: index 0 = the component language (may be null),
        // 1..n = distinct span languages. Per-codepoint index and per-language derived data.
        private readonly List<string> languageTable = new();
        private readonly List<byte[]> languageAscii = new();
        private readonly List<CjkLanguage> languageCjk = new();
        private byte[] cpLanguage;

        /// <summary>
        /// Default BCP 47 language of the text (e.g. "ja", "zh-Hant", "sr"), or null/empty for none. Passed to
        /// HarfBuzz for language-specific forms (locl) and used to pick the CJK system font face. A change
        /// requires a first-pass rebuild.
        /// </summary>
        public string Language
        {
            get => language;
            set => language = LanguageTags.Normalize(value);
        }

        /// <summary>Registers a language span for this text (called by <see cref="LanguageModifier"/> while parsing).</summary>
        public void AddLanguageSpan(LanguageSpan span)
        {
            span.language = LanguageTags.Normalize(span.language);
            languageSpans.Add(span);
        }

        /// <summary>The language that applies at codepoint <paramref name="index"/> after the last first pass.</summary>
        public string LanguageAt(int index)
        {
            if (cpLanguage != null && (uint)index < (uint)buf.codepoints.count && (uint)index < (uint)cpLanguage.Length)
            {
                var li = cpLanguage[index];
                if (li < languageTable.Count) return languageTable[li];
            }
            return language;
        }

        partial void OnClearTypographySpans() => languageSpans.Clear();

        /// <summary>
        /// Builds the language table and the per-codepoint language index (innermost span wins). With no
        /// language and no spans, <see cref="cpLanguage"/> stays null and itemization is unchanged.
        /// </summary>
        private void PrepareLanguages(int cpCount)
        {
            languageTable.Clear();
            languageAscii.Clear();
            languageCjk.Clear();
            AddLanguageEntry(language);

            if (languageSpans.Count == 0)
            {
                cpLanguage = null;
                return;
            }

            if (cpLanguage == null || cpLanguage.Length < cpCount)
                cpLanguage = new byte[Math.Max(cpCount, 16)];
            Array.Clear(cpLanguage, 0, cpCount);

            // Innermost wins: apply in ascending start order, later (nested) spans overwrite.
            languageSpans.Sort(static (a, b) => a.start.CompareTo(b.start));
            for (var i = 0; i < languageSpans.Count; i++)
            {
                var s = languageSpans[i];
                var idx = IndexOfLanguage(s.language);
                if (idx < 0)
                {
                    if (languageTable.Count >= byte.MaxValue) continue;
                    idx = AddLanguageEntry(s.language);
                }
                var e = Math.Min(s.end, cpCount);
                for (var c = Math.Max(0, s.start); c < e; c++) cpLanguage[c] = (byte)idx;
            }
        }

        private int IndexOfLanguage(string tag)
        {
            for (var i = 0; i < languageTable.Count; i++)
                if (string.Equals(languageTable[i], tag, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private int AddLanguageEntry(string tag)
        {
            languageTable.Add(tag);
            languageAscii.Add(string.IsNullOrEmpty(tag) ? null : Encoding.ASCII.GetBytes(tag));
            languageCjk.Add(LanguageTags.ClassifyCjk(tag));
            return languageTable.Count - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte LanguageIndexAt(int cp) =>
            cpLanguage != null && (uint)cp < (uint)cpLanguage.Length ? cpLanguage[cp] : (byte)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private CjkLanguage CjkLanguageAt(int cp)
        {
            var li = LanguageIndexAt(cp);
            return li < languageCjk.Count ? languageCjk[li] : CjkLanguage.Default;
        }

        private byte[] LanguageAsciiOf(byte index) => index < languageAscii.Count ? languageAscii[index] : null;
    }
}
