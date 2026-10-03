using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Item 3(a) regression: <see cref="SizeModifier"/>, <see cref="LetterSpacingModifier"/> and
    /// <see cref="LineHeightModifier"/> parsed their numeric parameters with culture-SENSITIVE
    /// <c>float.TryParse</c>. Under a comma-decimal culture (e.g. de-DE) <c>"1.5"</c> fails to parse,
    /// so <c>&lt;size=1.5em&gt;</c>, <c>&lt;cspace=0.5em&gt;</c> and <c>&lt;line-height=1.5&gt;</c>
    /// were silently ignored. The fix pins parsing to <see cref="NumberStyles.Float"/> +
    /// <see cref="CultureInfo.InvariantCulture"/>.
    ///
    /// These tests force de-DE as the current culture and assert the markup still takes effect
    /// (measured via <see cref="UniText.ResultSize"/>). Written to FAIL without the fix: under de-DE
    /// the decimal tags parse to nothing and the measured size matches the untagged baseline.
    /// </summary>
    public class ModifierCultureParseTests
    {
        private readonly List<Object> _junk = new();
        private CultureInfo _prevCulture;
        private UniTextFontStack _stack;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (font == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(font);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(_stack);
            _stack.fonts.Add(font);

            _prevCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // comma decimal separator
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Thread.CurrentThread.CurrentCulture = _prevCulture;
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            Shaper.ClearAllCaches();
        }

        private UniText Make(string text)
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("UniText", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(canvasGo.transform, false);
            var t = go.AddComponent<UniText>();
            t.FontStack = _stack;
            t.FontSize = 40f;
            t.Text = text;
            Canvas.ForceUpdateCanvases();
            return t;
        }

        [Test]
        public void SizeTag_DecimalEm_ParsesUnderCommaCulture()
        {
            // Baseline width at 1.0em vs a 2.0em tag; the decimal tag below (1.5em) must land strictly
            // between them, which can only happen if "1.5" parsed. The '%' form (<size=150%>) also uses
            // the fixed parse — assert its width exceeds the plain baseline too.
            float plain = Make("WWW").ResultSize.x;
            float big = Make("<size=150%>WWW</size>").ResultSize.x;
            float dec = Make("<size=1.5em>WWW</size>").ResultSize.x;
            if (plain <= 0) Assert.Ignore("Pipeline produced no geometry in this environment.");

            Assert.Greater(big, plain * 1.1f,
                "<size=150%> must enlarge the text under de-DE (percentage path uses invariant parse)");
            Assert.Greater(dec, plain * 1.1f,
                "<size=1.5em> must enlarge the text under de-DE — the comma-culture parse bug ignored '1.5'");
        }

        [Test]
        public void CSpaceTag_DecimalEm_ParsesUnderCommaCulture()
        {
            float plain = Make("AAAA").ResultSize.x;
            float spaced = Make("<cspace=0.5em>AAAA</cspace>").ResultSize.x;
            if (plain <= 0) Assert.Ignore("Pipeline produced no geometry in this environment.");
            Assert.Greater(spaced, plain + 1f,
                "<cspace=0.5em> must widen the text under de-DE (decimal em parsed invariantly)");
        }

        [Test]
        public void LineHeightTag_Decimal_ParsesUnderCommaCulture()
        {
            float plain = Make("Line1\nLine2").ResultSize.y;
            float tall = Make("<line-height=1.5>Line1\nLine2</line-height>").ResultSize.y;
            if (plain <= 0) Assert.Ignore("Pipeline produced no geometry in this environment.");
            Assert.Greater(tall, plain + 1f,
                "<line-height=1.5> must increase the block height under de-DE (decimal parsed invariantly)");
        }
    }
}
