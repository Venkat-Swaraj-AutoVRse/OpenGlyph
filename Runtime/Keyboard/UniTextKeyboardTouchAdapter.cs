using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// The built-in <see cref="UniTextKeyboard"/> behind the field's <see cref="IInputFieldTouchKeyboard"/>
    /// seam: the field opens it on focus and closes it when editing ends, exactly like a system keyboard.
    /// Unlike a system keyboard it does not keep its own copy of the text: its keys type through
    /// <see cref="UniTextInputField.ProcessText"/> / <see cref="UniTextInputField.ProcessKey"/>, so the
    /// text the field mirrors back is always the field's own (no text sync, the caret stays where it is).
    /// </summary>
    public sealed class UniTextKeyboardTouchAdapter : IInputFieldTouchKeyboard
    {
        private readonly UniTextInputField field;
        private UniTextKeyboard keyboard;
        private string text = string.Empty;

        public UniTextKeyboardTouchAdapter(UniTextInputField field) { this.field = field; }

        /// <summary>The keyboard opened for the field (null before the first open).</summary>
        public UniTextKeyboard Keyboard => keyboard;

        public bool IsSupported => true;

        public void Open(string initialText, TouchScreenKeyboardType type, bool autocorrection, bool multiline, bool secure, string placeholder, int characterLimit)
        {
            text = initialText ?? string.Empty;
            keyboard = UniTextKeyboard.ForField(field);
            if (keyboard != null) keyboard.Open(field, type, multiline, secure);
        }

        public void Close()
        {
            if (keyboard != null) keyboard.Close(field);
        }

        public bool Active => keyboard != null && keyboard.IsVisible && keyboard.Target == field;

        // The field pushes its text here after every edit; reading it back returns the same string, so the
        // field never re-applies it (that path is for system keyboards that edit their own copy).
        public string Text
        {
            get => text;
            set => text = value ?? string.Empty;
        }

        public RangeInt Selection { get; set; }
        public bool CanGetSelection => false;
        public bool CanSetSelection => false;
        public TouchScreenKeyboard.Status Status => Active ? TouchScreenKeyboard.Status.Visible : TouchScreenKeyboard.Status.LostFocus;
        public bool HideInput { get; set; }
    }
}
