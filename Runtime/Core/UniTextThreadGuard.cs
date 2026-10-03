using System.Diagnostics;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace LightSide
{
    /// <summary>
    /// Records the Unity main thread and asserts that main-thread-only work (resolving materials,
    /// synthesising styles from materials, invoking the public <c>Rebuilding</c> event) is never
    /// executed on a <see cref="UniTextWorkerPool"/> worker thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parallel text pipeline fans <c>DoFirstPass</c> / <c>DoGenerateMeshData</c> out to worker
    /// threads. Any Unity API touched there (<c>Shader.Find</c>, <c>new Material</c>,
    /// <c>Material.HasProperty/GetColor/IsKeywordEnabled</c>) is undefined behaviour off the main
    /// thread, and a plain <c>Dictionary</c> cache written there races. The threading fix moves all
    /// of that into a main-thread PREPARE step; this guard is the tripwire that proves the move is
    /// complete and stays complete — if any such call ever happens on a worker again,
    /// <see cref="AssertMainThread"/> logs an error naming the API.
    /// </para>
    /// <para>
    /// The assertion is compiled only into the Editor and development builds (guarded by the
    /// <c>UNITY_EDITOR</c> / <c>DEVELOPMENT_BUILD</c> conditional on <see cref="AssertMainThread"/>),
    /// so it carries zero cost in a shipping player.
    /// </para>
    /// </remarks>
    internal static class UniTextThreadGuard
    {
        // -1 until the main thread records itself. Volatile so a worker reads the published value.
        private static volatile int mainThreadId = -1;

        /// <summary>
        /// Records the calling thread as the Unity main thread. Idempotent; call from any point known
        /// to run on the main thread (pipeline init, the pre-render batch entry). Captured lazily so no
        /// static constructor ordering is required.
        /// </summary>
        public static void CaptureMainThread()
        {
            // Every caller IS the main thread, so the value is stable once set.
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>True when a main thread is not yet known, or the caller is on it.</summary>
        public static bool IsMainThread =>
            mainThreadId < 0 || Thread.CurrentThread.ManagedThreadId == mainThreadId;

        /// <summary>
        /// TEST ONLY: number of off-main-thread violations seen since the last reset. Lets a test
        /// prove the assertion FIRES before the fix and is SILENT after, without parsing the log.
        /// </summary>
        internal static long ViolationCount;

        /// <summary>TEST ONLY: zero the <see cref="ViolationCount"/>.</summary>
        internal static void ResetViolationCount() =>
            Interlocked.Exchange(ref ViolationCount, 0);

        /// <summary>
        /// Logs an error (Editor + development builds only) when called off the captured main thread.
        /// <paramref name="api"/> names the main-thread-only operation that was reached, so the log
        /// points straight at the leak. No-op in release players and before the main thread is known.
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void AssertMainThread(string api)
        {
            var captured = mainThreadId;
            if (captured < 0) return; // main thread not yet recorded — cannot judge
            if (Thread.CurrentThread.ManagedThreadId == captured) return;

            Interlocked.Increment(ref ViolationCount);
            Debug.LogError(
                $"[UniText] {api} was called on a worker thread (id " +
                $"{Thread.CurrentThread.ManagedThreadId}, main is {captured}). This Unity-only / " +
                "non-thread-safe operation must run on the main-thread prepare step before parallel " +
                "mesh generation is dispatched. See UniTextThreadGuard.");
        }
    }
}
