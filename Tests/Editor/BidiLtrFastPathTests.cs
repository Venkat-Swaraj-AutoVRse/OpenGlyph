using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using LightSide.Tests.Conformance;
using Debug = UnityEngine.Debug;

namespace LightSide.Tests
{
    /// <summary>
    /// BiDi LTR fast path: a paragraph with no R/AL/AN and no explicit embedding/override/isolate
    /// controls resolves to all-zero levels, so <see cref="BidiEngine"/> must skip the full UAX #9
    /// resolution for it. Output must be identical to the full resolution (the UAX #9 conformance
    /// suites are the authoritative check; this fixture adds a randomized fast-vs-full equivalence).
    /// The hooks are read by reflection so this file also compiles against a build without them.
    /// </summary>
    public class BidiLtrFastPathTests
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        private static PropertyInfo FastPathCounter =>
            typeof(BidiEngine).GetProperty("LtrFastPathCount", Static);

        private static FieldInfo DisableFlag =>
            typeof(BidiEngine).GetField("DisableLtrFastPath", Static);

        private static int ReadCounter() => (int)FastPathCounter.GetValue(null);

        [SetUp]
        public void SetUp() => ConformanceSupport.EnsureUnicode();

        [TearDown]
        public void TearDown() => DisableFlag?.SetValue(null, false);

        private static int[] Cps(string s)
        {
            var list = new System.Collections.Generic.List<int>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                int cp = char.ConvertToUtf32(s, i);
                if (char.IsHighSurrogate(s[i])) i++;
                list.Add(cp);
            }
            return list.ToArray();
        }

        [Test]
        public void LtrOnlyText_SkipsFullResolution()
        {
            Assert.IsNotNull(FastPathCounter,
                "BidiEngine has no LTR fast path: all-LTR paragraphs run the full UAX #9 resolution");

            var engine = new BidiEngine(UnicodeData.Provider);
            var ltr = Cps("Hello, world 123 +4.5% $9\tTab \u00AD\u0301 end.\u2029Second paragraph.");

            int before = ReadCounter();
            var res = engine.Process(ltr, BidiParagraphDirection.Auto);
            Assert.AreEqual(before + 1, ReadCounter(), "auto-direction LTR text takes the fast path");
            for (int i = 0; i < ltr.Length; i++) Assert.AreEqual(0, res.levels[i], $"level at {i}");
            Assert.AreEqual(2, res.paragraphCount);

            before = ReadCounter();
            engine.Process(ltr, BidiParagraphDirection.LeftToRight);
            Assert.AreEqual(before + 1, ReadCounter(), "forced-LTR text takes the fast path");

            // Must NOT fast-path: forced RTL, strong RTL, Arabic numbers, explicit controls.
            before = ReadCounter();
            engine.Process(ltr, BidiParagraphDirection.RightToLeft);
            engine.Process(Cps("abc \u05D0\u05D1 def"), BidiParagraphDirection.Auto);
            engine.Process(Cps("abc \u0661\u0662 def"), BidiParagraphDirection.Auto);
            engine.Process(Cps("abc \u202Bdef\u202C ghi"), BidiParagraphDirection.Auto);
            engine.Process(Cps("abc \u2067def\u2069 ghi"), BidiParagraphDirection.Auto);
            Assert.AreEqual(before, ReadCounter(), "RTL / AN / explicit-control text uses the full resolution");
        }

        [Test]
        public void FastPath_MatchesFullResolution_Randomized()
        {
            var flag = DisableFlag;
            if (flag == null) Assert.Ignore("No LTR fast path hook in this build; nothing to compare.");

            // One code point per bidi class that the fast path accepts.
            int[] pool =
            {
                0x0061, 0x00E9, 0x4E00, // L
                0x0030, 0x0039,         // EN
                0x002B, 0x002D,         // ES
                0x0024, 0x0025,         // ET
                0x002C, 0x002E, 0x003A, // CS
                0x0300, 0x0301,         // NSM
                0x00AD, 0x200B,         // BN
                0x2029, 0x000A,         // B
                0x0009, 0x001F,         // S
                0x0020, 0x3000,         // WS
                0x0021, 0x0028, 0x0029, 0x005B, 0x005D, // ON incl. paired brackets
            };

            var engine = new BidiEngine(UnicodeData.Provider);
            var rng = new System.Random(12345);
            int cases = 0;
            for (int iter = 0; iter < 3000; iter++)
            {
                int n = 1 + rng.Next(40);
                var cps = new int[n];
                for (int i = 0; i < n; i++) cps[i] = pool[rng.Next(pool.Length)];

                foreach (var dir in new[] { BidiParagraphDirection.Auto, BidiParagraphDirection.LeftToRight })
                {
                    flag.SetValue(null, false);
                    var fast = engine.Process(cps, dir);
                    var fastLevels = new byte[n];
                    Array.Copy(fast.levels, fastLevels, n);
                    var fastParas = new BidiParagraph[fast.paragraphCount];
                    Array.Copy(fast.paragraphs, fastParas, fast.paragraphCount);

                    flag.SetValue(null, true);
                    var full = engine.Process(cps, dir);

                    Assert.AreEqual(full.paragraphCount, fastParas.Length, "paragraph count");
                    for (int p = 0; p < fastParas.Length; p++)
                    {
                        Assert.AreEqual(full.paragraphs[p].startIndex, fastParas[p].startIndex);
                        Assert.AreEqual(full.paragraphs[p].endIndex, fastParas[p].endIndex);
                        Assert.AreEqual(full.paragraphs[p].baseLevel, fastParas[p].baseLevel);
                    }
                    for (int i = 0; i < n; i++)
                        Assert.AreEqual(full.levels[i], fastLevels[i],
                            $"level mismatch at {i} for [{string.Join(" ", Array.ConvertAll(cps, c => c.ToString("X4")))}] dir={dir}");
                    cases++;
                }
            }
            flag.SetValue(null, false);
            Assert.AreEqual(6000, cases);
        }

        [Test]
        public void LtrParagraph_Timing()
        {
            var sb = new StringBuilder();
            while (sb.Length < 20000) sb.Append("The quick brown fox jumps over the lazy dog, 1234 times. ");
            var cps = Cps(sb.ToString());
            var engine = new BidiEngine(UnicodeData.Provider);

            double Time()
            {
                for (int i = 0; i < 5; i++) engine.Process(cps, BidiParagraphDirection.Auto);
                double best = double.MaxValue;
                for (int s = 0; s < 5; s++)
                {
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 20; i++) engine.Process(cps, BidiParagraphDirection.Auto);
                    sw.Stop();
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds / 20);
                }
                return best;
            }

            double defaultMs = Time();
            string fullDesc = "n/a";
            var flag = DisableFlag;
            if (flag != null)
            {
                flag.SetValue(null, true);
                fullDesc = Time().ToString("F3");
                flag.SetValue(null, false);
            }
            Debug.Log($"[PERF2][BIDI] {cps.Length} cps LTR: default={defaultMs:F3} ms/call, forcedFull={fullDesc} ms/call");
            Assert.Pass();
        }
    }
}
