using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;

namespace LightSide.Tests
{
    /// <summary>
    /// Scrolling, world-space fields (ray caret placement), the touch / system keyboard flow, zero
    /// allocation while idle, the GlyphMeshProInputField TMP-shaped API and the read-only selectable text.
    /// </summary>
    public class Wave4FieldTests : Wave4TestBase
    {
        private static Rect ViewportInTextLocal(Field f)
        {
            var vp = f.Viewport;
            var tt = f.TextComponent.transform;
            var r = vp.rect;
            var a = tt.InverseTransformPoint(vp.TransformPoint(r.min));
            var b = tt.InverseTransformPoint(vp.TransformPoint(r.max));
            return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
        }

        [Test]
        public void SingleLine_ScrollsHorizontally_ToKeepTheCaretVisible()
        {
            var f = MakeField("", 220, 60);
            f.Chars("The quick brown fox jumps over the lazy dog");
            Update();
            var vr = ViewportInTextLocal(f);
            var c = f.CaretRect;
            Log($"scroll {f.Scroll} caret {c} viewport(text local) {vr}");
            Assert.Greater(f.Scroll.x, 100f, "long text scrolled left");
            Assert.GreaterOrEqual(c.center.x, vr.xMin - 0.5f, "caret inside the viewport");
            Assert.LessOrEqual(c.center.x, vr.xMax + 0.5f);
            f.Key("Home");
            Update();
            Assert.AreEqual(0f, f.Scroll.x, 0.5f, "Home scrolls back to the start");
            vr = ViewportInTextLocal(f);
            Assert.GreaterOrEqual(f.CaretRect.center.x, vr.xMin - 0.5f);
            // Moving right through the text keeps the caret visible at every step.
            for (var i = 0; i < 43; i++)
            {
                f.Key("Right");
                vr = ViewportInTextLocal(f);
                var x = f.CaretRect.center.x;
                Assert.IsTrue(x >= vr.xMin - 0.5f && x <= vr.xMax + 0.5f, $"step {i}: caret {x} outside {vr}");
            }
            // Deleting everything scrolls back.
            f.Key("A", "Control");
            f.Key("Backspace");
            Update();
            Assert.AreEqual(0f, f.Scroll.x, 0.5f);
            Assert.IsNotNull(f.Viewport.GetComponent<UnityEngine.UI.RectMask2D>(), "the Canvas viewport clips with RectMask2D");
        }

        [Test]
        public void MultiLine_ScrollsVertically_ToKeepTheCaretVisible()
        {
            var f = MakeField("", 300, 110, "MultiLineNewline");
            for (var i = 0; i < 10; i++) { f.Chars("line " + i); f.Key("Enter"); }
            f.Chars("last");
            Update();
            var vr = ViewportInTextLocal(f);
            var c = f.CaretRect;
            Log($"vscroll {f.Scroll} caret {c} viewport {vr}");
            Assert.Greater(f.Scroll.y, 100f, "scrolled down");
            Assert.GreaterOrEqual(c.yMin, vr.yMin - 0.5f, "caret line inside the viewport");
            Assert.LessOrEqual(c.yMax, vr.yMax + 0.5f);
            f.Key("Home", "Control");
            Update();
            Assert.AreEqual(0f, f.Scroll.y, 0.5f, "Ctrl+Home scrolls to the top");
            vr = ViewportInTextLocal(f);
            Assert.LessOrEqual(f.CaretRect.yMax, vr.yMax + 0.5f);
            f.Key("Down"); f.Key("Down"); f.Key("Down"); f.Key("Down");
            vr = ViewportInTextLocal(f);
            Assert.GreaterOrEqual(f.CaretRect.yMin, vr.yMin - 0.5f, "line 4 scrolled into view");
            f.Key("PageDown");
            Assert.Greater(f.Caret, 30, "PageDown moves by the visible lines");
        }

        [Test]
        public void WorldField_RayAndXrRaycast_PlaceTheCaret_AtAnAngle()
        {
            var root = new GameObject("world root");
            _junk.Add(root);
            var f = MakeField("Hello world", 400, 60, world: true, parent: root.transform);
            var go = f.C.gameObject;
            go.transform.position = new Vector3(0.3f, 1.2f, 2f);
            go.transform.rotation = Quaternion.Euler(10f, 30f, 0f);
            go.transform.localScale = Vector3.one * 0.005f;
            Update();
            var t = f.TextComponent;
            Assert.IsTrue(t.IsWorldText, "the text is a UniTextWorld (no Canvas)");
            Assert.IsNull(t.canvas);
            Assert.IsNotNull(go.GetComponent<BoxCollider>(), "world fields keep a collider for PhysicsRaycaster / XRI");

            var eye = new Vector3(0f, 1.5f, 0f);
            foreach (var (cluster, fx, expected) in new[] { (6, 0.25f, 6), (6, 0.75f, 7), (0, 0.1f, 0), (10, 0.9f, 11), (3, 0.3f, 3) })
            {
                var target = t.rectTransform.TransformPoint(GlyphLocal(t, cluster, fx));
                var ok = (bool)f.Call("SetCaretFromRay", new Ray(eye, (target - eye).normalized), false, 1);
                Assert.IsTrue(ok);
                Assert.AreEqual(expected, f.Caret, $"ray at cluster {cluster} x{fx}");
            }

            // EventSystem path: the raycast hit point of an XR ray (TrackedDevicePhysicsRaycaster) on the collider.
            var hit = t.rectTransform.TransformPoint(GlyphLocal(t, 8, 0.6f)) + go.transform.forward * -0.001f;
            var e = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                clickCount = 2,
                pointerCurrentRaycast = new RaycastResult { gameObject = go, worldPosition = hit, worldNormal = -go.transform.forward },
            };
            ((IPointerDownHandler)f.C).OnPointerDown(e);
            Assert.AreEqual((6, 11), (f.SelStart, f.SelEnd), "XR double-click selects 'world'");
            var rects = f.SelectionRects();
            Assert.AreEqual(1, rects.Count, "world selection drawn by the world overlay");
            Assert.IsNotNull(go.GetComponentInChildren(W4.Type("LightSide.UniTextInputWorldOverlay")), "world selection is drawn by a MeshRenderer overlay");

            // Scroll + object-space clip on world text.
            f.Key("End");
            f.Chars(" and a very long tail that overflows the field");
            Update();
            Assert.Greater(f.Scroll.x, 10f, "world field scrolls too");
        }

        [Test]
        public void TouchKeyboard_OpensWithContentTypeOptions_AndSyncsBack()
        {
            var f = MakeField("", activate: false);
            var tk = Activator.CreateInstance(W4.Type("LightSide.SimulatedTouchKeyboard"));
            f.Set("TouchKeyboard", tk);
            f.SetEnum("ContentType", "LightSide.InputFieldContentType", "Password");
            f.Set("CharacterLimit", 12);
            f.Call("ActivateInputField");
            Assert.AreEqual(1, (int)W4.Get(tk, "OpenCount"), "keyboard opened on focus");
            Assert.IsTrue((bool)W4.Get(tk, "OpenedSecure"), "secure for passwords");
            Assert.IsFalse((bool)W4.Get(tk, "OpenedMultiline"));
            Assert.AreEqual(12, (int)W4.Get(tk, "OpenedCharacterLimit"));
            Assert.AreEqual("Enter text...", W4.Get(tk, "OpenedPlaceholder"));
            f.Call("DeactivateInputField", false);

            f.SetEnum("ContentType", "LightSide.InputFieldContentType", "IntegerNumber");
            f.Call("ActivateInputField");
            Assert.AreEqual(TouchScreenKeyboardType.NumberPad, W4.Get(tk, "OpenedType"));
            // The user types on the system keyboard: the field mirrors it (validated).
            W4.Set(tk, "Text", "12a3");
            W4.Set(tk, "Selection", new RangeInt(4, 0));
            f.Tick();
            Assert.AreEqual("123", f.Text, "validated keyboard text");
            Assert.AreEqual("123", W4.Get(tk, "Text"), "corrected text pushed back to the keyboard");
            // Done submits.
            var submits = new List<string>();
            ((UnityEngine.Events.UnityEvent<string>)f.Get("onSubmit")).AddListener(submits.Add);
            W4.Set(tk, "Status", TouchScreenKeyboard.Status.Done);
            W4.Set(tk, "Active", false);
            f.Tick();
            CollectionAssert.AreEqual(new[] { "123" }, submits);
            Assert.IsFalse(f.Focused);

            // Cancel restores; multiline is reported.
            var g = MakeField("orig", 300, 120, "MultiLineNewline", activate: false);
            g.Set("TouchKeyboard", tk);
            g.Call("ActivateInputField");
            Assert.IsTrue((bool)W4.Get(tk, "OpenedMultiline"));
            W4.Set(tk, "Text", "changed\nlines");
            g.Tick();
            Assert.AreEqual("changed\nlines", g.Text);
            W4.Set(tk, "Status", TouchScreenKeyboard.Status.Canceled);
            W4.Set(tk, "Active", false);
            g.Tick();
            Assert.AreEqual("orig", g.Text, "Cancel on the keyboard restores the text");
            Assert.IsFalse(g.Focused);

            // HideSoftKeyboard: never opens (custom in-VR keyboard).
            var h = MakeField("", activate: false);
            var tk2 = Activator.CreateInstance(W4.Type("LightSide.SimulatedTouchKeyboard"));
            h.Set("TouchKeyboard", tk2);
            h.Set("HideSoftKeyboard", true);
            h.Call("ActivateInputField");
            Assert.AreEqual(0, (int)W4.Get(tk2, "OpenCount"));
        }

        private static int CountAllocs(Action a, int frames)
        {
            var recorder = Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            for (var i = 0; i < frames; i++) a();
            recorder.enabled = false;
            recorder.CollectFromAllThreads();
            return recorder.sampleBlockCount;
        }

        [Test]
        public void IdleFocused_CaretBlink_AllocatesNothingPerFrame()
        {
            var f = MakeField("idle text");
            f.Caret = 4;
            Update();
            var lateUpdate = f.C.GetType().GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(lateUpdate);
            var tick = (Action)Delegate.CreateDelegate(typeof(Action), f.C, lateUpdate);
            // Warm up, then 600 frames over 6 s: the caret blinks ~10 times.
            for (var i = 0; i < 10; i++) { _clock += 0.01f; tick(); }
            var visibleChanges = 0;
            var last = (bool)f.Get("CaretBlinkVisible");
            var allocs = CountAllocs(() =>
            {
                _clock += 0.01f;
                tick();
                var v = (bool)f.Get("CaretBlinkVisible");
                if (v != last) { visibleChanges++; last = v; }
            }, 600);
            // The reflection read above boxes a bool per frame; measure the tick alone too.
            var tickOnly = CountAllocs(() => { _clock += 0.01f; tick(); }, 600);
            var control = CountAllocs(() => { var _ = new object[4]; }, 10);
            Log($"blink toggles {visibleChanges}, allocs(tick+probe) {allocs}, allocs(tick) {tickOnly}, control {control}");
            Assert.GreaterOrEqual(control, 1, "the GC.Alloc recorder works here");
            Assert.Greater(visibleChanges, 8, "the caret blinked");
            Assert.AreEqual(0, tickOnly, "no managed allocation per idle frame while focused");
        }

        [Test]
        public void GlyphMeshProInputField_ForwardsTheTmpApi()
        {
            var f = MakeField("", factory: "OpenGlyph.GlyphMeshProInputField", method: "CreateGlyphMeshPro", activate: false);
            var c = f.C;
            Assert.IsTrue(W4.Type("LightSide.UniTextInputField").IsInstanceOfType(c), "adapter is a UniTextInputField");
            Assert.AreEqual("OpenGlyph.GlyphMeshProUGUI", W4.Get(c, "textComponent").GetType().FullName);
            W4.Set(c, "text", "h\u00E9llo \U0001F44D w\u00F6rld");
            Assert.AreEqual("h\u00E9llo \U0001F44D w\u00F6rld", f.Text);
            W4.Call(c, "ActivateInputField");
            Assert.IsTrue((bool)W4.Get(c, "isFocused"));
            W4.Call(c, "MoveTextEnd", false);
            Assert.AreEqual(14, (int)W4.Get(c, "stringPosition"), "UTF-16 index");
            Assert.AreEqual(13, (int)W4.Get(c, "caretPosition"), "character index (the emoji is one character)");
            W4.Set(c, "caretPosition", 7);
            Assert.AreEqual(8, (int)W4.Get(c, "stringPosition"), "char 7 is after the emoji");
            W4.Set(c, "selectionAnchorPosition", 2);
            Assert.AreEqual(2, (int)W4.Get(c, "selectionAnchorPosition"));
            Assert.AreEqual(7, (int)W4.Get(c, "selectionFocusPosition"));
            W4.Call(c, "MoveTextStart", false);
            Assert.AreEqual(0, (int)W4.Get(c, "caretPosition"));

            W4.Set(c, "contentType", W4.Enum("OpenGlyph.GlyphMeshProInputField+ContentType", "Pin"));
            Assert.AreEqual("Password", W4.Get(c, "inputType").ToString());
            Assert.AreEqual("Digit", W4.Get(c, "characterValidation").ToString());
            Assert.AreEqual(TouchScreenKeyboardType.NumberPad, W4.Get(c, "keyboardType"));
            Assert.AreEqual("SingleLine", W4.Get(c, "lineType").ToString());
            W4.Set(c, "contentType", W4.Enum("OpenGlyph.GlyphMeshProInputField+ContentType", "Standard"));
            W4.Set(c, "lineType", W4.Enum("OpenGlyph.GlyphMeshProInputField+LineType", "MultiLineNewline"));
            Assert.IsTrue((bool)W4.Get(c, "multiLine"));
            W4.Set(c, "characterValidation", W4.Enum("OpenGlyph.GlyphMeshProInputField+CharacterValidation", "Integer"));
            Assert.AreEqual("Custom", W4.Get(c, "contentType").ToString());
            Assert.AreEqual("Integer", W4.Get(c, "characterValidation").ToString());
            W4.Set(c, "characterValidation", W4.Enum("OpenGlyph.GlyphMeshProInputField+CharacterValidation", "None"));
            W4.Set(c, "characterLimit", 4);
            Assert.AreEqual(4, (int)f.Get("CharacterLimit"));
            Assert.AreEqual("h\u00E9ll", f.Text, "lowering the limit cuts the text");
            W4.Set(c, "readOnly", true);
            Assert.IsTrue((bool)f.Get("ReadOnly"));
            W4.Set(c, "readOnly", false);
            W4.Set(c, "caretWidth", 3);
            Assert.AreEqual(3f, (float)f.Get("CaretWidth"));
            W4.Set(c, "selectionColor", Color.red);
            Assert.AreEqual(Color.red, f.Get("SelectionColor"));
            W4.Set(c, "pointSize", 22f);
            Assert.AreEqual(22f, f.TextComponent.FontSize);
            Assert.AreEqual(22f, ((UniText)W4.Get(c, "placeholder")).FontSize);
            W4.Call(c, "ForceLabelUpdate");
            Assert.AreEqual("h\u00E9ll", ((OpenGlyph.GlyphMeshProUGUI)f.TextComponent).text, "label shows the text");

            // Events forward (TMP names).
            var ends = new List<string>();
            ((UnityEngine.Events.UnityEvent<string>)W4.Get(c, "onEndEdit")).AddListener(ends.Add);
            W4.Call(c, "DeactivateInputField", false);
            Assert.AreEqual(1, ends.Count);
            Assert.IsFalse((bool)W4.Get(c, "isFocused"));
        }

        /// <summary>The scripted TMP comparison sequence: (event, expected text, expected caret, expected anchor) per step.</summary>
        private static IEnumerable<(Event e, string text, int caret, int anchor)> Script()
        {
            Event K(KeyCode k, EventModifiers m = EventModifiers.None) => new Event { type = EventType.KeyDown, keyCode = k, modifiers = m };
            Event Ch(char ch) => new Event { type = EventType.KeyDown, keyCode = KeyCode.None, character = ch };
            yield return (K(KeyCode.LeftArrow), "hello world", 10, 10);
            yield return (K(KeyCode.LeftArrow), "hello world", 9, 9);
            yield return (Ch('X'), "hello worXld", 10, 10);
            yield return (K(KeyCode.LeftArrow, EventModifiers.Shift), "hello worXld", 9, 10);
            yield return (K(KeyCode.LeftArrow, EventModifiers.Shift), "hello worXld", 8, 10);
            yield return (K(KeyCode.LeftArrow, EventModifiers.Shift), "hello worXld", 7, 10);
            yield return (K(KeyCode.Backspace), "hello wld", 7, 7);
            yield return (K(KeyCode.Home), "hello wld", 0, 0);
            yield return (K(KeyCode.Delete), "ello wld", 0, 0);
            yield return (K(KeyCode.End), "ello wld", 8, 8);
            yield return (K(KeyCode.Backspace), "ello wl", 7, 7);
            yield return (Ch('!'), "ello wl!", 8, 8);
            yield return (K(KeyCode.RightArrow, EventModifiers.Shift), "ello wl!", 8, 8);
            yield return (K(KeyCode.Home, EventModifiers.Shift), "ello wl!", 0, 8);
            yield return (Ch('Z'), "Z", 1, 1);
        }

        [Test]
        public void GlyphMeshProInputField_MatchesTmpInputField_ForAScriptedSequence()
        {
            var f = MakeField("", factory: "OpenGlyph.GlyphMeshProInputField", method: "CreateGlyphMeshPro", activate: false);
            var c = f.C;
            W4.Set(c, "text", "hello world");
            W4.Call(c, "ActivateInputField");
            W4.Call(c, "MoveTextEnd", false);

            var tmp = MakeTmpInputField("hello world");
            var step = 0;
            var tmpCompared = 0;
            foreach (var (e, text, caretExp, anchorExp) in Script())
            {
                step++;
                W4.Call(c, "ProcessEvent", e);
                Assert.AreEqual(text, (string)W4.Get(c, "text"), $"GMP text at step {step}");
                Assert.AreEqual(caretExp, (int)W4.Get(c, "stringPosition"), $"GMP caret at step {step}");
                Assert.AreEqual(anchorExp, (int)W4.Get(c, "selectionStringAnchorPosition"), $"GMP anchor at step {step}");
                if (tmp == null) continue;
                tmp.ProcessEvent(new Event(e));
                tmp.textComponent.ForceMeshUpdate(true, true);
                Assert.AreEqual(tmp.text, (string)W4.Get(c, "text"), $"text equals TMP_InputField at step {step}");
                Assert.AreEqual(tmp.stringPosition, (int)W4.Get(c, "stringPosition"), $"caret equals TMP at step {step}");
                Assert.AreEqual(tmp.selectionStringAnchorPosition, (int)W4.Get(c, "selectionStringAnchorPosition"), $"anchor equals TMP at step {step}");
                tmpCompared++;
            }
            Log(tmp != null ? $"compared {tmpCompared} steps against a real TMP_InputField" : "TMP_InputField not drivable here; checked against TMP semantics");
        }

        private TMP_InputField MakeTmpInputField(string text)
        {
            try
            {
                const BindingFlags S = BindingFlags.NonPublic | BindingFlags.Static;
                var settings = typeof(TMP_Settings).GetField("s_Instance", S);
                if (settings != null && settings.GetValue(null) == null && Resources.Load<TMP_Settings>("TMP Settings") == null)
                {
                    var inst = ScriptableObject.CreateInstance<TMP_Settings>();
                    inst.hideFlags = HideFlags.DontSave;
                    settings.SetValue(null, inst);
                }
                var sdf = typeof(ShaderUtilities).GetField("k_ShaderRef_MobileSDF", S);
                if (sdf != null && sdf.GetValue(null) == null && Shader.Find("TextMeshPro/Mobile/Distance Field") == null)
                    sdf.SetValue(null, Shader.Find("UI/Default"));
                var font = TMP_FontAsset.CreateFontAsset(MsdfTestUtil.FindNotoSansPath(), 0, 90, 9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024);
                if (font == null) { Log("TMP font unavailable"); return null; }
                _junk.Add(font);
                var go = new GameObject("tmp field", typeof(RectTransform));
                _junk.Add(go);
                go.transform.SetParent(_canvasGo.transform, false);
                ((RectTransform)go.transform).sizeDelta = new Vector2(600, 60);
                var area = new GameObject("area", typeof(RectTransform));
                area.transform.SetParent(go.transform, false);
                var tgo = new GameObject("text", typeof(RectTransform));
                tgo.transform.SetParent(area.transform, false);
                ((RectTransform)tgo.transform).sizeDelta = new Vector2(580, 50);
                var label = tgo.AddComponent<TextMeshProUGUI>();
                label.font = font;
                label.fontSize = 30;
                var field = go.AddComponent<TMP_InputField>();
                field.textViewport = (RectTransform)area.transform;
                field.textComponent = label;
                field.text = text;
                // TMP would activate on its next LateUpdate (EventSystem needed); its key handling
                // (ProcessEvent) and caret API do not depend on focus, which is what is compared here.
                field.ActivateInputField();
                label.ForceMeshUpdate(true, true);
                field.MoveTextEnd(false);
                Log($"TMP field focused={field.isFocused} caret={field.stringPosition} text=\"{field.text}\"");
                return field;
            }
            catch (Exception e)
            {
                Log($"TMP_InputField unavailable: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        [Test]
        public void SelectableText_ReadOnlySelectAndCopy_OnAnyUniText()
        {
            var go = new GameObject("label", typeof(RectTransform));
            _junk.Add(go);
            var holder = new GameObject("holder", typeof(RectTransform));
            _junk.Add(holder);
            holder.transform.SetParent(_canvasGo.transform, false);
            go.transform.SetParent(holder.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(600, 60);
            var t = go.AddComponent<UniText>();
            t.FontStack = _stack;
            t.FontSize = 30;
            t.RegisterModifier(new ModRegister { Modifier = new BoldModifier(), Rule = new BoldParseRule() });
            t.Text = "Read <b>only</b> label";
            Update();
            Assert.AreEqual("Read only label", t.CleanText);

            var sel = (Component)go.AddComponent(W4.Type("LightSide.UniTextSelectableText"));
            var f = new Field(sel);
            f.Set("Clipboard", Activator.CreateInstance(W4.Type("LightSide.InputFieldMemoryClipboard")));
            Update();
            Assert.AreEqual("Read only label", f.Text, "selects over the visible (clean) text");
            ((IPointerDownHandler)sel).OnPointerDown(Pointer(ScreenOf(t, GlyphLocal(t, 6)), 2));
            Assert.AreEqual((5, 9), (f.SelStart, f.SelEnd), "double-click selects 'only'");
            f.Key("C", "Control");
            Assert.AreEqual("only", W4.Get(f.Get("Clipboard"), "Text"));
            f.Chars("x");
            f.Key("Backspace");
            Assert.AreEqual("Read <b>only</b> label", t.Text, "the label's source (with markup) is never edited");
            Assert.AreEqual(1, f.SelectionRects().Count, "selection highlight drawn");
            Assert.IsFalse(f.HasCaretRect, "no caret");
        }
    }
}
