using System;
using UnityEngine;
#if OPENGLYPH_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace LightSide
{
    /// <summary>
    /// Keyboard source of a focused <see cref="UniTextInputField"/>. Once per frame the field calls
    /// <see cref="Poll"/>, which delivers this frame's input through the field's public seam:
    /// <see cref="UniTextInputField.ProcessKey"/>, <see cref="UniTextInputField.ProcessText"/> and
    /// <see cref="UniTextInputField.SetComposition"/>. Implement it to feed a field from a custom device
    /// (an in-VR keyboard, a remote, a test).
    /// </summary>
    public interface IInputFieldKeyboardSource
    {
        /// <summary>The field gained keyboard focus (enable IME, subscribe to text events).</summary>
        void Begin(UniTextInputField field);
        /// <summary>The field lost keyboard focus.</summary>
        void End(UniTextInputField field);
        /// <summary>Deliver this frame's keys, characters and IME composition to the field.</summary>
        void Poll(UniTextInputField field);
        /// <summary>Where the IME candidate window should appear (screen pixels, origin bottom-left).</summary>
        void SetImeCursor(Vector2 screenPosition);
    }

    /// <summary>
    /// The built-in keyboard sources: the Input System (<c>Keyboard.current.onTextInput</c>,
    /// <c>onIMECompositionChange</c>) when the package is installed and active, else the legacy Input
    /// Manager (<c>Input.inputString</c>, <c>Input.compositionString</c>, <c>Input.GetKey</c>). Both repeat
    /// held navigation/deletion keys after <see cref="RepeatDelay"/> at <see cref="RepeatRate"/> per second.
    /// </summary>
    public static class InputFieldKeyboardSources
    {
        /// <summary>Seconds a key is held before it repeats.</summary>
        public static float RepeatDelay = 0.5f;
        /// <summary>Repeats per second while held.</summary>
        public static float RepeatRate = 30f;

        /// <summary>The default source for this build and project input settings, or null when no keyboard input is available.</summary>
        public static IInputFieldKeyboardSource CreateDefault()
        {
#if OPENGLYPH_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            return new InputSystemKeyboardSource();
#elif ENABLE_LEGACY_INPUT_MANAGER
            return new LegacyKeyboardSource();
#else
            return null;
#endif
        }

        /// <summary>Which backend <see cref="CreateDefault"/> uses in this build: "InputSystem", "Legacy" or "None".</summary>
        public static string DefaultBackendName
        {
            get
            {
#if OPENGLYPH_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
                return "InputSystem";
#elif ENABLE_LEGACY_INPUT_MANAGER
                return "Legacy";
#else
                return "None";
#endif
            }
        }

        /// <summary>Test/replay hook: when set, the Shift state used for Shift+click selection.</summary>
        public static Func<bool> ShiftOverride;

        /// <summary>Whether Shift is held (for Shift+click extending the selection).</summary>
        public static bool ShiftHeldForPointer()
        {
            if (ShiftOverride != null) return ShiftOverride();
#if OPENGLYPH_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.shiftKey.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#else
            return false;
#endif
        }

        internal static readonly InputFieldKey[] Keys =
        {
            InputFieldKey.Left, InputFieldKey.Right, InputFieldKey.Up, InputFieldKey.Down, InputFieldKey.Home, InputFieldKey.End,
            InputFieldKey.PageUp, InputFieldKey.PageDown, InputFieldKey.Backspace, InputFieldKey.Delete, InputFieldKey.Enter,
            InputFieldKey.KeypadEnter, InputFieldKey.Tab, InputFieldKey.Escape, InputFieldKey.A, InputFieldKey.C, InputFieldKey.V,
            InputFieldKey.X, InputFieldKey.Y, InputFieldKey.Z, InputFieldKey.Insert,
        };

        internal static bool Repeats(InputFieldKey k) =>
            k is InputFieldKey.Left or InputFieldKey.Right or InputFieldKey.Up or InputFieldKey.Down or InputFieldKey.Backspace
                or InputFieldKey.Delete or InputFieldKey.PageUp or InputFieldKey.PageDown or InputFieldKey.Z or InputFieldKey.Y
                or InputFieldKey.V;

        /// <summary>Key-repeat bookkeeping shared by the built-in sources (no allocation per frame).</summary>
        internal sealed class Repeater
        {
            private InputFieldKey held;
            private float nextRepeat;

            public void Reset() { held = InputFieldKey.None; }

            /// <summary>Call for each key with its state; returns true when the key should be processed this frame.</summary>
            public bool Step(InputFieldKey key, bool pressedThisFrame, bool isDown, float now)
            {
                if (pressedThisFrame)
                {
                    held = Repeats(key) ? key : InputFieldKey.None;
                    nextRepeat = now + RepeatDelay;
                    return true;
                }
                if (held != key) return false;
                if (!isDown) { held = InputFieldKey.None; return false; }
                if (now < nextRepeat) return false;
                nextRepeat = now + 1f / Mathf.Max(1f, RepeatRate);
                return true;
            }
        }

        /// <summary>Characters that the text streams deliver for keys handled as keys (Enter, Backspace, Tab, Esc, Ctrl+letters).</summary>
        internal static bool IsTextChar(char c) => c >= 0x20 && c != 0x7F && !(c >= 0x80 && c <= 0x9F);

#if ENABLE_LEGACY_INPUT_MANAGER
        /// <summary>Legacy Input Manager source.</summary>
        public sealed class LegacyKeyboardSource : IInputFieldKeyboardSource
        {
            private readonly Repeater repeater = new();
            private bool composing;
            private int beginFrame = -1;

            private static KeyCode Code(InputFieldKey k) => k switch
            {
                InputFieldKey.Left => KeyCode.LeftArrow,
                InputFieldKey.Right => KeyCode.RightArrow,
                InputFieldKey.Up => KeyCode.UpArrow,
                InputFieldKey.Down => KeyCode.DownArrow,
                InputFieldKey.Home => KeyCode.Home,
                InputFieldKey.End => KeyCode.End,
                InputFieldKey.PageUp => KeyCode.PageUp,
                InputFieldKey.PageDown => KeyCode.PageDown,
                InputFieldKey.Backspace => KeyCode.Backspace,
                InputFieldKey.Delete => KeyCode.Delete,
                InputFieldKey.Enter => KeyCode.Return,
                InputFieldKey.KeypadEnter => KeyCode.KeypadEnter,
                InputFieldKey.Tab => KeyCode.Tab,
                InputFieldKey.Escape => KeyCode.Escape,
                InputFieldKey.A => KeyCode.A,
                InputFieldKey.C => KeyCode.C,
                InputFieldKey.V => KeyCode.V,
                InputFieldKey.X => KeyCode.X,
                InputFieldKey.Y => KeyCode.Y,
                InputFieldKey.Z => KeyCode.Z,
                InputFieldKey.Insert => KeyCode.Insert,
                _ => KeyCode.None,
            };

            public void Begin(UniTextInputField field)
            {
                repeater.Reset();
                composing = false;
                // The key that focused the field (Enter on a selected field) must not act on it in the same frame.
                beginFrame = Time.frameCount;
                Input.imeCompositionMode = field.ReadOnly || field.InputType == InputFieldInputType.Password
                    ? IMECompositionMode.Off : IMECompositionMode.On;
            }

            public void End(UniTextInputField field)
            {
                Input.imeCompositionMode = IMECompositionMode.Auto;
                composing = false;
            }

            public void SetImeCursor(Vector2 screenPosition)
            {
                // Input.compositionCursorPos is top-left based.
                Input.compositionCursorPos = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            }

            public void Poll(UniTextInputField field)
            {
                var now = UniTextInputField.Now;
                // IME composition (only queried while an IME is selected or a composition is open).
                if (composing || Input.imeIsSelected)
                {
                    var comp = Input.compositionString;
                    if (!string.IsNullOrEmpty(comp) || composing)
                    {
                        field.SetComposition(comp);
                        composing = !string.IsNullOrEmpty(comp);
                    }
                }

                if (!Input.anyKey && !Input.anyKeyDown) { repeater.Reset(); return; }
                if (Time.frameCount == beginFrame && Application.isPlaying) return;

                var mods = InputFieldModifiers.None;
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) mods |= InputFieldModifiers.Shift;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) mods |= InputFieldModifiers.Control;
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr)) mods |= InputFieldModifiers.Alt;
                if (Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand)) mods |= InputFieldModifiers.Command;

                // Text first (typed before any navigation key of the same frame), skipping shortcuts.
                var shortcut = (mods & (InputFieldModifiers.Control | InputFieldModifiers.Command)) != 0 && (mods & InputFieldModifiers.Alt) == 0;
                var s = Input.inputString;
                if (!shortcut && !string.IsNullOrEmpty(s))
                    for (var i = 0; i < s.Length; i++)
                        if (IsTextChar(s[i])) field.ProcessChar(s[i]);

                for (var i = 0; i < Keys.Length; i++)
                {
                    var k = Keys[i];
                    var code = Code(k);
                    var letter = k is InputFieldKey.A or InputFieldKey.C or InputFieldKey.V or InputFieldKey.X or InputFieldKey.Y or InputFieldKey.Z;
                    if (letter && !shortcut) continue;
                    if (k == InputFieldKey.Insert && mods == InputFieldModifiers.None) continue;
                    if (repeater.Step(k, Input.GetKeyDown(code), Input.GetKey(code), now))
                        field.ProcessKey(k, mods);
                    if (!field.IsFocused) return;
                }
            }
        }
#endif

#if OPENGLYPH_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
        /// <summary>Input System source: <c>Keyboard.current</c> text and IME events plus key states.</summary>
        public sealed class InputSystemKeyboardSource : IInputFieldKeyboardSource
        {
            private readonly Repeater repeater = new();
            private Keyboard keyboard;
            private readonly char[] queue = new char[256];
            private int queued;
            private string composition = string.Empty;
            private bool compositionChanged;
            private readonly System.Action<char> onText;
            private readonly System.Action<IMECompositionString> onIme;
            // Key states seen at the previous poll: a press is an up -> down change between polls, so a key
            // that was already held when the field got focus (Enter that submitted into it) is not a press.
            private readonly bool[] wasDown = new bool[Keys.Length];

            public InputSystemKeyboardSource()
            {
                onText = c => { if (queued < queue.Length) queue[queued++] = c; };
                onIme = s => { composition = s.ToString(); compositionChanged = true; };
            }

            private static Key Code(InputFieldKey k) => k switch
            {
                InputFieldKey.Left => Key.LeftArrow,
                InputFieldKey.Right => Key.RightArrow,
                InputFieldKey.Up => Key.UpArrow,
                InputFieldKey.Down => Key.DownArrow,
                InputFieldKey.Home => Key.Home,
                InputFieldKey.End => Key.End,
                InputFieldKey.PageUp => Key.PageUp,
                InputFieldKey.PageDown => Key.PageDown,
                InputFieldKey.Backspace => Key.Backspace,
                InputFieldKey.Delete => Key.Delete,
                InputFieldKey.Enter => Key.Enter,
                InputFieldKey.KeypadEnter => Key.NumpadEnter,
                InputFieldKey.Tab => Key.Tab,
                InputFieldKey.Escape => Key.Escape,
                InputFieldKey.A => Key.A,
                InputFieldKey.C => Key.C,
                InputFieldKey.V => Key.V,
                InputFieldKey.X => Key.X,
                InputFieldKey.Y => Key.Y,
                InputFieldKey.Z => Key.Z,
                InputFieldKey.Insert => Key.Insert,
                _ => Key.None,
            };

            private void Attach(UniTextInputField field)
            {
                var kb = Keyboard.current;
                if (kb == keyboard) return;
                Detach();
                keyboard = kb;
                if (keyboard == null) return;
                keyboard.onTextInput += onText;
                keyboard.onIMECompositionChange += onIme;
                var ime = !field.ReadOnly && field.InputType != InputFieldInputType.Password;
                keyboard.SetIMEEnabled(ime);
            }

            private void Detach()
            {
                if (keyboard == null) return;
                keyboard.onTextInput -= onText;
                keyboard.onIMECompositionChange -= onIme;
                keyboard.SetIMEEnabled(false);
                keyboard = null;
            }

            public void Begin(UniTextInputField field)
            {
                repeater.Reset();
                queued = 0;
                composition = string.Empty;
                compositionChanged = false;
                Attach(field);
                for (var i = 0; i < Keys.Length; i++) wasDown[i] = keyboard != null && keyboard[Code(Keys[i])].isPressed;
            }

            public void End(UniTextInputField field)
            {
                Detach();
                queued = 0;
            }

            public void SetImeCursor(Vector2 screenPosition)
            {
                keyboard?.SetIMECursorPosition(screenPosition);
            }

            public void Poll(UniTextInputField field)
            {
                Attach(field);
                var kb = keyboard;
                if (kb == null) { queued = 0; return; }
                var now = UniTextInputField.Now;

                var mods = InputFieldModifiers.None;
                if (kb.shiftKey.isPressed) mods |= InputFieldModifiers.Shift;
                if (kb.ctrlKey.isPressed) mods |= InputFieldModifiers.Control;
                if (kb.altKey.isPressed) mods |= InputFieldModifiers.Alt;
                if (kb.leftMetaKey.isPressed || kb.rightMetaKey.isPressed) mods |= InputFieldModifiers.Command;
                var shortcut = (mods & (InputFieldModifiers.Control | InputFieldModifiers.Command)) != 0 && (mods & InputFieldModifiers.Alt) == 0;

                if (compositionChanged)
                {
                    compositionChanged = false;
                    field.SetComposition(composition);
                }
                if (queued > 0)
                {
                    var n = queued;
                    queued = 0;
                    if (!shortcut)
                        for (var i = 0; i < n; i++)
                            if (IsTextChar(queue[i])) field.ProcessChar(queue[i]);
                }

                if (!kb.anyKey.isPressed && !kb.anyKey.wasReleasedThisFrame)
                {
                    repeater.Reset();
                    for (var i = 0; i < wasDown.Length; i++) wasDown[i] = false;
                    return;
                }
                for (var i = 0; i < Keys.Length; i++)
                {
                    var k = Keys[i];
                    var control = kb[Code(k)];
                    var down = control.isPressed;
                    // Pressed since the last poll (also a press and release inside one update).
                    var pressed = !wasDown[i] && (down || control.wasPressedThisFrame);
                    wasDown[i] = down;
                    var letter = k is InputFieldKey.A or InputFieldKey.C or InputFieldKey.V or InputFieldKey.X or InputFieldKey.Y or InputFieldKey.Z;
                    if (letter && !shortcut) continue;
                    if (k == InputFieldKey.Insert && mods == InputFieldModifiers.None) continue;
                    if (repeater.Step(k, pressed, down, now))
                        field.ProcessKey(k, mods);
                    if (!field.IsFocused) return;
                }
            }
        }
#endif
    }

    /// <summary>
    /// The on-screen keyboard of mobile platforms, behind an interface so it can be replaced (tests, a custom
    /// in-VR keyboard, a wrapper around a vendor keyboard SDK). The default wraps <see cref="TouchScreenKeyboard"/>.
    /// On Meta Quest under OpenXR, <see cref="TouchScreenKeyboard"/> reports itself visible but shows nothing
    /// (the Meta system keyboard needs the Meta XR SDK's Virtual Keyboard); fields use the built-in
    /// <see cref="UniTextKeyboard"/> there (<see cref="InputFieldSoftKeyboard.Auto"/>).
    /// </summary>
    public interface IInputFieldTouchKeyboard
    {
        /// <summary>Whether a touch/system keyboard exists on this platform.</summary>
        bool IsSupported { get; }
        /// <summary>Opens (or re-targets) the keyboard.</summary>
        void Open(string text, TouchScreenKeyboardType type, bool autocorrection, bool multiline, bool secure, string placeholder, int characterLimit);
        /// <summary>Closes the keyboard.</summary>
        void Close();
        /// <summary>The keyboard is open.</summary>
        bool Active { get; }
        /// <summary>The keyboard's text (the platform edits its own copy; the field mirrors it).</summary>
        string Text { get; set; }
        /// <summary>The keyboard's selection, when the platform reports one (<see cref="CanGetSelection"/>).</summary>
        RangeInt Selection { get; set; }
        bool CanGetSelection { get; }
        bool CanSetSelection { get; }
        TouchScreenKeyboard.Status Status { get; }
        /// <summary><see cref="TouchScreenKeyboard.hideInput"/>: hide the platform's own input line above the keyboard (iOS/Android).</summary>
        bool HideInput { get; set; }
    }

    /// <summary><see cref="TouchScreenKeyboard"/> as an <see cref="IInputFieldTouchKeyboard"/>.</summary>
    public sealed class SystemTouchKeyboard : IInputFieldTouchKeyboard
    {
        private TouchScreenKeyboard keyboard;

        public bool IsSupported => TouchScreenKeyboard.isSupported;

        public void Open(string text, TouchScreenKeyboardType type, bool autocorrection, bool multiline, bool secure, string placeholder, int characterLimit)
        {
            keyboard = TouchScreenKeyboard.Open(text ?? string.Empty, type, autocorrection, multiline, secure, false, placeholder ?? string.Empty, characterLimit);
        }

        public void Close()
        {
            if (keyboard != null) keyboard.active = false;
            keyboard = null;
        }

        public bool Active => keyboard != null && keyboard.active;

        public string Text
        {
            get => keyboard != null ? keyboard.text : string.Empty;
            set { if (keyboard != null) keyboard.text = value; }
        }

        public RangeInt Selection
        {
            get => keyboard != null ? keyboard.selection : default;
            set { if (keyboard != null && keyboard.canSetSelection) keyboard.selection = value; }
        }

        public bool CanGetSelection => keyboard != null && keyboard.canGetSelection;
        public bool CanSetSelection => keyboard != null && keyboard.canSetSelection;
        public TouchScreenKeyboard.Status Status => keyboard != null ? keyboard.status : TouchScreenKeyboard.Status.Done;

        public bool HideInput
        {
            get => TouchScreenKeyboard.hideInput;
            set => TouchScreenKeyboard.hideInput = value;
        }
    }

    /// <summary>
    /// A touch keyboard that exists only in memory: lets the mobile / Quest system-keyboard flow (open on
    /// focus, text and selection sync, Done / Cancel / lost focus) run in the Editor and in tests. Script
    /// what the user "types" with <see cref="Text"/>, <see cref="Selection"/> and <see cref="Status"/>.
    /// </summary>
    public sealed class SimulatedTouchKeyboard : IInputFieldTouchKeyboard
    {
        public bool IsSupported { get; set; } = true;
        public bool Active { get; set; }
        public string Text { get; set; } = string.Empty;
        public RangeInt Selection { get; set; }
        public bool CanGetSelection { get; set; } = true;
        public bool CanSetSelection { get; set; } = true;
        public TouchScreenKeyboard.Status Status { get; set; } = TouchScreenKeyboard.Status.Visible;
        public bool HideInput { get; set; }

        /// <summary>Arguments of the last <see cref="Open"/>.</summary>
        public TouchScreenKeyboardType OpenedType { get; private set; }
        public bool OpenedAutocorrection { get; private set; }
        public bool OpenedMultiline { get; private set; }
        public bool OpenedSecure { get; private set; }
        public string OpenedPlaceholder { get; private set; }
        public int OpenedCharacterLimit { get; private set; }
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }

        public void Open(string text, TouchScreenKeyboardType type, bool autocorrection, bool multiline, bool secure, string placeholder, int characterLimit)
        {
            Text = text ?? string.Empty;
            OpenedType = type;
            OpenedAutocorrection = autocorrection;
            OpenedMultiline = multiline;
            OpenedSecure = secure;
            OpenedPlaceholder = placeholder;
            OpenedCharacterLimit = characterLimit;
            Selection = new RangeInt(Text.Length, 0);
            Status = TouchScreenKeyboard.Status.Visible;
            Active = true;
            OpenCount++;
        }

        public void Close()
        {
            Active = false;
            CloseCount++;
        }
    }
}

namespace LightSide
{
    /// <summary>
    /// Platform facts the input field uses to pick an on-screen keyboard (<see cref="InputFieldSoftKeyboard.Auto"/>).
    /// </summary>
    public static class InputFieldPlatform
    {
        /// <summary>Test / replay hook: when set, overrides <see cref="XRActive"/>.</summary>
        public static Func<bool> XRActiveOverride;

        /// <summary>Whether an XR display is running (OpenXR, Oculus, PC VR): <c>XRSettings.isDeviceActive</c>.</summary>
        public static bool XRActive
        {
            get
            {
                if (XRActiveOverride != null) return XRActiveOverride();
#if OPENGLYPH_VR
                return UnityEngine.XR.XRSettings.isDeviceActive;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Whether <paramref name="keyboard"/> can actually appear: it is supported, and it is not the default
        /// <see cref="TouchScreenKeyboard"/> wrapper while an XR device is active (under OpenXR on Quest it
        /// reports <c>visible</c> but draws nothing; PC VR has none). A custom implementation (e.g. a wrapper
        /// around the Meta XR Virtual Keyboard) is trusted.
        /// </summary>
        public static bool SystemKeyboardUsable(IInputFieldTouchKeyboard keyboard) =>
            keyboard != null && keyboard.IsSupported && !(keyboard is SystemTouchKeyboard && XRActive);
    }
}
