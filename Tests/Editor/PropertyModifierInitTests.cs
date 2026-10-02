using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Bug 2 (round 6): a property-driven style (component <see cref="UniText.FontWeight"/> /
    /// <see cref="UniText.FontStyleAxis"/>) with NO markup must still initialize the registered
    /// synthetic modifiers so their OnGlyph/OnShaped run — previously only a markup span prepared
    /// them (<see cref="AttributeParser"/> only Prepare()s modifiers with spans), so property-driven
    /// synthetic bold/italic silently did nothing.
    /// </summary>
    public class PropertyModifierInitTests
    {
        private GameObject _canvas, _go;
        private UniTextFont _reg;
        private UniTextFontStack _stack;
        private BoldModifier _bold;
        private ItalicModifier _italic;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans not found.");
            _reg = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (_reg == null) Assert.Ignore("Regular face failed.");
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_reg);

            _canvas = new GameObject("C", typeof(Canvas));
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            _go = new GameObject("T", typeof(RectTransform));
            _go.transform.SetParent(_canvas.transform, false);
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvas != null) Object.DestroyImmediate(_canvas);
            if (_stack != null) Object.DestroyImmediate(_stack);
            if (_reg != null) Object.DestroyImmediate(_reg);
            Shaper.ClearAllCaches();
        }

        private UniText MakeText(int weight, StyleAxis style)
        {
            var t = _go.AddComponent<UniText>();
            t.FontStack = _stack;
            t.FontSize = 48f;
            _bold = new BoldModifier();
            _italic = new ItalicModifier();
            t.RegisterModifier(new ModRegister { Modifier = _bold, Rule = new BoldParseRule() });
            t.RegisterModifier(new ModRegister { Modifier = _italic, Rule = new ItalicParseRule() });
            if (weight != FontStyleSpec.NormalWeight) t.FontWeight = weight;
            if (style != StyleAxis.Normal) t.FontStyleAxis = style;
            t.Text = "Reading"; // NO markup
            return t;
        }

        [Test]
        public void FontWeight700_NoMarkup_InitializesBoldModifier()
        {
            MakeText(700, StyleAxis.Normal);
            Canvas.ForceUpdateCanvases();
            Assert.IsTrue(_bold.IsInitialized,
                "Property FontWeight 700 (no markup) must initialize the registered BoldModifier so synthetic bold applies.");
        }

        [Test]
        public void FontStyleItalic_NoMarkup_InitializesItalicModifier()
        {
            MakeText(400, StyleAxis.Italic);
            Canvas.ForceUpdateCanvases();
            Assert.IsTrue(_italic.IsInitialized,
                "Property FontStyleAxis Italic (no markup) must initialize the registered ItalicModifier so synthetic shear applies.");
        }

        [Test]
        public void NormalProperties_NoMarkup_DoNotInitializeModifiers()
        {
            MakeText(400, StyleAxis.Normal);
            Canvas.ForceUpdateCanvases();
            Assert.IsFalse(_bold.IsInitialized, "Normal weight must not initialize BoldModifier.");
            Assert.IsFalse(_italic.IsInitialized, "Normal style must not initialize ItalicModifier.");
        }
    }
}
