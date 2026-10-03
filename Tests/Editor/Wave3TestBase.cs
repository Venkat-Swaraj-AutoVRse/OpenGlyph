using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Shared setup for the wave 3 tests: world-space text without a Canvas (UniTextWorld, GlyphMeshPro),
    /// the UniText/World/Uber shader, world-space pointer hit testing and the cost of many labels.
    /// </summary>
    /// <remarks>
    /// The new types and members are reached by reflection (<see cref="W3"/>), so this file also compiles
    /// against a build without them; there the tests fail with "missing" instead of breaking the assembly.
    /// </remarks>
    public abstract class Wave3TestBase
    {
        protected UniTextFontStack _stack;
        protected UniTextFont _font;
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
            var dir = Path.GetDirectoryName(noto);
            foreach (var extra in new[] { "NotoSansHebrew-Regular.ttf", "NotoSansArabic-Regular.ttf" })
            {
                var p = Path.Combine(dir, extra);
                if (!File.Exists(p)) continue;
                var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(p), 48);
                _junk.Add(f);
                _stack.fonts.Add(f);
            }
        }

        [TearDown]
        public void BaseTearDown()
        {
            UniText.UseParallel = _savedParallel;
            foreach (var o in _junk) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _junk.Clear();
            UnityEngine.Object.DestroyImmediate(_stack);
            UnityEngine.Object.DestroyImmediate(_font);
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        protected static bool HasGpu => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>Creates a world-space text component of <paramref name="typeName"/> (no Canvas anywhere above it).</summary>
        protected UniText MakeWorld(string text, bool unified, Action<UniText> setup = null, float width = 800, float height = 200,
            float fontSize = 40f, string typeName = "LightSide.UniTextWorld", Transform parent = null, bool update = true)
        {
            var go = new GameObject("w3", typeof(RectTransform));
            _junk.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(width, height);
            var t = (UniText)go.AddComponent(W3.Type(typeName));
            t.UnifiedRenderer = unified ? UniText.UnifiedRendererMode.ForceOn : UniText.UnifiedRendererMode.ForceOff;
            t.FontStack = _stack;
            t.FontSize = fontSize;
            setup?.Invoke(t);
            t.Text = text;
            if (update) Canvas.ForceUpdateCanvases();
            return t;
        }

        /// <summary>Creates a UniText on its own World Space canvas: the "equivalent Canvas text" reference.</summary>
        protected UniText MakeCanvasText(string text, bool unified, Action<UniText> setup = null, float width = 800, float height = 200,
            float fontSize = 40f, Type componentType = null, bool update = true)
        {
            var canvasGo = new GameObject("w3canvas", typeof(Canvas));
            _junk.Add(canvasGo);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal;
            ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(width, height);
            var go = new GameObject("w3ref", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(width, height);
            var t = (UniText)go.AddComponent(componentType ?? typeof(UniText));
            t.UnifiedRenderer = unified ? UniText.UnifiedRendererMode.ForceOn : UniText.UnifiedRendererMode.ForceOff;
            t.FontStack = _stack;
            t.FontSize = fontSize;
            setup?.Invoke(t);
            t.Text = text;
            if (update) Canvas.ForceUpdateCanvases();
            return t;
        }

        /// <summary>The mesh on the component's MeshFilter (what the MeshRenderer draws).</summary>
        protected static Mesh WorldMeshOf(Component t)
        {
            var mf = t.GetComponent<MeshFilter>();
            Assert.IsNotNull(mf, "world text has no MeshFilter");
            return mf.sharedMesh;
        }

        /// <summary>One drawn quad (4 vertices).</summary>
        protected struct Quad
        {
            public Vector3 v0, v1, v2, v3;
            public Color32 c0;
            public Vector2 Min => new(Mathf.Min(v0.x, v2.x), Mathf.Min(v0.y, v1.y));
            public Vector2 Max => new(Mathf.Max(v0.x, v2.x), Mathf.Max(v0.y, v1.y));
            public Vector2 Center => (Min + Max) * 0.5f;
            public float Width => Max.x - Min.x;
            public float Height => Max.y - Min.y;
        }

        protected static List<Quad> QuadsOf(Mesh m)
        {
            var list = new List<Quad>();
            if (m == null) return list;
            var v = m.vertices;
            var c = m.colors32;
            for (var i = 0; i + 3 < v.Length; i += 4)
                list.Add(new Quad { v0 = v[i], v1 = v[i + 1], v2 = v[i + 2], v3 = v[i + 3],
                    c0 = c.Length == v.Length ? c[i] : new Color32(255, 255, 255, 255) });
            return list;
        }

        /// <summary>Every quad submitted to a Canvas text's CanvasRenderers.</summary>
        protected static List<Quad> CanvasQuads(UniText t)
        {
            var list = new List<Quad>();
            foreach (var r in t.CanvasRenderers)
            {
                if (r == null || !r.gameObject.activeSelf) continue;
                list.AddRange(QuadsOf(r.GetMesh()));
            }
            return list;
        }

        protected static List<Quad> ByX(List<Quad> q) => q.OrderBy(x => x.Center.x).ThenBy(x => x.Center.y).ToList();

        protected static void Log(string msg) => Debug.Log("[W3] " + msg);

        protected static LinkModifier AddLinks(UniText t)
        {
            var link = new LinkModifier();
            // Never open a browser from a test.
            typeof(LinkModifier).GetField("autoOpenUrl", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(link, false);
            t.RegisterModifier(new ModRegister { Modifier = link, Rule = new LinkTagParseRule() });
            return link;
        }

        protected static void AddBasicTags(UniText t)
        {
            t.RegisterModifier(new ModRegister { Modifier = new ColorModifier(), Rule = new ColorParseRule() });
            t.RegisterModifier(new ModRegister { Modifier = new BoldModifier(), Rule = new BoldParseRule() });
            t.RegisterModifier(new ModRegister { Modifier = new SizeModifier(), Rule = new SizeParseRule() });
        }
    }

    /// <summary>Reflection access to APIs added in wave 3 (fails the test with a clear message when missing).</summary>
    internal static class W3
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static Type Type(string fullName)
        {
            var t = typeof(UniText).Assembly.GetType(fullName);
            if (t == null) Assert.Fail($"type {fullName} missing");
            return t;
        }

        public static object Get(object o, string member)
        {
            var t = o as Type ?? o.GetType();
            var target = o is Type ? null : o;
            var p = t.GetProperty(member, Any);
            if (p != null) return p.GetValue(target);
            var f = t.GetField(member, Any);
            if (f == null) Assert.Fail($"{t.Name}.{member} missing");
            return f.GetValue(target);
        }

        public static void Set(object o, string member, object value)
        {
            var p = o.GetType().GetProperty(member, Any);
            if (p != null && p.CanWrite) { p.SetValue(o, Coerce(value, p.PropertyType)); return; }
            var f = o.GetType().GetField(member, Any);
            if (f == null) Assert.Fail($"{o.GetType().Name}.{member} (settable) missing");
            f.SetValue(o, Coerce(value, f.FieldType));
        }

        private static object Coerce(object v, Type t)
        {
            if (v == null) return null;
            if (t.IsEnum && v is string s) return Enum.Parse(t, s);
            if (t.IsEnum && v is int i) return Enum.ToObject(t, i);
            return v;
        }

        public static object Call(object o, string method, params object[] args)
        {
            var m = o.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null) Assert.Fail($"{o.GetType().Name}.{method}({args.Length} args) missing");
            return m.Invoke(o, args);
        }

        public static object CallStatic(Type t, string method, params object[] args)
        {
            var m = t.GetMethods(Any).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null) Assert.Fail($"{t.Name}.{method} missing");
            return m.Invoke(null, args);
        }

        /// <summary>Hit result fields (TextHitResult is a struct).</summary>
        public static (bool hit, int cluster) Hit(object result) =>
            ((bool)Get(result, "hit"), (int)Get(result, "cluster"));
    }
}
