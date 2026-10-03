using System;
using System.Collections.Generic;

namespace LightSide
{
    /// <summary>
    /// Base for span tags that take either no parameter, a positional one or attribute-style key/values:
    /// <c>&lt;wave&gt;</c>, <c>&lt;wave=4&gt;</c>, <c>&lt;wave amp=4 freq=2&gt;</c> … <c>&lt;/wave&gt;</c>.
    /// The text between the tag name and <c>&gt;</c> (after <c>=</c> or whitespace) is passed to the
    /// modifier unchanged; null when the tag has none.
    /// </summary>
    [Serializable]
    public abstract class KeyValueTagParseRule : IParseRule
    {
        private readonly Stack<(int tagStart, int tagEnd, string parameter)> open = new(4);

        /// <summary>The tag name, lower case (matching is case-insensitive).</summary>
        protected abstract string TagName { get; }

        public void Reset() => open.Clear();

        public void Finalize(ReadOnlySpan<char> text, PooledList<ParsedRange> results)
        {
            while (open.Count > 0)
            {
                var o = open.Pop();
                results.Add(new ParsedRange(o.tagStart, o.tagEnd, text.Length, text.Length, o.parameter));
            }
        }

        public int TryMatch(ReadOnlySpan<char> text, int index, PooledList<ParsedRange> results)
        {
            if (text[index] != '<') return index;
            var name = TagName;
            var n = name.Length;

            // Closing tag </name>
            if (index + 1 < text.Length && text[index + 1] == '/')
            {
                var end = index + 2 + n;
                if (end >= text.Length || text[end] != '>' || !NameAt(text, index + 2, name)) return index;
                if (open.Count == 0) return index;
                var o = open.Pop();
                results.Add(new ParsedRange(o.tagStart, o.tagEnd, index, end + 1, o.parameter));
                return end + 1;
            }

            if (!NameAt(text, index + 1, name)) return index;
            var after = index + 1 + n;
            if (after >= text.Length) return index;
            var c = text[after];
            string parameter = null;
            int close;
            if (c == '>') close = after;
            else if (c == '=' || c == ' ' || c == '\t')
            {
                var rel = text.Slice(after).IndexOf('>');
                if (rel < 0) return index;
                close = after + rel;
                var p = text.Slice(after + 1, close - after - 1).Trim();
                if (p.Length >= 2 && (p[0] == '"' || p[0] == '\'') && p[p.Length - 1] == p[0]) p = p.Slice(1, p.Length - 2);
                parameter = p.IsEmpty ? null : p.ToString();
            }
            else return index;

            open.Push((index, close + 1, parameter));
            return close + 1;
        }

        private static bool NameAt(ReadOnlySpan<char> text, int at, string name)
        {
            if (at + name.Length > text.Length) return false;
            for (var i = 0; i < name.Length; i++)
            {
                var ch = text[at + i];
                if (ch >= 'A' && ch <= 'Z') ch = (char)(ch + 32);
                if (ch != name[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Splits a tag parameter into key/value pairs and positional values. Separators: whitespace and
        /// commas. <c>"amp=4 freq=2"</c> → keys; <c>"4,2"</c> → positional. Returns false for null/empty.
        /// </summary>
        public static bool TryGetValue(string parameter, string key, int position, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(parameter)) return false;
            var s = parameter.AsSpan();
            var pos = 0;
            var i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == ',' || s[i] == '\t')) i++;
                var start = i;
                while (i < s.Length && s[i] != ' ' && s[i] != ',' && s[i] != '\t') i++;
                if (i == start) break;
                var tok = s.Slice(start, i - start);
                var eq = tok.IndexOf('=');
                if (eq >= 0)
                {
                    var k = tok.Slice(0, eq);
                    if (key != null && k.Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase))
                        return float.TryParse(tok.Slice(eq + 1), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
                }
                else
                {
                    if (pos == position)
                        return float.TryParse(tok, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
                    pos++;
                }
            }
            return false;
        }
    }

    /// <summary><![CDATA[<wave>, <wave amp=4 freq=2 phase=0.6>]]>: glyphs move up and down on a sine wave.</summary>
    [Serializable, TypeGroup("Animation", 0)]
    public sealed class WaveParseRule : KeyValueTagParseRule { protected override string TagName => "wave"; }

    /// <summary><![CDATA[<shake>, <shake amp=2 freq=18>]]>: glyphs jitter randomly.</summary>
    [Serializable, TypeGroup("Animation", 0)]
    public sealed class ShakeParseRule : KeyValueTagParseRule { protected override string TagName => "shake"; }

    /// <summary><![CDATA[<pulse>, <pulse amp=0.2 freq=1.5>]]>: glyphs scale up and down.</summary>
    [Serializable, TypeGroup("Animation", 0)]
    public sealed class PulseParseRule : KeyValueTagParseRule { protected override string TagName => "pulse"; }

    /// <summary><![CDATA[<fade>, <fade min=0.2 freq=1>]]>: glyph opacity breathes.</summary>
    [Serializable, TypeGroup("Animation", 0)]
    public sealed class FadeParseRule : KeyValueTagParseRule { protected override string TagName => "fade"; }

    /// <summary><![CDATA[<rainbow>, <rainbow freq=0.5 phase=0.08>]]>: glyph colour cycles through hues.</summary>
    [Serializable, TypeGroup("Animation", 0)]
    public sealed class RainbowParseRule : KeyValueTagParseRule { protected override string TagName => "rainbow"; }

    /// <summary><![CDATA[<bounce>, <bounce amp=6 freq=1.5>]]>: glyphs hop up from the baseline.</summary>
    [Serializable, TypeGroup("Animation", 0)]
    public sealed class BounceParseRule : KeyValueTagParseRule { protected override string TagName => "bounce"; }
}
