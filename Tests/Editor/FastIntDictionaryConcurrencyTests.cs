using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Stresses the <see cref="FastIntDictionary{T}"/> single-writer / many-reader contract that the
    /// threading PR hardened with memory barriers: AddOrUpdate publishes a new slot by writing
    /// key/value FIRST and <c>hasValue</c> LAST via <see cref="Volatile.Write(ref bool,bool)"/>;
    /// readers snapshot the array and read <c>hasValue</c> via <see cref="Volatile.Read(ref bool)"/>;
    /// Grow publishes the resized array via Volatile.Write after it is fully populated.
    ///
    /// <para>The values are a self-describing invariant: key <c>k</c> always maps to value
    /// <c>k * 2654435761</c> (Knuth's multiplicative hash, chosen so the value shares no bits with
    /// the key). A reader that observed <c>hasValue==true</c> but a STALE key/value — the exact torn
    /// read the barriers prevent, visible on weak memory models like ARM — would read a value that
    /// does not match its key and the test fails. Run on all cores for many iterations.</para>
    ///
    /// <para>These are pure-managed tests (no Unity main-thread APIs), so they run real OS threads
    /// safely in edit mode.</para>
    /// </summary>
    [TestFixture]
    internal sealed class FastIntDictionaryConcurrencyTests
    {
        private const uint Knuth = 2654435761u;
        private static int ExpectedValue(int key) => unchecked((int)((uint)key * Knuth));

        [Test]
        public void SingleWriter_ManyReaders_NeverSeeTornSlot()
        {
            // Small initial capacity so Grow fires repeatedly DURING concurrent reads (exercises the
            // array-swap publication, not just slot publication).
            var dict = new FastIntDictionary<int>(8);
            const int keyCount = 20000;
            int readerThreads = Math.Max(2, Environment.ProcessorCount - 1);

            var errors = new ConcurrentQueue<string>();
            var writeDone = new ManualResetEventSlim(false);
            var start = new ManualResetEventSlim(false);

            // Readers: repeatedly look up keys the writer is inserting. For any key they DO observe,
            // the value MUST match the key invariant — a torn slot (hasValue seen before key/value)
            // would surface as a mismatch here.
            var readers = new Task[readerThreads];
            for (int r = 0; r < readerThreads; r++)
            {
                readers[r] = Task.Run(() =>
                {
                    start.Wait();
                    long reads = 0;
                    while (!writeDone.IsSet)
                    {
                        for (int k = 0; k < keyCount; k++)
                        {
                            if (dict.TryGetValue(k, out var v))
                            {
                                if (v != ExpectedValue(k))
                                {
                                    errors.Enqueue($"TORN READ: key {k} -> {v}, expected {ExpectedValue(k)}");
                                    return;
                                }
                                reads++;
                            }
                        }
                    }
                    // One final full sweep after writes complete: every key must now be present+correct.
                    for (int k = 0; k < keyCount; k++)
                    {
                        if (!dict.TryGetValue(k, out var v))
                            errors.Enqueue($"MISSING after publish: key {k}");
                        else if (v != ExpectedValue(k))
                            errors.Enqueue($"WRONG after publish: key {k} -> {v}, expected {ExpectedValue(k)}");
                    }
                    _ = reads;
                });
            }

            // Single writer.
            var writer = Task.Run(() =>
            {
                start.Wait();
                for (int k = 0; k < keyCount; k++)
                    dict.AddOrUpdate(k, ExpectedValue(k));
                writeDone.Set();
            });

            start.Set();
            Assert.IsTrue(Task.WaitAll(new[] { writer }, 30000), "writer did not finish in time");
            Assert.IsTrue(Task.WaitAll(readers, 30000), "readers did not finish in time");

            Assert.IsEmpty(errors,
                "FastIntDictionary showed a torn read / wrong value under single-writer+many-reader " +
                "concurrency: " + (errors.TryPeek(out var e) ? e : ""));
            Assert.AreEqual(keyCount, dict.Count, "all keys should be present after the writer finished");
        }

        [Test]
        public void Readers_DuringRepeatedGrow_NeverThrowOrTear()
        {
            // Hammer the specific window the array-swap barrier protects: a reader walking the table
            // while Grow replaces the backing array. Repeat the whole fill many times.
            var errors = new ConcurrentQueue<string>();

            for (int iter = 0; iter < 20; iter++)
            {
                var dict = new FastIntDictionary<int>(4); // tiny ⇒ many grows
                const int keyCount = 4096;
                var writeDone = new ManualResetEventSlim(false);
                var start = new ManualResetEventSlim(false);

                var reader = Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        while (!writeDone.IsSet)
                            for (int k = 0; k < keyCount; k++)
                                if (dict.TryGetValue(k, out var v) && v != ExpectedValue(k))
                                    errors.Enqueue($"iter {iter}: torn key {k} -> {v}");
                    }
                    catch (Exception ex) { errors.Enqueue($"iter {iter}: reader threw {ex.GetType().Name}: {ex.Message}"); }
                });

                var writer = Task.Run(() =>
                {
                    start.Wait();
                    for (int k = 0; k < keyCount; k++) dict.AddOrUpdate(k, ExpectedValue(k));
                    writeDone.Set();
                });

                start.Set();
                Task.WaitAll(new[] { writer, reader }, 30000);
            }

            Assert.IsEmpty(errors,
                "A reader tore a slot or threw while Grow republished the backing array: " +
                (errors.TryPeek(out var e) ? e : ""));
        }
    }
}
