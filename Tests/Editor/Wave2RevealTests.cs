using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 2, item 1: <c>UniTextReveal</c> (typewriter) by grapheme / word / line, with speed, delay,
    /// fade, slide, easing, events, Play/Pause/Skip/Restart, &lt;pause&gt; and RTL logical order, on
    /// both renderers. The clock is driven manually.
    /// </summary>
    public class Wave2RevealTests : Wave2TestBase
    {
        private Component AddReveal(UniText t, float unitsPerSecond, float fade = 0f, string unit = "Character", float delay = 0f)
        {
            var r = W2.Add(t.gameObject, "LightSide.UniTextReveal");
            W2.Set(r, "Unit", unit);
            W2.Set(r, "UnitsPerSecond", unitsPerSecond);
            W2.Set(r, "FadeDuration", fade);
            W2.Set(r, "Delay", delay);
            W2.Set(r, "Easing", "Linear");
            W2.Call(r, "Restart");
            return r;
        }

        private static int Visible(UniText t) => DrawnQuads(t).Count(q => q.MaxAlpha > 0);

        [TestCase(true)]
        [TestCase(false)]
        public void ByCharacter_RevealsAtSpeed(bool unified)
        {
            var t = Make("ABCDE", unified);
            AddReveal(t, 10f);
            Step(0f);
            Assert.AreEqual(5, DrawnQuads(t).Count, "all glyphs stay in the mesh");
            Assert.AreEqual(1, Visible(t), "t=0: unit 0 starts at 0");
            Step(0.25f);
            Assert.AreEqual(3, Visible(t), "t=0.25: units starting at 0, 0.1, 0.2");
            Step(0.45f);
            Assert.AreEqual(5, Visible(t));
            var first = ByX(DrawnQuads(t));
            Assert.IsTrue(first.All(q => q.MinAlpha == 255), "fully visible at the end");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FadeSlideAndDelay(bool unified)
        {
            var t = Make("AB", unified);
            var plain = ByX(DrawnQuads(Make("AB", unified)));
            var r = AddReveal(t, 1f, fade: 0.2f, delay: 0.5f);
            W2.Set(r, "SlideOffset", new Vector2(0f, -10f));
            Step(0f);
            Step(0.4f);
            Assert.AreEqual(0, Visible(t), "nothing before the delay");
            Step(0.6f); // unit 0 started at 0.5: f = 0.5 (linear)
            var q = ByX(DrawnQuads(t));
            Assert.AreEqual(128, q[0].MinAlpha, 1, "half faded in");
            Assert.AreEqual(-5f, q[0].v0.y - plain[0].v0.y, 0.05f, "half way through the slide");
            Assert.AreEqual(0, q[1].MaxAlpha, "unit 1 not started");
            Step(0.7f);
            q = ByX(DrawnQuads(t));
            Assert.AreEqual(255, q[0].MinAlpha);
            Assert.AreEqual(0f, q[0].v0.y - plain[0].v0.y, 0.05f, "slide finished");
        }

        [Test]
        public void Easing_ShapesTheFade()
        {
            Assert.AreEqual(0.25f, Ease(0.5f, "EaseIn"), 1e-5f);
            Assert.AreEqual(0.75f, Ease(0.5f, "EaseOut"), 1e-5f);
            Assert.AreEqual(0.5f, Ease(0.5f, "EaseInOut"), 1e-5f);
            Assert.AreEqual(0.5f, Ease(0.5f, "Smooth"), 1e-5f);
            Assert.AreEqual(0.104f, Ease(0.2f, "Smooth"), 1e-5f);
        }

        private static float Ease(float t, string e)
        {
            var type = W2.Type("LightSide.UniTextReveal");
            var enumT = W2.Type("LightSide.RevealEasing");
            return (float)W2.CallStatic(type, "Ease", t, System.Enum.Parse(enumT, e));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Events_PausePlaySkipRestart(bool unified)
        {
            var t = Make("ABCD", unified);
            var r = AddReveal(t, 10f);
            var started = 0; var completed = 0; var units = new List<int>();
            ((UnityEngine.Events.UnityEvent)W2.Get(r, "OnRevealStarted")).AddListener(() => started++);
            ((UnityEngine.Events.UnityEvent)W2.Get(r, "OnRevealCompleted")).AddListener(() => completed++);
            ((UnityEngine.Events.UnityEvent<int>)W2.Get(r, "OnUnitRevealed")).AddListener(u => units.Add(u));

            Step(0f);
            Step(0.15f);
            Assert.AreEqual(1, started);
            CollectionAssert.AreEqual(new[] { 0, 1 }, units, "units 0 (t=0) and 1 (t=0.1)");
            W2.Call(r, "Pause");
            Step(1.0f);
            Assert.AreEqual(2, Visible(t), "paused: no progress");
            Assert.AreEqual(2, units.Count);
            W2.Call(r, "Play");
            Step(1.1f); // +0.1 of playback -> 0.25
            Assert.AreEqual(3, Visible(t));
            W2.Call(r, "Skip");
            Step(1.2f);
            Assert.AreEqual(4, Visible(t), "skip shows everything");
            Assert.AreEqual(1, completed);
            Assert.AreEqual(3, units.Count, "skip raises no per-unit events");
            Assert.IsTrue((bool)W2.Get(r, "IsComplete"));
            W2.Call(r, "Restart");
            Step(1.25f);
            Assert.AreEqual(1, Visible(t), "restart begins again");
            Assert.AreEqual(2, started);
            Step(5f);
            Assert.AreEqual(2, completed);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 1, 2, 3 }, units);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Rtl_RevealsInLogicalOrder(bool unified)
        {
            // Hebrew "shalom": the first logical letter is drawn RIGHTMOST.
            var t = Make("\u05E9\u05DC\u05D5\u05DD", unified);
            AddReveal(t, 1f);
            Step(0f);
            var q = ByX(DrawnQuads(t));
            Assert.AreEqual(4, q.Count);
            var vis = q.Select(x => x.MaxAlpha > 0).ToArray();
            Assert.AreEqual(new[] { false, false, false, true }, vis, "only the rightmost (logical first) glyph is visible");
            Step(1f);
            vis = ByX(DrawnQuads(t)).Select(x => x.MaxAlpha > 0).ToArray();
            Assert.AreEqual(new[] { false, false, true, true }, vis, "then the next one to its left");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Bidi_MixedText_FollowsLogicalOrder(bool unified)
        {
            // "AB <hebrew 2 letters> CD": logical units A,B,space,ש,ל,space,C,D
            var t = Make("AB \u05E9\u05DC CD", unified);
            AddReveal(t, 1f);
            Step(0f);
            Step(3f); // units 0..3 started: A, B, space, shin (logical first Hebrew letter)
            var q = ByX(DrawnQuads(t));
            Assert.AreEqual(6, q.Count, "6 visible-glyph quads (spaces have none)");
            var vis = q.Select(x => x.MaxAlpha > 0).ToArray();
            // Visual order (LTR paragraph): A B [lamed shin] C D -> shin is the 4th quad from the left.
            Assert.AreEqual(new[] { true, true, false, true, false, false }, vis);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PauseTag_HoldsTheReveal(bool unified)
        {
            var t = Make("AB<pause=1>CD", unified, AddPauseTag);
            Assert.AreEqual("ABCD", t.CleanText, "<pause> adds no character");
            AddReveal(t, 10f);
            Step(0f);
            Step(0.5f);
            Assert.AreEqual(2, Visible(t), "held after B");
            Step(1.15f);
            Assert.AreEqual(2, Visible(t), "C starts at 0.2 + 1.0 = 1.2");
            Step(1.25f);
            Assert.AreEqual(3, Visible(t));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ByWord_AndByLine(bool unified)
        {
            var t = Make("one two three", unified);
            var r = AddReveal(t, 1f, unit: "Word");
            Step(0f);
            Assert.AreEqual(3, (int)W2.Get(r, "UnitCount"), "3 words");
            Assert.AreEqual(3, Visible(t), "first word: 3 glyphs");
            Step(1f);
            Assert.AreEqual(6, Visible(t));

            var lines = Make("north side\nsouth side", unified);
            var rl = AddReveal(lines, 1f, unit: "Line");
            Step(1.5f);
            Assert.AreEqual(2, (int)W2.Get(rl, "UnitCount"), "2 lines");
            Assert.AreEqual(9, Visible(lines), "the 9 glyphs of line 1 (northside)");
        }

        [Test]
        public void Graphemes_AreOneUnit()
        {
            // a, e + combining acute, b -> 3 grapheme units (4 codepoints).
            var t = Make("ae\u0301b", true);
            var r = AddReveal(t, 1f);
            Step(0f);
            Assert.AreEqual(3, (int)W2.Get(r, "UnitCount"));
            Step(1f); // units a and e+acute
            var total = DrawnQuads(t).Count;
            var visible = Visible(t);
            Assert.AreEqual(total - 1, visible, "everything but b: the mark appears with its base");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GlyphMeshPro_MaxVisibleCharacters_Composes(bool unified)
        {
            var t = Make("ABCDEFGH", unified, componentType: typeof(OpenGlyph.GlyphMeshProUGUI));
            var gmp = (OpenGlyph.GlyphMeshProUGUI)t;
            gmp.maxVisibleCharacters = 4;
            Canvas.ForceUpdateCanvases();
            AddReveal(t, 10f);
            Step(0f);
            Step(0.15f);
            Assert.AreEqual(2, Visible(t), "reveal limits first");
            Step(0.65f);
            Assert.AreEqual(4, Visible(t), "maxVisibleCharacters still caps the mesh");
            Assert.AreEqual(4, DrawnQuads(t).Count, "hidden characters emit no quad");
            gmp.maxVisibleCharacters = 6;
            Step(0.7f);
            Assert.AreEqual(6, Visible(t), "raising the cap keeps the reveal state");
        }

        [Test]
        public void TextChange_Restarts_AndRemovingTheComponentRestoresTheMesh()
        {
            var t = Make("ABCD", true);
            var r = AddReveal(t, 10f);
            Step(0f);
            Step(1f);
            Assert.AreEqual(4, Visible(t));
            t.Text = "WXYZ";
            Step(1.01f);
            Assert.AreEqual(1, Visible(t), "new text restarts the reveal");
            Object.DestroyImmediate(r);
            Step(1.02f);
            Assert.AreEqual(4, Visible(t), "without the reveal the plain mesh is back");
            Assert.IsFalse((bool)W2.Get(t, "HasVertexEffects"));
        }
    }
}
