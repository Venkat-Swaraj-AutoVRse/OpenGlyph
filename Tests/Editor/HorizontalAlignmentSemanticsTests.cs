using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpenGlyph;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// B23: <see cref="HorizontalAlignment"/> Left/Right semantics. Plain <see cref="UniText"/> treats them
    /// as the paragraph's start/end edge (RTL + Left = flush right); <see cref="GlyphMeshProUGUI"/> uses
    /// PHYSICAL alignment like TextMeshPro (Left = rect's left edge for every paragraph). Assertions read
    /// the x extents of the live component's positioned glyphs against the rect, on both renderers.
    /// </summary>
    [TestFixture]
    public class HorizontalAlignmentSemanticsTests
    {
        private const UniText.UnifiedRendererMode Legacy = UniText.UnifiedRendererMode.ForceOff;
        private const UniText.UnifiedRendererMode Unified = UniText.UnifiedRendererMode.ForceOn;

        private const string Arabic = "مرحبا بالعالم";
        private const string Latin = "Hello world";
        private const float Width = 600f;
        private const float Height = 100f;
        private const float FontSize = 20f;
        private const float EdgeTol = 6f;      // side bearing + quad padding
        private const float FarMin = 200f;     // the opposite edge must be clearly empty

        private readonly List<Object> _junk = new();
        private UniTextFontStack _stack;
        private Canvas _canvas;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();

            var noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            var arabicPath = Path.Combine(Path.GetDirectoryName(noto)!, "NotoSansArabic-Regular.ttf");
            if (!File.Exists(arabicPath)) Assert.Ignore("NotoSansArabic-Regular.ttf not found.");

            var latin = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            var arabic = UniTextFont.CreateFontAsset(File.ReadAllBytes(arabicPath), 48);
            if (latin == null || arabic == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(latin); _junk.Add(arabic);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(_stack);
            _stack.fonts.Add(latin);
            _stack.fonts.Add(arabic);

            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        private RectTransform NewRect(string name)
        {
            var go = new GameObject(name, typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(_canvas.transform, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(Width, Height);
            return rt;
        }

        private UniText MakeUniText(UniText.UnifiedRendererMode mode, string text, HorizontalAlignment align)
        {
            var t = NewRect("UniText").gameObject.AddComponent<UniText>();
            t.SetUnifiedRendererModeForTests(mode);
            t.FontStack = _stack;
            t.FontSize = FontSize;
            t.HorizontalAlignment = align;
            t.Text = text;
            Canvas.ForceUpdateCanvases();
            return t;
        }

        private GlyphMeshProUGUI MakeGlyphMesh(UniText.UnifiedRendererMode mode, string text, TextAlignmentOptions align)
        {
            var c = NewRect("GlyphMesh").gameObject.AddComponent<GlyphMeshProUGUI>();
            c.SetUnifiedRendererModeForTests(mode);
            c.FontStack = _stack;
            c.fontSize = FontSize;
            c.alignment = align;
            c.text = text;
            Canvas.ForceUpdateCanvases();
            return c;
        }

        private static (float minLeft, float maxRight) Extents(UniText t)
        {
            var g = t.ResultGlyphs;
            Assert.Greater(g.Length, 0, "no positioned glyphs");
            float l = float.MaxValue, r = float.MinValue;
            for (int i = 0; i < g.Length; i++) { l = Mathf.Min(l, g[i].left); r = Mathf.Max(r, g[i].right); }
            return (l, r);
        }

        private static void AssertHugsLeft(UniText t, string what)
        {
            var (l, r) = Extents(t);
            Assert.That(l, Is.InRange(-EdgeTol, EdgeTol), $"{what}: must hug the LEFT edge (minLeft={l}, maxRight={r})");
            Assert.Less(r, Width - FarMin, $"{what}: right side must be empty (maxRight={r})");
        }

        private static void AssertHugsRight(UniText t, string what)
        {
            var (l, r) = Extents(t);
            Assert.That(r, Is.InRange(Width - EdgeTol, Width + EdgeTol), $"{what}: must hug the RIGHT edge (minLeft={l}, maxRight={r})");
            Assert.Greater(l, FarMin, $"{what}: left side must be empty (minLeft={l})");
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void UniText_ArabicLeft_IsStartAligned_HugsRight(UniText.UnifiedRendererMode mode)
            => AssertHugsRight(MakeUniText(mode, Arabic, HorizontalAlignment.Left), "UniText Arabic+Left (start edge)");

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void UniText_ArabicRight_IsEndAligned_HugsLeft(UniText.UnifiedRendererMode mode)
            => AssertHugsLeft(MakeUniText(mode, Arabic, HorizontalAlignment.Right), "UniText Arabic+Right (end edge)");

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void GlyphMeshPro_ArabicLeft_IsPhysical_HugsLeft(UniText.UnifiedRendererMode mode)
            => AssertHugsLeft(MakeGlyphMesh(mode, Arabic, TextAlignmentOptions.TopLeft), "GlyphMeshPro Arabic+TopLeft");

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void GlyphMeshPro_ArabicRight_IsPhysical_HugsRight(UniText.UnifiedRendererMode mode)
            => AssertHugsRight(MakeGlyphMesh(mode, Arabic, TextAlignmentOptions.TopRight), "GlyphMeshPro Arabic+TopRight");

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void LatinLeftRight_SameInBothComponents(UniText.UnifiedRendererMode mode)
        {
            AssertHugsLeft(MakeUniText(mode, Latin, HorizontalAlignment.Left), "UniText Latin+Left");
            AssertHugsRight(MakeUniText(mode, Latin, HorizontalAlignment.Right), "UniText Latin+Right");
            AssertHugsLeft(MakeGlyphMesh(mode, Latin, TextAlignmentOptions.TopLeft), "GlyphMeshPro Latin+TopLeft");
            AssertHugsRight(MakeGlyphMesh(mode, Latin, TextAlignmentOptions.TopRight), "GlyphMeshPro Latin+TopRight");
        }

        [Test]
        public void PhysicalAlignmentFlag_IsOnlyOnGlyphMeshPro()
        {
            Assert.IsFalse(LayoutSettings.Default.physicalAlignment, "engine default must stay logical");
            var plain = MakeUniText(Legacy, Arabic, HorizontalAlignment.Left);
            var gmp = MakeGlyphMesh(Legacy, Arabic, TextAlignmentOptions.TopLeft);
            // Same rect, same text, same requested Left: the two components land on opposite edges.
            Assert.Greater(Extents(plain).minLeft, Extents(gmp).minLeft + FarMin);
        }
    }
}
