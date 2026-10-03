using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression for B15: <see cref="UniTextGradients.EnsureLookup"/> used to publish an EMPTY
    /// dictionary and then fill it. Concurrent <c>TryGetGradient</c> callers (GradientModifier.OnApply
    /// on parallel first-pass workers) saw the already-published, still-empty (or half-filled)
    /// dictionary and got "not found". The fix builds the dictionary fully and publishes it with
    /// <c>Interlocked.CompareExchange</c>.
    ///
    /// Each iteration nulls the private <c>lookup</c> field, releases N threads from a Barrier so they
    /// all hit the cold lookup at once, and each looks up the gradient added LAST (filled last by the
    /// unfixed code). Any miss or exception is a failure.
    /// </summary>
    [TestFixture]
    internal sealed class GradientLookupThreadSafetyTests
    {
        [Test]
        public void TryGetGradient_ConcurrentColdLookup_NeverMisses()
        {
            var field = typeof(UniTextGradients).GetField("lookup", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "UniTextGradients.lookup field not found (renamed?)");

            var gradients = ScriptableObject.CreateInstance<UniTextGradients>();
            try
            {
                const int gradientCount = 200;
                for (int i = 0; i < gradientCount; i++)
                    gradients.Add("grad" + i, new Gradient());
                string lastName = "grad" + (gradientCount - 1);

                const int iterations = 300, threads = 8;
                int misses = 0, errors = 0, totalCalls = 0;
                var firstError = new ConcurrentBag<string>();

                for (int it = 0; it < iterations; it++)
                {
                    field.SetValue(gradients, null); // cold lookup
                    using var barrier = new Barrier(threads);
                    var workers = new Thread[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        workers[t] = new Thread(() =>
                        {
                            try
                            {
                                barrier.SignalAndWait();
                                bool found = gradients.TryGetGradient(lastName, out var g);
                                Interlocked.Increment(ref totalCalls);
                                if (!found || g == null) Interlocked.Increment(ref misses);
                            }
                            catch (Exception e) // a concurrent Dictionary read during a write may throw
                            {
                                Interlocked.Increment(ref errors);
                                firstError.Add(e.GetType().Name);
                            }
                        }) { IsBackground = true };
                        workers[t].Start();
                    }
                    foreach (var w in workers) Assert.IsTrue(w.Join(10000), "worker timed out");
                }

                TestContext.WriteLine($"calls={totalCalls} misses={misses} errors={errors}");
                Assert.AreEqual(0, misses + errors,
                    $"TryGetGradient on a cold lookup returned 'not found'/threw under concurrency " +
                    $"(misses={misses}, errors={errors} of {iterations * threads}) - EnsureLookup published " +
                    "the dictionary before filling it (B15).");
            }
            finally { UnityEngine.Object.DestroyImmediate(gradients); }
        }
    }
}
