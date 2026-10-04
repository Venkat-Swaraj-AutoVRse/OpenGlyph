using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LightSide.Tests
{
    /// <summary>
    /// Caret and selection of UniTextInputField: grapheme clusters, BiDi visual order, words, vertical
    /// movement with column memory, multi-click selection. Input goes through ProcessKey / pointer events.
    /// </summary>
    public class Wave4CaretTests : Wave4TestBase
    {
        // a | family emoji (man ZWJ woman ZWJ girl, 8 UTF-16) | b | flag JP (2 regional indicators, 4 UTF-16) | c | e + combining acute
        private const string Graphemes = "a\U0001F468\u200D\U0001F469\u200D\U0001F467b\U0001F1EF\U0001F1F5ce\u0301";
        private static readonly int[] GraphemeStops = { 1, 9, 10, 14, 15, 17 };

        [Test]
        public void Caret_MovesByGraphemeCluster_NeverInsideEmojiFlagOrCombiningMark()
        {
            var f = MakeField(Graphemes);
            f.Caret = 0;
            var right = new List<int>();
            for (var i = 0; i < GraphemeStops.Length; i++) { f.Key("Right"); right.Add(f.Caret); }
            Log("right stops " + string.Join(",", right));
            CollectionAssert.AreEqual(GraphemeStops, right, "Right arrow visits exactly the grapheme boundaries");
            f.Key("Right");
            Assert.AreEqual(17, f.Caret, "end of text is a stop");

            var left = new List<int>();
            for (var i = 0; i < GraphemeStops.Length; i++) { f.Key("Left"); left.Add(f.Caret); }
            CollectionAssert.AreEqual(new[] { 15, 14, 10, 9, 1, 0 }, left, "Left arrow back over the same clusters");

            // Positions inside a cluster snap to its start.
            foreach (var inside in new[] { 2, 3, 5, 8 }) { f.Caret = inside; Assert.AreEqual(1, f.Caret, $"caret {inside} inside the ZWJ family snaps to 1"); }
            f.Caret = 12; Assert.AreEqual(10, f.Caret, "inside the flag snaps to its start");
            f.Caret = 16; Assert.AreEqual(15, f.Caret, "between e and its combining mark snaps before e");

            // Shift+Right selects whole clusters.
            f.Caret = 1;
            f.Key("Right", "Shift");
            Assert.AreEqual((1, 9), (f.SelStart, f.SelEnd), "shift-extend selects the whole family");
        }

        [Test]
        public void Delete_And_Backspace_RemoveWholeGraphemes()
        {
            var f = MakeField(Graphemes);
            f.Caret = 17;
            f.Key("Backspace");
            Assert.AreEqual("a\U0001F468\u200D\U0001F469\u200D\U0001F467b\U0001F1EF\U0001F1F5c", f.Text, "backspace removes e + combining acute together");
            f.Caret = 10;
            f.Key("Delete");
            Assert.AreEqual("a\U0001F468\u200D\U0001F469\u200D\U0001F467bc", f.Text, "delete removes both regional indicators");
            f.Caret = 9;
            f.Key("Backspace");
            Assert.AreEqual("abc", f.Text, "backspace after the family removes all five codepoints");
            Assert.AreEqual(1, f.Caret);
        }

        [Test]
        public void BiDi_VisualCaret_MixedHebrewEnglish_MovesInArrowDirection()
        {
            // a b c _ [shin lamed vav final-mem] _ d e f : LTR paragraph with an RTL run in the middle.
            const string s = "abc \u05E9\u05DC\u05D5\u05DD def";
            var f = MakeField(s);
            f.Caret = 0;
            var xs = new List<float> { f.CaretRect.center.x };
            var seq = new List<int>();
            for (var i = 0; i < 12; i++) { f.Key("Right"); seq.Add(f.Caret); xs.Add(f.CaretRect.center.x); }
            Log("bidi right: " + string.Join(",", seq) + " x: " + string.Join(",", xs.Select(x => x.ToString("F1"))));
            // Visual order: after the space the caret enters the Hebrew run at its visual left = logical end (8),
            // walks it right-to-left in logical terms (7,6,5,4 down), then continues with " def".
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 7, 6, 5, 4, 9, 10, 11, 12 }, seq, "logical positions in visual order");
            for (var i = 1; i < xs.Count; i++)
                Assert.Greater(xs[i], xs[i - 1] - 0.01f, $"Right arrow never moves the caret left (step {i})");
            Assert.Greater(xs[xs.Count - 1], xs[0] + 50f);

            // Position 4 has two places: after the space (upstream, left of the Hebrew run) and before shin (right end).
            Assert.Greater(xs[8], xs[4] + 20f, "the two caret places of logical 4 differ by the Hebrew run width");

            var back = new List<int>();
            var bx = new List<float> { f.CaretRect.center.x };
            for (var i = 0; i < 12; i++) { f.Key("Left"); back.Add(f.Caret); bx.Add(f.CaretRect.center.x); }
            Log("bidi left: " + string.Join(",", back));
            CollectionAssert.AreEqual(new[] { 11, 10, 9, 8, 5, 6, 7, 8, 3, 2, 1, 0 }, back);
            for (var i = 1; i < bx.Count; i++)
                Assert.Less(bx[i], bx[i - 1] + 0.01f, $"Left arrow never moves the caret right (step {i})");
        }

        [Test]
        public void BiDi_RtlParagraph_RightArrowMovesTowardTheLogicalStart()
        {
            const string s = "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD"; // two Hebrew words
            var f = MakeField(s);
            f.Caret = 0;
            var x0 = f.CaretRect.center.x;
            f.Key("Left");
            Assert.AreEqual(1, f.Caret, "in RTL text Left moves logically forward");
            Assert.Less(f.CaretRect.center.x, x0, "and visually left");
            f.Key("Right");
            Assert.AreEqual(0, f.Caret);
            f.Key("End");
            Assert.AreEqual(s.Length, f.Caret);
            Assert.Less(f.CaretRect.center.x, x0, "the logical end of an RTL line is on the left");
        }

        [Test]
        public void WordMovement_CtrlArrows_UAX29Words()
        {
            const string s = "Hello, world! It's 3.14 ok";
            var f = MakeField(s);
            f.Caret = 0;
            var r = new List<int>();
            for (var i = 0; i < 7; i++) { f.Key("Right", "Control"); r.Add(f.Caret); }
            Log("word right " + string.Join(",", r));
            CollectionAssert.AreEqual(new[] { 5, 7, 12, 14, 19, 24, 26 }, r, "word starts; It's and 3.14 are single words");
            var l = new List<int>();
            for (var i = 0; i < 7; i++) { f.Key("Left", "Control"); l.Add(f.Caret); }
            CollectionAssert.AreEqual(new[] { 24, 19, 14, 12, 7, 5, 0 }, l);

            // Alt works as the word modifier too (macOS); shift extends by words.
            f.Caret = 7;
            f.Key("Right", "Shift, Alt");
            Assert.AreEqual((7, 12), (f.SelStart, f.SelEnd));

            // Word delete.
            f.Caret = s.Length;
            f.Key("Backspace", "Control");
            Assert.AreEqual("Hello, world! It's 3.14 ", f.Text);
            f.Caret = 0;
            f.Key("Delete", "Control");
            Assert.AreEqual(", world! It's 3.14 ", f.Text);
        }

        [Test]
        public void HomeEnd_LineAndDocument()
        {
            var f = MakeField("first line\nsecond line\nthird", 600, 200, "MultiLineNewline");
            f.Caret = 14; // in "second"
            f.Key("Home");
            Assert.AreEqual(11, f.Caret, "Home = start of the line");
            f.Key("End");
            Assert.AreEqual(22, f.Caret, "End = end of the line (before the newline)");
            f.Key("End", "Control");
            Assert.AreEqual(28, f.Caret, "Ctrl+End = end of text");
            f.Key("Home", "Control, Shift");
            Assert.AreEqual((0, 28), (f.SelStart, f.SelEnd), "Ctrl+Shift+Home selects to the start");
        }

        [Test]
        public void VerticalMovement_KeepsTheColumn_AcrossAShortLine()
        {
            var f = MakeField("abcdefgh\nab\nabcdefgh", 600, 200, "MultiLineNewline");
            f.Caret = 6; // after "abcdef"
            var x6 = f.CaretRect.center.x;
            f.Key("Down");
            Assert.AreEqual(11, f.Caret, "the short line: caret at its end");
            f.Key("Down");
            Assert.AreEqual(18, f.Caret, "back to column 6 on the long line (column memory)");
            Assert.AreEqual(x6, f.CaretRect.center.x, 0.5f);
            f.Key("Up");
            Assert.AreEqual(11, f.Caret);
            f.Key("Up");
            Assert.AreEqual(6, f.Caret, "column remembered upwards");
            f.Key("Up");
            Assert.AreEqual(0, f.Caret, "Up on the first line goes to the start");
            f.Caret = 18;
            f.Key("Down");
            Assert.AreEqual(20, f.Caret, "Down on the last line goes to the end");

            // Wrapped (soft) lines.
            var g = MakeField("one two three four five six seven eight nine ten", 260, 300, "MultiLineSubmit");
            var lines = g.TextComponent.Buffers.lines.count;
            Assert.GreaterOrEqual(lines, 3, "text wraps onto several lines");
            g.Caret = 0;
            g.Key("Down");
            var l1 = g.TextComponent.Buffers.lines.data[1].range.start;
            Assert.AreEqual(l1, g.Caret, "Down from column 0 lands at the start of the wrapped second line");
            Assert.Greater(g.CaretRect.center.y, -1000f);
        }

        [Test]
        public void SingleLine_UpDown_GoToStartEnd()
        {
            var f = MakeField("hello world");
            f.Caret = 5;
            f.Key("Up");
            Assert.AreEqual(0, f.Caret);
            f.Key("Down");
            Assert.AreEqual(11, f.Caret);
        }

        [Test]
        public void Pointer_ClickPlacesCaret_DoubleClickWord_TripleClickLine_DragSelects()
        {
            var f = MakeField("Hello brave new world");
            var t = f.TextComponent;
            // Left half of 'b' (cluster 6) -> caret 6; right half -> 7.
            ((IPointerDownHandler)f.C).OnPointerDown(Pointer(ScreenOf(t, GlyphLocal(t, 6, 0.2f))));
            Assert.AreEqual(6, f.Caret, "click on the left half of 'b'");
            Assert.AreEqual(f.Caret, f.Anchor);
            ((IPointerDownHandler)f.C).OnPointerDown(Pointer(ScreenOf(t, GlyphLocal(t, 6, 0.8f))));
            Assert.AreEqual(7, f.Caret, "click on the right half of 'b'");

            ((IPointerDownHandler)f.C).OnPointerDown(Pointer(ScreenOf(t, GlyphLocal(t, 8)), 2));
            Assert.AreEqual((6, 11), (f.SelStart, f.SelEnd), "double-click selects 'brave'");

            ((IPointerDownHandler)f.C).OnPointerDown(Pointer(ScreenOf(t, GlyphLocal(t, 13)), 3));
            Assert.AreEqual((0, 21), (f.SelStart, f.SelEnd), "triple-click selects the line");

            // Drag from 'H' to inside 'new'.
            ((IPointerDownHandler)f.C).OnPointerDown(Pointer(ScreenOf(t, GlyphLocal(t, 0, 0.1f))));
            var drag = Pointer(ScreenOf(t, GlyphLocal(t, 13, 0.9f)));
            ((IBeginDragHandler)f.C).OnBeginDrag(drag);
            ((IDragHandler)f.C).OnDrag(drag);
            Assert.AreEqual((0, 14), (f.SelStart, f.SelEnd), "drag extends the selection from the press point");
            Assert.AreEqual(0, f.Anchor);
            Assert.Greater(f.SelectionRects().Count, 0, "selection highlight drawn");

            // Triple-click on a wrapped multi-line field selects that visual line only.
            var g = MakeField("alpha beta gamma delta epsilon zeta eta theta", 220, 300, "MultiLineSubmit");
            var gt = g.TextComponent;
            var line1 = gt.Buffers.lines.data[1];
            ((IPointerDownHandler)g.C).OnPointerDown(Pointer(ScreenOf(gt, GlyphLocal(gt, line1.range.start + 1)), 3));
            Assert.AreEqual(line1.range.start, g.SelStart, "triple-click starts at the visual line start");
            Assert.LessOrEqual(g.SelEnd, line1.range.start + line1.range.length);
            Assert.Greater(g.SelEnd, g.SelStart + 2);
        }

        [Test]
        public void Selection_Highlight_CoversSelectedGlyphs_BiDiSplitsIntoRuns()
        {
            var f = MakeField("abc \u05E9\u05DC\u05D5\u05DD def");
            f.Call("SetSelection", 2, 6); // "c ש" + "ל": crosses the direction boundary
            var rects = f.SelectionRects();
            Log("bidi selection rects " + string.Join(" ", rects));
            Assert.GreaterOrEqual(rects.Count, 2, "a logical range across a direction change is two visual pieces");
            f.Call("SetSelection", 0, 3);
            rects = f.SelectionRects();
            Assert.AreEqual(1, rects.Count);
            var t = f.TextComponent;
            Assert.AreEqual(GlyphLocal(t, 0, 0f).x, rects[0].xMin, 0.5f);
            Assert.AreEqual(GlyphLocal(t, 2, 1f).x, rects[0].xMax, 0.5f);
            Assert.IsFalse(f.HasCaretRect, "no caret while a selection is shown");
        }
    }
}
