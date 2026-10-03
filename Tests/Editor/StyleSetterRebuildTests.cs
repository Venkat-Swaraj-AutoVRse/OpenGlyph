using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Item 3(d) regression: the <see cref="UniText.UnifiedRenderer"/>, <see cref="UniText.OverrideStyle"/>,
    /// <see cref="UniText.Style"/> and <see cref="UniText.StyleSheet"/> setters called the inherited
    /// <c>MaskableGraphic.SetVerticesDirty()</c>, which routes to UniText's deliberately-empty
    /// <c>Rebuild()</c> override — so changing them did NOTHING (no re-shade, no re-mesh) until some
    /// other change happened to mark the component dirty. The fix routes them through UniText's own
    /// <c>SetDirty(DirtyFlags.Material)</c> path (as text/fontSize/appearance already do).
    ///
    /// Written to FAIL without the fix: before the fix, after a clean rebuild the setter leaves
    /// <see cref="UniText.CurrentDirtyFlags"/> at None and no mesh re-upload occurs.
    /// </summary>
    public class StyleSetterRebuildTests
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

        private UniText MakeText(UniText.UnifiedRendererMode mode)
        {
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (font == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(font);
            var app = ScriptableObject.CreateInstance<UniTextAppearance>(); _junk.Add(app);

            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("UniText", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(canvasGo.transform, false);
            var t = go.AddComponent<UniText>();
            t.SetUnifiedRendererModeForTests(mode);
            t.FontStack = stack;
            t.Appearance = app;
            t.FontSize = 40f;
            t.Text = "Reading 123";
            Canvas.ForceUpdateCanvases();
            return t;
        }

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();
        }

        [Test]
        public void StyleSetter_MarksDirty_AndRegeneratesMesh()
        {
            var t = MakeText(UniText.UnifiedRendererMode.ForceOn);
            t.OverrideStyle = true;
            Canvas.ForceUpdateCanvases();
            // Clean baseline: a completed rebuild leaves no pending dirty flags.
            Assert.AreEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags, "component is clean before the Style change");

            // Change the style to a visibly different one (different face colour).
            var newStyle = t.Style;
            newStyle.faceColor = new Color(0.04f, 0.08f, 0.12f, 1f);
            t.Style = newStyle;

            // The fix: the setter marks the component dirty via UniText's own path (SetVerticesDirty
            // was a no-op). Before the fix this stays None.
            Assert.AreNotEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags,
                "setting Style must mark the component dirty (SetVerticesDirty was a no-op — the bug)");

            // Driving the canvas must consume that dirty flag by actually rebuilding the mesh — a
            // clean flag afterwards proves the rebuild ran (not that the change was silently dropped).
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags,
                "the Style change was consumed by a real mesh rebuild (flag cleared)");
            Assert.Greater(t.GetDrawnVerticesForTests().Count, 0, "the component still renders geometry after the re-mesh");
        }

        [Test]
        public void OverrideStyleSetter_MarksDirty()
        {
            var t = MakeText(UniText.UnifiedRendererMode.ForceOn);
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags);

            t.OverrideStyle = !t.OverrideStyle;
            Assert.AreNotEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags,
                "toggling OverrideStyle must mark the component dirty");
        }

        [Test]
        public void UnifiedRendererSetter_MarksDirty()
        {
            var t = MakeText(UniText.UnifiedRendererMode.ForceOff);
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags);

            t.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOn;
            Assert.AreNotEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags,
                "switching the render path must mark the component dirty");
        }

        [Test]
        public void StyleSheetSetter_MarksDirty()
        {
            var t = MakeText(UniText.UnifiedRendererMode.ForceOn);
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags);

            var sheet = ScriptableObject.CreateInstance<UniTextStyleSheet>(); _junk.Add(sheet);
            t.StyleSheet = sheet;
            Assert.AreNotEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags,
                "assigning a StyleSheet must mark the component dirty");
        }
    }
}
