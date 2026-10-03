using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 2, item 4: radial and angular gradients, per span (<c>&lt;gradient=name,radial&gt;</c>,
    /// <c>&lt;gradient=name,angular,deg&gt;</c>) and whole text (<c>UniText.GradientFill</c>). Gradients are
    /// vertex colours, so the drawn vertex colours are checked on both renderers.
    /// </summary>
    public class Wave2GradientTests : Wave2TestBase
    {
        private UniTextGradients _savedGradients;
        private UniTextGradients _gradients;
        private Gradient _bw;

        [SetUp]
        public void GradientSetUp()
        {
            _savedGradients = UniTextSettings.Gradients;
            _gradients = ScriptableObject.CreateInstance<UniTextGradients>();
            _junk.Add(_gradients);
            _bw = new Gradient();
            _bw.SetKeys(new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            _gradients.Add("bw", _bw);
            UniTextSettings.Gradients = _gradients;
        }

        [TearDown]
        public void GradientTearDown() => UniTextSettings.Gradients = _savedGradients;

        private static void AddTags(UniText t)
        {
            t.RegisterModifier(new ModRegister { Rule = new GradientParseRule(), Modifier = new GradientModifier() });
            t.RegisterModifier(new ModRegister { Rule = new ColorParseRule(), Modifier = new ColorModifier() });
        }

        private static float Lum(Color32 c) => (c.r + c.g + c.b) / 3f;
        private static float Mean(Quad q) => (Lum(q.c0) + Lum(q.c1) + Lum(q.c2) + Lum(q.c3)) / 4f;

        [TestCase(true)]
        [TestCase(false)]
        public void SpanRadial_DarkAtCentre_BrightAtEnds(bool unified)
        {
            var t = Make("<gradient=bw,radial>MMMMMMM</gradient>", unified, AddTags);
            var q = ByX(DrawnQuads(t));
            Assert.AreEqual(7, q.Count);
            var means = q.Select(Mean).ToArray();
            Debug.Log($"[W2] radial span mean luminance per glyph (unified={unified}): {Fmt(means)}");
            Assert.Less(means[3], 90f, "middle glyph near t=0 (black)");
            Assert.Greater(means[0], 170f, "first glyph near the rim (white)");
            Assert.Greater(means[6], 170f, "last glyph near the rim (white)");
            Assert.AreEqual(means[0], means[6], 12f, "symmetric about the centre");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SpanRadial_CentreAndRadiusParameters(bool unified)
        {
            // Centre at the left edge, radius 0.5: brightness grows left to right and saturates half way.
            var t = Make("<gradient=bw,radial,0,0.5,0.5>MMMMMMM</gradient>", unified, AddTags);
            var means = ByX(DrawnQuads(t)).Select(Mean).ToArray();
            Debug.Log($"[W2] radial cx=0 r=0.5 means: {Fmt(means)}");
            for (var i = 1; i < 3; i++) Assert.Greater(means[i], means[i - 1], "monotonic away from the centre");
            Assert.Greater(means[5], 250f, "past the radius: t clamps to 1");
            Assert.Greater(means[6], 250f, "past the radius: t clamps to 1");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SpanAngular_SweepsAroundTheCentre(bool unified)
        {
            // Start angle 90 (up), counter-clockwise: left side (180 deg) = t 0.25, bottom 0.5, right 0.75.
            var t = Make("<gradient=bw,angular,90>MMMMMMM</gradient>", unified, AddTags);
            var q = ByX(DrawnQuads(t));
            var left = Mean(q[0]); var right = Mean(q[6]);
            Debug.Log($"[W2] angular start=90: left={left:0.#} right={right:0.#}");
            Assert.AreEqual(0.25f * 255f, left, 30f, "left end ~ t=0.25");
            Assert.AreEqual(0.75f * 255f, right, 30f, "right end ~ t=0.75");

            // Start angle 0 puts the seam on the right: the last glyph straddles it and must not be
            // interpolated through the whole gradient (corners unwrapped around the quad centre).
            var s = Make("<gradient=bw,angular,0>MMMMMMM</gradient>", unified, AddTags);
            var last = ByX(DrawnQuads(s))[6];
            var spread = new[] { Lum(last.c0), Lum(last.c1), Lum(last.c2), Lum(last.c3) };
            Debug.Log($"[W2] angular seam glyph corner luminance: {Fmt(spread)}");
            Assert.Less(spread.Max() - spread.Min(), 128f, "seam glyph is not smeared through the full gradient");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void WholeText_RadialFill_AndSpanColourOverrides(bool unified)
        {
            var fillT = W2.Type("LightSide.UniTextGradientFill");
            var fill = W2.CallStatic(fillT, "Radial", _bw, new Vector2(0.5f, 0.5f), 1f);
            var t = Make("MMM<color=#FF0000>M</color>MMM", unified, x =>
            {
                AddTags(x);
                W2.Set(x, "GradientFill", fill);
            });
            var q = ByX(DrawnQuads(t));
            Assert.AreEqual(7, q.Count);
            Debug.Log($"[W2] whole-text radial means: {Fmt(q.Select(Mean))}; centre c0={q[3].c0}");
            Assert.Greater(Mean(q[0]), 170f, "whole-text fill: ends bright");
            Assert.Less(Mean(q[2]), Mean(q[0]) - 40f, "darker towards the centre");
            Assert.AreEqual(new Color32(255, 0, 0, 255), q[3].c0, "<color> span overrides the fill");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void WholeText_LinearAndAngularFill_AndOff(bool unified)
        {
            var fillT = W2.Type("LightSide.UniTextGradientFill");
            var t = Make("MMMMMMM", unified, x => W2.Set(x, "GradientFill", W2.CallStatic(fillT, "Linear", _bw, 0f)));
            var lin = ByX(DrawnQuads(t)).Select(Mean).ToArray();
            for (var i = 1; i < lin.Length; i++) Assert.Greater(lin[i], lin[i - 1], "linear 0 deg: left to right");

            W2.Set(t, "GradientFill", W2.CallStatic(fillT, "Angular", _bw, new Vector2(0.5f, 0.5f), 90f));
            Canvas.ForceUpdateCanvases();
            var ang = ByX(DrawnQuads(t));
            Assert.AreEqual(0.25f * 255f, Mean(ang[0]), 30f);
            Assert.AreEqual(0.75f * 255f, Mean(ang[6]), 30f);

            W2.Set(t, "GradientFill", W2.StaticGet(fillT, "None"));
            Canvas.ForceUpdateCanvases();
            Assert.IsTrue(DrawnQuads(t).All(x => x.c0.r == 255 && x.c0.g == 255 && x.c0.b == 255), "None restores the plain colour");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SpanLinear_Unchanged(bool unified)
        {
            var t = Make("<gradient=bw>MMMMM</gradient>", unified, AddTags);
            var means = ByX(DrawnQuads(t)).Select(Mean).ToArray();
            for (var i = 1; i < means.Length; i++) Assert.Greater(means[i], means[i - 1], "existing linear mode still left to right");
            var l = Make("<gradient=bw,L>MMMMM</gradient>", unified, AddTags);
            var lq = ByX(DrawnQuads(l));
            Assert.AreEqual(0, lq[0].c0.r, 1, "logical mode starts at t=0");
            Assert.AreEqual(255, lq[4].c0.r, 1, "logical mode ends at t=1");
        }
    }
}
