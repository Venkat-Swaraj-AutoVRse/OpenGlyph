using System;
using NUnit.Framework;
using UnityEngine;
#if OPENGLYPH_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace LightSide.Tests
{
    /// <summary>
    /// Keyboard backends: the compile-time choice (Input System when the package is installed and active,
    /// else the legacy Input Manager), the Input System source driven by a virtual keyboard (text, keys,
    /// IME composition events), and Selectable/Tab navigation between fields.
    /// </summary>
    public class Wave4BackendTests : Wave4TestBase
    {
        [Test]
        public void DefaultBackend_FollowsTheProjectInputHandling()
        {
            var name = (string)W4.Get(W4.Type("LightSide.InputFieldKeyboardSources"), "DefaultBackendName");
            var src = W4.Call(W4.Type("LightSide.InputFieldKeyboardSources"), "CreateDefault");
            Log($"default keyboard backend: {name} ({src?.GetType().Name ?? "null"})");
#if OPENGLYPH_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            Assert.AreEqual("InputSystem", name, "Input System package installed and active");
#elif ENABLE_LEGACY_INPUT_MANAGER
            Assert.AreEqual("Legacy", name, "legacy Input Manager only");
#else
            Assert.AreEqual("None", name);
#endif
            if (name != "None")
            {
                Assert.IsNotNull(src);
                // A focused field with the real backend polls without throwing when no key is pressed.
                var f = MakeField("abc");
                f.Set("KeyboardSource", src);
                f.Tick();
                f.Tick();
                Assert.AreEqual("abc", f.Text);
                f.Call("DeactivateInputField", false);
            }
        }

#if OPENGLYPH_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
        [Test]
        public void InputSystem_VirtualKeyboard_TextKeysAndIme_ReachTheField()
        {
            var settings = InputSystem.settings;
            var savedMode = settings.updateMode;
            var savedBackground = settings.backgroundBehavior;
            var savedEditor = settings.editorInputBehaviorInPlayMode;
            settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            var kb = InputSystem.AddDevice<Keyboard>("W4TestKeyboard");
            try
            {
                kb.MakeCurrent();
                InputSystem.Update();
                var f = MakeField("", activate: false);
                f.Set("KeyboardSource", W4.Call(W4.Type("LightSide.InputFieldKeyboardSources"), "CreateDefault"));
                f.Call("ActivateInputField");

                foreach (var ch in "hi\U0001F44D") InputSystem.QueueTextEvent(kb, ch);
                InputSystem.Update();
                f.Tick();
                Assert.AreEqual("hi\U0001F44D", f.Text, "onTextInput characters (surrogate pair joined)");

                InputSystem.QueueStateEvent(kb, new KeyboardState(Key.LeftArrow));
                InputSystem.Update();
                Log($"left isPressed={kb.leftArrowKey.isPressed} wasPressed={kb.leftArrowKey.wasPressedThisFrame} anyKey={kb.anyKey.isPressed} current={Keyboard.current == kb}");
                f.Tick();
                Assert.AreEqual(2, f.Caret, "LeftArrow press moved over the emoji");
                InputSystem.QueueStateEvent(kb, new KeyboardState());
                InputSystem.Update();
                f.Tick();

                InputSystem.QueueStateEvent(kb, new KeyboardState(Key.Backspace));
                InputSystem.Update();
                f.Tick();
                Assert.AreEqual("h\U0001F44D", f.Text, "Backspace");
                InputSystem.QueueStateEvent(kb, new KeyboardState());
                InputSystem.Update();
                f.Tick();

                // Ctrl+A is a shortcut, its text event (if any) is not typed.
                InputSystem.QueueStateEvent(kb, new KeyboardState(Key.LeftCtrl, Key.A));
                InputSystem.Update();
                f.Tick();
                Assert.AreEqual((0, 3), (f.SelStart, f.SelEnd), "Ctrl+A selects all");
                InputSystem.QueueStateEvent(kb, new KeyboardState());
                InputSystem.Update();
                f.Tick();

                // IME composition event, then the committed text.
                f.Key("End");
                var ime = IMECompositionEvent.Create(kb.deviceId, "\u306B\u307B", InputState.currentTime);
                InputSystem.QueueEvent(ref ime);
                InputSystem.Update();
                f.Tick();
                Assert.AreEqual("h\U0001F44D\u306B\u307B", f.Displayed, "composition shown inline");
                Assert.AreEqual("h\U0001F44D", f.Text);
                var end = IMECompositionEvent.Create(kb.deviceId, "", InputState.currentTime);
                InputSystem.QueueEvent(ref end);
                InputSystem.QueueTextEvent(kb, '\u65E5');
                InputSystem.Update();
                f.Tick();
                Assert.AreEqual("h\U0001F44D\u65E5", f.Text, "committed");
                f.Call("DeactivateInputField", false);
            }
            finally
            {
                InputSystem.RemoveDevice(kb);
                settings.updateMode = savedMode;
                settings.backgroundBehavior = savedBackground;
                settings.editorInputBehaviorInPlayMode = savedEditor;
            }
        }
#endif

        [Test]
        public void Tab_MovesFocusToTheNextField_ShiftTabBack()
        {
            var a = MakeField("first", 300, 50);
            var b = MakeField("second", 300, 50, activate: false);
            ((RectTransform)a.C.transform).anchoredPosition = new Vector2(0, 100);
            ((RectTransform)b.C.transform).anchoredPosition = new Vector2(0, 0);
            Assert.IsTrue(a.Focused);
            a.Key("Tab");
            Assert.IsFalse(a.Focused, "Tab leaves the first field");
            Assert.IsTrue(b.Focused, "and focuses the field below");
            b.Key("Tab", "Shift");
            Assert.IsTrue(a.Focused, "Shift+Tab goes back");
            a.Set("TabNavigation", false);
            Assert.IsFalse(a.Key("Tab"), "Tab not handled when navigation is off");
            Assert.IsTrue(a.Focused);
        }
    }
}
