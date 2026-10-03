using System;
using System.Collections.Generic;

namespace LightSide
{
    /// <summary>
    /// Parses OpenType feature settings written as text, e.g. <c>"tnum"</c>, <c>"liga=0"</c>,
    /// <c>"-kern"</c>, <c>"+smcp"</c>, <c>"ss01=1"</c>, <c>"aalt=2"</c>, <c>"liga=off"</c>.
    /// </summary>
    /// <remarks>
    /// Grammar of one entry: an optional <c>+</c> (on) or <c>-</c> (off), a 1–4 character tag made of
    /// ASCII letters, digits or spaces (shorter tags are padded with spaces, as in OpenType), then
    /// optionally <c>=</c> followed by a non-negative integer or <c>on</c>/<c>off</c>/<c>true</c>/<c>false</c>.
    /// Without a value the feature is set to 1. Entries that do not match are ignored.
    /// </remarks>
    public static class OpenTypeFeatures
    {
        /// <summary>Parses one entry (string overload of <see cref="TryParse(ReadOnlySpan{char}, out uint, out uint)"/>).</summary>
        public static bool TryParse(string text, out uint tag, out uint value) =>
            TryParse((text ?? string.Empty).AsSpan(), out tag, out value);

        /// <summary>Parses a list (string overload of <see cref="ParseList(ReadOnlySpan{char}, List{uint}, List{uint})"/>).</summary>
        public static int ParseList(string list, List<uint> tags, List<uint> values) =>
            ParseList((list ?? string.Empty).AsSpan(), tags, values);

        /// <summary>Parses one entry. Returns false (and a default feature) when the text is not a feature setting.</summary>
        public static bool TryParse(ReadOnlySpan<char> text, out uint tag, out uint value)
        {
            tag = 0;
            value = 1;
            var s = text.Trim();
            if (s.IsEmpty) return false;

            if (s[0] == '+') s = s.Slice(1).TrimStart();
            else if (s[0] == '-') { value = 0; s = s.Slice(1).TrimStart(); }

            var eq = s.IndexOf('=');
            var name = (eq >= 0 ? s.Slice(0, eq) : s).Trim();
            if (name.Length < 1 || name.Length > 4) return false;
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' '))
                    return false;
            }

            if (eq >= 0)
            {
                var v = s.Slice(eq + 1).Trim();
                if (v.IsEmpty) return false;
                if (v.Equals("on".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    v.Equals("true".AsSpan(), StringComparison.OrdinalIgnoreCase)) value = 1;
                else if (v.Equals("off".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                         v.Equals("false".AsSpan(), StringComparison.OrdinalIgnoreCase)) value = 0;
                else
                {
                    uint n = 0;
                    for (var i = 0; i < v.Length; i++)
                    {
                        var c = v[i];
                        if (c < '0' || c > '9') return false;
                        n = n * 10 + (uint)(c - '0');
                        if (n > 0xFFFF) return false;
                    }
                    value = n;
                }
            }

            uint t = 0;
            for (var i = 0; i < 4; i++)
                t = (t << 8) | (i < name.Length ? name[i] : ' ');
            tag = t;
            return true;
        }

        /// <summary>
        /// Parses a comma-, semicolon- or whitespace-separated list (the <c>&lt;feature=…&gt;</c> tag
        /// parameter) and appends every valid entry to <paramref name="tags"/>/<paramref name="values"/>.
        /// Returns the number of entries added.
        /// </summary>
        public static int ParseList(ReadOnlySpan<char> list, List<uint> tags, List<uint> values)
        {
            var added = 0;
            var i = 0;
            while (i < list.Length)
            {
                while (i < list.Length && IsSeparator(list[i])) i++;
                var start = i;
                // An entry may contain spaces only around '=' ("liga = 0"); split on commas/semicolons,
                // and on whitespace that is not next to '='.
                while (i < list.Length && list[i] != ',' && list[i] != ';' && !IsBreakingSpace(list, i)) i++;
                if (i > start && TryParse(list.Slice(start, i - start), out var tag, out var value))
                {
                    tags.Add(tag);
                    values.Add(value);
                    added++;
                }
                if (i < list.Length) i++;
            }
            return added;
        }

        private static bool IsSeparator(char c) => c == ',' || c == ';' || char.IsWhiteSpace(c);

        private static bool IsBreakingSpace(ReadOnlySpan<char> s, int i)
        {
            if (!char.IsWhiteSpace(s[i])) return false;
            var p = i - 1;
            while (p >= 0 && char.IsWhiteSpace(s[p])) p--;
            var n = i + 1;
            while (n < s.Length && char.IsWhiteSpace(s[n])) n++;
            var prevIsEq = p >= 0 && s[p] == '=';
            var nextIsEq = n < s.Length && s[n] == '=';
            return !prevIsEq && !nextIsEq;
        }

        /// <summary>Formats a tag as its four characters (trailing spaces trimmed), e.g. 0x746E756D → "tnum".</summary>
        public static string TagToString(uint tag)
        {
            Span<char> c = stackalloc char[4];
            c[0] = (char)((tag >> 24) & 0xFF);
            c[1] = (char)((tag >> 16) & 0xFF);
            c[2] = (char)((tag >> 8) & 0xFF);
            c[3] = (char)(tag & 0xFF);
            return new string(c).TrimEnd();
        }
    }
}
