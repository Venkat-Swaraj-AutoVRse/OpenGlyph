using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression guard: features a plain <see cref="UniText"/> does not use must cost nothing. A plain
    /// component (no GlyphMeshPro, padding, language, features, gradient or animation) must not run the
    /// TMP-parity, language or vertex-effect passes, and the system-font prepare scan must run only before
    /// a first pass on text it has not already prepared (never on layout / colour / mesh-only rebuilds).
    /// </summary>
    [TestFixture]
    public class PlainTextCostTests
    {
        // Latin + accented Latin + Cyrillic + Greek + Common punctuation: all covered by Noto Sans.
        private const string Text = "Héllo wörld — Привет мир, Ελληνικά. Ünïcödé “quotes” 123 ÀÉÎÕÜ ñç ß";

        private UniTextFontStack _stack;
        private UniTextFont _font;
        private GameObject _canvasGo;
        private bool _savedSetting;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            var noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            SegHelper.EnsureUnicode();
            _savedSetting = UniTextSettings.UseSystemFontFallback;
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);
            _canvasGo = new GameObject("Canvas", typeof(Canvas));
            _canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            SharedFontCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(_savedSetting);
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
            Object.DestroyImmediate(_stack);
            Object.DestroyImmediate(_font);
            SharedFontCache.Clear();
            UnifiedRenderBuilder.ResetShared();
        }

        private UniText Make(string text)
        {
            var go = new GameObject("plain", typeof(RectTransform));
            go.transform.SetParent(_canvasGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(800, 400);
            var t = go.AddComponent<UniText>();
            t.FontStack = _stack;
            t.FontSize = 24;
            t.WordWrap = true;
            t.Text = text;
            Canvas.ForceUpdateCanvases();
            return t;
        }

        private readonly struct Counters
        {
            public readonly long prepare, firstPass, layout, tmpParity, language, captures, uploads;

            public Counters(bool _)
            {
                prepare = SystemFontFallback.PrepareForTextCalls;
                firstPass = TextProcessor.FirstPassCount;
                layout = TextProcessor.LayoutCount;
                tmpParity = TextProcessor.TmpParityPassCount;
                language = TextProcessor.LanguagePassCount;
                captures = UniText.VertexEffectCaptures;
                uploads = UniText.VertexEffectUploads;
            }
        }

        private static void AssertUnusedFeaturesIdle(Counters before, string step)
        {
            var now = new Counters(true);
            Assert.AreEqual(before.tmpParity, now.tmpParity, $"{step}: TMP-parity spacing/indent pass ran for plain text");
            Assert.AreEqual(before.language, now.language, $"{step}: language table built without <lang> spans");
            Assert.AreEqual(before.captures, now.captures, $"{step}: vertex-effect capture ran without effects");
            Assert.AreEqual(before.uploads, now.uploads, $"{step}: vertex-effect upload ran without effects");
        }

        private static bool PrivateBool(UniText t, string field) =>
            (bool)typeof(UniText).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(t);

        [Test]
        public void PlainComponent_UnusedFeaturesCostNothing()
        {
            var before = new Counters(true);
            var t = Make(Text);
            var afterCreate = new Counters(true);
            Assert.Greater(afterCreate.firstPass, before.firstPass, "creation shapes the text");
            Assert.AreEqual(before.prepare + 1, afterCreate.prepare, "creation prepares system fonts exactly once");
            AssertUnusedFeaturesIdle(before, "create");
            Assert.IsFalse(PrivateBool(t, "fillHooked"), "no gradient fill -> no per-glyph fill hook");
            Assert.IsFalse(PrivateBool(t, "effectHooked"), "no vertex effects -> no per-glyph tagging hook");

            // Layout-only rebuilds (rect size): relayout, no first pass, no system-font scan.
            var rt = (RectTransform)t.transform;
            for (var i = 0; i < 3; i++)
            {
                var c = new Counters(true);
                rt.sizeDelta = new Vector2(300 + i * 150, 400);
                Canvas.ForceUpdateCanvases();
                var n = new Counters(true);
                Assert.Greater(n.layout, c.layout, "rect change relays out");
                Assert.AreEqual(c.firstPass, n.firstPass, "rect change must not reshape");
                Assert.AreEqual(c.prepare, n.prepare, "rect change must not rescan for system fonts");
                AssertUnusedFeaturesIdle(c, "layout");
            }

            // Colour-only rebuild: mesh only.
            {
                var c = new Counters(true);
                t.color = Color.red;
                Canvas.ForceUpdateCanvases();
                var n = new Counters(true);
                Assert.AreEqual(c.firstPass, n.firstPass, "colour change must not reshape");
                Assert.AreEqual(c.prepare, n.prepare, "colour change must not rescan for system fonts");
                AssertUnusedFeaturesIdle(c, "colour");
            }

            // Reshape of the SAME text: first pass runs, the prepare scan is memoised.
            {
                var c = new Counters(true);
                t.SetDirty(UniText.DirtyFlags.Text);
                Canvas.ForceUpdateCanvases();
                var n = new Counters(true);
                Assert.Greater(n.firstPass, c.firstPass, "a text-dirty rebuild reshapes");
                Assert.AreEqual(c.prepare, n.prepare, "unchanged text is not rescanned for system fonts");
                AssertUnusedFeaturesIdle(c, "reshape");
            }

            // Equal text in a new string instance: still no rescan.
            {
                var c = new Counters(true);
                t.Text = new string(Text.ToCharArray());
                Canvas.ForceUpdateCanvases();
                Assert.AreEqual(c.prepare, new Counters(true).prepare, "equal text is not rescanned");
            }

            // New text: exactly one scan.
            {
                var c = new Counters(true);
                t.Text = Text + " Ω";
                Canvas.ForceUpdateCanvases();
                var n = new Counters(true);
                Assert.Greater(n.firstPass, c.firstPass);
                Assert.AreEqual(c.prepare + 1, n.prepare, "changed text is scanned once (not again in the mesh pass)");
                AssertUnusedFeaturesIdle(c, "new text");
            }
        }

        [Test]
        public void FontStackChange_RescansSameText()
        {
            var t = Make(Text);
            var c = SystemFontFallback.PrepareForTextCalls;
            var other = ScriptableObject.CreateInstance<UniTextFontStack>();
            try
            {
                other.fonts.Add(_font);
                t.FontStack = other;
                Canvas.ForceUpdateCanvases();
                Assert.AreEqual(c + 1, SystemFontFallback.PrepareForTextCalls, "a new font stack re-prepares the same text");
            }
            finally
            {
                t.FontStack = _stack;
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void FallbackReset_RescansSameText()
        {
            var t = Make(Text);
            var c = SystemFontFallback.PrepareForTextCalls;
            SystemFontFallback.ResetForTests();
            t.SetDirty(UniText.DirtyFlags.Text);
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(c + 1, SystemFontFallback.PrepareForTextCalls, "after a fallback reset the text is prepared again");
        }
    }
}
