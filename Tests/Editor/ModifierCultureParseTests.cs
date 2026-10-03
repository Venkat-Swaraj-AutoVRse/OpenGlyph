using System;
using System.Globalization;
using System.Threading;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Item 3(a) regression: the markup modifiers (<see cref="SizeModifier"/>,
    /// <see cref="LetterSpacingModifier"/>, <see cref="LineHeightModifier"/>) parsed numeric params
    /// with culture-SENSITIVE <c>float.TryParse</c>. Under a comma-decimal culture (de-DE) the default
    /// parse reads "1.5" with '.' as a GROUP separator and yields <b>15</b> (and "0.5" -> 5), silently
    /// mis-sizing <c>&lt;size=1.5em&gt;</c> / <c>&lt;cspace=0.5em&gt;</c> / <c>&lt;line-height=1.5&gt;</c>.
    /// The fix routes all three through <see cref="ModifierNumberParse"/>, which pins parsing to
    /// <see cref="NumberStyles.Float"/> + <see cref="CultureInfo.InvariantCulture"/>.
    ///
    /// This tests the shared parse helper directly (deterministic, no render pipeline / no worker-thread
    /// culture propagation). Each case first documents that the DEFAULT culture-sensitive parse misreads
    /// the value under de-DE (so the bug is real on this runtime), then asserts the helper is correct.
    /// Written to FAIL without the fix: a culture-sensitive helper would return 15 for "1.5" under de-DE.
    /// </summary>
    public class ModifierCultureParseTests
    {
        private CultureInfo _prev;

        [SetUp]
        public void SetUp()
        {
            _prev = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // comma decimal separator
        }

        [TearDown]
        public void TearDown() => Thread.CurrentThread.CurrentCulture = _prev;

        [Test]
        public void InvariantHelper_ParsesDecimalPoint_UnderCommaCulture_String()
        {
            // Document the bug on THIS runtime: default (culture-sensitive) parse misreads "1.5".
            bool defOk = float.TryParse("1.5", out float def);
            Assert.IsTrue(defOk && Math.Abs(def - 1.5f) > 0.5f,
                $"sanity: default de-DE float.TryParse(\"1.5\") must NOT yield 1.5 (got {def}) — the bug this fix addresses");

            // The fix: the shared helper parses "1.5" as 1.5 regardless of culture.
            Assert.IsTrue(ModifierNumberParse.TryParseFloat("1.5", out float v), "helper parses");
            Assert.AreEqual(1.5f, v, 1e-4f, "<...=1.5...> must parse to 1.5 under de-DE (not 15)");

            Assert.IsTrue(ModifierNumberParse.TryParseFloat("0.5", out float h));
            Assert.AreEqual(0.5f, h, 1e-4f, "\"0.5\" must parse to 0.5 under de-DE (not 5)");
        }

        [Test]
        public void InvariantHelper_ParsesDecimalPoint_UnderCommaCulture_Span()
        {
            // The modifiers use the SPAN overload for em/percent slicing (e.g. "1.5em" -> "1.5").
            ReadOnlySpan<char> s = "1.5em".AsSpan(0, 3); // "1.5"
            Assert.IsTrue(ModifierNumberParse.TryParseFloat(s, out float v), "span helper parses");
            Assert.AreEqual(1.5f, v, 1e-4f, "span <size=1.5em> slice must parse to 1.5 under de-DE");
        }

        [Test]
        public void InvariantHelper_IsCultureStable()
        {
            // Same input, two very different cultures, identical result — parsing is culture-independent.
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.IsTrue(ModifierNumberParse.TryParseFloat("1.25", out float de));
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                Assert.IsTrue(ModifierNumberParse.TryParseFloat("1.25", out float inv));
                Assert.AreEqual(inv, de, 1e-6f, "the helper yields the same value in every culture");
                Assert.AreEqual(1.25f, inv, 1e-4f);
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }
    }
}
