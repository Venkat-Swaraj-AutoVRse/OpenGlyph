using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>
    /// Shared setup for the wave 2 tests: reveal, span animations, glow / inner shadow / second outline,
    /// radial and angular gradients.
    /// </summary>
    /// <remarks>
    /// Every API added in wave 2 is reached by reflection (<see cref="W2"/>) so this file also compiles
    /// against a build without them; there each test fails with "missing API" instead of breaking the
    /// test assembly.
    /// </remarks>
    public abstract class Wave2TestBase
    {
        protected UniTextFontStack _stack;
        protected UniTextFont _font;
        protected GameObject _canvasGo;
        protected readonly List<UnityEngine.Object> _junk = new();
        private bool _savedParallel;

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
            var hebrew = Path.Combine(Path.GetDirectoryName(noto), "NotoSansHebrew-Regular.ttf");
            if (File.Exists(hebrew))
            {
                var hf = UniTextFont.CreateFontAsset(File.ReadAllBytes(hebrew), 48);
                _junk.Add(hf);
                _stack.fonts.Add(hf);
            }
            _canvasGo = new GameObject("Canvas", typeof(Canvas));
            _canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            W2.ManualClock(true, 0f);
        }

        [TearDown]
        public void BaseTearDown()
        {
            W2.ManualClock(false, 0f);
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

        protected UniText Make(string text, bool unified, Action<UniText> setup = null, float width = 1200, float height = 300,
            float fontSize = 40f, Type componentType = null, GameObject parent = null)
        {
            var go = new GameObject("w2", typeof(RectTransform));
            go.transform.SetParent((parent != null ? parent : _canvasGo).transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(width, height);
            var t = (UniText)go.AddComponent(componentType ?? typeof(UniText));
            t.UnifiedRenderer = unified ? UniText.UnifiedRendererMode.ForceOn : UniText.UnifiedRendererMode.ForceOff;
            t.FontStack = _stack;
            t.FontSize = fontSize;
            setup?.Invoke(t);
            t.Text = text;
            Canvas.ForceUpdateCanvases();
            return t;
        }

        /// <summary>Registers the wave-2 animation tags (wave shake pulse fade rainbow bounce).</summary>
        protected static void AddAnimationTags(UniText t) => W2.CallStatic(W2.Type("LightSide.TextAnimationModifier"), "RegisterAll", t);

        /// <summary>Registers &lt;pause&gt;.</summary>
        protected static void AddPauseTag(UniText t) =>
            t.RegisterModifier(new ModRegister
            {
                Rule = (IParseRule)Activator.CreateInstance(W2.Type("LightSide.PauseParseRule")),
                Modifier = (BaseModifier)Activator.CreateInstance(W2.Type("LightSide.RevealPauseModifier")),
            });

        /// <summary>Advances the manual animation clock to <paramref name="time"/> and runs one canvas update (rebuilds + effect tick).</summary>
        protected static void Step(float time)
        {
            W2.ManualClock(true, time);
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>One drawn quad (4 vertices) read back from the meshes on the component's renderers.</summary>
        protected struct Quad
        {
            public Vector3 v0, v1, v2, v3;
            public Color32 c0, c1, c2, c3;
            public Vector2 Center => new((v0.x + v2.x) * 0.5f, (v0.y + v1.y) * 0.5f);
            public float Width => Mathf.Abs(v2.x - v0.x);
            public float Height => Mathf.Abs(v1.y - v0.y);
            public int MinAlpha => Mathf.Min(Mathf.Min(c0.a, c1.a), Mathf.Min(c2.a, c3.a));
            public int MaxAlpha => Mathf.Max(Mathf.Max(c0.a, c1.a), Mathf.Max(c2.a, c3.a));
        }

        /// <summary>Every quad currently submitted to the component's CanvasRenderers (what the GPU draws).</summary>
        protected static List<Quad> DrawnQuads(UniText t)
        {
            var list = new List<Quad>();
            foreach (var r in t.CanvasRenderers)
            {
                if (r == null || !r.gameObject.activeSelf) continue;
                var m = r.GetMesh();
                if (m == null) continue;
                var v = m.vertices;
                var c = m.colors32;
                for (var i = 0; i + 3 < v.Length; i += 4)
                {
                    list.Add(new Quad
                    {
                        v0 = v[i], v1 = v[i + 1], v2 = v[i + 2], v3 = v[i + 3],
                        c0 = c.Length == v.Length ? c[i] : new Color32(255, 255, 255, 255),
                        c1 = c.Length == v.Length ? c[i + 1] : new Color32(255, 255, 255, 255),
                        c2 = c.Length == v.Length ? c[i + 2] : new Color32(255, 255, 255, 255),
                        c3 = c.Length == v.Length ? c[i + 3] : new Color32(255, 255, 255, 255),
                    });
                }
            }
            return list;
        }

        /// <summary>Quads sorted left to right.</summary>
        protected static List<Quad> ByX(List<Quad> q) => q.OrderBy(x => x.Center.x).ToList();

        protected static string Fmt(IEnumerable<float> a) => string.Join(", ", a.Select(v => v.ToString("0.###")));
    }

    /// <summary>Reflection access to APIs added in wave 2 (fails the test with a clear message when missing).</summary>
    internal static class W2
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static Type Type(string fullName)
        {
            var t = typeof(UniText).Assembly.GetType(fullName);
            if (t == null) Assert.Fail($"type {fullName} missing");
            return t;
        }

        public static bool Has(string fullName) => typeof(UniText).Assembly.GetType(fullName) != null;

        public static object Get(object o, string prop)
        {
            var p = o.GetType().GetProperty(prop, Any);
            if (p != null) return p.GetValue(o);
            var f = o.GetType().GetField(prop, Any);
            if (f == null) Assert.Fail($"{o.GetType().Name}.{prop} missing");
            return f.GetValue(o);
        }

        public static void Set(object o, string prop, object value)
        {
            var p = o.GetType().GetProperty(prop, Any);
            if (p != null && p.CanWrite) { p.SetValue(o, Coerce(value, p.PropertyType)); return; }
            var f = o.GetType().GetField(prop, Any);
            if (f == null) Assert.Fail($"{o.GetType().Name}.{prop} (settable) missing");
            f.SetValue(o, Coerce(value, f.FieldType));
        }

        /// <summary>Sets a field on a boxed struct and returns the box.</summary>
        public static object SetField(object box, string field, object value)
        {
            var f = box.GetType().GetField(field, Any);
            if (f == null) Assert.Fail($"{box.GetType().Name}.{field} missing");
            f.SetValue(box, Coerce(value, f.FieldType));
            return box;
        }

        private static object Coerce(object v, Type t)
        {
            if (v == null) return null;
            if (t.IsEnum && v is string s) return Enum.Parse(t, s);
            if (t.IsEnum && v is int i) return Enum.ToObject(t, i);
            if (t == typeof(float) && v is double d) return (float)d;
            if (t == typeof(float) && v is int n) return (float)n;
            return v;
        }

        public static object Call(object o, string method, params object[] args)
        {
            var m = o.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null) Assert.Fail($"{o.GetType().Name}.{method} missing");
            return m.Invoke(o, args);
        }

        public static object CallStatic(Type t, string method, params object[] args)
        {
            var m = t.GetMethods(Any).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null) Assert.Fail($"{t.Name}.{method} missing");
            return m.Invoke(null, args);
        }

        public static object StaticGet(Type t, string member)
        {
            var p = t.GetProperty(member, Any);
            if (p != null) return p.GetValue(null);
            var f = t.GetField(member, Any);
            if (f == null) Assert.Fail($"{t.Name}.{member} missing");
            return f.GetValue(null);
        }

        public static long Counter(string member) => Convert.ToInt64(StaticGet(typeof(UniText), member));

        /// <summary>Drives <c>UniTextAnimationClock</c> (no-op when the type is missing, so SetUp/TearDown never fail on main).</summary>
        public static void ManualClock(bool on, float time)
        {
            var t = typeof(UniText).Assembly.GetType("LightSide.UniTextAnimationClock");
            if (t == null) return;
            t.GetProperty("UseManualTime", Any)?.SetValue(null, on);
            t.GetProperty("ManualTime", Any)?.SetValue(null, time);
        }

        /// <summary>Adds a component by type name (e.g. LightSide.UniTextReveal).</summary>
        public static Component Add(GameObject go, string typeName) => go.AddComponent(Type(typeName));
    }
}
