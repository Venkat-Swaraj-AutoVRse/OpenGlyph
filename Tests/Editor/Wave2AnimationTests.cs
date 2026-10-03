using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 2, item 2: time-driven span animations (&lt;wave&gt; &lt;shake&gt; &lt;pulse&gt; &lt;fade&gt;
    /// &lt;rainbow&gt; &lt;bounce&gt;). The clock is driven manually, so vertex positions / colours at a
    /// given time are exact. Every case runs on the unified and the legacy renderer.
    /// </summary>
    public class Wave2AnimationTests : Wave2TestBase
    {
        private UniText MakeAnimated(string text, bool unified) => Make(text, unified, AddAnimationTags);

        // Quads whose (x, y) moved between two read-backs, by more than eps.
        private static int CountMoved(System.Collections.Generic.List<Quad> a, System.Collections.Generic.List<Quad> b, float dx, float dy, float eps = 0.05f)
        {
            var n = 0;
            for (var i = 0; i < a.Count && i < b.Count; i++)
                if (Mathf.Abs(b[i].v0.x - a[i].v0.x - dx) < eps && Mathf.Abs(b[i].v0.y - a[i].v0.y - dy) < eps &&
                    Mathf.Abs(b[i].v2.x - a[i].v2.x - dx) < eps && Mathf.Abs(b[i].v2.y - a[i].v2.y - dy) < eps) n++;
            return n;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Wave_AtQuarterPeriod_LiftsOnlyTheSpanByAmp(bool unified)
        {
            var t = MakeAnimated("AB<wave amp=10 freq=1 phase=0>CD</wave>EF", unified);
            Step(0f);
            var q0 = DrawnQuads(t);
            Assert.AreEqual(6, q0.Count, "6 glyph quads");
            Step(0.25f); // sin(2*pi*1*0.25) = 1
            var q1 = DrawnQuads(t);
            Assert.AreEqual(2, CountMoved(q0, q1, 0f, 10f), "C and D lifted by exactly amp=10");
            Assert.AreEqual(4, CountMoved(q0, q1, 0f, 0f), "A, B, E, F unchanged");
            Step(0.75f); // sin(1.5 pi) = -1
            var q2 = DrawnQuads(t);
            Assert.AreEqual(2, CountMoved(q0, q2, 0f, -10f), "C and D lowered by amp at 3/4 period");
            Debug.Log($"[W2] wave unified={unified}: C dy@0.25={q1.Count} quads, moved {CountMoved(q0, q1, 0f, 10f)}");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Wave_PhasePerCharacter_ShiftsNeighbours(bool unified)
        {
            // phase = pi/2 per character: at t=0, glyph k has dy = amp*sin(-k*pi/2) = 0, -amp, 0, +amp.
            var t = MakeAnimated("<wave amp=8 freq=1 phase=1.5707964>ABCD</wave>", unified);
            Step(0f);
            var baseline = ByX(DrawnQuads(Make("ABCD", unified)));
            var q = ByX(DrawnQuads(t));
            var dys = q.Select((x, i) => x.v0.y - baseline[i].v0.y).ToArray();
            Debug.Log($"[W2] wave phase dys unified={unified}: {Fmt(dys)}");
            Assert.AreEqual(0f, dys[0], 0.05f);
            Assert.AreEqual(-8f, dys[1], 0.05f);
            Assert.AreEqual(0f, dys[2], 0.05f);
            Assert.AreEqual(8f, dys[3], 0.05f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Bounce_IsAlwaysUpward(bool unified)
        {
            var t = MakeAnimated("<bounce amp=6 freq=1 phase=0>AB</bounce>", unified);
            Step(0f);
            var q0 = DrawnQuads(t);
            Step(0.5f); // |sin(pi*0.5)| = 1
            Assert.AreEqual(2, CountMoved(q0, DrawnQuads(t), 0f, 6f), "lifted by amp at the top of the hop");
            Step(1.5f); // |sin(1.5 pi)| = 1 -> still up (abs)
            Assert.AreEqual(2, CountMoved(q0, DrawnQuads(t), 0f, 6f), "bounce never goes below the baseline");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Pulse_ScalesAboutGlyphCentre(bool unified)
        {
            var t = MakeAnimated("<pulse amp=0.5 freq=1 phase=0>H</pulse>", unified);
            Step(0f);
            var a = DrawnQuads(t)[0];
            Step(0.25f); // s = 1 + 0.5*(0.5+0.5*sin(pi/2)) = 1.5
            var b = DrawnQuads(t)[0];
            Step(0.75f); // s = 1 + 0.5*(0.5-0.5) = 1
            var c = DrawnQuads(t)[0];
            // At t=0: s = 1 + 0.5*0.5 = 1.25 (sin 0 = 0); so b/a = 1.5/1.25, c/a = 1/1.25.
            Assert.AreEqual(1.5f / 1.25f, b.Width / a.Width, 1e-3f, "width ratio at the peak");
            Assert.AreEqual(1f / 1.25f, c.Width / a.Width, 1e-3f, "width ratio at the trough");
            Assert.AreEqual(a.Center.x, b.Center.x, 1e-3f, "scaled about the centre");
            Assert.AreEqual(a.Center.y, b.Center.y, 1e-3f, "scaled about the centre");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Fade_ModulatesVertexAlpha(bool unified)
        {
            var t = MakeAnimated("A<fade min=0 freq=1 phase=0>B</fade>C", unified);
            Step(0f); // cos(0)=1 -> full
            var q0 = ByX(DrawnQuads(t));
            Assert.AreEqual(255, q0[1].MinAlpha);
            Step(0.5f); // cos(pi) = -1 -> min = 0
            var q1 = ByX(DrawnQuads(t));
            Assert.AreEqual(0, q1[1].MaxAlpha, "faded glyph alpha 0 at half period");
            Assert.AreEqual(255, q1[0].MinAlpha, "neighbours untouched");
            Assert.AreEqual(255, q1[2].MinAlpha, "neighbours untouched");
            Step(0.75f); // cos(1.5pi) = 0 -> 0.5
            Assert.AreEqual(128, ByX(DrawnQuads(t))[1].MinAlpha, 1, "half alpha at 3/4 period");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Rainbow_SetsHueKeepsAlpha(bool unified)
        {
            var t = MakeAnimated("A<rainbow freq=0.25 phase=0 sat=1>B</rainbow>", unified);
            Step(0f); // hue 0 = red
            var q = ByX(DrawnQuads(t));
            Assert.AreEqual(new Color32(255, 0, 0, 255), q[1].c0, "hue 0 at t=0");
            Assert.AreEqual(new Color32(255, 255, 255, 255), q[0].c0, "A keeps the base colour");
            Step(1f); // hue = -0.25 -> 0.75 (violet/blue-magenta: r~128, g 0, b 255)
            var c = ByX(DrawnQuads(t))[1].c0;
            var expected = (Color32)Color.HSVToRGB(0.75f, 1f, 1f);
            Assert.AreEqual(expected.r, c.r, 1); Assert.AreEqual(expected.g, c.g, 1); Assert.AreEqual(expected.b, c.b, 1);
            Assert.AreEqual(255, c.a);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Shake_IsBoundedAndStepped(bool unified)
        {
            var t = MakeAnimated("<shake amp=5 freq=10>ABCD</shake>", unified);
            Step(0f);
            var baseline = ByX(DrawnQuads(Make("ABCD", unified)));
            Step(0.31f);
            var a = ByX(DrawnQuads(t));
            Step(0.33f); // same 1/freq step (floor(3.1) == floor(3.3)) -> identical offsets
            var b = ByX(DrawnQuads(t));
            var moved = 0;
            for (var i = 0; i < 4; i++)
            {
                var dx = a[i].v0.x - baseline[i].v0.x;
                var dy = a[i].v0.y - baseline[i].v0.y;
                Assert.LessOrEqual(Mathf.Abs(dx), 5.001f); Assert.LessOrEqual(Mathf.Abs(dy), 5.001f);
                if (Mathf.Abs(dx) > 0.01f || Mathf.Abs(dy) > 0.01f) moved++;
                Assert.AreEqual(a[i].v0.x, b[i].v0.x, 1e-4f, "constant within one jitter step");
                Assert.AreEqual(a[i].v0.y, b[i].v0.y, 1e-4f, "constant within one jitter step");
            }
            Assert.Greater(moved, 0, "shake moved the glyphs");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Animation_NoReshapeRelayoutOrRebuild_PerFrame(bool unified)
        {
            var t = MakeAnimated("Wave <wave>these</wave> and <rainbow>colour</rainbow> <shake>this</shake>", unified);
            Step(0f);
            Step(0.01f);
            var fp = TextProcessor.FirstPassCount;
            var lay = TextProcessor.LayoutCount;
            var up = UniText.MeshUploadCount;
            var effectUploads = W2.Counter("VertexEffectUploads");
            for (var i = 1; i <= 30; i++) Step(0.01f + i / 60f);
            Assert.AreEqual(fp, TextProcessor.FirstPassCount, "no shaping per frame");
            Assert.AreEqual(lay, TextProcessor.LayoutCount, "no layout per frame");
            Assert.AreEqual(up, UniText.MeshUploadCount, "no mesh generation / rebuild per frame");
            Assert.AreEqual(30, W2.Counter("VertexEffectUploads") - effectUploads, "one vertex upload per frame");
        }

        private static int CountAllocations(System.Action action)
        {
            var recorder = Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try { action(); }
            finally
            {
                recorder.enabled = false;
                recorder.CollectFromAllThreads();
            }
            return recorder.sampleBlockCount;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Animation_AndReveal_ZeroGcPerFrame(bool unified)
        {
            var t = MakeAnimated("Zero <wave>garbage</wave> <pulse>per</pulse> <fade>frame</fade> <bounce>on</bounce> <shake>Quest</shake> <rainbow>!</rainbow>", unified);
            var reveal = W2.Add(t.gameObject, "LightSide.UniTextReveal");
            W2.Set(reveal, "UnitsPerSecond", 20f);
            W2.Set(reveal, "SlideOffset", new Vector2(0f, -6f));
            W2.Call(reveal, "Restart");
            Step(0f);
            var tick = (System.Action)System.Delegate.CreateDelegate(typeof(System.Action),
                W2.Type("LightSide.UniText").GetMethod("TickVertexEffects", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            // Drive the clock through a typed delegate: reflection SetValue would box (test-side garbage).
            var setTime = (System.Action<float>)System.Delegate.CreateDelegate(typeof(System.Action<float>),
                W2.Type("LightSide.UniTextAnimationClock").GetProperty("ManualTime").GetSetMethod());
            var clock = 0f;
            for (var i = 0; i < 10; i++) { clock += 1f / 72f; setTime(clock); tick(); } // warm-up (JIT, first events)
            var before = W2.Counter("VertexEffectUploads");
            var allocs = CountAllocations(() =>
            {
                for (var i = 0; i < 60; i++) { clock += 1f / 72f; setTime(clock); tick(); }
            });
            var uploads = W2.Counter("VertexEffectUploads") - before;
            Debug.Log($"[W2] GC.Alloc samples over 60 animated frames (unified={unified}): {allocs}; uploads={uploads}; glyphs={((UniTextVertexEffectContextProxy)t).GlyphCount}");
            Assert.AreEqual(60, uploads, "every frame uploaded");
            Assert.AreEqual(0, allocs, "the per-frame animation pass must not allocate managed memory");
        }

        [Test]
        public void Animation_CostPerGlyph()
        {
            // 400 animated glyphs (wave), unified renderer: time the per-frame pass (informational).
            var sb = new System.Text.StringBuilder("<wave>");
            for (var i = 0; i < 40; i++) sb.Append("ABCDEFGHIJ");
            sb.Append("</wave>");
            var t = Make(sb.ToString(), true, AddAnimationTags, width: 4000, height: 2000, fontSize: 20f);
            var n = ((UniTextVertexEffectContextProxy)t).GlyphCount;
            var setTime = (System.Action<float>)System.Delegate.CreateDelegate(typeof(System.Action<float>),
                W2.Type("LightSide.UniTextAnimationClock").GetProperty("ManualTime").GetSetMethod());
            var tick = (System.Action)System.Delegate.CreateDelegate(typeof(System.Action),
                W2.Type("LightSide.UniText").GetMethod("TickVertexEffects", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            var clock = 0f;
            for (var i = 0; i < 20; i++) { clock += 1f / 72f; setTime(clock); tick(); }
            const int frames = 300;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < frames; i++) { clock += 1f / 72f; setTime(clock); tick(); }
            sw.Stop();
            var usPerFrame = sw.Elapsed.TotalMilliseconds * 1000.0 / frames;
            Debug.Log($"[W2] animation pass: {n} animated glyphs, {usPerFrame:0.0} us/frame, {usPerFrame * 1000.0 / n:0} ns/glyph (Editor, incl. mesh upload)");
            Assert.AreEqual(400, n);
            Assert.Less(usPerFrame, 5000.0, "sanity bound");
        }

        [Test]
        public void NoAnimatedSpans_NoCaptureNoTick()
        {
            var captures = W2.Counter("VertexEffectCaptures");
            var t = Make("Plain <b>text</b> with no animation", true, AddAnimationTags);
            for (var i = 0; i < 5; i++) Step(i * 0.1f);
            Assert.AreEqual(captures, W2.Counter("VertexEffectCaptures"), "a component without animated spans is never captured");
            Assert.IsFalse((bool)W2.Get(t, "HasVertexEffects"));
        }

        [Test]
        public void AnimationTimeScaleAndPhase_ChangeTheClock()
        {
            var t = MakeAnimated("<wave amp=10 freq=1 phase=0>A</wave>", true);
            W2.Set(t, "AnimationTimeScale", 0.5f);
            Step(0f);
            var q0 = DrawnQuads(t)[0];
            Step(0.5f); // scaled time 0.25 -> +10
            Assert.AreEqual(10f, DrawnQuads(t)[0].v0.y - q0.v0.y, 0.05f, "time scale 0.5 halves the speed");
            W2.Set(t, "AnimationTimeScale", 1f);
            W2.Set(t, "AnimationPhase", 0.5f); // time now 0.25 + 0.5 = 0.75 -> -10
            Step(0.5f + 1e-6f);
            Assert.AreEqual(-10f, DrawnQuads(t)[0].v0.y - q0.v0.y, 0.05f, "phase offsets the time");
        }
    }

    /// <summary>Reads the glyph count of a component's effect context by reflection.</summary>
    internal readonly struct UniTextVertexEffectContextProxy
    {
        private readonly UniText t;
        private UniTextVertexEffectContextProxy(UniText t) { this.t = t; }
        public static explicit operator UniTextVertexEffectContextProxy(UniText t) => new(t);
        public int GlyphCount
        {
            get
            {
                var ctx = W2.Get(t, "VertexEffectContext");
                return ctx == null ? 0 : (int)W2.Get(ctx, "GlyphCount");
            }
        }
    }
}
