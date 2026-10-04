using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace LightSide
{
    /// <summary>What a key of a <see cref="UniTextKeyboard"/> does.</summary>
    public enum KeyboardKeyAction
    {
        /// <summary>Types <see cref="KeyboardKey.text"/> (or <see cref="KeyboardKey.shiftText"/> while shifted).</summary>
        Text = 0,
        /// <summary>Deletes the grapheme before the caret (or the selection); repeats while held.</summary>
        Backspace = 1,
        /// <summary>Enter: a newline in a multi-line (newline) field, otherwise submit.</summary>
        Enter = 2,
        /// <summary>Types a space.</summary>
        Space = 3,
        /// <summary>Shift: tap for one capital, double-tap for caps lock.</summary>
        Shift = 4,
        /// <summary>Moves the caret one grapheme left (visually).</summary>
        Left = 5,
        /// <summary>Moves the caret one grapheme right (visually).</summary>
        Right = 6,
        /// <summary>Hides the keyboard and ends editing.</summary>
        Hide = 7,
        /// <summary>Switches to page <see cref="KeyboardKey.page"/> of the layout (letters / symbols).</summary>
        Page = 8,
        /// <summary>Switches to the next layout in <see cref="UniTextKeyboard.Layouts"/> (shown only when there are several).</summary>
        NextLayout = 9,
        /// <summary>Tab (moves focus to the next field when the field allows it).</summary>
        Tab = 10,
    }

    /// <summary>One key of a <see cref="UniTextKeyboardLayout"/> page.</summary>
    [Serializable]
    public class KeyboardKey
    {
        [Tooltip("What the key does.")]
        public KeyboardKeyAction action;

        [Tooltip("Text shown on the key. Empty: the text it types (Text keys) or an icon (Backspace, Enter, Shift, arrows, Hide, layout switch).")]
        public string label;

        [Tooltip("Text typed by a Text key (any Unicode, also several code points: \"क्ष\", \".com\").")]
        public string text;

        [Tooltip("Text typed while shifted. Empty: the upper case of Text.")]
        public string shiftText;

        [Tooltip("Label while shifted. Empty: the shifted text.")]
        public string shiftLabel;

        [Tooltip("Width in key units (1 = a letter key).")]
        public float width = 1f;

        [Tooltip("Page shown by a Page key.")]
        public int page;

        /// <summary>The key width, at least 0.25 units (a missing JSON width counts as 1).</summary>
        public float Width => width <= 0f ? 1f : Mathf.Max(0.25f, width);

        /// <summary>The text this key types with the given shift state (Text keys).</summary>
        public string Output(bool shifted)
        {
            var t = text ?? string.Empty;
            if (!shifted) return t;
            if (!string.IsNullOrEmpty(shiftText)) return shiftText;
            return t.ToUpper(CultureInfo.InvariantCulture);
        }

        /// <summary>The label of this key with the given shift state (empty for icon keys).</summary>
        public string Label(bool shifted)
        {
            if (shifted && !string.IsNullOrEmpty(shiftLabel)) return shiftLabel;
            if (!string.IsNullOrEmpty(label))
                return shifted && action == KeyboardKeyAction.Text && string.IsNullOrEmpty(shiftText)
                    ? label.ToUpper(CultureInfo.InvariantCulture) : label;
            return action == KeyboardKeyAction.Text ? Output(shifted) : string.Empty;
        }

        public KeyboardKey Clone() => (KeyboardKey)MemberwiseClone();
    }

    /// <summary>A row of keys. <see cref="indent"/> shifts it right (in key units), e.g. 0.5 for the home row.</summary>
    [Serializable]
    public class KeyboardRow
    {
        public float indent;
        public List<KeyboardKey> keys = new();

        /// <summary>Row width in key units (indent included).</summary>
        public float Units
        {
            get
            {
                var w = Mathf.Max(0f, indent);
                if (keys != null) for (var i = 0; i < keys.Count; i++) if (keys[i] != null) w += keys[i].Width;
                return w;
            }
        }
    }

    /// <summary>A page of a layout (letters, symbols, ...).</summary>
    [Serializable]
    public class KeyboardPage
    {
        public string name;
        public List<KeyboardRow> rows = new();
    }

    /// <summary>
    /// A keyboard layout for <see cref="UniTextKeyboard"/>: pages of rows of keys. Layouts are data: create
    /// one as an asset (Assets &gt; Create &gt; OpenGlyph &gt; Keyboard Layout), load one from JSON
    /// (<see cref="FromJson"/>), or use a built-in one (<see cref="Qwerty"/>, <see cref="Email"/>,
    /// <see cref="Numeric"/>, <see cref="HindiInScript"/>, <see cref="Arabic"/>).
    /// </summary>
    /// <remarks>
    /// Keys type any Unicode text: the keyboard draws the labels with OpenGlyph, so a layout for a complex
    /// script (Devanagari, Arabic, Thai, ...) shapes correctly as long as the keyboard's font stack covers it.
    /// </remarks>
    [CreateAssetMenu(fileName = "KeyboardLayout", menuName = "OpenGlyph/Keyboard Layout")]
    public sealed class UniTextKeyboardLayout : ScriptableObject
    {
        [Tooltip("Name shown on the space bar when several layouts are available (e.g. \"English\", \"हिन्दी\").")]
        public string displayName = "Custom";

        [Tooltip("Short name (e.g. \"EN\").")]
        public string shortName = "";

        [Tooltip("BCP 47 language tag of the layout (e.g. \"hi\", \"ar\"): used for the key labels' language-specific shaping.")]
        public string language = "";

        [Tooltip("Pages; page 0 is shown first.")]
        public List<KeyboardPage> pages = new();

        /// <summary>The page at <paramref name="index"/> (clamped), or null when the layout has no pages.</summary>
        public KeyboardPage Page(int index)
        {
            if (pages == null || pages.Count == 0) return null;
            return pages[Mathf.Clamp(index, 0, pages.Count - 1)];
        }

        /// <summary>Creates a layout from JSON (the format of <see cref="ToJson"/>; <c>JsonUtility</c> field names).</summary>
        public static UniTextKeyboardLayout FromJson(string json)
        {
            var l = CreateInstance<UniTextKeyboardLayout>();
            if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, l);
            l.pages ??= new List<KeyboardPage>();
            if (string.IsNullOrEmpty(l.name)) l.name = string.IsNullOrEmpty(l.displayName) ? "Keyboard Layout" : l.displayName;
            return l;
        }

        /// <summary>This layout as JSON (load it back with <see cref="FromJson"/>).</summary>
        public string ToJson(bool pretty = true) => JsonUtility.ToJson(this, pretty);

        // ---- built-in layouts ------------------------------------------------------------------

        private static UniTextKeyboardLayout s_qwerty, s_email, s_numeric, s_hindi, s_arabic;

        /// <summary>English QWERTY: letters (Shift / caps lock) and a numbers &amp; symbols page.</summary>
        public static UniTextKeyboardLayout Qwerty => Cached(ref s_qwerty, BuildQwerty);

        /// <summary>QWERTY with an e-mail bottom row (@ . .com); chosen for e-mail fields.</summary>
        public static UniTextKeyboardLayout Email => Cached(ref s_email, BuildEmail);

        /// <summary>Numeric pad (digits, minus, decimal point); chosen for Integer, Decimal and PIN fields.</summary>
        public static UniTextKeyboardLayout Numeric => Cached(ref s_numeric, BuildNumeric);

        /// <summary>Hindi (Devanagari) InScript-lite: consonants, vowel signs and virama, Shift for independent vowels and aspirates.</summary>
        public static UniTextKeyboardLayout HindiInScript => Cached(ref s_hindi, BuildHindi);

        /// <summary>Arabic (PC layout letters).</summary>
        public static UniTextKeyboardLayout Arabic => Cached(ref s_arabic, BuildArabic);

        private static UniTextKeyboardLayout Cached(ref UniTextKeyboardLayout slot, Func<UniTextKeyboardLayout> build)
        {
            if (slot != null) return slot;
            slot = build();
            slot.hideFlags = HideFlags.HideAndDontSave;
            return slot;
        }

        private static UniTextKeyboardLayout New(string name, string display, string shortName, string language)
        {
            var l = CreateInstance<UniTextKeyboardLayout>();
            l.name = name;
            l.displayName = display;
            l.shortName = shortName;
            l.language = language;
            return l;
        }

        // Key helpers.
        private static KeyboardKey T(string text, string shift = null, float w = 1f) =>
            new() { action = KeyboardKeyAction.Text, text = text, shiftText = shift, width = w };
        private static KeyboardKey L(string label, string text, float w) =>
            new() { action = KeyboardKeyAction.Text, label = label, text = text, shiftText = text, width = w };
        private static KeyboardKey A(KeyboardKeyAction a, float w = 1f, string label = null, int page = 0) =>
            new() { action = a, width = w, label = label, page = page };

        private static KeyboardRow Row(float indent, params KeyboardKey[] keys) => new() { indent = indent, keys = new List<KeyboardKey>(keys) };

        /// <summary>A row of Text keys from the characters of <paramref name="chars"/> (one key per char).</summary>
        private static List<KeyboardKey> Chars(string chars, string shifted = null, float w = 1f)
        {
            var l = new List<KeyboardKey>(chars.Length);
            for (var i = 0; i < chars.Length; i++)
                l.Add(T(chars[i].ToString(), shifted != null && i < shifted.Length ? shifted[i].ToString() : null, w));
            return l;
        }

        private static KeyboardRow Row(float indent, List<KeyboardKey> keys, params KeyboardKey[] tail)
        {
            var r = new KeyboardRow { indent = indent, keys = keys };
            r.keys.AddRange(tail);
            return r;
        }

        private static KeyboardRow Prefix(KeyboardKey head, KeyboardRow row)
        {
            row.keys.Insert(0, head);
            return row;
        }

        private static KeyboardRow BottomRow(string pageLabel, int page, float space) =>
            Row(0f, A(KeyboardKeyAction.Page, 1.5f, pageLabel, page), A(KeyboardKeyAction.NextLayout), A(KeyboardKeyAction.Space, space),
                A(KeyboardKeyAction.Left), A(KeyboardKeyAction.Right), A(KeyboardKeyAction.Hide));

        /// <summary>The numbers &amp; symbols page shared by the text layouts (11.5 units wide).</summary>
        private static KeyboardPage SymbolsPage(string backLabel)
        {
            var p = new KeyboardPage { name = "symbols" };
            p.rows.Add(Row(0f, Chars("1234567890"), A(KeyboardKeyAction.Backspace, 1.5f)));
            p.rows.Add(Row(0.25f, Chars("@#$%&-+()"), A(KeyboardKeyAction.Enter, 2.25f)));
            p.rows.Add(Row(0f, Chars("*\"':;!?/,.", null, 1.15f)));
            p.rows.Add(BottomRow(backLabel, 0, 6f));
            return p;
        }

        private static UniTextKeyboardLayout BuildQwerty()
        {
            var l = New("QWERTY", "English", "EN", "en");
            var p = new KeyboardPage { name = "letters" };
            p.rows.Add(Row(0f, Chars("qwertyuiop"), A(KeyboardKeyAction.Backspace, 1.5f)));
            p.rows.Add(Row(0.25f, Chars("asdfghjkl"), A(KeyboardKeyAction.Enter, 2.25f)));
            p.rows.Add(Prefix(A(KeyboardKeyAction.Shift, 1.5f), Row(0f, Chars("zxcvbnm,."), A(KeyboardKeyAction.Shift))));
            p.rows[2].keys[8].shiftText = "!";
            p.rows[2].keys[9].shiftText = "?";
            p.rows.Add(BottomRow("?123", 1, 6f));
            l.pages.Add(p);
            l.pages.Add(SymbolsPage("ABC"));
            return l;
        }

        private static UniTextKeyboardLayout BuildEmail()
        {
            var l = BuildQwerty();
            l.name = "Email";
            l.displayName = "E-mail";
            l.shortName = "@";
            var bottom = Row(0f, A(KeyboardKeyAction.Page, 1.5f, "?123", 1), T("@", "@"), A(KeyboardKeyAction.Space, 2.5f), T(".", "."),
                L(".com", ".com", 1.5f), T("_", "_"), A(KeyboardKeyAction.Left), A(KeyboardKeyAction.Right), A(KeyboardKeyAction.Hide));
            l.pages[0].rows[3] = bottom;
            return l;
        }

        private static UniTextKeyboardLayout BuildNumeric()
        {
            var l = New("Numeric", "123", "123", "");
            var p = new KeyboardPage { name = "numbers" };
            p.rows.Add(Row(0f, Chars("123"), A(KeyboardKeyAction.Backspace, 1.5f)));
            p.rows.Add(Row(0f, Chars("456"), A(KeyboardKeyAction.Enter, 1.5f)));
            p.rows.Add(Row(0f, Chars("789"), A(KeyboardKeyAction.Left, 0.75f), A(KeyboardKeyAction.Right, 0.75f)));
            p.rows.Add(Row(0f, Chars("-0."), A(KeyboardKeyAction.Hide, 1.5f)));
            l.pages.Add(p);
            return l;
        }

        private static UniTextKeyboardLayout BuildHindi()
        {
            // InScript key positions (QWERTY rows), reduced to the keys Hindi needs. Shift gives the
            // independent vowels and the aspirated consonants on the same keys, as on a PC.
            var l = New("HindiInScript", "हिन्दी", "हि", "hi");
            var p = new KeyboardPage { name = "letters" };
            p.rows.Add(Row(0f, Chars("ौैाीूबहगदजड", "औऐआईऊभङघधझढ"), A(KeyboardKeyAction.Backspace, 1.5f)));
            p.rows.Add(Row(0f, Chars("ोे्िुपरकतचट", "ओएअइउफऱखथछठ"), A(KeyboardKeyAction.Enter, 1.5f)));
            p.rows.Add(Prefix(A(KeyboardKeyAction.Shift, 1.75f), Row(0f, Chars("ंमनवलस,.य", "ँणञृळशष।ऋ"), A(KeyboardKeyAction.Shift, 1.75f))));
            p.rows.Add(BottomRow("?123", 1, 7f));
            l.pages.Add(p);
            var s = SymbolsPage("कखग");
            // Devanagari digits on the number row (Shift: ASCII digits).
            var digits = Chars("१२३४५६७८९०", "1234567890");
            for (var i = 0; i < digits.Count; i++) s.rows[0].keys[i] = digits[i];
            l.pages.Add(s);
            return l;
        }

        private static UniTextKeyboardLayout BuildArabic()
        {
            var l = New("Arabic", "العربية", "ع", "ar");
            var p = new KeyboardPage { name = "letters" };
            p.rows.Add(Row(0f, Chars("ضصثقفغعهخحج"), A(KeyboardKeyAction.Backspace, 1.5f)));
            p.rows.Add(Row(0f, Chars("شسيبلاتنمكط"), A(KeyboardKeyAction.Enter, 1.5f)));
            p.rows.Add(Row(0.5f, Chars("ذئءؤرىةوزظد"), T("،", "؟", 1f)));
            p.rows.Add(BottomRow("؟١٢٣", 1, 7f));
            l.pages.Add(p);
            var s = SymbolsPage("أبج");
            // Arabic-Indic digits on the number row (Shift: ASCII digits).
            var digits = Chars("١٢٣٤٥٦٧٨٩٠", "1234567890");
            for (var i = 0; i < digits.Count; i++) s.rows[0].keys[i] = digits[i];
            l.pages.Add(s);
            return l;
        }
    }
}
