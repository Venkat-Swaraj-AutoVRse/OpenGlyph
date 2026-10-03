using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpenGlyph;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>
    /// <see cref="UniText.Overflow"/> (Overflow / Truncate / Ellipsis / Clip) and the
    /// <see cref="GlyphMeshProUGUI.overflowMode"/> mapping (QA bug B13), each run on BOTH the legacy and
    /// the unified renderer. Layout assertions read the real positioned glyphs of a live component.
    /// </summary>
    [TestFixture]
    public class OverflowModeTests
    {
        private const UniText.UnifiedRendererMode Legacy = UniText.UnifiedRendererMode.ForceOff;
        private const UniText.UnifiedRendererMode Unified = UniText.UnifiedRendererMode.ForceOn;

        private const string Lines6 = "Alpha\nBravo\nCharlie\nDelta\nEcho\nFoxtrot";
        private const string Paragraph =
            "The quick brown fox jumps over the lazy dog and keeps running through the forest until night falls";

        private const float FontSize = 20f;
        private const float Eps = 0.05f;

        private readonly List<Object> _junk = new();
        private UniTextFontStack _stack;
        private UniTextFont _font;
        private Canvas _canvas;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();

            var noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (_font == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(_font);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(_stack);
            _stack.fonts.Add(_font);

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

        // ------------------------------------------------------------------ helpers

        private UniText Make(UniText.UnifiedRendererMode mode, string text, float w, float h, TextOverflow overflow,
            Transform parent = null, UniTextFontStack stack = null, bool wordWrap = true)
        {
            var go = new GameObject("UniText", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(parent != null ? parent : _canvas.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(w, h);
            var t = go.AddComponent<UniText>();
            t.SetUnifiedRendererModeForTests(mode);
            t.FontStack = stack != null ? stack : _stack;
            t.WordWrap = wordWrap;
            t.FontSize = FontSize;
            t.Overflow = overflow;
            t.Text = text;
            Canvas.ForceUpdateCanvases();
            return t;
        }

        /// <summary>Distinct baselines (visual lines) of the laid out glyphs, top to bottom.</summary>
        private static List<float> Baselines(UniText t)
        {
            var ys = new List<float>();
            foreach (var g in t.ResultGlyphs)
            {
                var found = false;
                foreach (var y in ys) if (Mathf.Abs(y - g.y) < 0.01f) { found = true; break; }
                if (!found) ys.Add(g.y);
            }
            ys.Sort();
            return ys;
        }

        private static float MaxBottom(UniText t)
        {
            var b = float.MinValue;
            foreach (var g in t.ResultGlyphs) b = Mathf.Max(b, g.bottom);
            return b;
        }

        /// <summary>Number of lines of the UNtruncated layout whose bottom fits in <paramref name="height"/>.</summary>
        private int FittingLines(UniText.UnifiedRendererMode mode, string text, float w, float height)
        {
            var full = Make(mode, text, w, height, TextOverflow.Overflow);
            var bottomByLine = new SortedDictionary<float, float>();
            foreach (var g in full.ResultGlyphs)
            {
                var key = Mathf.Round(g.y * 100f) / 100f;
                bottomByLine[key] = Mathf.Max(bottomByLine.TryGetValue(key, out var b) ? b : float.MinValue, g.bottom);
            }
            var n = 0;
            foreach (var kv in bottomByLine) if (kv.Value <= height + Eps) n++;
            return Mathf.Max(1, n);
        }

        private uint EllipsisGlyphId(UniText t) => t.MainFont.GetGlyphIndexForUnicode(0x2026);

        private static int CountGlyphs(UniText t, int glyphId)
        {
            var n = 0;
            foreach (var g in t.ResultGlyphs) if (g.glyphId == glyphId) n++;
            return n;
        }

        // ------------------------------------------------------------------ default

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Default_IsOverflow_AndLayoutUnchanged(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, Lines6, 200f, 70f, TextOverflow.Overflow);
            Assert.AreEqual(TextOverflow.Overflow, t.Overflow);
            Assert.AreEqual(TextOverflow.Overflow, new GameObject("d", typeof(RectTransform)).AddComponent<UniText>().Overflow,
                "a fresh component defaults to Overflow");

            var baseline = Baselines(t);
            Assert.AreEqual(6, baseline.Count, "Overflow keeps every line");
            Assert.Greater(MaxBottom(t), 70f, "Overflow lets the text spill below the rect");

            // Round trip through another mode must restore the identical layout.
            var snapshot = new List<(int id, int cl, float x, float y)>();
            foreach (var g in t.ResultGlyphs) snapshot.Add((g.glyphId, g.cluster, g.x, g.y));
            t.Overflow = TextOverflow.Truncate; Canvas.ForceUpdateCanvases();
            Assert.Less(Baselines(t).Count, 6);
            t.Overflow = TextOverflow.Overflow; Canvas.ForceUpdateCanvases();
            var i = 0;
            Assert.AreEqual(snapshot.Count, t.ResultGlyphs.Length);
            foreach (var g in t.ResultGlyphs)
            {
                Assert.AreEqual(snapshot[i].id, g.glyphId); Assert.AreEqual(snapshot[i].cl, g.cluster);
                Assert.AreEqual(snapshot[i].x, g.x, 1e-3f); Assert.AreEqual(snapshot[i].y, g.y, 1e-3f);
                i++;
            }
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Setter_MarksDirty_ThroughSetDirty(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, Lines6, 200f, 70f, TextOverflow.Overflow);
            Assert.AreEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags);
            t.Overflow = TextOverflow.Truncate;
            Assert.AreNotEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags, "Overflow setter must mark the component dirty");
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(UniText.DirtyFlags.None, t.CurrentDirtyFlags, "the change was consumed by a rebuild");
        }

        // ------------------------------------------------------------------ truncate

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Truncate_KeepsExactlyTheFittingLines(UniText.UnifiedRendererMode mode)
        {
            const float h = 100f;
            var expected = FittingLines(mode, Lines6, 200f, h);
            Assert.That(expected, Is.InRange(2, 5), "test premise: some but not all lines fit");

            var t = Make(mode, Lines6, 200f, h, TextOverflow.Truncate);
            Assert.AreEqual(expected, Baselines(t).Count, "visible line count");
            Assert.LessOrEqual(MaxBottom(t), h + Eps, "no glyph's bottom may exceed the rect height");
            Assert.AreEqual(0, CountGlyphs(t, (int)EllipsisGlyphId(t)), "Truncate never adds an ellipsis");
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Truncate_KeepsFirstLine_EvenWhenItDoesNotFit(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, Lines6, 200f, 5f, TextOverflow.Truncate);
            Assert.AreEqual(1, Baselines(t).Count);
            Assert.Greater(t.ResultGlyphs.Length, 0);
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Truncate_NoOpWhenTextFits(UniText.UnifiedRendererMode mode)
        {
            var truncated = Make(mode, Lines6, 200f, 400f, TextOverflow.Truncate);
            var plain = Make(mode, Lines6, 200f, 400f, TextOverflow.Overflow);
            Assert.AreEqual(plain.ResultGlyphs.Length, truncated.ResultGlyphs.Length);
            for (var i = 0; i < plain.ResultGlyphs.Length; i++)
            {
                Assert.AreEqual(plain.ResultGlyphs[i].glyphId, truncated.ResultGlyphs[i].glyphId);
                Assert.AreEqual(plain.ResultGlyphs[i].x, truncated.ResultGlyphs[i].x, 1e-3f);
                Assert.AreEqual(plain.ResultGlyphs[i].y, truncated.ResultGlyphs[i].y, 1e-3f);
            }
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Truncate_WordWrappedParagraph(UniText.UnifiedRendererMode mode)
        {
            const float h = 60f;
            var expected = FittingLines(mode, Paragraph, 150f, h);
            var t = Make(mode, Paragraph, 150f, h, TextOverflow.Truncate);
            Assert.AreEqual(expected, Baselines(t).Count);
            Assert.LessOrEqual(MaxBottom(t), h + Eps);
        }

        // ------------------------------------------------------------------ ellipsis

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Ellipsis_LastGlyphIsEllipsis_AndLineFitsWidth(UniText.UnifiedRendererMode mode)
        {
            const float w = 150f, h = 60f;
            var expected = FittingLines(mode, Paragraph, w, h);
            var t = Make(mode, Paragraph, w, h, TextOverflow.Ellipsis);

            var eId = (int)EllipsisGlyphId(t);
            Assert.AreNotEqual(0, eId, "the test font must have U+2026");
            var glyphs = t.ResultGlyphs;
            Assert.AreEqual(expected, Baselines(t).Count, "same visible lines as Truncate");
            Assert.AreEqual(eId, glyphs[glyphs.Length - 1].glyphId, "the last visible glyph is the ellipsis");
            Assert.AreEqual(1, CountGlyphs(t, eId), "exactly one ellipsis");
            Assert.LessOrEqual(glyphs[glyphs.Length - 1].right, w + Eps, "line + ellipsis fits the rect width");
            Assert.LessOrEqual(MaxBottom(t), h + Eps);

            // The ellipsis sits on the last kept line, after the glyphs it follows.
            var last = glyphs[glyphs.Length - 1];
            Assert.GreaterOrEqual(last.left, glyphs[glyphs.Length - 2].right - Eps);
            Assert.AreEqual(glyphs[glyphs.Length - 2].y, last.y, 0.01f);
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Ellipsis_OnTextThatFits_AddsNoEllipsis(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, "Short text", 400f, 100f, TextOverflow.Ellipsis);
            Assert.AreEqual(0, CountGlyphs(t, (int)EllipsisGlyphId(t)));
            var plain = Make(mode, "Short text", 400f, 100f, TextOverflow.Overflow);
            Assert.AreEqual(plain.ResultGlyphs.Length, t.ResultGlyphs.Length, "text that fits is untouched");
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Ellipsis_WhenLineHasRoomToSpare_StillEndsWithEllipsis(UniText.UnifiedRendererMode mode)
        {
            // Hard line breaks: the last kept line is short, so nothing needs removing before the ellipsis.
            var t = Make(mode, Lines6, 300f, 70f, TextOverflow.Ellipsis);
            var eId = (int)EllipsisGlyphId(t);
            var g = t.ResultGlyphs;
            Assert.AreEqual(eId, g[g.Length - 1].glyphId);
            Assert.AreEqual(1, CountGlyphs(t, eId));
            Assert.LessOrEqual(MaxBottom(t), 70f + Eps);
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Ellipsis_NeverSplitsGraphemeCluster(UniText.UnifiedRendererMode mode)
        {
            // "e" + COMBINING ACUTE, repeated: every grapheme is two codepoints. Whatever width the
            // ellipsis forces, a kept base letter must keep its mark (same glyph count per cluster as the
            // untruncated layout) and no orphan mark cluster may remain.
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 60; i++) sb.Append("é");
            var text = sb.ToString();

            var full = Make(mode, text, 120f, 400f, TextOverflow.Overflow);
            var perCluster = new Dictionary<int, int>();
            foreach (var g in full.ResultGlyphs)
                perCluster[g.cluster] = perCluster.TryGetValue(g.cluster, out var n) ? n + 1 : 1;

            var t = Make(mode, text, 120f, 40f, TextOverflow.Ellipsis);
            var eId = (int)EllipsisGlyphId(t);
            var glyphs = t.ResultGlyphs;
            Assert.AreEqual(eId, glyphs[glyphs.Length - 1].glyphId);

            var kept = new Dictionary<int, int>();
            for (var i = 0; i < glyphs.Length - 1; i++)
                kept[glyphs[i].cluster] = kept.TryGetValue(glyphs[i].cluster, out var n) ? n + 1 : 1;
            foreach (var kv in kept)
            {
                Assert.AreEqual(0, kv.Key % 2, "only grapheme starts (base letters) carry glyphs: " + kv.Key);
                Assert.AreEqual(perCluster[kv.Key], kv.Value, "grapheme at cluster " + kv.Key + " kept whole");
            }
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Ellipsis_Rtl_GoesAtLogicalEnd_VisualLeft(UniText.UnifiedRendererMode mode)
        {
            var hebrewPath = Path.Combine(Path.GetDirectoryName(MsdfTestUtil.FindNotoSansPath()), "NotoSansHebrew-Regular.ttf");
            if (!File.Exists(hebrewPath)) Assert.Ignore("NotoSansHebrew-Regular.ttf not found.");
            var hebrew = UniTextFont.CreateFontAsset(File.ReadAllBytes(hebrewPath), 48);
            if (hebrew == null) Assert.Ignore("Hebrew font asset creation failed.");
            _junk.Add(hebrew);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(_font);
            stack.fonts.Add(hebrew);

            const string text = "שלום עולם זהו טקסט ארוך מאוד שממשיך ממשיך וממשיך עד שאין לו סוף בכלל ועוד קצת";
            const float w = 150f, h = 60f;
            var t = Make(mode, text, w, h, TextOverflow.Ellipsis, stack: stack);
            t.BaseDirection = TextDirection.RightToLeft;
            Canvas.ForceUpdateCanvases();

            var eId = (int)EllipsisGlyphId(t);
            Assert.AreEqual(1, CountGlyphs(t, eId));
            var glyphs = t.ResultGlyphs;
            PositionedGlyph e = default;
            foreach (var g in glyphs) if (g.glyphId == eId) e = g;

            // The ellipsis shares the last line with Hebrew glyphs and is its LEFTMOST glyph (RTL logical end).
            var lineMinLeft = float.MaxValue;
            foreach (var g in glyphs)
                if (Mathf.Abs(g.y - e.y) < 0.01f) lineMinLeft = Mathf.Min(lineMinLeft, g.left);
            Assert.AreEqual(lineMinLeft, e.left, 0.01f, "ellipsis is the visually leftmost glyph of the last RTL line");
            Assert.GreaterOrEqual(e.left, -Eps, "line + ellipsis fits the rect");
            foreach (var g in glyphs) Assert.LessOrEqual(g.right, w + Eps);
            Assert.LessOrEqual(MaxBottom(t), h + Eps);
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Ellipsis_WithAutoSize_ShrinksFirst_ThenEllipsizesAtMinSize(UniText.UnifiedRendererMode mode)
        {
            // Fits once shrunk (min size small enough): no ellipsis, current size below max.
            var fits = Make(mode, Paragraph, 200f, 90f, TextOverflow.Ellipsis);
            fits.MinFontSize = 6f; fits.MaxFontSize = 40f; fits.AutoSize = true;
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(0, CountGlyphs(fits, (int)EllipsisGlyphId(fits)), "auto-size shrank the text to fit: no ellipsis");
            Assert.Less(fits.CurrentFontSize, 40f);
            Assert.LessOrEqual(MaxBottom(fits), 90f + Eps);

            // Cannot fit even at the minimum size: overflow handling applies at min size.
            var tooBig = Make(mode, Paragraph + " " + Paragraph + " " + Paragraph, 150f, 40f, TextOverflow.Ellipsis);
            tooBig.MinFontSize = 18f; tooBig.MaxFontSize = 24f; tooBig.AutoSize = true;
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(18f, tooBig.CurrentFontSize, 0.5f, "shrunk to the minimum first");
            var g = tooBig.ResultGlyphs;
            Assert.AreEqual((int)EllipsisGlyphId(tooBig), g[g.Length - 1].glyphId, "ellipsis at min size");
            Assert.LessOrEqual(MaxBottom(tooBig), 40f + Eps);
        }

        // ------------------------------------------------------------------ clip

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Clip_EnablesRectClipping_WithTheComponentWorldRect(UniText.UnifiedRendererMode mode)
        {
            var plain = Make(mode, Lines6, 200f, 70f, TextOverflow.Overflow);
            Assert.IsFalse(plain.AllSubMeshRenderersRectClippedForTests(out _), "Overflow does not clip");

            var t = Make(mode, Lines6, 200f, 70f, TextOverflow.Clip);
            Assert.IsTrue(t.AllSubMeshRenderersRectClippedForTests(out var active), "every active sub-mesh renderer is rect clipped");
            Assert.Greater(active, 0);
            Assert.AreEqual(6, Baselines(t).Count, "Clip leaves the layout untouched");

            Assert.IsTrue(t.TryGetEffectiveClipForTests(out var clip));
            var rt = (RectTransform)t.transform;
            var root = _canvas.transform;
            var expectedCenter = (Vector2)root.InverseTransformPoint(rt.TransformPoint(rt.rect.center));
            Assert.AreEqual(expectedCenter.x, clip.center.x, 0.01f);
            Assert.AreEqual(expectedCenter.y, clip.center.y, 0.01f);
            Assert.AreEqual(200f, clip.width, 0.01f);
            Assert.AreEqual(70f, clip.height, 0.01f);

            // Toggle off again.
            t.Overflow = TextOverflow.Overflow;
            Canvas.ForceUpdateCanvases();
            Assert.IsFalse(t.AllSubMeshRenderersRectClippedForTests(out _), "leaving Clip disables rect clipping");
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Clip_FollowsRectResizeAndMove(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, Lines6, 200f, 70f, TextOverflow.Clip);
            var rt = (RectTransform)t.transform;
            rt.sizeDelta = new Vector2(120f, 50f);
            rt.anchoredPosition = new Vector2(30f, -10f);
            Canvas.ForceUpdateCanvases();
            Assert.IsTrue(t.TryGetEffectiveClipForTests(out var clip));
            Assert.AreEqual(120f, clip.width, 0.01f);
            Assert.AreEqual(50f, clip.height, 0.01f);
            Assert.IsTrue(t.AllSubMeshRenderersRectClippedForTests(out _));
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Clip_WithParentRectMask2D_UsesTheIntersection(UniText.UnifiedRendererMode mode)
        {
            var maskGo = new GameObject("Mask", typeof(RectTransform), typeof(RectMask2D)); _junk.Add(maskGo);
            maskGo.transform.SetParent(_canvas.transform, false);
            var maskRt = (RectTransform)maskGo.transform;
            maskRt.sizeDelta = new Vector2(80f, 30f);

            // Own rect 200x70 centred on the same point: the 80x30 mask is the intersection.
            var t = Make(mode, Lines6, 200f, 70f, TextOverflow.Clip, parent: maskGo.transform);
            Canvas.ForceUpdateCanvases();
            Assert.IsTrue(t.TryGetEffectiveClipForTests(out var clip));
            Assert.AreEqual(80f, clip.width, 0.5f, "intersection width");
            Assert.AreEqual(30f, clip.height, 0.5f, "intersection height");
            Assert.IsTrue(t.AllSubMeshRenderersRectClippedForTests(out _));

            // Without Clip the parent mask alone still applies (existing behaviour untouched).
            t.Overflow = TextOverflow.Overflow;
            Canvas.ForceUpdateCanvases();
            Assert.IsTrue(t.TryGetEffectiveClipForTests(out var parentOnly));
            Assert.AreEqual(80f, parentOnly.width, 0.5f);
            Assert.IsTrue(t.AllSubMeshRenderersRectClippedForTests(out _));
        }

        // ------------------------------------------------------------------ GlyphMeshProUGUI

        private GlyphMeshProUGUI MakeGlyphMesh(UniText.UnifiedRendererMode mode, TextOverflowModes overflowMode,
            string text, float w, float h)
        {
            var go = new GameObject("GlyphMesh", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(_canvas.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(w, h);
            var c = go.AddComponent<GlyphMeshProUGUI>();
            c.SetUnifiedRendererModeForTests(mode);
            c.FontStack = _stack;
            c.fontSize = FontSize;
            c.overflowMode = overflowMode;
            c.text = text;
            Canvas.ForceUpdateCanvases();
            return c;
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void GlyphMesh_OverflowModeEllipsis_ProducesEllipsis_B13(UniText.UnifiedRendererMode mode)
        {
            var c = MakeGlyphMesh(mode, TextOverflowModes.Ellipsis, Paragraph, 150f, 60f);
            Assert.AreEqual(TextOverflow.Ellipsis, c.Overflow);
            var eId = (int)EllipsisGlyphId(c);
            var g = c.ResultGlyphs;
            Assert.AreEqual(eId, g[g.Length - 1].glyphId, "B13: overflowMode=Ellipsis must render an ellipsis");
            Assert.LessOrEqual(MaxBottom(c), 60f + Eps, "B13: the text no longer spills");

            var plain = MakeGlyphMesh(mode, TextOverflowModes.Overflow, Paragraph, 150f, 60f);
            Assert.AreEqual(TextOverflow.Overflow, plain.Overflow);
            Assert.AreEqual(0, CountGlyphs(plain, eId));
            Assert.Greater(MaxBottom(plain), 60f);
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void GlyphMesh_TruncateAndMasking_Map(UniText.UnifiedRendererMode mode)
        {
            var trunc = MakeGlyphMesh(mode, TextOverflowModes.Truncate, Lines6, 200f, 100f);
            Assert.AreEqual(TextOverflow.Truncate, trunc.Overflow);
            Assert.LessOrEqual(MaxBottom(trunc), 100f + Eps);

            var mask = MakeGlyphMesh(mode, TextOverflowModes.Masking, Lines6, 200f, 70f);
            Assert.AreEqual(TextOverflow.Clip, mask.Overflow);
            Assert.IsTrue(mask.AllSubMeshRenderersRectClippedForTests(out _));
        }

        [Test]
        public void GlyphMesh_UnsupportedModes_FallBackToOverflow_WithOneWarning()
        {
            var c = MakeGlyphMesh(Legacy, TextOverflowModes.Overflow, Lines6, 200f, 70f);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("not supported"));
            c.overflowMode = TextOverflowModes.Page;
            Assert.AreEqual(TextOverflow.Overflow, c.Overflow);
            // One-time: further unsupported modes do not warn again (an unexpected warning would fail the test).
            c.overflowMode = TextOverflowModes.ScrollRect;
            c.overflowMode = TextOverflowModes.Linked;
            Assert.AreEqual(TextOverflow.Overflow, c.Overflow);
        }

        // ------------------------------------------------------------------ review regressions

        [TestCase(Legacy, TextOverflow.Truncate)]
        [TestCase(Legacy, TextOverflow.Ellipsis)]
        [TestCase(Unified, TextOverflow.Truncate)]
        [TestCase(Unified, TextOverflow.Ellipsis)]
        public void GetRangeBounds_OnTruncatedText_DoesNotThrow_AndStaysInsideVisibleLines(
            UniText.UnifiedRendererMode mode, TextOverflow overflow)
        {
            const float h = 60f;
            var t = Make(mode, Paragraph, 150f, h, overflow);
            var visible = Baselines(t).Count;
            Assert.Less(visible, 5, "test premise: the paragraph is truncated");

            var rects = new List<Rect>();
            Assert.DoesNotThrow(() => t.GetRangeBounds(0, int.MaxValue, rects));
            Assert.AreEqual(visible, rects.Count, "one bounds rect per VISIBLE line");
            var rt = (RectTransform)t.transform;
            foreach (var r in rects)
                Assert.GreaterOrEqual(r.yMin, rt.rect.yMax - h - Eps, "no rect below the visible area");
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void GetRangeBounds_Ellipsis_UsesPositionedLayout_NotPatchedRuns(UniText.UnifiedRendererMode mode)
        {
            // A sub-range wholly inside the kept text must give the same rects with Ellipsis and Truncate.
            var e = Make(mode, Paragraph, 150f, 60f, TextOverflow.Ellipsis);
            var rects = new List<Rect>();
            e.GetRangeBounds(0, 3, rects);
            Assert.AreEqual(1, rects.Count);
            Assert.AreEqual(((RectTransform)e.transform).rect.xMin, rects[0].xMin, 0.5f);
        }

        [Test]
        public void GlyphCache_Growth_InvalidatesTheCache()
        {
            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(4);
            try
            {
                buffers.hasValidGlyphCache = true;
                buffers.EnsureGlyphCacheCapacity(buffers.glyphDataCache.Capacity); // no growth
                Assert.IsTrue(buffers.hasValidGlyphCache, "no growth keeps the cache");
                buffers.EnsureGlyphCacheCapacity(buffers.glyphDataCache.Capacity + 1000);
                Assert.IsFalse(buffers.hasValidGlyphCache, "growth swaps in an uncleared array: cache must be invalidated");
            }
            finally { buffers.EnsureReturnBuffers(); }
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Ellipsis_Cluster_IsTheLastKeptGlyph_NotTheFirstHiddenCodepoint(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, Lines6, 300f, 70f, TextOverflow.Ellipsis);
            var g = t.ResultGlyphs;
            var e = g[g.Length - 1];
            Assert.AreEqual(g[g.Length - 2].cluster, e.cluster,
                "the ellipsis inherits the attributes of the last kept glyph (link/colour/gradient), not hidden text");
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Ellipsis_RestoresLinesAndRuns_AfterLayout(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, Paragraph, 150f, 60f, TextOverflow.Ellipsis);
            t.Overflow = TextOverflow.Overflow;
            Canvas.ForceUpdateCanvases();
            var fresh = Make(mode, Paragraph, 150f, 60f, TextOverflow.Overflow);
            Assert.AreEqual(fresh.ResultGlyphs.Length, t.ResultGlyphs.Length);
            for (var i = 0; i < fresh.ResultGlyphs.Length; i++)
            {
                Assert.AreEqual(fresh.ResultGlyphs[i].glyphId, t.ResultGlyphs[i].glyphId);
                Assert.AreEqual(fresh.ResultGlyphs[i].x, t.ResultGlyphs[i].x, 1e-3f);
            }
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void RegisterOverflowEllipsisGlyphs_RegistersDotFallback_EvenWhenEllipsisAlreadyVirtual(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, Paragraph, 150f, 60f, TextOverflow.Ellipsis);
            var vc = t.Buffers.virtualCodepoints;
            vc.count = 0;
            vc.Add(0x2026);
            t.Buffers.virtualCodepoints = vc;
            t.TextProcessor.RegisterOverflowEllipsisGlyphs();
            var found = false;
            var after = t.Buffers.virtualCodepoints;
            for (var i = 0; i < after.count; i++) if (after.data[i] == '.') found = true;
            Assert.IsTrue(found, "'.' fallback registered although U+2026 was already virtual");
            int ellipsisCount = 0;
            for (var i = 0; i < after.count; i++) if (after.data[i] == 0x2026) ellipsisCount++;
            Assert.AreEqual(1, ellipsisCount, "U+2026 not duplicated");
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void Overflow_ChangedBypassingSetter_ReappliesClip(UniText.UnifiedRendererMode mode)
        {
            var t = Make(mode, Lines6, 200f, 70f, TextOverflow.Overflow);
            Assert.IsFalse(t.AllSubMeshRenderersRectClippedForTests(out _));
            var so = new UnityEditor.SerializedObject(t);
            so.FindProperty("overflow").enumValueIndex = (int)TextOverflow.Clip;
            so.ApplyModifiedProperties(); // undo / prefab revert / Inspector path: field written directly
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(TextOverflow.Clip, t.Overflow);
            Assert.IsTrue(t.AllSubMeshRenderersRectClippedForTests(out _), "clip applied without going through the setter");
        }
    }
}
