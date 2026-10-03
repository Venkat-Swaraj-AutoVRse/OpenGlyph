using System;

namespace LightSide
{
    /// <summary>The CJK writing conventions a BCP 47 language selects between (glyph forms and font face).</summary>
    public enum CjkLanguage : byte
    {
        /// <summary>No CJK language requested: the project default (<see cref="SystemFontFallback.PreferredLanguage"/>, else the UI culture).</summary>
        Default = 0,
        /// <summary>Japanese (<c>ja</c>).</summary>
        Japanese = 1,
        /// <summary>Korean (<c>ko</c>).</summary>
        Korean = 2,
        /// <summary>Simplified Chinese (<c>zh</c>, <c>zh-Hans</c>, <c>zh-CN</c>, <c>zh-SG</c>).</summary>
        SimplifiedChinese = 3,
        /// <summary>Traditional Chinese, Taiwan conventions (<c>zh-Hant</c>, <c>zh-TW</c>, <c>zh-MO</c>).</summary>
        TraditionalChinese = 4,
        /// <summary>Traditional Chinese, Hong Kong conventions (<c>zh-HK</c>, <c>yue</c>).</summary>
        HongKongChinese = 5,
    }

    /// <summary>Helpers for BCP 47 language tags (case-insensitive, '-' or '_' separators).</summary>
    public static class LanguageTags
    {
        /// <summary>
        /// Normalises a BCP 47 tag for display/comparison: trims, maps '_' to '-'. Returns null for an
        /// empty or whitespace tag (meaning "unset").
        /// </summary>
        public static string Normalize(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            tag = tag.Trim();
            return tag.IndexOf('_') >= 0 ? tag.Replace('_', '-') : tag;
        }

        /// <summary>
        /// Which CJK convention <paramref name="tag"/> asks for. <c>zh-HK</c>/<c>yue</c> → Hong Kong;
        /// <c>zh-Hant</c>/<c>zh-TW</c>/<c>zh-MO</c> → Traditional; other <c>zh</c> → Simplified (an explicit
        /// <c>-Hans</c> script subtag wins over a region). Non-CJK or empty tags → <see cref="CjkLanguage.Default"/>.
        /// </summary>
        public static CjkLanguage ClassifyCjk(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return CjkLanguage.Default;
            var s = tag.Trim().Replace('_', '-').ToLowerInvariant();
            var primary = s;
            var dash = s.IndexOf('-');
            if (dash >= 0) primary = s.Substring(0, dash);

            switch (primary)
            {
                case "ja": return CjkLanguage.Japanese;
                case "ko": return CjkLanguage.Korean;
                case "yue": return s.Contains("-hans") ? CjkLanguage.SimplifiedChinese : CjkLanguage.HongKongChinese;
                case "zh":
                case "cmn":
                    if (HasSubtag(s, "hans")) return CjkLanguage.SimplifiedChinese;
                    if (HasSubtag(s, "hk")) return CjkLanguage.HongKongChinese;
                    if (HasSubtag(s, "hant") || HasSubtag(s, "tw") || HasSubtag(s, "mo")) return CjkLanguage.TraditionalChinese;
                    return CjkLanguage.SimplifiedChinese;
                default: return CjkLanguage.Default;
            }
        }

        private static bool HasSubtag(string tag, string sub)
        {
            var i = 0;
            while (i < tag.Length)
            {
                var j = tag.IndexOf('-', i);
                if (j < 0) j = tag.Length;
                if (j - i == sub.Length && string.CompareOrdinal(tag, i, sub, 0, sub.Length) == 0) return true;
                i = j + 1;
            }
            return false;
        }
    }
}
