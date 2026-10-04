using System;
using System.Text;

namespace LightSide
{
    /// <summary>
    /// Character validation per <see cref="InputFieldCharacterValidation"/> and sanitising of pasted or
    /// externally set text. Rules are checked against the text as it will be after the selection is
    /// replaced: <paramref name="prefix"/> = text before the insertion point, <paramref name="suffix"/> =
    /// text after the replaced selection.
    /// </summary>
    public static class InputFieldValidation
    {
        private const string EmailSpecials = "!#$%&'*+-/=?^_`{|}~";

        /// <summary>Returns the character to insert ('\0' = rejected).</summary>
        public static char Validate(InputFieldCharacterValidation validation, string prefix, string suffix, char ch) =>
            Validate(validation, (prefix ?? string.Empty).AsSpan(), (suffix ?? string.Empty).AsSpan(), ch);

        /// <summary>Span overload of <see cref="Validate(InputFieldCharacterValidation, string, string, char)"/> (no allocation).</summary>
        public static char Validate(InputFieldCharacterValidation validation, ReadOnlySpan<char> prefix, ReadOnlySpan<char> suffix, char ch)
        {
            switch (validation)
            {
                case InputFieldCharacterValidation.None:
                case InputFieldCharacterValidation.CustomValidator:
                    return ch;
                case InputFieldCharacterValidation.Digit:
                    return ch >= '0' && ch <= '9' ? ch : '\0';
                case InputFieldCharacterValidation.Integer:
                case InputFieldCharacterValidation.Decimal:
                {
                    var atStart = prefix.Length == 0;
                    var dashAfter = suffix.Length > 0 && suffix[0] == '-' && atStart;
                    if (dashAfter) return '\0'; // nothing goes in front of the sign
                    if (ch >= '0' && ch <= '9') return ch;
                    if (ch == '-') return atStart && (suffix.Length == 0 || suffix[0] != '-') ? ch : '\0';
                    if ((ch == '.' || ch == ',') && validation == InputFieldCharacterValidation.Decimal &&
                        prefix.IndexOfAny('.', ',') < 0 && suffix.IndexOfAny('.', ',') < 0)
                        return ch;
                    return '\0';
                }
                case InputFieldCharacterValidation.Alphanumeric:
                    return ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' ? ch : '\0';
                case InputFieldCharacterValidation.Name:
                {
                    var prev = prefix.Length > 0 ? prefix[prefix.Length - 1] : '\0';
                    var next = suffix.Length > 0 ? suffix[0] : '\0';
                    if (char.IsLetter(ch))
                    {
                        if (prefix.Length == 0 || prev == ' ') return char.ToUpperInvariant(ch);
                        if (prev != '\'') return char.ToLowerInvariant(ch);
                        return ch;
                    }
                    var adjacentBlocked = prev == ' ' || prev == '\'' || next == ' ' || next == '\'';
                    if (ch == '\'')
                        return prefix.IndexOf('\'') < 0 && suffix.IndexOf('\'') < 0 && !adjacentBlocked && prefix.Length > 0 ? ch : '\0';
                    if (ch == ' ')
                        return prefix.Length > 0 && !adjacentBlocked ? ch : '\0';
                    return '\0';
                }
                case InputFieldCharacterValidation.EmailAddress:
                {
                    if (ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9') return ch;
                    if (ch == '@') return prefix.IndexOf('@') < 0 && suffix.IndexOf('@') < 0 ? ch : '\0';
                    if (ch == '.')
                    {
                        var prev = prefix.Length > 0 ? prefix[prefix.Length - 1] : '\0';
                        var next = suffix.Length > 0 ? suffix[0] : '\0';
                        return prev != '.' && next != '.' ? ch : '\0';
                    }
                    return EmailSpecials.IndexOf(ch) >= 0 ? ch : '\0';
                }
            }
            return ch;
        }

        /// <summary>The validation implied by a content type (<see cref="InputFieldContentType.Custom"/> keeps <paramref name="current"/>).</summary>
        public static InputFieldCharacterValidation ValidationFor(InputFieldContentType type, InputFieldCharacterValidation current) =>
            type switch
            {
                InputFieldContentType.IntegerNumber => InputFieldCharacterValidation.Integer,
                InputFieldContentType.DecimalNumber => InputFieldCharacterValidation.Decimal,
                InputFieldContentType.Alphanumeric => InputFieldCharacterValidation.Alphanumeric,
                InputFieldContentType.Name => InputFieldCharacterValidation.Name,
                InputFieldContentType.EmailAddress => InputFieldCharacterValidation.EmailAddress,
                InputFieldContentType.Pin => InputFieldCharacterValidation.Digit,
                InputFieldContentType.Custom => current,
                _ => InputFieldCharacterValidation.None,
            };

        /// <summary>
        /// Normalises line endings to '\n' and removes control characters (C0, DEL, C1). Newlines are kept
        /// only when <paramref name="multiLine"/>. The result is cut to <paramref name="maxLength"/> UTF-16
        /// units without splitting a surrogate pair.
        /// </summary>
        public static string Sanitize(string s, bool multiLine, int maxLength = int.MaxValue)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var clean = true;
            for (var i = 0; i < s.Length; i++)
                if (IsStripped(s[i], multiLine) || s[i] == '\r') { clean = false; break; }
            if (clean && s.Length <= maxLength) return s;

            var sb = new StringBuilder(System.Math.Min(s.Length, maxLength));
            for (var i = 0; i < s.Length && sb.Length < maxLength; i++)
            {
                var c = s[i];
                if (c == '\r')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\n') continue;
                    c = '\n';
                }
                if (IsStripped(c, multiLine)) continue;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) continue; // lone surrogate
                    if (sb.Length + 2 > maxLength) break;
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsStripped(char c, bool multiLine)
        {
            if (c == '\n') return !multiLine;
            return c < 0x20 || c == 0x7F || c >= 0x80 && c <= 0x9F;
        }
    }
}
