using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>
    /// Shared setup for the wave 4 tests: the editable field (UniTextInputField), its TMP-shaped adapter
    /// (GlyphMeshProInputField) and the read-only selectable text.
    /// </summary>
    /// <remarks>
    /// Every API added in wave 4 is reached by reflection (<see cref="Field"/>, <see cref="W4"/>), so this
    /// file also compiles against a build without them; there each test fails with "missing" instead of
    /// breaking the test assembly. Input is injected through the field's test seam (ProcessKey,
    /// ProcessText, ProcessChar, SetComposition, pointer events built in code); no device is used.
    /// </remarks>
    public abstract class Wave4TestBase
    {
        protected UniTextFontStack _stack;
        protected UniTextFont _font;
        protected GameObject _canvasGo;
        protected readonly List<UnityEngine.Object> _junk = new();
        private bool _savedParallel;
        protected float _clock;

        [SetUp]
        public void BaseSetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            var noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            SegHelper.EnsureUnicode();
            SharedGlyphAtlas.Clear();
            _savedParallel = UniText.UseParallel;
            UniText.UseParallel = false;
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);
            var dir = Path.GetDirectoryName(noto);
            foreach (var extra in new[] { "NotoSansHebrew-Regular.ttf", "NotoSansArabic-Regular.ttf" })
            {
                var p = Path.Combine(dir, extra);
                if (!File.Exists(p)) continue;
                var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(p), 48);
                _junk.Add(f);
                _stack.fonts.Add(f);
            }
            _canvasGo = new GameObject("Canvas", typeof(Canvas));
            var c = _canvasGo.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal;
            _clock = 100f;
            W4.SetStatic("LightSide.UniTextInputField", "TimeSource", (Func<float>)(() => _clock));
        }

        [TearDown]
        public void BaseTearDown()
        {
            try { W4.SetStatic("LightSide.UniTextInputField", "TimeSource", null, optional: true); } catch { }
            try { W4.SetStatic("LightSide.InputFieldKeyboardSources", "ShiftOverride", null, optional: true); } catch { }
            UniText.UseParallel = _savedParallel;
            if (_canvasGo != null) UnityEngine.Object.DestroyImmediate(_canvasGo);
            foreach (var o in _junk) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _junk.Clear();
            UnityEngine.Object.DestroyImmediate(_stack);
            UnityEngine.Object.DestroyImmediate(_font);
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        protected static void Update() => Canvas.ForceUpdateCanvases();

        protected static void Log(string msg) => Debug.Log("[W4] " + msg);

        /// <summary>A Canvas field (UniTextInputField.Create) with the test fonts, no device keyboard, an in-memory clipboard, focused.</summary>
        protected Field MakeField(string text = "", float width = 600, float height = 60, string lineType = null,
            bool activate = true, float fontSize = 30f, string factory = "LightSide.UniTextInputField", string method = "Create",
            bool world = false, Transform parent = null)
        {
            var type = W4.Type(factory);
            var m = type.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == method);
            if (m == null) Assert.Fail($"{factory}.{method} missing");
            var comp = (Component)m.Invoke(null, new object[] { parent != null ? parent : world ? null : _canvasGo.transform, world, new Vector2(width, height), "Enter text...", "field" });
            _junk.Add(comp.gameObject);
            var f = new Field(comp);
            foreach (var t in new[] { f.TextComponent, f.Placeholder as UniText })
            {
                if (t == null) continue;
                t.FontStack = _stack;
                t.FontSize = fontSize;
            }
            f.Set("KeyboardSource", null);
            f.Set("Clipboard", Activator.CreateInstance(W4.Type("LightSide.InputFieldMemoryClipboard")));
            if (lineType != null) f.Set("LineType", W4.Enum("LightSide.InputFieldLineType", lineType));
            if (lineType != null && lineType != "SingleLine") f.TextComponent.VerticalAlignment = VerticalAlignment.Top;
            f.Text = text;
            Update();
            if (activate) { f.Call("ActivateInputField"); Update(); }
            return f;
        }

        /// <summary>Screen point of a text-local point on an overlay canvas.</summary>
        protected static Vector2 ScreenOf(UniText t, Vector2 local) =>
            RectTransformUtility.WorldToScreenPoint(null, t.rectTransform.TransformPoint(local));

        /// <summary>Text-local centre of the first glyph of cluster (codepoint index) <paramref name="cluster"/>.</summary>
        protected static Vector2 GlyphLocal(UniText t, int cluster, float fx = 0.5f)
        {
            var area = (Rect)W4.Get(t, "TextAreaRect");
            foreach (var g in t.ResultGlyphs.ToArray())
                if (g.cluster == cluster)
                    return new Vector2(area.xMin + Mathf.Lerp(g.left, g.right, fx), area.yMax - (g.top + g.bottom) * 0.5f);
            Assert.Fail($"no glyph for cluster {cluster}");
            return default;
        }

        protected static PointerEventData Pointer(Vector2 screen, int clicks = 1)
        {
            var e = new PointerEventData(EventSystem.current) { position = screen, clickCount = clicks, button = PointerEventData.InputButton.Left };
            return e;
        }

        /// <summary>Reflection wrapper over a UniTextInputField (or subclass).</summary>
        protected sealed class Field
        {
            public readonly Component C;
            public Field(Component c) { C = c; }

            public string Text { get => (string)Get("Text"); set => Set("Text", value); }
            public int Caret { get => (int)Get("CaretPosition"); set => Set("CaretPosition", value); }
            public int Anchor { get => (int)Get("SelectionAnchorPosition"); set => Set("SelectionAnchorPosition", value); }
            public int SelStart => (int)Get("SelectionStart");
            public int SelEnd => (int)Get("SelectionEnd");
            public UniText TextComponent => (UniText)Get("TextComponent");
            public Graphic Placeholder => (Graphic)Get("Placeholder");
            public RectTransform Viewport => (RectTransform)Get("TextViewport");
            public bool Focused => (bool)Get("IsFocused");
            public string Displayed => (string)Get("DisplayedText");
            public Rect CaretRect => (Rect)Get("CaretLocalRect");
            public bool HasCaretRect => (bool)Get("HasCaretRect");
            public Vector2 Scroll => (Vector2)Get("ScrollOffset");

            public object Get(string m) => W4.Get(C, m);
            public void Set(string m, object v) => W4.Set(C, m, v);
            public object Call(string m, params object[] a) => W4.Call(C, m, a);

            public bool Key(string key, string mods = "None") =>
                (bool)Call("ProcessKey", W4.Enum("LightSide.InputFieldKey", key), W4.Enum("LightSide.InputFieldModifiers", mods));

            public void Keys(string key, int times, string mods = "None") { for (var i = 0; i < times; i++) Key(key, mods); }
            public void Type(string s) => Call("ProcessText", s);
            public void Chars(string s) { foreach (var ch in s) Call("ProcessChar", ch); }
            public void Compose(string s) => Call("SetComposition", s);
            public void Tick() => W4.Call(C, "LateUpdate");

            public List<Rect> SelectionRects()
            {
                var l = new List<Rect>();
                Call("GetSelectionRects", l);
                return l;
            }

            public void SetEnum(string prop, string enumType, string value) => Set(prop, W4.Enum(enumType, value));
        }
    }

    /// <summary>Reflection access to APIs added in wave 4 (fails the test with a clear message when missing).</summary>
    internal static class W4
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static Type Type(string fullName)
        {
            var t = typeof(UniText).Assembly.GetType(fullName);
            if (t == null) Assert.Fail($"type {fullName} missing");
            return t;
        }

        public static object Enum(string type, string value) => System.Enum.Parse(Type(type), value);

        public static object Get(object o, string member)
        {
            var t = o as Type ?? o.GetType();
            var target = o is Type ? null : o;
            for (var tt = t; tt != null; tt = tt.BaseType)
            {
                var p = tt.GetProperty(member, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
                var f = tt.GetField(member, Any | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
            }
            Assert.Fail($"{t.Name}.{member} missing");
            return null;
        }

        public static void Set(object o, string member, object value)
        {
            for (var tt = o.GetType(); tt != null; tt = tt.BaseType)
            {
                var p = tt.GetProperty(member, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite) { p.SetValue(o, Coerce(value, p.PropertyType)); return; }
                var f = tt.GetField(member, Any | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(o, Coerce(value, f.FieldType)); return; }
            }
            Assert.Fail($"{o.GetType().Name}.{member} (settable) missing");
        }

        public static void SetStatic(string type, string member, object value, bool optional = false)
        {
            var t = typeof(UniText).Assembly.GetType(type);
            if (t == null) { if (optional) return; Assert.Fail($"type {type} missing"); }
            var f = t.GetField(member, Any);
            if (f != null) { f.SetValue(null, value); return; }
            var p = t.GetProperty(member, Any);
            if (p != null) { p.SetValue(null, value); return; }
            if (!optional) Assert.Fail($"{type}.{member} missing");
        }

        private static object Coerce(object v, Type t)
        {
            if (v == null) return null;
            if (t.IsEnum && v is string s) return System.Enum.Parse(t, s);
            if (t.IsEnum && v is int i) return System.Enum.ToObject(t, i);
            return v;
        }

        public static object Call(object o, string method, params object[] args)
        {
            var t = o as Type ?? o.GetType();
            var target = o is Type ? null : o;
            for (var tt = t; tt != null; tt = tt.BaseType)
            {
                var m = tt.GetMethods(Any | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == method && Matches(x.GetParameters(), args));
                if (m != null)
                {
                    var full = new object[m.GetParameters().Length];
                    for (var i = 0; i < full.Length; i++)
                        full[i] = i < args.Length ? args[i] : m.GetParameters()[i].DefaultValue;
                    try { return m.Invoke(target, full); }
                    catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
                }
            }
            Assert.Fail($"{t.Name}.{method}({args.Length} args) missing");
            return null;
        }

        private static bool Matches(ParameterInfo[] ps, object[] args)
        {
            if (args.Length > ps.Length) return false;
            for (var i = 0; i < ps.Length; i++)
            {
                if (i >= args.Length) { if (!ps[i].HasDefaultValue) return false; continue; }
                if (args[i] == null) { if (ps[i].ParameterType.IsValueType) return false; continue; }
                if (!ps[i].ParameterType.IsInstanceOfType(args[i])) return false;
            }
            return true;
        }
    }
}
