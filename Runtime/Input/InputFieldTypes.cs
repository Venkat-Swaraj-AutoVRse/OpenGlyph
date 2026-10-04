using System;
using UnityEngine;
using UnityEngine.Events;

namespace LightSide
{
    /// <summary>What an input field accepts, and how it is shown and typed (sets line type, input type,
    /// keyboard type and character validation together, like a uGUI/TMP input field).</summary>
    public enum InputFieldContentType
    {
        /// <summary>Any text.</summary>
        Standard = 0,
        /// <summary>Any text; the touch keyboard autocorrects.</summary>
        Autocorrected = 1,
        /// <summary>Digits and a leading minus sign.</summary>
        IntegerNumber = 2,
        /// <summary>Digits, a leading minus sign and one decimal separator ('.' or ',').</summary>
        DecimalNumber = 3,
        /// <summary>ASCII letters and digits.</summary>
        Alphanumeric = 4,
        /// <summary>Letters, single spaces and one apostrophe; each word is capitalised.</summary>
        Name = 5,
        /// <summary>The characters of an e-mail address (one '@', no consecutive dots).</summary>
        EmailAddress = 6,
        /// <summary>Any text, shown as mask characters.</summary>
        Password = 7,
        /// <summary>Digits only, shown as mask characters.</summary>
        Pin = 8,
        /// <summary>Line type, input type, keyboard type and validation are set individually.</summary>
        Custom = 9,
    }

    /// <summary>Line handling of an input field.</summary>
    public enum InputFieldLineType
    {
        /// <summary>One line, scrolled horizontally. Enter submits; newlines are never inserted.</summary>
        SingleLine = 0,
        /// <summary>Wrapped lines, scrolled vertically. Enter submits; newlines are never inserted.</summary>
        MultiLineSubmit = 1,
        /// <summary>Wrapped lines, scrolled vertically. Enter inserts a newline.</summary>
        MultiLineNewline = 2,
    }

    /// <summary>How the text is shown.</summary>
    public enum InputFieldInputType
    {
        /// <summary>The text as typed.</summary>
        Standard = 0,
        /// <summary>The text as typed; the touch keyboard autocorrects.</summary>
        AutoCorrect = 1,
        /// <summary>One mask character per grapheme; the touch keyboard is secure.</summary>
        Password = 2,
    }

    /// <summary>Per-character validation applied to typed and pasted text.</summary>
    public enum InputFieldCharacterValidation
    {
        None = 0,
        /// <summary>Digits only.</summary>
        Digit = 1,
        Integer = 2,
        Decimal = 3,
        Alphanumeric = 4,
        Name = 5,
        EmailAddress = 6,
        /// <summary>Only <see cref="UniTextInputField.onValidateInput"/> decides.</summary>
        CustomValidator = 7,
    }

    /// <summary>Built-in caret shapes. A custom caret object can replace them (<see cref="UniTextInputField.CustomCaret"/>).</summary>
    public enum InputFieldCaretShape
    {
        /// <summary>A vertical bar of <see cref="UniTextInputField.CaretWidth"/> (default).</summary>
        Bar = 0,
        /// <summary>A box over the next grapheme.</summary>
        Block = 1,
        /// <summary>A line under the next grapheme.</summary>
        Underline = 2,
    }

    /// <summary>Editing keys understood by <see cref="UniTextInputField.ProcessKey"/>. Backends translate
    /// the Input Manager / Input System keys into these.</summary>
    public enum InputFieldKey
    {
        None = 0,
        Left, Right, Up, Down, Home, End, PageUp, PageDown,
        Backspace, Delete, Enter, KeypadEnter, Tab, Escape,
        A, C, V, X, Y, Z, Insert,
    }

    /// <summary>Modifier keys held with an <see cref="InputFieldKey"/>.</summary>
    [Flags]
    public enum InputFieldModifiers
    {
        None = 0,
        Shift = 1,
        Control = 2,
        Alt = 4,
        /// <summary>Command (macOS) / Windows key.</summary>
        Command = 8,
    }

    /// <summary>Event with the field's text.</summary>
    [Serializable] public class InputFieldTextEvent : UnityEvent<string> { }

    /// <summary>Event with the field's text and the selection (string indices, start &lt;= end).</summary>
    [Serializable] public class InputFieldSelectionEvent : UnityEvent<string, int, int> { }

    /// <summary>
    /// Character filter: return the character to insert (possibly changed) or '\0' to reject it.
    /// <paramref name="charIndex"/> is the string index the character would be inserted at.
    /// </summary>
    public delegate char InputFieldValidateInput(string text, int charIndex, char addedChar);

    /// <summary>Clipboard used by cut/copy/paste. The default is <see cref="GUIUtility.systemCopyBuffer"/>.</summary>
    public interface IInputFieldClipboard
    {
        string Text { get; set; }
    }

    /// <summary>The system clipboard (<see cref="GUIUtility.systemCopyBuffer"/>).</summary>
    public sealed class SystemInputFieldClipboard : IInputFieldClipboard
    {
        public static readonly SystemInputFieldClipboard Instance = new();
        public string Text
        {
            get => GUIUtility.systemCopyBuffer;
            set => GUIUtility.systemCopyBuffer = value;
        }
    }

    /// <summary>An in-app clipboard (a VR app without OS clipboard access, tests, sandboxed kiosks).</summary>
    public sealed class InputFieldMemoryClipboard : IInputFieldClipboard
    {
        public string Text { get; set; } = string.Empty;
    }
}
