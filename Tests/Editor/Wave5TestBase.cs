using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LightSide.Tests
{
    /// <summary>
    /// Shared setup for the wave 5 tests: the built-in keyboard (UniTextKeyboard) typing into a
    /// UniTextInputField, and RegisterDefaultMarkup.
    /// </summary>
    /// <remarks>
    /// Every API added in wave 5 is reached by reflection (<see cref="W4"/>, <see cref="Kb"/>), so the file
    /// compiles against a build without them and each test then fails with "missing". Keys are pressed with
    /// EventSystem pointer events (<see cref="ExecuteEvents"/>), the clock is the manual wave 4 clock.
    /// </remarks>
    public abstract class Wave5TestBase : Wave4TestBase
    {
        protected UniTextFont _devanagari;

        protected const string KbType = "LightSide.UniTextKeyboard";
        protected const string LayoutType = "LightSide.UniTextKeyboardLayout";

        [SetUp]
        public void Wave5SetUp()
        {
            var noto = MsdfTestUtil.FindNotoSansPath();
            var root = Path.GetDirectoryName(Path.GetDirectoryName(noto));
            var deva = Path.Combine(root, "Tests", "Editor", "Fixtures", "NotoSansDevanagari-Regular.ttf");
            if (File.Exists(deva))
            {
                _devanagari = UniTextFont.CreateFontAsset(File.ReadAllBytes(deva), 48);
                _junk.Add(_devanagari);
                _stack.fonts.Add(_devanagari);
            }
            W4.SetStatic("LightSide.InputFieldPlatform", "XRActiveOverride", null, optional: true);
        }

        [TearDown]
        public void Wave5TearDown()
        {
            try { W4.SetStatic("LightSide.InputFieldPlatform", "XRActiveOverride", null, optional: true); } catch { }
            var t = typeof(UniText).Assembly.GetType(KbType);
            if (t != null)
            {
                // Keyboards created on demand by fields (UniTextKeyboard.ForField) are not in _junk.
                var list = new List<Component>();
                foreach (var k in (IEnumerable)t.GetProperty("Instances").GetValue(null)) list.Add((Component)k);
                foreach (var k in list) if (k != null) UnityEngine.Object.DestroyImmediate(k.gameObject);
            }
            if (_eventSystem != null)
            {
                // Leave the EventSystem list as it was (OnEnable registered it, see MakeEventSystem).
                typeof(EventSystem).GetMethod("OnDisable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_eventSystem, null);
                UnityEngine.Object.DestroyImmediate(_eventSystem.gameObject);
                _eventSystem = null;
            }
        }

        private EventSystem _eventSystem;

        protected static void L5(string msg) => Debug.Log("[W5] " + msg);

        /// <summary>A keyboard (UniTextKeyboard.Create) with the test fonts.</summary>
        protected Kb MakeKeyboard(bool world, Transform parent = null)
        {
            var c = (Component)W4.Call(W4.Type(KbType), "Create", parent != null ? parent : world ? null : _canvasGo.transform, world, _stack, 0.045f);
            _junk.Add(c.gameObject);
            var kb = new Kb(c);
            Update();
            return kb;
        }

        /// <summary>A Canvas field bound to <paramref name="kb"/> (SoftKeyboard = BuiltIn), not focused.</summary>
        protected Field FieldFor(Kb kb, string text = "", string lineType = null, float width = 600, float height = 60, bool world = false, Transform parent = null)
        {
            var f = MakeField(text, width, height, lineType, activate: false, world: world, parent: parent);
            f.SetEnum("SoftKeyboard", "LightSide.InputFieldSoftKeyboard", "BuiltIn");
            if (kb != null) f.Set("BuiltInKeyboard", kb.C);
            return f;
        }

        protected static UnityEngine.Object BuiltInLayout(string name) => (UnityEngine.Object)W4.Get(W4.Type(LayoutType), name);

        /// <summary>
        /// An EventSystem that is <see cref="EventSystem.current"/>. EditMode does not run its OnEnable (it is not
        /// ExecuteAlways), so it is invoked here, as entering Play Mode would.
        /// </summary>
        protected EventSystem MakeEventSystem()
        {
            var go = new GameObject("W5 EventSystem", typeof(EventSystem));
            var es = go.GetComponent<EventSystem>();
            typeof(EventSystem).GetMethod("OnEnable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(es, null);
            _eventSystem = es;
            Assert.AreSame(es, EventSystem.current, "test EventSystem is current");
            return es;
        }

        /// <summary>Reflection wrapper over a UniTextKeyboard.</summary>
        protected sealed class Kb
        {
            public readonly Component C;
            public Kb(Component c) { C = c; }

            public object Get(string m) => W4.Get(C, m);
            public void Set(string m, object v) => W4.Set(C, m, v);
            public object Call(string m, params object[] a) => W4.Call(C, m, a);

            public bool Visible => (bool)Get("IsVisible");
            public Component Target => (Component)Get("Target");
            public string LayoutName => ((UnityEngine.Object)Get("CurrentLayout")).name;
            public int Page => (int)Get("Page");
            public string Shift => Get("Shift").ToString();
            public int KeyCount => (int)Get("KeyCount");
            public Component KeyAt(int i) => (Component)Call("KeyAt", i);
            public void Tick() => Call("Update");

            /// <summary>The key with this label / text / action name (fails when missing).</summary>
            public Component Key(string labelOrAction)
            {
                var k = (Component)Call("FindKey", labelOrAction);
                Assert.IsNotNull(k, $"key '{labelOrAction}' on layout {LayoutName} page {Page}");
                return k;
            }

            public Component TryKey(string labelOrAction) => (Component)Call("FindKey", labelOrAction);

            public static PointerEventData Ped(GameObject go)
            {
                var e = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, clickCount = 1 };
                e.pointerCurrentRaycast = new RaycastResult { gameObject = go };
                e.pointerPressRaycast = e.pointerCurrentRaycast;
                e.pointerEnter = go;
                return e;
            }

            public void Down(Component key) => ExecuteEvents.Execute(key.gameObject, Ped(key.gameObject), ExecuteEvents.pointerDownHandler);
            public void Up(Component key) => ExecuteEvents.Execute(key.gameObject, Ped(key.gameObject), ExecuteEvents.pointerUpHandler);

            /// <summary>Press and release through the EventSystem pointer interfaces (also click, like a real input module).</summary>
            public void Tap(string labelOrAction)
            {
                var k = Key(labelOrAction);
                var e = Ped(k.gameObject);
                ExecuteEvents.Execute(k.gameObject, e, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.Execute(k.gameObject, e, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(k.gameObject, e, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(k.gameObject, e, ExecuteEvents.pointerClickHandler);
                ExecuteEvents.Execute(k.gameObject, e, ExecuteEvents.pointerExitHandler);
            }

            public void TypeKeys(params string[] labels) { foreach (var l in labels) Tap(l); }

            public string KeyLabel(Component key) => ((UniText)W4.Get(key, "Label")).Text;
            public string KeyAction(Component key) => W4.Get(W4.Get(key, "Definition"), "action").ToString();
        }
    }
}
