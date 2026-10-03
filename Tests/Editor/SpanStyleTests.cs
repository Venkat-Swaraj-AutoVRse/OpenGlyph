using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2: per-span style markup on the unified path.
    /// Covers (1) grammar parsing, (2) nesting/composition, (3) a deduped StyleTable row per distinct
    /// span style, and (4) end-to-end: distinct per-glyph styleIdx within a SINGLE renderer.
    /// </summary>
    public class SpanStyleTests
    {
        private readonly List<Object> _junk = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        // ---- (1) grammar parsing ---------------------------------------------------------------

        [Test]
        public void ParseOutline_ColorAndWidth()
        {
            Assert.IsTrue(SpanStyleMarkup.TryParseOutline("#FF0000,0.25", out var c, out var w));
            Assert.AreEqual(new Color(1, 0, 0, 1), c);
            Assert.AreEqual(0.25f, w, 1e-5f);
        }

        [Test]
        public void ParseOutline_WithExplicitAlpha()
        {
            Assert.IsTrue(SpanStyleMarkup.TryParseOutline("#00FF0080,0.1", out var c, out _));
            Assert.AreEqual(0f, c.r, 1e-3f);
            Assert.AreEqual(1f, c.g, 1e-3f);
            Assert.AreEqual(128 / 255f, c.a, 1e-3f);
        }

        [Test]
        public void ParseUnderlay_FullParams()
        {
            Assert.IsTrue(SpanStyleMarkup.TryParseUnderlay("#000000FF,0.01,-0.02,0.03,0.4",
                out var c, out var x, out var y, out var d, out var s));
            Assert.AreEqual(1f, c.a, 1e-3f);
            Assert.AreEqual(0.01f, x, 1e-5f);
            Assert.AreEqual(-0.02f, y, 1e-5f);
            Assert.AreEqual(0.03f, d, 1e-5f);
            Assert.AreEqual(0.4f, s, 1e-5f);
        }

        [Test]
        public void ParseScalar_DilateSoftness()
        {
            Assert.IsTrue(SpanStyleMarkup.TryParseScalar("0.3", out var v));
            Assert.AreEqual(0.3f, v, 1e-5f);
            Assert.IsFalse(SpanStyleMarkup.TryParseScalar("not-a-number", out _));
        }

        [Test]
        public void ParseMalformed_Rejected()
        {
            Assert.IsFalse(SpanStyleMarkup.TryParseOutline("", out _, out _));
            Assert.IsFalse(SpanStyleMarkup.TryParseOutline("notacolor,0.2", out _, out _));
            Assert.IsFalse(SpanStyleMarkup.TryParseHtmlColor("FF0000", out _), "missing #");
        }

        // ---- (2) nesting / composition --------------------------------------------------------

        [Test]
        public void Nesting_InnerLayersOverOuter_PerField()
        {
            var outer = new SpanStyleOverride { set = SpanStyleOverride.F.Outline, outlineColor = Color.red, outlineWidth = 0.2f };
            var inner = new SpanStyleOverride { set = SpanStyleOverride.F.Dilate, faceDilate = 0.5f };
            var merged = outer.MergeOver(inner);

            Assert.AreEqual(SpanStyleOverride.F.Outline | SpanStyleOverride.F.Dilate, merged.set,
                "merged keeps outer outline AND inner dilate");

            var composed = merged.ComposeOnto(GlyphStyle.Default);
            Assert.AreEqual(Color.red, composed.outlineColor);
            Assert.AreEqual(0.2f, composed.outlineWidth, 1e-5f);
            Assert.AreEqual(0.5f, composed.faceDilate, 1e-5f);
        }

        [Test]
        public void Nesting_InnerSameField_Wins()
        {
            var outer = new SpanStyleOverride { set = SpanStyleOverride.F.Softness, softness = 0.1f };
            var inner = new SpanStyleOverride { set = SpanStyleOverride.F.Softness, softness = 0.9f };
            var composed = outer.MergeOver(inner).ComposeOnto(GlyphStyle.Default);
            Assert.AreEqual(0.9f, composed.softness, 1e-5f, "inner span wins on the same field");
        }

        [Test]
        public void StyleSheet_WholeStyle_ThenFieldOverride()
        {
            var sheet = ScriptableObject.CreateInstance<UniTextStyleSheet>(); _junk.Add(sheet);
            var heading = UniTextStyle.Default; heading.faceColor = Color.blue; heading.outlineColor = Color.green; heading.outlineWidth = 0.3f;
            sheet.Set("Heading", heading);
            Assert.IsTrue(sheet.TryGet("heading", out var got), "case-insensitive lookup");
            Assert.AreEqual(Color.blue, got.faceColor);

            var whole = new SpanStyleOverride { set = SpanStyleOverride.F.Face, whole = got.ToGlyphStyle() };
            var innerOutline = new SpanStyleOverride { set = SpanStyleOverride.F.Outline, outlineColor = Color.red, outlineWidth = 0.1f };
            var composed = whole.MergeOver(innerOutline).ComposeOnto(GlyphStyle.Default);
            Assert.AreEqual(Color.blue, composed.faceColor, "named style's face kept");
            Assert.AreEqual(Color.red, composed.outlineColor, "inner outline overrides the named style's outline");
        }

        // ---- (3) dedup: a row per distinct span style -----------------------------------------

        [Test]
        public void Collector_DedupsIdenticalStyles_RowPerDistinct()
        {
            var c = new SpanStyleCollector();
            c.Reset(GlyphStyle.Default);
            Assert.AreEqual(1, c.Count, "base occupies local id 0");

            var red = GlyphStyle.Default; red.outlineColor = Color.red; red.outlineWidth = 0.2f;
            var blue = GlyphStyle.Default; blue.outlineColor = Color.blue; blue.outlineWidth = 0.2f;

            int r1 = c.GetOrAdd(red);
            int r2 = c.GetOrAdd(red);   // identical -> same row
            int b1 = c.GetOrAdd(blue);

            Assert.AreEqual(r1, r2, "identical span styles dedup to one row");
            Assert.AreNotEqual(r1, b1, "distinct span styles get distinct rows");
            Assert.AreEqual(3, c.Count, "base + red + blue = 3 distinct rows");
        }

        // ---- (4) end-to-end: per-glyph styleIdx in ONE renderer -------------------------------

        private UniText MakeText(string text, UniTextFontStack stack, UniText.UnifiedRendererMode mode = UniText.UnifiedRendererMode.ForceOn)
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("UniText", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(canvasGo.transform, false);
            var t = go.AddComponent<UniText>();
            t.SetUnifiedRendererModeForTests(mode);
            // Register the per-span style tags.
            t.RegisterModifier(new ModRegister { Modifier = new SpanStyleModifier(SpanStyleModifier.Kind.Outline), Rule = new OutlineParseRule() });
            t.RegisterModifier(new ModRegister { Modifier = new SpanStyleModifier(SpanStyleModifier.Kind.Underlay), Rule = new UnderlayParseRule() });
            t.RegisterModifier(new ModRegister { Modifier = new SpanStyleModifier(SpanStyleModifier.Kind.Dilate), Rule = new DilateParseRule() });
            t.RegisterModifier(new ModRegister { Modifier = new SpanStyleModifier(SpanStyleModifier.Kind.Softness), Rule = new SoftnessParseRule() });
            t.FontStack = stack;
            t.FontSize = 40f;
            t.Text = text;
            Canvas.ForceUpdateCanvases();
            return t;
        }

        [Test]
        public void EndToEnd_PerSpanOutline_OneRenderer_DistinctStyleIdxPerGlyph()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (font == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(font);

            // "AB<outline=#FF0000,0.2>CD</outline>EF" — the middle span has an outline, the rest doesn't.
            var t = MakeText("AB<outline=#FF0000,0.2>CD</outline>EF", stack);
            if (t.GetDrawnVerticesForTests().Count == 0) Assert.Ignore("Pipeline produced no geometry in this environment.");

            // One SDF font -> exactly ONE unified draw group (renderer count = 1).
            Assert.AreEqual(1, t.UnifiedGroupCountForTests, "a single SDF font + a per-span outline is ONE renderer");
            foreach (var sh in t.GetUnifiedGroupShaderNamesForTests())
                Assert.AreEqual("UniText/Uber", sh);

            // The collector resolved at least TWO distinct styles (base + outline span).
            Assert.GreaterOrEqual(t.SpanStyleLocalCountForTests, 2, "base + outline span = >=2 distinct styles");

            // Per-glyph styleIdx (UV1.w) is not uniform: the outlined glyphs point at a different row.
            var idx = t.GetUnifiedStyleIndicesForTests();
            Assert.Greater(idx.Count, 0);
            Assert.Greater(idx.Distinct().Count(), 1,
                "the outline span's glyphs carry a different styleIdx than the plain glyphs (per-glyph row in one renderer)");
        }

        // ---- B8: legacy renderer warns (once) that span styles are not rendered ---------------------

        private UniTextFontStack MakeNotoStack()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (font == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(font);
            return stack;
        }

        [Test]
        public void LegacyRenderer_SpanStyles_WarnsExactlyOncePerComponent()
        {
            var stack = MakeNotoStack();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"\[OpenGlyph\] '.*' uses <outline>/<underlay>/\.\.\. span styles, which only render with the unified renderer"));
            var t = MakeText("AB<outline=#FF0000,0.2>CD</outline>EF", stack, UniText.UnifiedRendererMode.ForceOff);
            if (t.GetDrawnVerticesForTests().Count == 0) Assert.Ignore("Pipeline produced no geometry in this environment.");
            // Re-render several times: must not warn again (LogAssert fails the test on any unexpected/extra warning).
            t.Text = "AB<outline=#00FF00,0.2>CD</outline>EFG";
            Canvas.ForceUpdateCanvases();
            t.Text = "AB<outline=#0000FF,0.2>CD</outline>EFGH";
            Canvas.ForceUpdateCanvases();
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void LegacyRenderer_NoSpanStyles_NoWarning()
        {
            var stack = MakeNotoStack();
            var t = MakeText("ABCDEF", stack, UniText.UnifiedRendererMode.ForceOff);
            if (t.GetDrawnVerticesForTests().Count == 0) Assert.Ignore("Pipeline produced no geometry in this environment.");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void UnifiedRenderer_SpanStyles_NoWarning()
        {
            var stack = MakeNotoStack();
            var t = MakeText("AB<outline=#FF0000,0.2>CD</outline>EF", stack, UniText.UnifiedRendererMode.ForceOn);
            if (t.GetDrawnVerticesForTests().Count == 0) Assert.Ignore("Pipeline produced no geometry in this environment.");
            LogAssert.NoUnexpectedReceived();
        }
    }
}
