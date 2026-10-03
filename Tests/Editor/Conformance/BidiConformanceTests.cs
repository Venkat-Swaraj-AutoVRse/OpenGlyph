using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace LightSide.Tests.Conformance
{
    /// <summary>
    /// UAX #9 conformance: BidiTest.txt (class-level, one case per input x paragraph-direction bit) and
    /// BidiCharacterTest.txt (code-point-level incl. paired brackets, one case per data line).
    /// Levels marked 'x' (removed by X9) are not compared; the visual order is computed with the engine's own
    /// L2 implementation (<see cref="BidiEngine.ReorderLine"/>) over the retained characters' engine levels.
    /// No cases are excluded.
    /// </summary>
    [TestFixture, Category("Conformance")]
    internal sealed class BidiConformanceTests
    {
        // Representative code point per Bidi_Class. None of these is a paired bracket / mirrored pair member.
        private static int RepresentativeCodePoint(string cls)
        {
            switch (cls)
            {
                case "L": return 0x0061;
                case "R": return 0x05D0;
                case "AL": return 0x0627;
                case "EN": return 0x0030;
                case "ES": return 0x002B;
                case "ET": return 0x0024;
                case "AN": return 0x0660;
                case "CS": return 0x002C;
                case "NSM": return 0x0300;
                case "BN": return 0x00AD;
                case "B": return 0x2029;
                case "S": return 0x0009;
                case "WS": return 0x0020;
                case "ON": return 0x0021;
                case "LRE": return 0x202A;
                case "RLE": return 0x202B;
                case "PDF": return 0x202C;
                case "LRO": return 0x202D;
                case "RLO": return 0x202E;
                case "LRI": return 0x2066;
                case "RLI": return 0x2067;
                case "FSI": return 0x2068;
                case "PDI": return 0x2069;
                default: throw new InvalidDataException("Unknown bidi class token: " + cls);
            }
        }

        private static string Join(int[] a, int n)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                if (a[i] < 0) sb.Append('x'); else sb.Append(a[i]);
            }
            return sb.ToString();
        }

        private static int[] ParseIntsWithX(string s)
        {
            var toks = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var r = new int[toks.Length];
            for (var i = 0; i < toks.Length; i++)
                r[i] = toks[i] == "x" ? -1 : int.Parse(toks[i], CultureInfo.InvariantCulture);
            return r;
        }

        /// <summary>
        /// Compares engine output to expectations. expectedLevels uses -1 for 'x'. expectedOrder lists logical
        /// indices (x skipped). Returns null on success or a failure class.
        /// </summary>
        private static string Compare(BidiEngine engine, int[] cps, int n, int direction,
            int[] expectedLevels, int[] expectedOrder, int expectedParaLevel,
            byte[] scratchLevels, int[] scratchMap, int[] scratchIdx,
            out string actualDesc)
        {
            var res = engine.Process(new ReadOnlySpan<int>(cps, 0, n), direction);
            actualDesc = null;

            string cls = null;
            if (expectedParaLevel >= 0)
            {
                var pl = res.paragraphCount > 0 ? res.paragraphs[0].baseLevel : -1;
                if (pl != expectedParaLevel) cls = "paragraph-level-mismatch";
            }

            var m = 0;
            for (var i = 0; i < n; i++)
            {
                if (expectedLevels[i] < 0) continue;
                var lv = res.levels[i];
                if (lv != expectedLevels[i] && cls == null) cls = "level-mismatch";
                scratchLevels[m] = lv;
                scratchIdx[m] = i;
                m++;
            }

            var orderBad = false;
            if (m > 0)
            {
                BidiEngine.ReorderLine(scratchLevels, 0, m - 1, scratchMap);
                if (m != expectedOrder.Length) orderBad = true;
                else
                    for (var j = 0; j < m; j++)
                        if (scratchIdx[scratchMap[j]] != expectedOrder[j]) { orderBad = true; break; }
            }
            else if (expectedOrder.Length != 0) orderBad = true;

            if (orderBad && cls == null) cls = "order-mismatch";
            if (cls == null) return null;

            // Failure path only: allocate descriptions.
            var lvls = new int[n];
            for (var i = 0; i < n; i++) lvls[i] = expectedLevels[i] < 0 ? -1 : res.levels[i];
            var ord = new int[m];
            for (var j = 0; j < m; j++) ord[j] = scratchIdx[scratchMap[j]];
            var pl2 = res.paragraphCount > 0 ? res.paragraphs[0].baseLevel : -1;
            actualDesc = $"paraLevel={pl2} levels={Join(lvls, n)} order={Join(ord, m)}";
            return cls;
        }

        [Test]
        public void BidiTest_Unicode17()
        {
            var path = ConformanceSupport.RequireData("BidiTest.txt");
            ConformanceSupport.EnsureUnicode();
            var engine = new BidiEngine(UnicodeData.Provider);
            var report = new ConformanceReport("UAX9-bidi", "Unicode Bidirectional Algorithm (BidiTest)", "BidiTest.txt");

            var cps = new int[512];
            var scratchLevels = new byte[512];
            var scratchMap = new int[512];
            var scratchIdx = new int[512];
            var cpCache = new Dictionary<string, int>();
            int[] expLevels = Array.Empty<int>();
            int[] expOrder = Array.Empty<int>();
            var lineNo = 0;

            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                lineNo++;
                if (raw.Length == 0 || raw[0] == '#') continue;
                if (raw[0] == '@')
                {
                    if (raw.StartsWith("@Levels:", StringComparison.Ordinal)) expLevels = ParseIntsWithX(raw.Substring(8));
                    else if (raw.StartsWith("@Reorder:", StringComparison.Ordinal)) expOrder = ParseIntsWithX(raw.Substring(9));
                    continue;
                }
                var semi = raw.IndexOf(';');
                if (semi < 0) continue;
                var input = raw.Substring(0, semi).Trim();
                var bitset = int.Parse(raw.Substring(semi + 1).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                var toks = input.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var n = toks.Length;
                if (n != expLevels.Length)
                    throw new InvalidDataException($"BidiTest line {lineNo}: {n} input tokens vs {expLevels.Length} expected levels");
                for (var i = 0; i < n; i++)
                {
                    if (!cpCache.TryGetValue(toks[i], out var cp)) cpCache[toks[i]] = cp = RepresentativeCodePoint(toks[i]);
                    cps[i] = cp;
                }

                for (var bit = 1; bit <= 4; bit <<= 1)
                {
                    if ((bitset & bit) == 0) continue;
                    var dir = bit == 1 ? 2 : bit == 2 ? 0 : 1;
                    var cls = Compare(engine, cps, n, dir, expLevels, expOrder, -1,
                        scratchLevels, scratchMap, scratchIdx, out var actual);
                    if (cls == null) { report.Pass(); continue; }
                    var dirName = bit == 1 ? "auto-LTR" : bit == 2 ? "LTR" : "RTL";
                    report.Fail(cls, lineNo, input + " ; " + dirName,
                        $"levels={Join(expLevels, n)} order={Join(expOrder, expOrder.Length)}", actual);
                }
            }
            report.WriteAndAssert();
        }

        [Test]
        public void BidiCharacterTest_Unicode17()
        {
            var path = ConformanceSupport.RequireData("BidiCharacterTest.txt");
            ConformanceSupport.EnsureUnicode();
            var engine = new BidiEngine(UnicodeData.Provider);
            var report = new ConformanceReport("UAX9-bidi-character", "Unicode Bidirectional Algorithm (BidiCharacterTest)", "BidiCharacterTest.txt");

            var cps = new int[1024];
            var scratchLevels = new byte[1024];
            var scratchMap = new int[1024];
            var scratchIdx = new int[1024];
            var lineNo = 0;

            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                lineNo++;
                if (raw.Length == 0 || raw[0] == '#') continue;
                var f = raw.Split(';');
                if (f.Length < 5) continue;

                var cpToks = f[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var n = cpToks.Length;
                if (n > cps.Length)
                {
                    cps = new int[n * 2]; scratchLevels = new byte[n * 2]; scratchMap = new int[n * 2]; scratchIdx = new int[n * 2];
                }
                for (var i = 0; i < n; i++) cps[i] = ConformanceSupport.ParseHex(cpToks[i]);

                var dir = int.Parse(f[1].Trim(), CultureInfo.InvariantCulture);
                var paraLevel = int.Parse(f[2].Trim(), CultureInfo.InvariantCulture);
                var expLevels = ParseIntsWithX(f[3]);
                var expOrder = ParseIntsWithX(f[4]);
                if (expLevels.Length != n)
                    throw new InvalidDataException($"BidiCharacterTest line {lineNo}: {n} code points vs {expLevels.Length} levels");

                var cls = Compare(engine, cps, n, dir, expLevels, expOrder, paraLevel,
                    scratchLevels, scratchMap, scratchIdx, out var actual);
                if (cls == null) { report.Pass(); continue; }
                report.Fail(cls, lineNo, ConformanceSupport.Cps(new ReadOnlySpan<int>(cps, 0, n)) + " ; dir=" + dir,
                    $"paraLevel={paraLevel} levels={Join(expLevels, n)} order={Join(expOrder, expOrder.Length)}", actual);
            }
            report.WriteAndAssert();
        }
    }
}
