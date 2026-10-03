using System;
using System.Threading;
using UnityEngine;
using ThreadPriority = System.Threading.ThreadPriority;

namespace LightSide
{
    /// <summary>
    /// Thread pool for parallel text processing across multiple UniText components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Manages a pool of worker threads (CPU count - 1) that can process UniText
    /// components in parallel during the first pass (shaping, BiDi analysis).
    /// </para>
    /// <para>
    /// Uses a barrier-based synchronization model: the main thread distributes work
    /// across workers and waits for all to complete before proceeding.
    /// </para>
    /// </remarks>
    /// <seealso cref="UniText"/>
    internal static class UniTextWorkerPool
    {
        private static readonly int ThreadCount = Math.Max(1, Environment.ProcessorCount - 1);
        private const int BarrierTimeoutMs = 5000;

        private static Thread[] workers;
        private static AutoResetEvent[] workReady;
        private static CountdownEvent barrier;
        private static volatile bool isInitialized;
        private static volatile bool isShuttingDown;

    #if UNITY_EDITOR
        static UniTextWorkerPool()
        {
            Reseter.ManagedCleaning += ForceShutdown;
        }
    #endif

        private static void ForceShutdown()
        {
            lock (typeof(UniTextWorkerPool))
            {
                isShuttingDown = true;

                if (workReady != null)
                {
                    for (var i = 0; i < workReady.Length; i++)
                        workReady[i]?.Set();
                }

                if (workers != null)
                {
                    for (var i = 0; i < workers.Length; i++)
                        workers[i]?.Join(50);
                }

                if (workReady != null)
                {
                    for (var i = 0; i < workReady.Length; i++)
                    {
                        try { workReady[i]?.Dispose(); } catch { }
                    }
                }

                try { barrier?.Dispose(); } catch { }

                workers = null;
                workReady = null;
                barrier = null;
                threadStartIndices = null;
                threadEndIndices = null;
                threadExceptions = null;
                currentComponents = null;
                currentAction = null;
                isInitialized = false;
                isShuttingDown = false;
            }
        }

        private static UniText[] currentComponents;
        private static int currentComponentCount;
        private static Action<UniText> currentAction;

        private static int[] threadStartIndices;
        private static int[] threadEndIndices;

        private static Exception[] threadExceptions;
        private static volatile int exceptionCount;

        /// <summary>Returns true if parallel processing is supported (more than 1 CPU core, not WebGL).</summary>
        public static bool IsParallelSupported =>
#if UNITY_WEBGL && !UNITY_EDITOR
            false;
#else
            ThreadCount > 1;
#endif

        /// <summary>Ensures the worker pool is initialized.</summary>
        public static void EnsureInitialized()
        {
            if (isInitialized || isShuttingDown) return;

            lock (typeof(UniTextWorkerPool))
            {
                if (isInitialized || isShuttingDown) return;

                isShuttingDown = false;

                workers = new Thread[ThreadCount];
                workReady = new AutoResetEvent[ThreadCount];
                threadStartIndices = new int[ThreadCount];
                threadEndIndices = new int[ThreadCount];
                threadExceptions = new Exception[ThreadCount];
                barrier = new CountdownEvent(1);

                for (var i = 0; i < ThreadCount; i++)
                {
                    workReady[i] = new AutoResetEvent(false);

                    var threadIdx = i;
                    workers[i] = new Thread(() => WorkerLoop(threadIdx))
                    {
                        IsBackground = true,
                        Name = $"UniTextWorker_{i}",
                        Priority = ThreadPriority.Normal
                    };
                    workers[i].Start();
                }

                Application.quitting += Shutdown;
                isInitialized = true;

                Cat.MeowFormat("[UniTextWorkerPool] Initialized with {0} threads", ThreadCount);
            }
        }

        private static void Shutdown()
        {
            // Serialise against ForceShutdown / EnsureInitialized (all take the same type lock) so the
            // worker/event arrays are not nulled from under us mid-iteration, and null-guard every
            // slot in case a concurrent ForceShutdown already tore them down.
            lock (typeof(UniTextWorkerPool))
            {
                if (!isInitialized) return;

                isShuttingDown = true;

                var ready = workReady;
                if (ready != null)
                    for (var i = 0; i < ready.Length; i++)
                        ready[i]?.Set();

                // Give each worker time to drain its current cycle rather than abandoning one mid-run.
                // A single worker only ever runs one component-batch slice, bounded by one text layout,
                // so a short join is ample; this never blocks app quit for long.
                var ws = workers;
                if (ws != null)
                    for (var i = 0; i < ws.Length; i++)
                        ws[i]?.Join(500);

                isInitialized = false;
            }
        }

        private static void WorkerLoop(int threadIdx)
        {
            while (!isShuttingDown)
            {
                try
                {
                    workReady[threadIdx].WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                // Woken to EXIT (shutdown Set the event without a matching barrier.Reset): leave
                // without signalling. Execute is not waiting on a fresh cycle in this case (it breaks
                // on isShuttingDown), so there is no barrier to decrement here.
                if (isShuttingDown) break;

                var localBarrier = barrier;
                if (localBarrier == null) break;

                // Woken to WORK. From here the barrier was Reset to ThreadCount by Execute and is
                // waiting for exactly one Signal from each worker, so this cycle MUST signal exactly
                // once no matter what — including if the action throws or isShuttingDown flips while we
                // run. The try/finally guarantees that, which is what lets Execute wait unconditionally
                // for completion instead of bailing on a timeout while workers are still live.
                try
                {
                    var starts = threadStartIndices;
                    var ends = threadEndIndices;
                    var components = currentComponents;
                    var action = currentAction;

                    if (starts != null && ends != null && components != null && action != null)
                    {
                        var start = starts[threadIdx];
                        var end = ends[threadIdx];

                        for (var i = start; i < end; i++)
                        {
                            var comp = components[i];
                            if (comp != null)
                                action(comp);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var exceptions = threadExceptions;
                    if (exceptions != null)
                    {
                        exceptions[threadIdx] = ex;
                        Interlocked.Increment(ref exceptionCount);
                    }
                }
                finally
                {
                    try { localBarrier.Signal(); }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { } // barrier already fully signalled / disposed
                }
            }
        }

        /// <summary>Executes an action on multiple UniText components, optionally in parallel.</summary>
        /// <param name="components">Array of components to process.</param>
        /// <param name="count">Number of components in the array.</param>
        /// <param name="action">Action to execute on each component.</param>
        public static void Execute(UniText[] components, int count, Action<UniText> action)
        {
            if (count == 0) return;

            if (count == 1 || !IsParallelSupported || isShuttingDown)
            {
                for (var i = 0; i < count; i++)
                {
                    var comp = components[i];
                    if (comp != null)
                        action(comp);
                }
                return;
            }

            EnsureInitialized();

            if (!isInitialized || isShuttingDown)
            {
                for (var i = 0; i < count; i++)
                {
                    var comp = components[i];
                    if (comp != null)
                        action(comp);
                }
                return;
            }

            currentComponents = components;
            currentComponentCount = count;
            currentAction = action;
            exceptionCount = 0;

            var perThread = count / ThreadCount;
            var remainder = count % ThreadCount;
            var offset = 0;

            for (var i = 0; i < ThreadCount; i++)
            {
                threadStartIndices[i] = offset;
                var threadCount = perThread + (i < remainder ? 1 : 0);
                offset += threadCount;
                threadEndIndices[i] = offset;
                threadExceptions[i] = null;
            }

            barrier.Reset(ThreadCount);

            for (var i = 0; i < ThreadCount; i++)
                workReady[i].Set();

            // ALWAYS wait for every worker to finish before returning. The previous code bailed after
            // BarrierTimeoutMs and then cleared currentComponents/currentAction while workers might
            // still be running action(comp) against them — a use-after-free of the batch state. Each
            // worker now signals exactly once per cycle (WorkerLoop's try/finally), so this wait
            // completes in normal operation; the timeout only emits ONE diagnostic warning and then
            // keeps waiting. The sole early exit is a real shutdown (isShuttingDown), which Joins the
            // workers itself.
            try
            {
                var localBarrier = barrier;
                if (localBarrier != null)
                {
                    const int pollIntervalMs = 10;
                    var elapsed = 0;
                    var warned = false;
                    while (!localBarrier.Wait(pollIntervalMs))
                    {
                        if (isShuttingDown) break;
                        elapsed += pollIntervalMs;
                        if (!warned && elapsed >= BarrierTimeoutMs)
                        {
                            warned = true;
                            Debug.LogWarning(
                                $"[UniTextWorkerPool] Parallel text pass exceeded {BarrierTimeoutMs}ms; " +
                                "still waiting for all workers to finish (not abandoning them — doing so " +
                                "would free the batch state out from under a running worker).");
                        }
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }

            // Safe to clear now: either every worker signalled (work done, no worker still touches
            // these) or a shutdown is in progress and Shutdown/ForceShutdown Joins the workers.
            currentComponents = null;
            currentAction = null;

            if (exceptionCount > 0)
            {
                for (var i = 0; i < ThreadCount; i++)
                {
                    if (threadExceptions[i] != null)
                    {
                        Debug.LogException(threadExceptions[i]);
                        threadExceptions[i] = null;
                    }
                }
            }
        }
    }

}
