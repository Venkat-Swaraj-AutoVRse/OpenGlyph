using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LightSide.Tests
{
    /// <summary>
    /// Threading PR tests for the parallel text pipeline. These drive REAL <see cref="UniText"/>
    /// components through <see cref="Canvas.ForceUpdateCanvases"/> — the same batch path production
    /// uses — with enough components and characters to cross the parallel thresholds
    /// (<c>&gt;500</c> total chars, <c>&ge;3</c> dirty components), so the forced-parallel runs
    /// genuinely fan out to <see cref="UniTextWorkerPool"/> workers.
    ///
    /// <para>Coverage:
    /// <list type="bullet">
    /// <item><b>Byte-identical</b>: a forced-PARALLEL batch must produce the same generated vertices
    /// and the same layout (glyph count + result width) as a forced-SERIAL batch, over mixed
    /// Latin+Thai text on an MSDF font, with the unified renderer both OFF and ON, repeated many
    /// times — and must log no errors/exceptions.</item>
    /// <item><b>Off-main-thread guard</b>: <see cref="UniTextThreadGuard"/> FIRES when the
    /// material/style resolution is invoked on a worker (proving it detects the pre-fix bug), and is
    /// SILENT across a full forced-parallel rebuild (proving the fix moved all of it to the
    /// main-thread prepare step).</item>
    /// </list>
    /// </para>
    /// </summary>
    [TestFixture]
    internal sealed class ParallelPipelineThreadingTests
    {
        // Mixed Latin + Thai, each > 500 chars-worth across the batch, DIFFERENT per component so the
        // text-keyed shaping/glyph cache never short-circuits real work (project benchmark lesson).
        private const string ThaiRun = "สวัสดีชาวโลกนี่คือการทดสอบการตัดคำภาษาไทยที่ยาวพอสมควรเพื่อให้เกิดการทำงานแบบขนาน";

        private static string Mixed(int seed)
        {
            // ~120+ chars each; 4 components ⇒ well over the 500-char parallel threshold.
            return $"Component {seed}: the quick brown fox jumps over the lazy dog {seed}. " +
                   ThaiRun + $" (batch item {seed}, latin tail {seed}{seed}{seed}).";
        }

        private sealed class Rig : System.IDisposable
        {
            public GameObject canvasGo;
            public readonly List<UniText> texts = new();
            private readonly List<Object> _owned = new();

            public static Rig Create(int componentCount, UniText.UnifiedRendererMode unified)
            {
                string latinPath = MsdfTestUtil.FindNotoSansPath();
                string thaiPath = RealLayoutFixtures.FindThaiFontPath();
                if (latinPath == null || thaiPath == null) return null;

                // MSDF Latin main font + Thai fallback — exercises the MSDF material (Shader.Find /
                // new Material path in the appearance) that must be resolved on the main thread.
                var latin = UniTextFont.CreateFontAsset(File.ReadAllBytes(latinPath), 72, 0.1f, UniTextRenderMode.Msdf, 1024);
                var thai = UniTextFont.CreateFontAsset(File.ReadAllBytes(thaiPath), 72, 0.1f, UniTextRenderMode.Msdf, 1024);
                if (latin == null || thai == null) return null;

                var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
                stack.fonts.Add(latin);
                stack.fonts.Add(thai);
                var appearance = RealLayoutFixtures.LoadDefaultAppearance();

                var canvasGo = new GameObject("ThreadParCanvas", typeof(Canvas));
                var rig = new Rig { canvasGo = canvasGo };
                rig._owned.Add(latin); rig._owned.Add(thai); rig._owned.Add(stack);

                for (int i = 0; i < componentCount; i++)
                {
                    var ut = new GameObject("UT" + i, typeof(RectTransform)).AddComponent<UniText>();
                    ut.transform.SetParent(canvasGo.transform, false);
                    ((RectTransform)ut.transform).sizeDelta = new Vector2(1400, 400);
                    ut.FontStack = stack;
                    ut.Appearance = appearance;
                    ut.UnifiedRenderer = unified;
                    rig.texts.Add(ut);
                }
                return rig;
            }

            /// <summary>Assigns per-component distinct text and forces ONE synchronous batch rebuild.</summary>
            public void RebuildAll(int round)
            {
                for (int i = 0; i < texts.Count; i++)
                    texts[i].Text = Mixed(round * 100 + i);
                Canvas.ForceUpdateCanvases();
            }

            public void Dispose()
            {
                if (canvasGo != null) Object.DestroyImmediate(canvasGo);
                foreach (var o in _owned) if (o != null) Object.DestroyImmediate(o);
            }
        }

        // Snapshot of one component's output used for byte-identity comparison.
        private struct Snap { public List<Vector3> verts; public int glyphs; public float width; }

        private static Snap[] Capture(Rig rig)
        {
            var snaps = new Snap[rig.texts.Count];
            for (int i = 0; i < rig.texts.Count; i++)
            {
                var ut = rig.texts[i];
                snaps[i] = new Snap
                {
                    verts = ut.GetGeneratedVerticesForEditorTests(),
                    glyphs = ut.ResultGlyphs.Length,
                    width = ut.ResultSize.x,
                };
            }
            return snaps;
        }

        private static void AssertIdentical(Snap[] serial, Snap[] parallel, string ctx)
        {
            Assert.AreEqual(serial.Length, parallel.Length, $"{ctx}: component count differs");
            for (int i = 0; i < serial.Length; i++)
            {
                Assert.AreEqual(serial[i].glyphs, parallel[i].glyphs,
                    $"{ctx}: component {i} glyph count (line-break / layout) differs serial vs parallel");
                Assert.AreEqual(serial[i].width, parallel[i].width, 1e-4f,
                    $"{ctx}: component {i} result width (line breaks) differs serial vs parallel");
                var a = serial[i].verts; var b = parallel[i].verts;
                Assert.AreEqual(a.Count, b.Count,
                    $"{ctx}: component {i} vertex COUNT differs serial({a.Count}) vs parallel({b.Count})");
                for (int v = 0; v < a.Count; v++)
                    Assert.AreEqual(a[v], b[v],
                        $"{ctx}: component {i} vertex {v} differs serial{a[v]} vs parallel{b[v]} (not byte-identical)");
            }
        }

        [TestCase(UniText.UnifiedRendererMode.ForceOff, TestName = "ByteIdentical_UnifiedOff")]
        [TestCase(UniText.UnifiedRendererMode.ForceOn, TestName = "ByteIdentical_UnifiedOn")]
        public void ParallelMatchesSerial_ByteIdentical_NoErrors(UniText.UnifiedRendererMode unified)
        {
            if (!UniTextWorkerPool.IsParallelSupported)
            { Assert.Ignore("Single-core host: parallel path not supported, nothing to compare."); return; }

            SegHelper.EnsureUnicode();
            SegHelper.AssignDictionaries(); // so Thai actually segments (interior line breaks)

            using var rig = Rig.Create(4, unified); // 4 comps × ~160 chars ⇒ parallel thresholds crossed
            if (rig == null) { Assert.Ignore("Font fixtures unavailable on this host."); return; }

            // Warm once; bail if the native backend produced nothing (test would be vacuous).
            UniText.UseParallel = false;
            rig.RebuildAll(0);
            if (rig.texts[0].ResultGlyphs.Length == 0)
            { Assert.Ignore("Font backend produced no glyphs on this host."); return; }

            const int rounds = 50;
            bool savedParallel = UniText.UseParallel;
            try
            {
                for (int r = 1; r <= rounds; r++)
                {
                    // Serial reference.
                    UniText.UseParallel = false;
                    rig.RebuildAll(r);
                    var serial = Capture(rig);

                    // Forced parallel on the SAME text (RebuildAll re-assigns deterministically from r).
                    UniText.UseParallel = true;
                    rig.RebuildAll(r);
                    var parallel = Capture(rig);

                    AssertIdentical(serial, parallel, $"{unified} round {r}");
                }
            }
            finally { UniText.UseParallel = savedParallel; }
            // If any error/exception was logged during the 50 rounds, the test runner fails it by
            // default (no LogAssert.Expect was issued), satisfying "no exceptions/errors logged".
        }

        /// <summary>
        /// The off-main-thread guard must FIRE when a material/style resolution is forced onto a
        /// worker thread (this proves the assertion actually detects the pre-fix bug), and must stay
        /// SILENT across a real forced-parallel rebuild (proving the fix keeps all of it on the main
        /// thread). Guarded by the Editor-only AssertMainThread conditional, which is compiled in
        /// edit-mode test runs.
        /// </summary>
        [Test]
        public void OffMainThreadGuard_FiresWhenViolated_SilentAfterFix()
        {
            if (!UniTextWorkerPool.IsParallelSupported)
            { Assert.Ignore("Single-core host: no worker thread to violate on."); return; }

            SegHelper.EnsureUnicode();
            UniTextThreadGuard.CaptureMainThread();

            // (1) PROVE DETECTION: calling a guarded API on a worker increments the violation counter.
            UniTextThreadGuard.ResetViolationCount();
            // Expect the error the guard logs, so the test runner does not fail on it.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"\[UniText\] AppearanceStyleShim\.StyleFromMaterials was called on a worker thread"));
            var done = new ManualResetEventSlim(false);
            var t = new Thread(() =>
            {
                // Null array path still runs the assertion before returning GlyphStyle.Default.
                AppearanceStyleShim.StyleFromMaterials(null);
                done.Set();
            });
            t.IsBackground = true; t.Start();
            Assert.IsTrue(done.Wait(5000), "worker thread did not complete");
            t.Join(1000);
            Assert.AreEqual(1L, Interlocked.Read(ref UniTextThreadGuard.ViolationCount),
                "the guard must FIRE (count the violation) when StyleFromMaterials runs on a worker — " +
                "this is what detects the pre-fix off-main-thread resolution.");

            // (2) PROVE THE FIX: a full forced-parallel rebuild must trip the guard ZERO times, because
            // every material/style resolution now happens on the main-thread prepare step.
            using var rig = Rig.Create(4, UniText.UnifiedRendererMode.ForceOn);
            if (rig == null) { Assert.Ignore("Font fixtures unavailable on this host."); return; }
            UniText.UseParallel = true;
            UniTextThreadGuard.ResetViolationCount();
            for (int r = 0; r < 10; r++) rig.RebuildAll(r);
            Assert.AreEqual(0L, Interlocked.Read(ref UniTextThreadGuard.ViolationCount),
                "after the fix, a forced-parallel rebuild must resolve all materials/styles on the " +
                "main thread — the guard must never fire during mesh generation.");
        }

        /// <summary>
        /// Measurement (not a hard perf gate): times the full editor batch rebuild of a parallel-sized
        /// batch SERIAL vs forced-PARALLEL and reports both, so the PR can show the main-thread prepare
        /// step added no regression worth noting on the parallel path. Asserts only that parallel is
        /// not grossly slower than serial (≤ 2× — editor single-step batches are dominated by the
        /// synchronous rasterizer, so parallel's win is modest and the point is "no regression").
        /// </summary>
        [Test, Explicit("measurement — run on demand; prints timings to the log")]
        public void Measure_FullRebuildTime_SerialVsParallel()
        {
            if (!UniTextWorkerPool.IsParallelSupported) { Assert.Ignore("Single-core host."); return; }
            SegHelper.EnsureUnicode(); SegHelper.AssignDictionaries();

            using var rig = Rig.Create(8, UniText.UnifiedRendererMode.ForceOff);
            if (rig == null) { Assert.Ignore("Font fixtures unavailable."); return; }
            rig.RebuildAll(0);
            if (rig.texts[0].ResultGlyphs.Length == 0) { Assert.Ignore("No glyphs on this host."); return; }

            const int warm = 5, measure = 40;
            double Time(bool parallel)
            {
                UniText.UseParallel = parallel;
                for (int r = 0; r < warm; r++) rig.RebuildAll(1000 + r);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int r = 0; r < measure; r++) rig.RebuildAll(2000 + r + (parallel ? 100000 : 0));
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds / measure;
            }

            double serialMs = Time(false);
            double parallelMs = Time(true);
            UnityEngine.Debug.Log(
                $"[ParallelPipelineTiming] 8-component mixed Latin+Thai MSDF batch, full rebuild: " +
                $"serial={serialMs:F3} ms/rebuild, parallel={parallelMs:F3} ms/rebuild " +
                $"(ratio parallel/serial={parallelMs / serialMs:F2}).");

            Assert.Less(parallelMs, serialMs * 2.0,
                $"parallel full-rebuild ({parallelMs:F3} ms) regressed badly vs serial ({serialMs:F3} ms).");
        }
    }
}
