using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 5: the built-in keyboard (UniTextKeyboard). Keys are pressed with EventSystem pointer events and
    /// type into a focused UniTextInputField through its input seam.
    /// </summary>
    public class Wave5KeyboardTests : Wave5TestBase
    {
        [Test]
        public void Keys_TypeIntoTheFocusedField_ThroughPointerEvents()
        {
            var kb = MakeKeyboard(world: false);
            var f = FieldFor(kb);
            var ids = new List<string>();
            ((UnityEvent<string>)kb.Get("onKeyPressed")).AddListener(ids.Add);
            f.Call("ActivateInputField");
            Assert.IsTrue(kb.Visible, "focusing a BuiltIn field shows the keyboard");
            Assert.AreSame(f.C, kb.Target);
            Assert.AreEqual("QWERTY", kb.LayoutName);

            kb.TypeKeys("h", "e", "l", "l", "o", "Space", "w");
            Assert.AreEqual("hello w", f.Text);
            kb.Tap("Left");
            kb.Tap("Left");
            Assert.AreEqual(5, f.Caret, "arrow keys move the caret through the field");
            kb.Tap("x");
            Assert.AreEqual("hellox w", f.Text);
            kb.Tap("Right");
            kb.Tap("Backspace");
            Assert.AreEqual("helloxw", f.Text, "Backspace deletes before the caret (the space)");
            Assert.IsTrue(f.Focused, "keys never take focus from the field");
            CollectionAssert.AreEqual(new[] { "h", "e", "l", "l", "o", " ", "w", "Left", "Left", "x", "Right", "Backspace" }, ids,
                "onKeyPressed reports every key (for click sounds / haptics)");
            // Undo works on what the keyboard typed: it is the field's own editing.
            Assert.Greater((int)f.Get("UndoCount"), 0);
        }

        [Test]
        public void Shift_TapIsOneCapital_DoubleTapIsCapsLock_AndPagesSwitch()
        {
            var kb = MakeKeyboard(world: false);
            var f = FieldFor(kb);
            f.Call("ActivateInputField");
            kb.Tap("Shift");
            Assert.AreEqual("Once", kb.Shift);
            Assert.AreEqual("A", kb.KeyLabel(kb.Key("A")), "labels show capitals while shifted");
            kb.TypeKeys("A", "b");
            Assert.AreEqual("Ab", f.Text, "one capital, then Shift turns off");
            Assert.AreEqual("Off", kb.Shift);
            Assert.AreEqual("c", kb.KeyLabel(kb.Key("c")));

            kb.Tap("Shift");
            _clock += 0.2f;
            kb.Tap("Shift");
            Assert.AreEqual("Locked", kb.Shift, "double-tap within the window = caps lock");
            kb.TypeKeys("C", "D");
            Assert.AreEqual("AbCD", f.Text, "caps lock stays on");
            kb.Tap("Shift");
            Assert.AreEqual("Off", kb.Shift);
            _clock += 1f;
            kb.Tap("Shift");
            _clock += 1f;
            kb.Tap("Shift");
            Assert.AreEqual("Off", kb.Shift, "two slow taps are on/off, not caps lock");
            kb.Tap(".");
            Assert.AreEqual("AbCD.", f.Text);

            kb.Tap("?123");
            Assert.AreEqual(1, kb.Page, "numbers & symbols page");
            kb.TypeKeys("1", "@", "#");
            kb.Tap("ABC");
            Assert.AreEqual(0, kb.Page);
            kb.Tap("z");
            Assert.AreEqual("AbCD.1@#z", f.Text);
        }

        [Test]
        public void LayoutKey_CyclesThroughLayouts_AndIsHiddenWithOne()
        {
            var kb = MakeKeyboard(world: false);
            var f = FieldFor(kb);
            f.Call("ActivateInputField");
            Assert.IsNull(kb.TryKey("NextLayout"), "one layout: no layout key");
            var space1 = (Rect)W4.Get(kb.Key("Space"), "LocalRect");

            var layouts = (System.Collections.IList)kb.Get("Layouts");
            layouts.Add(BuiltInLayout("Qwerty"));
            layouts.Add(BuiltInLayout("HindiInScript"));
            layouts.Add(BuiltInLayout("Arabic"));
            kb.Call("Rebuild");
            Assert.IsNotNull(kb.TryKey("NextLayout"), "several layouts: the layout key appears");
            var space2 = (Rect)W4.Get(kb.Key("Space"), "LocalRect");
            Assert.Greater(space1.width, space2.width + 50f, "without the layout key the space bar takes its width");
            Assert.AreEqual("English", kb.KeyLabel(kb.Key("Space")), "space bar names the layout");

            kb.Tap("NextLayout");
            Assert.AreEqual("HindiInScript", kb.LayoutName);
            Assert.AreEqual("हिन्दी", kb.KeyLabel(kb.Key("Space")));
            kb.Tap("NextLayout");
            Assert.AreEqual("Arabic", kb.LayoutName);
            kb.Tap("NextLayout");
            Assert.AreEqual("QWERTY", kb.LayoutName, "cycles back");
            Assert.IsTrue(f.Focused);

            // The chosen text layout is kept for the next text field.
            kb.Tap("NextLayout");
            f.Call("DeactivateInputField", false);
            Assert.IsFalse(kb.Visible);
            var g = FieldFor(kb);
            g.Call("ActivateInputField");
            Assert.AreEqual("HindiInScript", kb.LayoutName);
        }

        [Test]
        public void Backspace_RepeatsWhileHeld_OnTheManualClock()
        {
            var kb = MakeKeyboard(world: false);
            var f = FieldFor(kb, "abcdefghij");
            f.Call("ActivateInputField");
            f.Call("MoveTextEnd", false);
            var bs = kb.Key("Backspace");
            kb.Down(bs);
            Assert.AreEqual("abcdefghi", f.Text, "the press deletes once");
            _clock += 0.3f; kb.Tick();
            Assert.AreEqual("abcdefghi", f.Text, "no repeat before the delay");
            _clock += 0.25f; kb.Tick();
            Assert.AreEqual("abcdefgh", f.Text, "repeat after the delay (0.5 s)");
            for (var i = 0; i < 3; i++) { _clock += 1f / 30f + 0.001f; kb.Tick(); }
            Assert.AreEqual("abcde", f.Text, "then at the repeat rate (30/s)");
            kb.Up(bs);
            for (var i = 0; i < 5; i++) { _clock += 0.1f; kb.Tick(); }
            Assert.AreEqual("abcde", f.Text, "release stops the repeat");
        }

        [Test]
        public void Enter_SubmitsASingleLineField_InsertsANewlineInAMultiLineField()
        {
            var kb = MakeKeyboard(world: false);
            var f = FieldFor(kb, "go");
            var submits = new List<string>();
            ((UnityEvent<string>)f.Get("onSubmit")).AddListener(submits.Add);
            f.Call("ActivateInputField");
            kb.Tap("Enter");
            CollectionAssert.AreEqual(new[] { "go" }, submits, "Enter submits");
            Assert.IsFalse(f.Focused);
            Assert.IsFalse(kb.Visible, "submit hides the keyboard");

            var m = FieldFor(kb, "", "MultiLineNewline", 600, 160);
            m.Call("ActivateInputField");
            kb.TypeKeys("a", "Enter", "b");
            Assert.AreEqual("a\nb", m.Text, "multi-line: Enter is a newline");
            Assert.IsTrue(m.Focused);
            Assert.IsTrue(kb.Visible);
            kb.Tap("Hide");
            Assert.IsFalse(kb.Visible, "Hide key");
            Assert.IsFalse(m.Focused, "Hide ends editing");
        }

        [Test]
        public void ContentType_SelectsNumericPad_EmailRow_OrText()
        {
            var kb = MakeKeyboard(world: false);
            foreach (var ct in new[] { "IntegerNumber", "DecimalNumber", "Pin" })
            {
                var f = FieldFor(kb);
                f.SetEnum("ContentType", "LightSide.InputFieldContentType", ct);
                f.Call("ActivateInputField");
                Assert.AreEqual("Numeric", kb.LayoutName, ct);
                Assert.Less(kb.KeyCount, 20, "a small pad");
                kb.TypeKeys("4", "2", "-", ".");
                var expected = ct == "IntegerNumber" ? "42" : ct == "DecimalNumber" ? "42." : "42";
                Assert.AreEqual(expected, f.Text, $"{ct}: the field's validation applies to keyboard input");
                Assert.AreEqual(ct == "Pin", (bool)kb.Get("IsSecure"), "PIN is secure");
                f.Call("DeactivateInputField", false);
            }

            var e = FieldFor(kb);
            e.SetEnum("ContentType", "LightSide.InputFieldContentType", "EmailAddress");
            e.Call("ActivateInputField");
            Assert.AreEqual("Email", kb.LayoutName);
            kb.TypeKeys("a", "@", "b", ".com");
            Assert.AreEqual("a@b.com", e.Text, "e-mail row: @ and .com");
            e.Call("DeactivateInputField", false);

            var s = FieldFor(kb);
            s.Call("ActivateInputField");
            Assert.AreEqual("QWERTY", kb.LayoutName, "standard fields get the text layout");
        }

        [Test]
        public void Auto_UsesTheBuiltInKeyboard_WhenTheSystemKeyboardCannotShow()
        {
            var kb = MakeKeyboard(world: false);
            var f = MakeField("", activate: false);
            f.Set("BuiltInKeyboard", kb.C);
            Assert.AreEqual("Auto", f.Get("SoftKeyboard").ToString(), "Auto is the default");

            // XR active (Quest / OpenXR): TouchScreenKeyboard reports itself but shows nothing.
            W4.SetStatic("LightSide.InputFieldPlatform", "XRActiveOverride", (Func<bool>)(() => true));
            Assert.AreEqual("BuiltIn", f.Get("ResolvedSoftKeyboard").ToString());
            f.Call("ActivateInputField");
            Assert.IsTrue(kb.Visible, "auto-show on focus");
            Assert.AreSame(kb.C, f.Get("OpenBuiltInKeyboard"));
            f.Call("DeactivateInputField", false);
            Assert.IsFalse(kb.Visible, "hidden when editing ends");

            // A supported system keyboard (phone) is used when XR is off.
            W4.SetStatic("LightSide.InputFieldPlatform", "XRActiveOverride", (Func<bool>)(() => false));
            var sim = Activator.CreateInstance(W4.Type("LightSide.SimulatedTouchKeyboard"));
            f.Set("TouchKeyboard", sim);
            Assert.AreEqual("System", f.Get("ResolvedSoftKeyboard").ToString());
            f.Call("ActivateInputField");
            Assert.AreEqual(1, (int)W4.Get(sim, "OpenCount"));
            Assert.IsFalse(kb.Visible);
            f.Call("DeactivateInputField", false);

            // No working system keyboard and no XR (desktop): none, as before.
            W4.Set(sim, "IsSupported", false);
            Assert.AreEqual("None", f.Get("ResolvedSoftKeyboard").ToString());
            // No working system keyboard in XR: built-in.
            W4.SetStatic("LightSide.InputFieldPlatform", "XRActiveOverride", (Func<bool>)(() => true));
            Assert.AreEqual("BuiltIn", f.Get("ResolvedSoftKeyboard").ToString());
            // A custom keyboard implementation is trusted even in XR.
            W4.Set(sim, "IsSupported", true);
            Assert.AreEqual("System", f.Get("ResolvedSoftKeyboard").ToString());

            // Forced modes.
            f.SetEnum("SoftKeyboard", "LightSide.InputFieldSoftKeyboard", "None");
            f.Call("ActivateInputField");
            Assert.IsFalse(kb.Visible, "None never shows");
            f.Call("DeactivateInputField", false);
            W4.SetStatic("LightSide.InputFieldPlatform", "XRActiveOverride", (Func<bool>)(() => false));
            f.SetEnum("SoftKeyboard", "LightSide.InputFieldSoftKeyboard", "BuiltIn");
            f.Call("ActivateInputField");
            Assert.IsTrue(kb.Visible, "BuiltIn forces the keyboard (desktop, no XR)");
            f.Call("DeactivateInputField", false);
            f.Set("HideSoftKeyboard", true);
            f.Call("ActivateInputField");
            Assert.IsFalse(kb.Visible, "HideSoftKeyboard still wins");
        }

        [Test]
        public void Auto_CreatesAKeyboardOnDemand_WhenTheSceneHasNone()
        {
            W4.SetStatic("LightSide.InputFieldPlatform", "XRActiveOverride", (Func<bool>)(() => true));
            var f = MakeField("", 400, 60, world: true, activate: false);
            f.C.transform.position = new Vector3(0f, 1.3f, 1f);
            f.C.transform.localScale = Vector3.one * 0.001f;
            f.Call("ActivateInputField");
            var kb = (Component)f.Get("OpenBuiltInKeyboard");
            Assert.IsNotNull(kb, "a keyboard was created for the world field");
            StringAssert.Contains("(auto)", kb.name);
            Assert.IsTrue((bool)W4.Get(kb, "IsWorld"), "world fields get a world keyboard");
            Assert.AreSame(f.TextComponent.FontStack, W4.Get(kb, "FontStack"), "it uses the field's fonts");
            Assert.IsTrue((bool)W4.Get(kb, "IsVisible"));
            new Kb(kb).TypeKeys("o", "k");
            Assert.AreEqual("ok", f.Text);
        }

        [Test]
        public void PressOnTheKeyboard_KeepsTheFieldSelected_ElsewhereDeselects()
        {
            var es = MakeEventSystem();
            var kb = MakeKeyboard(world: false);
            var f = FieldFor(kb);
            es.SetSelectedGameObject(f.C.gameObject);
            Assert.IsTrue(f.Focused);
            Assert.IsTrue(kb.Visible);

            // What an input module does on a press over a non-selectable object: deselect with that pointer event.
            var key = kb.Key("q");
            es.SetSelectedGameObject(null, Kb.Ped(key.gameObject));
            Assert.IsTrue(f.Focused, "a press on a key keeps editing");
            Assert.IsTrue(kb.Visible);
            f.Tick();
            Assert.AreSame(f.C.gameObject, es.currentSelectedGameObject, "the field selects itself again");
            kb.Tap("q");
            Assert.AreEqual("q", f.Text);

            var elsewhere = new GameObject("elsewhere");
            _junk.Add(elsewhere);
            es.SetSelectedGameObject(null, Kb.Ped(elsewhere));
            Assert.IsFalse(f.Focused, "a press elsewhere ends editing");
            Assert.IsFalse(kb.Visible, "and hides the keyboard");
        }

        [Test]
        public void WorldKeys_AreHitByAPhysicsRaycaster_AndTypeIntoAWorldField()
        {
            var es = MakeEventSystem();
            var kb = MakeKeyboard(world: true);
            var root = new GameObject("world");
            _junk.Add(root);
            var f = FieldFor(kb, "", world: true, parent: root.transform);
            f.C.transform.position = new Vector3(0f, 1.4f, 1f);
            f.C.transform.rotation = Quaternion.Euler(10f, 25f, 0f);
            f.C.transform.localScale = Vector3.one * 0.001f;
            f.Call("ActivateInputField");
            Assert.IsTrue(kb.Visible);
            Update();

            // Every visible key keeps a collider (UniTextWorld collider option, Always).
            var labels = Enumerable.Range(0, kb.KeyCount).Select(i => kb.KeyAt(i)).ToList();
            Assert.IsTrue(labels.All(k => k.GetComponent<UniTextWorld>() != null), "world keys are UniTextWorld labels");
            Assert.IsTrue(labels.All(k => k.GetComponent<BoxCollider>() != null && k.GetComponent<BoxCollider>().enabled), "per-key colliders");

            var camGo = new GameObject("Cam", typeof(Camera), typeof(PhysicsRaycaster));
            _junk.Add(camGo);
            var cam = camGo.GetComponent<Camera>();
            var rt = new RenderTexture(800, 600, 24);
            _junk.Add(rt);
            cam.targetTexture = rt;
            var kbt = kb.C.transform;
            cam.transform.position = kbt.position + kbt.rotation * new Vector3(0.05f, 0.25f, -0.5f);
            cam.transform.LookAt(kbt.position);
            Physics.SyncTransforms();
            var raycaster = camGo.GetComponent<PhysicsRaycaster>();

            foreach (var label in new[] { "v", "a", "l", "v", "e" })
            {
                var key = kb.Key(label);
                var center = key.transform.TransformPoint(((RectTransform)key.transform).rect.center);
                var ped = new PointerEventData(es) { position = cam.WorldToScreenPoint(center), button = PointerEventData.InputButton.Left };
                var results = new List<RaycastResult>();
                raycaster.Raycast(ped, results);
                Assert.IsTrue(results.Count > 0, $"ray hits key {label}");
                var hit = results.OrderBy(r => r.distance).First();
                Assert.AreSame(key.gameObject, hit.gameObject, $"the nearest hit is key {label}");
                ped.pointerCurrentRaycast = ped.pointerPressRaycast = hit;
                // Like an input module: deselect with the press, then the press goes to the key.
                es.SetSelectedGameObject(null, ped);
                ExecuteEvents.ExecuteHierarchy(hit.gameObject, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(hit.gameObject, ped, ExecuteEvents.pointerUpHandler);
                f.Tick();
                Assert.AreSame(key, (Component)kb.Call("GetKeyAt", hit.worldPosition), "GetKeyAt agrees");
            }
            Assert.AreEqual("valve", f.Text);
            Assert.IsTrue(f.Focused);

            f.Call("DeactivateInputField", false);
            Assert.IsFalse(kb.Visible);
            Assert.IsTrue(labels.All(k => !k.GetComponent<BoxCollider>().enabled), "hidden: colliders off");
            Assert.IsTrue(labels.All(k => !k.GetComponent<MeshRenderer>().enabled), "hidden: renderers off, no rebuild");
        }

        [Test]
        public void KeyPreview_ShowsForTextKeys_NeverForPasswordFields()
        {
            var kb = MakeKeyboard(world: true);
            var f = FieldFor(kb);
            f.Call("ActivateInputField");
            var a = kb.Key("a");
            kb.Down(a);
            Assert.IsTrue((bool)kb.Get("IsPreviewVisible"), "preview while a letter is held");
            Assert.AreEqual("a", ((UniText)kb.Get("PreviewLabel")).Text);
            kb.Up(a);
            Assert.IsFalse((bool)kb.Get("IsPreviewVisible"));
            f.Call("DeactivateInputField", false);

            var p = FieldFor(kb);
            p.SetEnum("ContentType", "LightSide.InputFieldContentType", "Password");
            p.Call("ActivateInputField");
            Assert.IsTrue((bool)kb.Get("IsSecure"));
            var b = kb.Key("b");
            kb.Down(b);
            Assert.IsFalse((bool)kb.Get("IsPreviewVisible"), "no key preview for a password");
            kb.Up(b);
            kb.TypeKeys("c", "d");
            Assert.AreEqual("bcd", p.Text, "keys type the same");
            Assert.AreEqual("***", p.Displayed, "the field still masks");
        }

        [Test]
        public void Hindi_And_Arabic_Layouts_InsertTheRightCodePoints_AndTheFieldShapesThem()
        {
            if (_devanagari == null) Assert.Ignore("NotoSansDevanagari-Regular.ttf fixture not found.");
            var kb = MakeKeyboard(world: false);
            var f = FieldFor(kb, "", width: 800);
            f.Call("ActivateInputField");
            kb.Call("SetLayout", BuiltInLayout("HindiInScript"));
            Assert.AreEqual("HindiInScript", kb.LayoutName);
            // न म स ् त े = "नमस्ते"
            kb.TypeKeys("न", "म", "स", "्", "त", "े");
            Assert.AreEqual("\u0928\u092E\u0938\u094D\u0924\u0947", f.Text, "InScript keys insert the Devanagari code points");
            kb.Tap("Shift");
            kb.Tap("आ");
            Assert.AreEqual("\u0928\u092E\u0938\u094D\u0924\u0947\u0906", f.Text, "Shift gives the independent vowel");
            Update();
            var glyphs = f.TextComponent.ResultGlyphs.ToArray();
            L5($"Devanagari: {f.Text.Length} code points -> {glyphs.Length} glyphs: {string.Join(",", glyphs.Select(g => g.glyphId))}");
            Assert.IsTrue(glyphs.All(g => g.glyphId != 0), "no missing glyphs");
            Assert.Less(glyphs.Length, f.Text.Length, "the virama forms a conjunct / half form (shaped, not one glyph per code point)");

            // The key labels are shaped too (a vowel sign on its own key gets the dotted circle).
            var matra = kb.Key("ा");
            Update();
            var lg = ((UniText)W4.Get(matra, "Label")).ResultGlyphs.ToArray();
            Assert.IsTrue(lg.Length > 0 && lg.All(g => g.glyphId != 0), "the vowel-sign key label renders");

            f.Text = "";
            kb.Call("SetLayout", BuiltInLayout("Arabic"));
            kb.TypeKeys("س", "ل", "ا", "م");
            Assert.AreEqual("\u0633\u0644\u0627\u0645", f.Text, "Arabic keys insert the code points");
            Update();
            var ag = f.TextComponent.ResultGlyphs.ToArray();
            var first = ag.First(g => g.cluster == 0);
            var last = ag.First(g => g.cluster == 3);
            Assert.Greater(first.left, last.left, "RTL: the first letter is on the right");
            Assert.IsTrue(ag.All(g => g.glyphId != 0));
        }

        [Test]
        public void CustomLayout_FromJson_TypesItsKeys()
        {
            var t = W4.Type(LayoutType);
            var json = "{\"displayName\":\"Deutsch\",\"shortName\":\"DE\",\"language\":\"de\",\"pages\":[{\"name\":\"letters\",\"rows\":[" +
                       "{\"indent\":0,\"keys\":[{\"action\":0,\"text\":\"ä\"},{\"action\":0,\"text\":\"ß\",\"shiftText\":\"ẞ\"},{\"action\":1,\"width\":1.5}]}," +
                       "{\"indent\":0.5,\"keys\":[{\"action\":4},{\"action\":0,\"text\":\"ö\"},{\"action\":3,\"width\":2}]}]}]}";
            var custom = (UnityEngine.Object)W4.Call(t, "FromJson", json);
            _junk.Add(custom);
            var kb = MakeKeyboard(world: false);
            var f = FieldFor(kb);
            f.Call("ActivateInputField");
            kb.Call("SetLayout", custom);
            Assert.AreEqual(6, kb.KeyCount);
            kb.TypeKeys("ä", "ö", "Space", "Shift", "ẞ", "Backspace", "ß");
            Assert.AreEqual("äö ß", f.Text);
            Assert.AreEqual("Deutsch", (string)W4.Get(custom, "displayName"));

            // The built-ins round-trip through JSON.
            var q = BuiltInLayout("Qwerty");
            var back = (UnityEngine.Object)W4.Call(t, "FromJson", (string)W4.Call(q, "ToJson", true));
            _junk.Add(back);
            kb.Call("SetLayout", back);
            Assert.AreEqual(37, kb.KeyCount, "QWERTY letters page (one layout: no layout key)");
        }

        [Test]
        public void BelowField_Placement_And_LazyFollowHead()
        {
            var kb = MakeKeyboard(world: true);
            var root = new GameObject("world");
            _junk.Add(root);
            var f = FieldFor(kb, "", width: 400, world: true, parent: root.transform);
            var ft = (RectTransform)f.C.transform;
            ft.position = new Vector3(0.2f, 1.3f, 1.2f);
            ft.rotation = Quaternion.Euler(0f, 30f, 0f);
            ft.localScale = Vector3.one * 0.001f;
            f.Call("ActivateInputField");
            var corners = new Vector3[4];
            ft.GetWorldCorners(corners);
            var fieldBottom = (corners[0] + corners[3]) * 0.5f;
            var kt = (RectTransform)kb.C.transform;
            kt.GetWorldCorners(corners);
            var kbTop = (corners[1] + corners[2]) * 0.5f;
            var expectedTop = fieldBottom - ft.up * 0.02f - ft.forward * 0.04f;
            Assert.Less(Vector3.Distance(kbTop, expectedTop), 1e-3f, "the keyboard's top edge is just below the field, towards the viewer");
            Assert.Less(Quaternion.Angle(kt.rotation, ft.rotation * Quaternion.Euler(20f, 0f, 0f)), 0.1f, "tilted 20 degrees");
            Assert.AreEqual(0.045f, kt.lossyScale.x * (float)kb.Get("KeySize"), 1e-4f, "4.5 cm keys");
            f.Call("DeactivateInputField", false);

            var head = new GameObject("head").transform;
            _junk.Add(head.gameObject);
            head.position = new Vector3(0f, 1.6f, 0f);
            kb.Set("Placement", W4.Enum("LightSide.KeyboardPlacement", "FollowHead"));
            kb.Set("Head", head);
            f.Call("ActivateInputField");
            var p0 = kt.position;
            Assert.AreEqual(0.45f, Vector3.Distance(new Vector3(p0.x, 0, p0.z), Vector3.zero), 1e-3f, "in front of the head");
            Assert.AreEqual(1.6f - 0.28f, p0.y, 1e-3f, "below eye level");
            head.rotation = Quaternion.Euler(0f, 10f, 0f);
            _clock += 0.1f; kb.Tick(); _clock += 0.1f; kb.Tick();
            Assert.IsFalse((bool)kb.Get("IsFollowing"), "small head turns do not move it (lazy)");
            Assert.Less(Vector3.Distance(kt.position, p0), 1e-5f);
            head.rotation = Quaternion.Euler(0f, 70f, 0f);
            _clock += 0.05f; kb.Tick();
            Assert.IsTrue((bool)kb.Get("IsFollowing"), "outside the view cone it follows");
            for (var i = 0; i < 120; i++) { _clock += 0.05f; kb.Tick(); }
            Assert.IsFalse((bool)kb.Get("IsFollowing"), "and stops when it arrives");
            var fwd = Quaternion.Euler(0f, 70f, 0f) * Vector3.forward;
            Assert.Less(Vector3.Distance(kt.position, head.position + fwd * 0.45f + Vector3.up * -0.28f), 0.02f);
        }

        private static int CountAllocs(Action a, int frames)
        {
            var recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            for (var i = 0; i < frames; i++) a();
            recorder.enabled = false;
            recorder.CollectFromAllThreads();
            return recorder.sampleBlockCount;
        }

        [Test]
        public void IdleVisibleKeyboard_AllocatesNothingPerFrame_AndDrawsWithSharedMaterials()
        {
            var kb = MakeKeyboard(world: true);
            var f = FieldFor(kb, "idle");
            f.Call("ActivateInputField");
            Update();
            var kbUpdate = kb.C.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            var fieldLate = f.C.GetType().GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var tickKb = (Action)Delegate.CreateDelegate(typeof(Action), kb.C, kbUpdate);
            var tickField = (Action)Delegate.CreateDelegate(typeof(Action), f.C, fieldLate);
            for (var i = 0; i < 10; i++) { _clock += 0.016f; tickKb(); tickField(); }
            var allocs = CountAllocs(() => { _clock += 0.016f; tickKb(); tickField(); }, 600);
            var control = CountAllocs(() => { var _ = new object[3]; }, 10);
            Assert.GreaterOrEqual(control, 1, "the GC.Alloc recorder works here");
            Assert.AreEqual(0, allocs, "no managed allocation per idle frame (keyboard shown, field focused)");

            // Draw cost: one renderer per key label with glyphs + one background mesh; all labels share one material.
            var renderers = kb.C.GetComponentsInChildren<MeshRenderer>(false).Where(r => r.enabled).ToList();
            var withGeometry = renderers.Where(r => r.GetComponent<MeshFilter>().sharedMesh != null && r.GetComponent<MeshFilter>().sharedMesh.vertexCount > 0).ToList();
            var labelMats = withGeometry.Where(r => r.GetComponent<UniText>() != null).SelectMany(r => r.sharedMaterials).Distinct().ToList();
            var surfaceMats = withGeometry.Where(r => r.GetComponent<UniText>() == null).SelectMany(r => r.sharedMaterials).Distinct().ToList();
            L5($"keyboard: {kb.KeyCount} keys, {withGeometry.Count} renderers with geometry ({withGeometry.Count(r => r.GetComponent<UniText>() != null)} labels + {withGeometry.Count(r => r.GetComponent<UniText>() == null)} background), " +
               $"label materials {labelMats.Count}, background materials {surfaceMats.Count}, idle allocs {allocs}");
            Assert.AreEqual(1, labelMats.Count, "every key label shares one material");
            Assert.AreEqual(1, surfaceMats.Count, "one background mesh material");
            Assert.AreEqual(1, withGeometry.Count(r => r.GetComponent<UniText>() == null), "all key backgrounds and icons are one mesh");
        }
    }
}
