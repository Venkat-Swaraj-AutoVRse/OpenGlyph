using System;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Parses <![CDATA[<noparse>...</noparse>]]>: everything between the tags is shown literally — no
    /// other rule sees it (TextMeshPro <c>&lt;noparse&gt;</c>). Pair it with <see cref="EmptyModifier"/>.
    /// </summary>
    /// <remarks>
    /// Matched before every other rule (high priority). An unclosed tag makes the rest of the text
    /// literal. <see cref="CloseAtLastTag"/> closes at the LAST closing tag instead of the first, which
    /// GlyphMeshPro uses to show a whole run literally when <c>richText</c> is off.
    /// </remarks>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class NoParseParseRule : IParseRule
    {
        [SerializeField, Tooltip("Tag name (default \"noparse\").")]
        private string tagName = "noparse";

        [SerializeField, Tooltip("Close at the last closing tag in the text instead of the first.")]
        private bool closeAtLastTag;

        [NonSerialized] private string openTag;
        [NonSerialized] private string closeTag;

        /// <summary>The tag name (without brackets).</summary>
        public string TagName
        {
            get => tagName;
            set { tagName = value; openTag = null; closeTag = null; }
        }

        /// <summary>Close at the last closing tag in the text (true) or the first (false, TMP behaviour).</summary>
        public bool CloseAtLastTag
        {
            get => closeAtLastTag;
            set => closeAtLastTag = value;
        }

        public int Priority => 1000;

        public int TryMatch(ReadOnlySpan<char> text, int index, PooledList<ParsedRange> results)
        {
            if (text[index] != '<' || string.IsNullOrEmpty(tagName)) return index;
            openTag ??= "<" + tagName + ">";
            closeTag ??= "</" + tagName + ">";

            if (index + openTag.Length > text.Length) return index;
            if (!text.Slice(index, openTag.Length).Equals(openTag.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return index;

            var openEnd = index + openTag.Length;
            var rest = text.Slice(openEnd);
            var rel = closeAtLastTag
                ? LastIndexOfIgnoreCase(rest, closeTag.AsSpan())
                : rest.IndexOf(closeTag.AsSpan(), StringComparison.OrdinalIgnoreCase);

            int closeStart, closeEnd;
            if (rel < 0) { closeStart = closeEnd = text.Length; }
            else { closeStart = openEnd + rel; closeEnd = closeStart + closeTag.Length; }

            results.Add(new ParsedRange(index, openEnd, closeStart, closeEnd));
            return closeEnd > index ? closeEnd : index + 1;
        }

        private static int LastIndexOfIgnoreCase(ReadOnlySpan<char> text, ReadOnlySpan<char> value)
        {
            for (var i = text.Length - value.Length; i >= 0; i--)
                if (text.Slice(i, value.Length).Equals(value, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
    }
}
