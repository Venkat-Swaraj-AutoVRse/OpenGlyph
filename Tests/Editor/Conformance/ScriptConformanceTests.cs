using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace LightSide.Tests.Conformance
{
    /// <summary>
    /// UAX #24 Script / Script_Extensions property conformance against Scripts.txt and ScriptExtensions.txt.
    /// One case per code point listed in the file. Tests the data provider's property lookups
    /// (<see cref="UnicodeDataProvider.GetScript"/>, <see cref="UnicodeDataProvider.GetScriptExtensions"/>),
    /// not ScriptAnalyzer's Common/Inherited run propagation (that is a layout heuristic, not the UCD property).
    /// Script names are resolved to <see cref="UnicodeScript"/> independently of the data builder: long names
    /// via the enum member name, 4-letter codes via PropertyValueAliases.txt (sc lines). A name with no enum
    /// member is reported as a failure of class "enum-missing-script", never silently skipped.
    /// </summary>
    [TestFixture, Category("Conformance")]
    internal sealed class ScriptConformanceTests
    {
        private static bool TryLong(string longName, out UnicodeScript s) =>
            Enum.TryParse(longName.Replace("_", ""), true, out s) && Enum.IsDefined(typeof(UnicodeScript), s);

        private static Dictionary<string, string> LoadShortToLong()
        {
            var path = ConformanceSupport.RequireData("PropertyValueAliases.txt");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (!line.StartsWith("sc ", StringComparison.Ordinal)) continue;
                var hash = line.IndexOf('#');
                var l = hash >= 0 ? line.Substring(0, hash) : line;
                var f = l.Split(';').Select(x => x.Trim()).ToArray();
                if (f.Length < 3) continue;
                map[f[1]] = f[2];
                for (var i = 3; i < f.Length; i++) if (f[i].Length > 0) map[f[i]] = f[2];
            }
            return map;
        }

        [Test]
        public void ScriptsTxt_Unicode17()
        {
            var path = ConformanceSupport.RequireData("Scripts.txt");
            ConformanceSupport.EnsureUnicode();
            var provider = UnicodeData.Provider;
            var report = new ConformanceReport("UAX24-script", "Unicode Script property (Scripts.txt)", "Scripts.txt");

            var lineNo = 0;
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                lineNo++;
                var hash = raw.IndexOf('#');
                var line = hash >= 0 ? raw.Substring(0, hash) : raw;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var f = line.Split(';');
                if (f.Length < 2) continue;
                ConformanceSupport.ParseRange(f[0].Trim(), out var lo, out var hi);
                var name = f[1].Trim();
                var known = TryLong(name, out var expected);
                for (var cp = lo; cp <= hi; cp++)
                {
                    if (!known)
                    {
                        report.Fail("enum-missing-script", lineNo, cp.ToString("X4"), name, "(no UnicodeScript member)");
                        continue;
                    }
                    var got = provider.GetScript(cp);
                    if (got == expected) report.Pass();
                    else report.Fail("script-mismatch", lineNo, cp.ToString("X4"), name, got.ToString());
                }
            }
            report.WriteAndAssert();
        }

        [Test]
        public void ScriptExtensionsTxt_Unicode17()
        {
            var path = ConformanceSupport.RequireData("ScriptExtensions.txt");
            var shortToLong = LoadShortToLong();
            ConformanceSupport.EnsureUnicode();
            var provider = UnicodeData.Provider;
            var report = new ConformanceReport("UAX24-script-extensions", "Unicode Script_Extensions property (ScriptExtensions.txt)", "ScriptExtensions.txt");

            var lineNo = 0;
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                lineNo++;
                var hash = raw.IndexOf('#');
                var line = hash >= 0 ? raw.Substring(0, hash) : raw;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var f = line.Split(';');
                if (f.Length < 2) continue;
                ConformanceSupport.ParseRange(f[0].Trim(), out var lo, out var hi);
                var codes = f[1].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                var expected = new List<UnicodeScript>();
                string missing = null;
                foreach (var code in codes)
                {
                    if (shortToLong.TryGetValue(code, out var longName) && TryLong(longName, out var s)) expected.Add(s);
                    else missing = code;
                }
                expected.Sort();

                for (var cp = lo; cp <= hi; cp++)
                {
                    if (missing != null)
                    {
                        report.Fail("enum-missing-script", lineNo, cp.ToString("X4"), f[1].Trim(), "(no UnicodeScript member for " + missing + ")");
                        continue;
                    }
                    var got = provider.GetScriptExtensions(cp).OrderBy(x => x).ToArray();
                    if (got.Length == expected.Count && got.SequenceEqual(expected)) { report.Pass(); continue; }
                    report.Fail("extension-set-mismatch", lineNo, cp.ToString("X4"),
                        string.Join(" ", expected), string.Join(" ", got));
                }
            }
            report.WriteAndAssert();
        }
    }
}
