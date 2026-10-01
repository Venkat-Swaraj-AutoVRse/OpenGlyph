using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Phase-1b dictionary reload safety: a segmentation-dictionary change must apply only BETWEEN
    /// layout passes, never mid-pass on a worker thread. DictionarySegmenter now holds an immutable
    /// snapshot captured once at the start of InjectBreaks; UniTextSettings.Changed only flips a
    /// dirty flag, and the snapshot is rebuilt at the next pass start. These tests exercise that:
    ///   (1) a settings flip from another thread WHILE many parallel passes run must not throw or
    ///       produce a torn read (an NRE / inconsistent trie mid-pass);
    ///   (2) after a change settles, the NEXT pass reflects the new dictionary (break opportunities
    ///       appear when dictionaries are assigned, vanish when cleared).
    /// </summary>
    [TestFixture]
    public class DictionaryReloadConcurrencyTests
    {
        [OneTimeSetUp]
        public void Setup()
        {
            SegHelper.EnsureUnicode();
            SegHelper.AssignDictionaries();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            // Leave the shipped dictionaries assigned for subsequent fixtures.
            SegHelper.ResetDictionaryAssignment();
            SegHelper.AssignDictionaries();
        }

        private static int[] ThaiCodepoints()
        {
            string text = SegmentationFixtures.Thai[0].Text + SegmentationFixtures.Thai[1].Text
                        + SegmentationFixtures.Thai[2].Text + SegmentationFixtures.Thai[3].Text;
            var l = new List<int>(text.Length);
            for (int i = 0; i < text.Length; i++) l.Add(text[i]); // BMP Thai — no surrogates
            return l.ToArray();
        }

        private static int SegBreaks(LineBreakAlgorithm lba, int[] cps)
        {
            var breaks = new LineBreakType[cps.Length + 1];
            lba.GetBreakOpportunitiesWithSegmentation(cps, breaks);
            int n = 0;
            for (int i = 1; i < cps.Length; i++) if (breaks[i] != LineBreakType.None) n++;
            return n;
        }

        [Test]
        public void SettingsFlip_DuringParallelPasses_NoTornReadsOrExceptions()
        {
            int[] cps = ThaiCodepoints();
            Assert.Greater(cps.Length, 8, "Thai fixture too short to segment.");

            // Each worker owns its own LineBreakAlgorithm (hence its own DictionarySegmenter), as the
            // layout pipeline uses per-pass instances; the SHARED mutable state under test is
            // UniTextSettings + the Changed→RebuildSnapshot→atomic-swap path. Settings are flipped on
            // THIS (main) thread, because resolving a dictionary reads TextAsset.bytes which Unity
            // permits only on the main thread — which is exactly why the reload must be a main-thread
            // snapshot build that workers only read. Workers run concurrently on the thread pool.
            const int workers = 8;
            const int passesPerWorker = 400;
            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var withDicts = UniTextSettings.Instance;
            var empty = ScriptableObject.CreateInstance<UniTextSettings>();

            // Create AND warm each worker's LineBreakAlgorithm on the MAIN thread first. This matches
            // production, where the segmenter (which resolves dictionaries via TextAsset.bytes, a
            // main-thread-only API) is first built on the main thread; a worker then only READS the
            // already-resolved snapshot. One warm pass forces the segmenter + its first snapshot into
            // existence here, so no worker triggers an off-thread dictionary build.
            var lbas = new LineBreakAlgorithm[workers];
            for (int w = 0; w < workers; w++)
            {
                lbas[w] = new LineBreakAlgorithm();
                SegBreaks(lbas[w], cps); // warm: builds the segmenter + initial snapshot on this thread
            }

            var tasks = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                var lba = lbas[w];
                tasks[w] = Task.Run(() =>
                {
                    for (int p = 0; p < passesPerWorker; p++)
                    {
                        try { SegBreaks(lba, cps); }
                        catch (Exception ex) { errors.Enqueue(ex); }
                    }
                });
            }

            try
            {
                // The flip to `empty` makes Load emit the expected "no dictionary assigned" warnings;
                // ignore log messages here so the test asserts only on real worker exceptions.
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
                // Flip the setting on the main thread while the workers hammer layout passes. Each
                // SetInstance fires Changed → RebuildSnapshot (main thread, reads asset.bytes safely),
                // publishing a new snapshot that in-flight worker passes do not tear on.
                bool on = false;
                while (!Task.WaitAll(tasks, 0))
                {
                    on = !on;
                    UniTextSettings.SetInstance(on ? withDicts : empty);
                    Thread.Sleep(1);
                }
            }
            finally
            {
                Task.WaitAll(tasks);
                UniTextSettings.SetInstance(withDicts);
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }

            Assert.IsEmpty(errors,
                "Dictionary reload during parallel layout caused exceptions (torn read / NRE): " +
                (errors.TryPeek(out var e) ? e.ToString() : ""));
        }

        [Test]
        public void SettingsChange_AppliesOnNextPass_NotMidPass()
        {
            int[] cps = ThaiCodepoints();
            var lba = new LineBreakAlgorithm();

            // With dictionaries assigned, the next pass must find interior word breaks.
            UniTextSettings.SetInstance(UniTextSettings.Instance); // ensure Changed fired at least once
            SegHelper.ResetDictionaryAssignment();
            SegHelper.AssignDictionaries();
            int withDicts = SegBreaks(lba, cps);
            Assert.Greater(withDicts, 0, "With a Thai dictionary assigned, the pass should add interior breaks.");

            // Clear the dictionaries: swap in an empty settings instance (fires Changed → MarkDirty).
            var empty = ScriptableObject.CreateInstance<UniTextSettings>();
            UniTextSettings.SetInstance(empty);
            // The NEXT pass must reflect the cleared dictionary (no dictionary interior breaks).
            int cleared = SegBreaks(lba, cps);
            Assert.AreEqual(0, cleared,
                "After clearing the dictionary, the next pass must use the new (empty) snapshot.");

            // Re-assign: the subsequent pass must regain the breaks (snapshot rebuilt at pass start).
            SegHelper.ResetDictionaryAssignment();
            SegHelper.AssignDictionaries();
            int reassigned = SegBreaks(lba, cps);
            Assert.Greater(reassigned, 0, "After re-assigning, the next pass must use the restored dictionary.");
        }
    }
}
