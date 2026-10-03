using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression for B16: the PARALLEL FIRST PASS must not touch Unity materials on workers.
    ///
    /// Why it fails without the fix: <c>BoldModifier.OnShaped</c> -> <c>GetWeightDelta</c> ->
    /// <c>UniTextFontProvider.GetMaterials</c> runs inside <c>DoFirstPass</c> on a worker. On a FRESH
    /// component nothing has called <c>PrepareMaterials()</c> yet (the only other call is in the
    /// mesh-generation prepare step, which runs AFTER the first pass), so <c>GetMaterials</c> missed
    /// its cache and resolved live through the appearance, tripping
    /// <see cref="UniTextThreadGuard"/> (<c>ViolationCount</c> &gt; 0 + logged errors). The fix prepares
    /// materials for every component before the dispatch and makes a worker cache miss fall back to the
    /// main font's prepared materials.
    ///
    /// The batch is built from brand-new components (new font provider, nothing prepared) with
    /// <c>&lt;b&gt;</c> text, crossing the parallel thresholds, and rebuilt with one
    /// <see cref="Canvas.ForceUpdateCanvases"/>. A probe <see cref="BoldModifier"/> subclass records
    /// the thread each first-pass apply ran on, proving the workers really ran.
    /// </summary>
    [TestFixture]
    internal sealed class ParallelFirstPassMaterialTests
    {
        // Records the managed thread id its OnApply ran on (OnApply runs in the first pass).
        private sealed class ProbeBoldModifier : BoldModifier
        {
            public static readonly ConcurrentBag<int> ApplyThreads = new();
            protected override void OnApply(int start, int end, string parameter)
            {
                ApplyThreads.Add(Thread.CurrentThread.ManagedThreadId);
                base.OnApply(start, end, parameter);
            }
        }

        private readonly List<Object> _junk = new();
        private bool _savedParallel;

        [SetUp]
        public void SetUp()
        {
            _savedParallel = UniText.UseParallel;
            ProbeBoldModifier.ApplyThreads.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            UniText.UseParallel = _savedParallel;
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        [Test]
        public void ParallelFirstPass_BoldMarkup_FreshComponents_NeverTouchesMaterialsOnWorkers()
        {
            if (!UniTextWorkerPool.IsParallelSupported)
            { Assert.Ignore("Single-core host: parallel path not supported."); return; }
            string latinPath = MsdfTestUtil.FindNotoSansPath();
            if (latinPath == null) { Assert.Ignore("NotoSans-Regular.ttf not found."); return; }

            SegHelper.EnsureUnicode();
            UniTextThreadGuard.CaptureMainThread();
            int mainId = Thread.CurrentThread.ManagedThreadId;

            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(latinPath), 72, 0.1f, UniTextRenderMode.Msdf, 1024);
            if (font == null) { Assert.Ignore("font asset creation failed."); return; }
            _junk.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(font);
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();

            var canvasGo = new GameObject("B16Canvas", typeof(Canvas)); _junk.Add(canvasGo);

            // 6 components x ~140 chars = ~840 chars > ParallelCharacterThreshold (500), 6 >= MinComponentsForParallel (3).
            const int count = 6;
            int totalChars = 0;
            var texts = new List<UniText>();
            for (int i = 0; i < count; i++)
            {
                var ut = new GameObject("UT" + i, typeof(RectTransform)).AddComponent<UniText>();
                ut.transform.SetParent(canvasGo.transform, false);
                ((RectTransform)ut.transform).sizeDelta = new Vector2(1400, 400);
                ut.FontStack = stack;
                ut.Appearance = appearance;
                ut.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOff;
                ut.RegisterModifier(new ModRegister { Modifier = new ProbeBoldModifier(), Rule = new BoldParseRule() });
                string s = $"Item {i}: the <b>quick brown fox</b> jumps over the lazy dog {i}. " +
                           $"Another <b>bold run number {i}{i}{i}</b> followed by plain text to pad the line out nicely.";
                totalChars += s.Length;
                ut.Text = s; // all dirty, nothing prepared yet
                texts.Add(ut);
            }
            Assert.Greater(totalChars, 500, "precondition: total chars must exceed ParallelCharacterThreshold");

            UniText.UseParallel = true;
            UniTextThreadGuard.ResetViolationCount();
            Canvas.ForceUpdateCanvases(); // fresh batch: first pass runs before any mesh-gen PrepareMaterials

            // Precondition: the first pass really ran on worker threads.
            var ids = new List<int>(ProbeBoldModifier.ApplyThreads);
            Assert.Greater(ids.Count, 0, "probe modifier never applied: <b> markup not parsed in the first pass");
            Assert.IsTrue(ids.Exists(id => id != mainId),
                "precondition: the first pass must run on worker threads (parallel path not taken; thresholds/UseParallel?)");

            // Checked BEFORE the glyph check: without the fix the worker throws
            // "get_shader can only be called from the main thread", aborting the first pass, so the
            // text produces no glyphs at all — that is the bug, not a host limitation.
            Assert.AreEqual(0L, Interlocked.Read(ref UniTextThreadGuard.ViolationCount),
                "BoldModifier.OnShaped resolved materials off the main thread during the parallel first pass " +
                "(PrepareMaterials was not run before the dispatch / no worker fallback) - B16.");
            LogAssert.NoUnexpectedReceived();
            Assert.Greater(texts[0].ResultGlyphs.Length, 0, "bold text produced no glyphs");
        }
    }
}
