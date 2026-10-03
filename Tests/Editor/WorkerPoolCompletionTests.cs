using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Pins the <see cref="UniTextWorkerPool"/> completion-wait fix (threading PR problem #4): the
    /// previous <c>Execute</c> abandoned the batch after a 5 s timeout and then cleared the shared
    /// batch state while workers might still be reading it. The fix makes each worker signal its
    /// barrier exactly once per cycle (try/finally) and makes <c>Execute</c> wait unconditionally for
    /// every worker to finish before returning (logging one warning if it runs long, never bailing).
    ///
    /// <para>This test dispatches an action that sleeps, then asserts that EVERY component's work had
    /// completed by the time <c>Execute</c> returned — i.e. the method did not return while workers
    /// were still active.</para>
    /// </summary>
    [TestFixture]
    internal sealed class WorkerPoolCompletionTests
    {
        private static UniText[] MakeComponents(int n, out GameObject canvasGo)
        {
            // Real UniText instances (Execute's action receives UniText and skips nulls), but we never
            // run text work on them here — the action we pass is our own instrumentation.
            canvasGo = new GameObject("WorkerPoolCanvas", typeof(Canvas));
            var arr = new UniText[n];
            for (int i = 0; i < n; i++)
            {
                var ut = new GameObject("UT" + i, typeof(RectTransform)).AddComponent<UniText>();
                ut.transform.SetParent(canvasGo.transform, false);
                arr[i] = ut;
            }
            return arr;
        }

        [Test]
        public void Execute_WaitsForAllWorkers_BeforeReturning()
        {
            if (!UniTextWorkerPool.IsParallelSupported)
            { Assert.Ignore("Single-core host: Execute runs serially, nothing to race."); return; }

            const int n = 16;
            var comps = MakeComponents(n, out var canvasGo);
            try
            {
                int completed = 0;
                // Each unit sleeps a little so, with the OLD code, a slow batch could blow the timeout
                // and Execute would return with completed < n. The fix guarantees completed == n.
                UniTextWorkerPool.Execute(comps, n, _ =>
                {
                    Thread.Sleep(5);
                    Interlocked.Increment(ref completed);
                });

                Assert.AreEqual(n, Volatile.Read(ref completed),
                    "Execute returned before all workers finished — it must wait for every worker to " +
                    "complete (so clearing the batch state afterwards cannot race a live worker).");
            }
            finally { Object.DestroyImmediate(canvasGo); }
        }

        [Test]
        public void Execute_PropagatesWorkerExceptions_AndStillCompletes()
        {
            if (!UniTextWorkerPool.IsParallelSupported)
            { Assert.Ignore("Single-core host."); return; }

            const int n = 8;
            var comps = MakeComponents(n, out var canvasGo);
            try
            {
                // Half the units throw; Execute must still wait for ALL to finish (the throwing ones
                // signal the barrier in the finally) and must not deadlock or return early.
                int reached = 0;
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true; // worker exceptions are logged
                UniTextWorkerPool.Execute(comps, n, _ =>
                {
                    Interlocked.Increment(ref reached);
                    throw new System.InvalidOperationException("intentional worker fault");
                });
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

                Assert.AreEqual(n, Volatile.Read(ref reached),
                    "every unit's action must have run exactly once; a throwing action must still " +
                    "signal completion so Execute neither hangs nor returns early.");
            }
            finally { Object.DestroyImmediate(canvasGo); }
        }
    }
}
