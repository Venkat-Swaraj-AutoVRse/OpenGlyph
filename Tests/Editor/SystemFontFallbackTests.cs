using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>C1: CJK renders through the lazily loaded OS font when the stack has none.</summary>
    [TestFixture]
    public class SystemFontFallbackTests
    {
        private const string Cjk = "你好世界 こんにちは 안녕하세요";

        private UniTextFontStack _stack;
        private UniTextFont _font;
        private GameObject _canvasGo;
        private bool _savedSetting;

        private static bool WindowsFontsPresent()
        {
            var d = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Fonts);
            return File.Exists(Path.Combine(d, "msyh.ttc")) && File.Exists(Path.Combine(d, "malgun.ttf")) &&
                   (File.Exists(Path.Combine(d, "YuGothM.ttc")) || File.Exists(Path.Combine(d, "msgothic.ttc")));
        }

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            var noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            SharedGlyphAtlas.Clear();
            _savedSetting = UniTextSettings.UseSystemFontFallback;
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
            UniTextSettings.SetUseSystemFontFallbackForTests(_savedSetting);
            Object.DestroyImmediate(_canvasGo);
            Object.DestroyImmediate(_stack);
            Object.DestroyImmediate(_font);
            SystemFontFallback.ResetForTests();
            SharedFontCache.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        private UniText Make(string text)
        {
            var go = new GameObject("t", typeof(RectTransform));
            go.transform.SetParent(_canvasGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(600, 100);
            var t = go.AddComponent<UniText>();
            t.FontStack = _stack;
            t.FontSize = 24;
            t.Text = text;
            Canvas.ForceUpdateCanvases();
            return t;
        }

        private static int CountNotdef(UniText t)
        {
            var n = 0;
            foreach (var g in t.ResultGlyphs) if (g.glyphId == 0) n++;
            return n;
        }

        [Test]
        public void Cjk_UsesSystemFont_NoNotdef()
        {
#if !(UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
            Assert.Ignore("Windows system font paths only.");
#endif
            if (!WindowsFontsPresent()) Assert.Ignore("Windows CJK fonts (msyh/malgun/YuGoth) not present.");
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "must be lazy: nothing loaded before first CJK text.");

            var t = Make(Cjk);

            Assert.IsTrue(SystemFontFallback.AnyLoaded, "system font loads on first CJK code point.");
            Assert.Greater(t.ResultGlyphs.Length, 0);
            Assert.AreEqual(0, CountNotdef(t), "CJK must shape to real glyphs (no .notdef).");
        }

        [Test]
        public void SettingOff_StaysTofu()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(false);
            var t = Make(Cjk);
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "nothing may load when disabled.");
            Assert.Greater(CountNotdef(t), 0, "with the fallback off, CJK stays .notdef (tofu).");
        }

        [Test]
        public void NonCjkText_NeverLoadsSystemFont()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            Make("Hello world");
            Assert.IsFalse(SystemFontFallback.AnyLoaded);
        }
    }
}
