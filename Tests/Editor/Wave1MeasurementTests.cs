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
    /// <summary>Wave 1: Content measurement: min-/max-content width and height for a width.</summary>
    [TestFixture]
    public class Wave1MeasurementTests : Wave1TestBase
    {
        // ================================================================== 3. Content measurement

        [Test]
        public void MinContentWidth_IsTheWidestUnbreakableSegment()
        {
            // Longest word last: no trailing space in the widest segment.
            var t = Make("a bb ccc extraordinarily", 600, 300);
            var word = Make("extraordinarily", 600, 300, c => c.WordWrap = false);
            var expected = PreferredWidth(word);
            Assert.AreEqual(expected, W1.Float(t, "GetMinContentWidth"), 0f, "min-content = longest word");

            // Longest word followed by a space: the breaker counts the breaking space, so min-content does too.
            var t2 = Make("extraordinarily a bb", 600, 300);
            var word2 = Make("extraordinarily ", 600, 300, c => c.WordWrap = false);
            Assert.AreEqual(PreferredWidth(word2), W1.Float(t2, "GetMinContentWidth"), 0f, "min-content counts the breaking space");

            // Laid out at exactly the min-content width, no word is broken: one line per segment as wide as possible.
            var narrow = Make("a bb ccc extraordinarily", expected, 300);
            var lines = narrow.TextProcessor.LayoutLineCount;
            Assert.AreEqual(2, lines, "'a bb ccc ' fits on line 1, 'extraordinarily' on line 2");

            // Padding is included; Auto Size measures at the min size.
            W1.Set(t, "Padding", new Vector4(10, 0, 15, 0));
            Assert.AreEqual(expected + 25f, W1.Float(t, "GetMinContentWidth"), 0f);
            var auto = Make("a bb ccc extraordinarily", 600, 300, c => { c.AutoSize = true; c.MinFontSize = 12; c.MaxFontSize = 60; });
            var word12 = Make("extraordinarily", 600, 300, c => { c.WordWrap = false; c.FontSize = 12; });
            Assert.AreEqual(PreferredWidth(word12), W1.Float(auto, "GetMinContentWidth"), 0f, "Auto Size: measured at MinFontSize");

            // No word wrap: the widest hard line.
            var nowrap = Make("a bb\nccc dd", 600, 300, c => c.WordWrap = false);
            var line = Make("ccc dd", 600, 300, c => c.WordWrap = false);
            Assert.AreEqual(PreferredWidth(line), W1.Float(nowrap, "GetMinContentWidth"), 0f);
        }

        [Test]
        public void MaxContentWidth_IsTheWidestLineBetweenHardBreaks()
        {
            var t = Make("short\nthe widest line here\nmid", 200, 300);
            var widest = Make("the widest line here", 2000, 300, c => c.WordWrap = false);
            var expected = PreferredWidth(widest);
            Assert.AreEqual(expected, W1.Float(t, "GetMaxContentWidth"), 0f);
            Assert.AreEqual(PreferredWidth(t), W1.Float(t, "GetMaxContentWidth"), 0f, "equals preferredWidth");
            Assert.Less(W1.Float(t, "GetMinContentWidth"), expected);
            W1.Set(t, "Padding", new Vector4(7, 3, 5, 1));
            Assert.AreEqual(expected + 12f, W1.Float(t, "GetMaxContentWidth"), 0f);
        }

        [Test]
        public void HeightForWidth_IsTheWrappedHeight_AndDoesNotTouchLayout()
        {
            var t = Make("mmmm mmmm mmmm", 1000, 400);
            t.FontProvider.GetLineMetrics(36f, out var asc, out var desc, out var lh);
            var min = W1.Float(t, "GetMinContentWidth");

            var before = Glyphs(t).Select(g => (g.glyphId, g.x, g.y)).ToArray();
            var lineCount = t.TextProcessor.LayoutLineCount;
            var rect = t.rectTransform.rect;
            var prefH = PreferredHeight(t);

            var one = W1.Float(t, "GetHeightForWidth", 1000f);
            var three = W1.Float(t, "GetHeightForWidth", min);
            var veryNarrow = W1.Float(t, "GetHeightForWidth", 5f);
            Debug.Log($"[W1] heightForWidth: 1000→{one}, {min}→{three}, 5→{veryNarrow}; asc={asc} desc={desc} lh={lh}");

            Assert.AreEqual(asc - desc, one, 0.01f, "one line");
            Assert.AreEqual(asc - desc + 2 * lh, three, 0.01f, "three lines at the min-content width");
            Assert.Greater(veryNarrow, three, "narrower than a word: the words break, more lines");

            // Same as the real layout at that width.
            var at = Make("mmmm mmmm mmmm", min, 400);
            Assert.AreEqual(PreferredHeight(at), three, 0.01f, "matches preferredHeight of a component that wide");
            Assert.AreEqual(3, at.TextProcessor.LayoutLineCount);

            // Nothing about the measured component changed.
            Canvas.ForceUpdateCanvases();
            CollectionAssert.AreEqual(before, Glyphs(t).Select(g => (g.glyphId, g.x, g.y)).ToArray(), "positions unchanged");
            Assert.AreEqual(lineCount, t.TextProcessor.LayoutLineCount);
            Assert.AreEqual(rect, t.rectTransform.rect);
            Assert.AreEqual(prefH, PreferredHeight(t), 0.001f);

            // Padding adds vertically; the width given is the outer width.
            W1.Set(t, "Padding", new Vector4(20, 4, 20, 6));
            Assert.AreEqual(asc - desc + 2 * lh + 10f, W1.Float(t, "GetHeightForWidth", min + 40f), 0.01f);
        }

        [Test]
        public void ContentMinWidth_FeedsILayoutElementMinWidth()
        {
            var t = Make("a bb ccc extraordinarily", 600, 300);
            Assert.AreEqual(0f, t.minWidth, "off by default");
            W1.Set(t, "ContentMinWidth", true);
            Assert.AreEqual(W1.Float(t, "GetMinContentWidth"), t.minWidth, 0f);
            Assert.Greater(t.minWidth, 0f);
        }
    }
}
