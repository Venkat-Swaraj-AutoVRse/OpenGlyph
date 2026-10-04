// OpenGlyph GlyphMeshProInputField — a TMP_InputField-shaped editable field.
//
// Clean-room adapter: TMP_InputField member *names* are mirrored so migration is a rename; every member
// forwards to LightSide.UniTextInputField (the engine field this class derives from), the same pattern as
// GlyphMeshProUGUI over UniText. No TMP source is copied. Gaps: Documentation/GlyphMeshPro-Parity.md.

using System;
using LightSide;
using UnityEngine;
using UnityEngine.UI;

namespace OpenGlyph
{
    /// <summary>
    /// Input field with the <c>TMP_InputField</c> public API over <see cref="UniTextInputField"/>. Positions
    /// follow TMP: <see cref="caretPosition"/> / <see cref="selectionAnchorPosition"/> /
    /// <see cref="selectionFocusPosition"/> count characters (codepoints), the <c>string*</c> variants count
    /// UTF-16 units. The text component is a <see cref="GlyphMeshProUGUI"/> (or world-space
    /// <see cref="GlyphMeshPro"/>).
    /// </summary>
    [AddComponentMenu("")] // created through GameObject > UI > OpenGlyph
    [DisallowMultipleComponent]
    public class GlyphMeshProInputField : UniTextInputField
    {
        /// <summary>Mirrors <c>TMP_InputField.ContentType</c>.</summary>
        public new enum ContentType { Standard, Autocorrected, IntegerNumber, DecimalNumber, Alphanumeric, Name, EmailAddress, Password, Pin, Custom }

        /// <summary>Mirrors <c>TMP_InputField.InputType</c>.</summary>
        public new enum InputType { Standard, AutoCorrect, Password }

        /// <summary>Mirrors <c>TMP_InputField.CharacterValidation</c>. <see cref="Regex"/> is not supported (treated as no validation; use <see cref="UniTextInputField.onValidateInput"/>).</summary>
        public new enum CharacterValidation { None, Digit, Integer, Decimal, Alphanumeric, Name, Regex, EmailAddress, CustomValidator }

        /// <summary>Mirrors <c>TMP_InputField.LineType</c>.</summary>
        public new enum LineType { SingleLine, MultiLineSubmit, MultiLineNewline }

        [SerializeField] private CharacterValidation m_TmpValidation = CharacterValidation.None;

        /// <summary>Builds a GlyphMeshPro input field (GlyphMeshProUGUI text on a Canvas, GlyphMeshPro in world space).</summary>
        public static GlyphMeshProInputField CreateGlyphMeshPro(Transform parent, bool world = false, Vector2? size = null,
            string placeholderText = "Enter text...", string name = "InputField (GlyphMeshPro)") =>
            (GlyphMeshProInputField)CreateInternal(parent, world, size, placeholderText, name, typeof(GlyphMeshProInputField),
                world ? typeof(GlyphMeshPro) : typeof(GlyphMeshProUGUI));

        // ---- text and positions --------------------------------------------------------------------

        /// <summary>Mirrors <c>TMP_InputField.text</c>.</summary>
        public string text { get => Text; set => Text = value; }

        /// <summary>Mirrors <c>TMP_InputField.caretPosition</c> (character index; collapses the selection).</summary>
        public int caretPosition
        {
            get => ToChar(CaretPosition);
            set => CaretPosition = ToStringIndex(value);
        }

        /// <summary>Mirrors <c>TMP_InputField.stringPosition</c> (UTF-16 index).</summary>
        public int stringPosition
        {
            get => CaretPosition;
            set => CaretPosition = value;
        }

        /// <summary>Mirrors <c>TMP_InputField.selectionAnchorPosition</c> (character index).</summary>
        public int selectionAnchorPosition
        {
            get => ToChar(SelectionAnchorPosition);
            set => SelectionAnchorPosition = ToStringIndex(value);
        }

        /// <summary>Mirrors <c>TMP_InputField.selectionFocusPosition</c> (character index).</summary>
        public int selectionFocusPosition
        {
            get => ToChar(SelectionFocusPosition);
            set => SelectionFocusPosition = ToStringIndex(value);
        }

        /// <summary>Mirrors <c>TMP_InputField.selectionStringAnchorPosition</c>.</summary>
        public int selectionStringAnchorPosition
        {
            get => SelectionAnchorPosition;
            set => SelectionAnchorPosition = value;
        }

        /// <summary>Mirrors <c>TMP_InputField.selectionStringFocusPosition</c>.</summary>
        public int selectionStringFocusPosition
        {
            get => SelectionFocusPosition;
            set => SelectionFocusPosition = value;
        }

        private int ToChar(int stringIndex)
        {
            var s = Text;
            stringIndex = Mathf.Clamp(stringIndex, 0, s.Length);
            var n = 0;
            for (var i = 0; i < stringIndex; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                n++;
            }
            return n;
        }

        private int ToStringIndex(int charIndex)
        {
            var s = Text;
            var n = 0;
            var i = 0;
            while (i < s.Length && n < charIndex)
            {
                i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                n++;
            }
            return i;
        }

        // ---- configuration -------------------------------------------------------------------------

        /// <summary>Mirrors <c>TMP_InputField.characterLimit</c> (UTF-16 units, as TMP counts string length).</summary>
        public int characterLimit { get => CharacterLimit; set => CharacterLimit = value; }

        /// <summary>Mirrors <c>TMP_InputField.contentType</c>.</summary>
        public ContentType contentType
        {
            get => (ContentType)(int)base.ContentType;
            set
            {
                base.ContentType = (InputFieldContentType)(int)value;
                m_TmpValidation = FromEngine(base.CharacterValidation);
            }
        }

        /// <summary>Mirrors <c>TMP_InputField.lineType</c>.</summary>
        public LineType lineType
        {
            get => (LineType)(int)base.LineType;
            set => base.LineType = (InputFieldLineType)(int)value;
        }

        /// <summary>Mirrors <c>TMP_InputField.inputType</c>.</summary>
        public InputType inputType
        {
            get => (InputType)(int)base.InputType;
            set => base.InputType = (InputFieldInputType)(int)value;
        }

        /// <summary>Mirrors <c>TMP_InputField.keyboardType</c>.</summary>
        public TouchScreenKeyboardType keyboardType { get => KeyboardType; set => KeyboardType = value; }

        /// <summary>Mirrors <c>TMP_InputField.characterValidation</c>.</summary>
        public CharacterValidation characterValidation
        {
            get => base.ContentType == InputFieldContentType.Custom ? m_TmpValidation : FromEngine(base.CharacterValidation);
            set
            {
                m_TmpValidation = value;
                base.CharacterValidation = value switch
                {
                    CharacterValidation.Digit => InputFieldCharacterValidation.Digit,
                    CharacterValidation.Integer => InputFieldCharacterValidation.Integer,
                    CharacterValidation.Decimal => InputFieldCharacterValidation.Decimal,
                    CharacterValidation.Alphanumeric => InputFieldCharacterValidation.Alphanumeric,
                    CharacterValidation.Name => InputFieldCharacterValidation.Name,
                    CharacterValidation.EmailAddress => InputFieldCharacterValidation.EmailAddress,
                    CharacterValidation.CustomValidator => InputFieldCharacterValidation.CustomValidator,
                    _ => InputFieldCharacterValidation.None, // None, Regex (unsupported)
                };
            }
        }

        private static CharacterValidation FromEngine(InputFieldCharacterValidation v) => v switch
        {
            InputFieldCharacterValidation.Digit => CharacterValidation.Digit,
            InputFieldCharacterValidation.Integer => CharacterValidation.Integer,
            InputFieldCharacterValidation.Decimal => CharacterValidation.Decimal,
            InputFieldCharacterValidation.Alphanumeric => CharacterValidation.Alphanumeric,
            InputFieldCharacterValidation.Name => CharacterValidation.Name,
            InputFieldCharacterValidation.EmailAddress => CharacterValidation.EmailAddress,
            InputFieldCharacterValidation.CustomValidator => CharacterValidation.CustomValidator,
            _ => CharacterValidation.None,
        };

        /// <summary>Mirrors <c>TMP_InputField.readOnly</c>.</summary>
        public bool readOnly { get => ReadOnly; set => ReadOnly = value; }

        /// <summary>Mirrors <c>TMP_InputField.richText</c>.</summary>
        public bool richText { get => RichText; set => RichText = value; }

        /// <summary>Mirrors <c>TMP_InputField.multiLine</c>.</summary>
        public bool multiLine => MultiLine;

        /// <summary>Mirrors <c>TMP_InputField.isFocused</c>.</summary>
        public bool isFocused => IsFocused;

        /// <summary>Mirrors <c>TMP_InputField.placeholder</c>.</summary>
        public Graphic placeholder { get => Placeholder; set => Placeholder = value; }

        /// <summary>Mirrors <c>TMP_InputField.textComponent</c> (a GlyphMeshProUGUI / GlyphMeshPro, or any UniText).</summary>
        public UniText textComponent { get => TextComponent; set => TextComponent = value; }

        /// <summary>Mirrors <c>TMP_InputField.textViewport</c>.</summary>
        public RectTransform textViewport { get => TextViewport; set => TextViewport = value; }

        /// <summary>Mirrors <c>TMP_InputField.pointSize</c>: font size of the text and the placeholder.</summary>
        public float pointSize
        {
            get => TextComponent != null ? TextComponent.FontSize : 0f;
            set
            {
                if (TextComponent != null) TextComponent.FontSize = value;
                if (Placeholder is UniText p) p.FontSize = value;
            }
        }

        /// <summary>Mirrors <c>TMP_InputField.caretBlinkRate</c>.</summary>
        public float caretBlinkRate { get => CaretBlinkRate; set => CaretBlinkRate = value; }

        /// <summary>Mirrors <c>TMP_InputField.caretWidth</c> (local units).</summary>
        public int caretWidth { get => Mathf.RoundToInt(CaretWidth); set => CaretWidth = value; }

        /// <summary>Mirrors <c>TMP_InputField.caretColor</c>.</summary>
        public Color caretColor { get => CaretColor; set => CaretColor = value; }

        /// <summary>Mirrors <c>TMP_InputField.customCaretColor</c>.</summary>
        public bool customCaretColor { get => CustomCaretColor; set => CustomCaretColor = value; }

        /// <summary>Mirrors <c>TMP_InputField.selectionColor</c>.</summary>
        public Color selectionColor { get => SelectionColor; set => SelectionColor = value; }

        /// <summary>Mirrors <c>TMP_InputField.asteriskChar</c>.</summary>
        public char asteriskChar { get => AsteriskChar; set => AsteriskChar = value; }

        /// <summary>Mirrors <c>TMP_InputField.onFocusSelectAll</c>.</summary>
        public bool onFocusSelectAll { get => OnFocusSelectAll; set => OnFocusSelectAll = value; }

        /// <summary>Mirrors <c>TMP_InputField.resetOnDeActivation</c>.</summary>
        public bool resetOnDeActivation { get => ResetOnDeActivation; set => ResetOnDeActivation = value; }

        /// <summary>Mirrors <c>TMP_InputField.restoreOriginalTextOnEscape</c>.</summary>
        public bool restoreOriginalTextOnEscape { get => RestoreOriginalTextOnEscape; set => RestoreOriginalTextOnEscape = value; }

        /// <summary>Mirrors <c>TMP_InputField.shouldHideMobileInput</c>.</summary>
        public bool shouldHideMobileInput { get => HideMobileInput; set => HideMobileInput = value; }

        /// <summary>Mirrors <c>TMP_InputField.shouldHideSoftKeyboard</c>.</summary>
        public bool shouldHideSoftKeyboard { get => HideSoftKeyboard; set => HideSoftKeyboard = value; }

        // ---- methods -------------------------------------------------------------------------------

        /// <summary>Mirrors <c>TMP_InputField.MoveToEndOfLine(bool shift, bool ctrl)</c>: ctrl = end of the text.</summary>
        public void MoveToEndOfLine(bool shift, bool ctrl)
        {
            if (ctrl) MoveTextEnd(shift); else MoveToEndOfLine(shift);
        }

        /// <summary>Mirrors <c>TMP_InputField.MoveToStartOfLine(bool shift, bool ctrl)</c>: ctrl = start of the text.</summary>
        public void MoveToStartOfLine(bool shift, bool ctrl)
        {
            if (ctrl) MoveTextStart(shift); else MoveToStartOfLine(shift);
        }

        /// <summary>
        /// Mirrors <c>TMP_InputField.ProcessEvent(Event)</c>: applies an IMGUI key event (keyCode, character,
        /// modifiers) as a keystroke.
        /// </summary>
        public void ProcessEvent(Event e)
        {
            if (e == null || e.type != EventType.KeyDown) return;
            var mods = InputFieldModifiers.None;
            if ((e.modifiers & EventModifiers.Shift) != 0) mods |= InputFieldModifiers.Shift;
            if ((e.modifiers & EventModifiers.Control) != 0) mods |= InputFieldModifiers.Control;
            if ((e.modifiers & EventModifiers.Alt) != 0) mods |= InputFieldModifiers.Alt;
            if ((e.modifiers & EventModifiers.Command) != 0) mods |= InputFieldModifiers.Command;
            var key = e.keyCode switch
            {
                KeyCode.LeftArrow => InputFieldKey.Left,
                KeyCode.RightArrow => InputFieldKey.Right,
                KeyCode.UpArrow => InputFieldKey.Up,
                KeyCode.DownArrow => InputFieldKey.Down,
                KeyCode.Home => InputFieldKey.Home,
                KeyCode.End => InputFieldKey.End,
                KeyCode.PageUp => InputFieldKey.PageUp,
                KeyCode.PageDown => InputFieldKey.PageDown,
                KeyCode.Backspace => InputFieldKey.Backspace,
                KeyCode.Delete => InputFieldKey.Delete,
                KeyCode.Return => InputFieldKey.Enter,
                KeyCode.KeypadEnter => InputFieldKey.KeypadEnter,
                KeyCode.Tab => InputFieldKey.Tab,
                KeyCode.Escape => InputFieldKey.Escape,
                KeyCode.A => InputFieldKey.A,
                KeyCode.C => InputFieldKey.C,
                KeyCode.V => InputFieldKey.V,
                KeyCode.X => InputFieldKey.X,
                KeyCode.Y => InputFieldKey.Y,
                KeyCode.Z => InputFieldKey.Z,
                KeyCode.Insert => InputFieldKey.Insert,
                _ => InputFieldKey.None,
            };
            if (key != InputFieldKey.None && ProcessKey(key, mods)) return;
            var c = e.character;
            var shortcut = (mods & (InputFieldModifiers.Control | InputFieldModifiers.Command)) != 0 && (mods & InputFieldModifiers.Alt) == 0;
            if (c == '\0' || shortcut || c < 0x20 || c == 0x7F) return;
            ProcessChar(c);
        }
    }
}
