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
    /// Shared setup for the wave 1 tests: OpenType features (component list + &lt;feature&gt; tag),
    /// language-aware shaping (Language + &lt;lang&gt; tag, locl, CJK system face), content measurement,
    /// Auto Size fit steps and padding.
    /// </summary>
    /// <remarks>
    /// Every new API is reached by reflection (<see cref="W1"/>) so this file also compiles against a
    /// build without them; there each test fails with "missing API" instead of breaking the assembly.
    /// </remarks>
    public abstract class Wave1TestBase
    {
        protected UniTextFontStack _stack;
        protected UniTextFont _font;
        protected GameObject _canvasGo;
        protected bool _savedParallel;
        protected bool _savedSysFallback;
        protected readonly List<UniTextFontStack> _extraStacks = new();
        protected readonly List<UniTextFont> _extraFonts = new();

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            var noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            SegHelper.EnsureUnicode();
            SharedGlyphAtlas.Clear();
            _savedParallel = UniText.UseParallel;
            _savedSysFallback = UniTextSettings.UseSystemFontFallback;
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);
            _canvasGo = new GameObject("Canvas", typeof(Canvas));
            _canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            SystemFontFallback.ResetForTests();
            SharedFontCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            UniText.UseParallel = _savedParallel;
            UniTextSettings.SetUseSystemFontFallbackForTests(_savedSysFallback);
            if (_canvasGo != null) UnityEngine.Object.DestroyImmediate(_canvasGo);
            UnityEngine.Object.DestroyImmediate(_stack);
            UnityEngine.Object.DestroyImmediate(_font);
            foreach (var s in _extraStacks) if (s != null) UnityEngine.Object.DestroyImmediate(s);
            foreach (var f in _extraFonts) if (f != null) UnityEngine.Object.DestroyImmediate(f);
            _extraStacks.Clear();
            _extraFonts.Clear();
            SystemFontFallback.ResetForTests();
            SharedFontCache.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        // ------------------------------------------------------------------ helpers

        protected UniText Make(string text, float width = 1400, float height = 400, Action<UniText> setup = null,
            string name = "w1", bool update = true, UniTextFontStack stack = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_canvasGo.transform, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(width, height);
            var t = go.AddComponent<UniText>();
            t.FontStack = stack ?? _stack;
            t.FontSize = 36;
            setup?.Invoke(t);
            t.Text = text;
            if (update) Canvas.ForceUpdateCanvases();
            return t;
        }

        protected static ModRegister Reg(string ruleType, string modifierType) => new()
        {
            Rule = (IParseRule)Activator.CreateInstance(W1.Type(ruleType)),
            Modifier = (BaseModifier)Activator.CreateInstance(W1.Type(modifierType))
        };

        protected static void AddFeatureTag(UniText t) => t.RegisterModifier(Reg("LightSide.FeatureParseRule", "LightSide.FeatureModifier"));
        protected static void AddLangTag(UniText t) => t.RegisterModifier(Reg("LightSide.LanguageParseRule", "LightSide.LanguageModifier"));

        protected static void SetFeatures(UniText t, params string[] features) =>
            W1.Set(t, "FontFeatures", (IReadOnlyList<string>)new List<string>(features));

        /// <summary>Glyphs in logical (cluster) order.</summary>
        protected static PositionedGlyph[] Glyphs(UniText t) => t.ResultGlyphs.ToArray().OrderBy(g => g.cluster).ThenBy(g => g.x).ToArray();

        /// <summary>Pen advance of each glyph (x of the next glyph minus x of this one); last glyph omitted.</summary>
        protected static float[] Advances(PositionedGlyph[] g, int from, int count)
        {
            var r = new float[count];
            for (var i = 0; i < count; i++) r[i] = g[from + i + 1].x - g[from + i].x;
            return r;
        }

        protected static bool AllEqual(float[] a, float eps = 0.01f)
        {
            for (var i = 1; i < a.Length; i++) if (Mathf.Abs(a[i] - a[0]) > eps) return false;
            return true;
        }

        protected static string Fmt(IEnumerable<float> a) => string.Join(", ", a.Select(v => v.ToString("0.###")));

        protected static float PreferredWidth(UniText t)
        {
            ((ILayoutElement)t).CalculateLayoutInputHorizontal();
            return t.preferredWidth;
        }

        protected static float PreferredHeight(UniText t)
        {
            ((ILayoutElement)t).CalculateLayoutInputHorizontal();
            ((ILayoutElement)t).CalculateLayoutInputVertical();
            return t.preferredHeight;
        }
    }

    /// <summary>Reflection access to APIs added in wave 1 (fails the test with a clear message when missing).</summary>
    internal static class W1
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static Type Type(string fullName)
        {
            var t = typeof(UniText).Assembly.GetType(fullName);
            if (t == null) Assert.Fail($"type {fullName} missing");
            return t;
        }

        public static object Get(object o, string prop)
        {
            var p = o.GetType().GetProperty(prop, Any);
            if (p == null) Assert.Fail($"{o.GetType().Name}.{prop} missing");
            return p.GetValue(o);
        }

        public static void Set(object o, string prop, object value)
        {
            var p = o.GetType().GetProperty(prop, Any);
            if (p == null || !p.CanWrite) Assert.Fail($"{o.GetType().Name}.{prop} (settable) missing");
            p.SetValue(o, value);
        }

        public static float Float(object o, string method, params object[] args)
        {
            var types = args.Select(a => a.GetType()).ToArray();
            var m = o.GetType().GetMethod(method, Any, null, types, null);
            if (m == null) Assert.Fail($"{o.GetType().Name}.{method}({string.Join(",", types.Select(t => t.Name))}) missing");
            return (float)m.Invoke(o, args);
        }

        public static object StaticProp(Type t, string prop)
        {
            var p = t.GetProperty(prop, Any);
            if (p == null) Assert.Fail($"{t.Name}.{prop} missing");
            return p.GetValue(null);
        }

        public static object CallStatic(Type t, string method, params object[] args)
        {
            var m = t.GetMethods(Any).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null) Assert.Fail($"{t.Name}.{method} missing");
            return m.Invoke(null, args);
        }
    }
}
