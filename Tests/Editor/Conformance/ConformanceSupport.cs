using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;

namespace LightSide.Tests.Conformance
{
    /// <summary>One recorded failing case: the input, what the standard expects, what the engine produced.</summary>
    internal readonly struct ConformanceFailure
    {
        public readonly int line;
        public readonly string input;
        public readonly string expected;
        public readonly string actual;
        public ConformanceFailure(int line, string input, string expected, string actual)
        {
            this.line = line; this.input = input; this.expected = expected; this.actual = actual;
        }
    }

    /// <summary>
    /// Collects totals, failures (first <see cref="MaxRecorded"/> kept verbatim), explicit exclusions and
    /// per-class failure counters, then writes Conformance~/results/&lt;standard&gt;.json and echoes it via TestContext.
    /// </summary>
    internal sealed class ConformanceReport
    {
        public const int MaxRecorded = 20;

        public readonly string standard;
        public readonly string name;
        public readonly string dataFile;
        public long total;
        public long passed;
        public long failed;
        public long excluded;
        public readonly List<ConformanceFailure> firstFailures = new List<ConformanceFailure>();
        public readonly SortedDictionary<string, long> failureClasses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, long> exclusions = new SortedDictionary<string, long>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, string> exclusionReasons = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public ConformanceReport(string standard, string name, string dataFile)
        {
            this.standard = standard; this.name = name; this.dataFile = dataFile;
        }

        public void Pass() { total++; passed++; }

        public void Fail(string failureClass, int line, string input, string expected, string actual)
        {
            total++; failed++;
            failureClasses.TryGetValue(failureClass, out var c);
            failureClasses[failureClass] = c + 1;
            if (firstFailures.Count < MaxRecorded)
                firstFailures.Add(new ConformanceFailure(line, input, expected, actual));
        }

        /// <summary>A case deliberately not run. Counted in <see cref="excluded"/>, NOT in total/passed/failed.</summary>
        public void Exclude(string key, string reason)
        {
            excluded++;
            exclusions.TryGetValue(key, out var c);
            exclusions[key] = c + 1;
            exclusionReasons[key] = reason;
        }

        private static void Esc(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        public string ToJson()
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\n");
            sb.Append("  \"standard\": "); Esc(sb, standard); sb.Append(",\n");
            sb.Append("  \"name\": "); Esc(sb, name); sb.Append(",\n");
            sb.Append("  \"unicodeVersion\": \"17.0.0\",\n");
            sb.Append("  \"dataFile\": "); Esc(sb, dataFile); sb.Append(",\n");
            sb.Append("  \"total\": ").Append(total).Append(",\n");
            sb.Append("  \"passed\": ").Append(passed).Append(",\n");
            sb.Append("  \"failed\": ").Append(failed).Append(",\n");
            sb.Append("  \"excluded\": ").Append(excluded).Append(",\n");
            sb.Append("  \"exclusions\": [");
            var first = true;
            foreach (var kv in exclusions)
            {
                sb.Append(first ? "\n" : ",\n"); first = false;
                sb.Append("    { \"key\": "); Esc(sb, kv.Key);
                sb.Append(", \"count\": ").Append(kv.Value).Append(", \"reason\": "); Esc(sb, exclusionReasons[kv.Key]); sb.Append(" }");
            }
            sb.Append(first ? "],\n" : "\n  ],\n");
            sb.Append("  \"failureClasses\": {");
            first = true;
            foreach (var kv in failureClasses)
            {
                sb.Append(first ? "\n" : ",\n"); first = false;
                sb.Append("    "); Esc(sb, kv.Key); sb.Append(": ").Append(kv.Value);
            }
            sb.Append(first ? "},\n" : "\n  },\n");
            sb.Append("  \"firstFailures\": [");
            first = true;
            foreach (var f in firstFailures)
            {
                sb.Append(first ? "\n" : ",\n"); first = false;
                sb.Append("    { \"line\": ").Append(f.line).Append(", \"input\": "); Esc(sb, f.input);
                sb.Append(", \"expected\": "); Esc(sb, f.expected);
                sb.Append(", \"actual\": "); Esc(sb, f.actual); sb.Append(" }");
            }
            sb.Append(first ? "]\n" : "\n  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>Writes the JSON summary, prints it, and asserts zero failures.</summary>
        public void WriteAndAssert()
        {
            var json = ToJson();
            var dir = Path.Combine(ConformanceSupport.PackageRoot, "Conformance~", "results");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, standard + ".json"), json, new UTF8Encoding(false));
            TestContext.WriteLine($"[Conformance] {standard} {name}: total={total} passed={passed} failed={failed} excluded={excluded}");
            TestContext.WriteLine(json);
            Assert.AreEqual(0, failed,
                $"{standard} ({name}): {failed} of {total} conformance cases failed. See Conformance~/results/{standard}.json");
        }
    }

    internal static class ConformanceSupport
    {
        private static string packageRoot;

        /// <summary>Package root (folder containing package.json).</summary>
        public static string PackageRoot
        {
            get
            {
                if (packageRoot != null) return packageRoot;
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnicodeData).Assembly);
                if (info != null && Directory.Exists(Path.Combine(info.resolvedPath, "Conformance~")))
                    return packageRoot = info.resolvedPath;
                return packageRoot = FromCallerPath();
            }
        }

        private static string FromCallerPath([CallerFilePath] string here = "")
        {
            // <root>/Tests/Editor/Conformance/ConformanceSupport.cs
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", "..", ".."));
        }

        /// <summary>Returns the UCD file path, or calls Assert.Ignore when it is missing.</summary>
        public static string RequireData(string fileName)
        {
            var p = Path.Combine(PackageRoot, "Conformance~", "ucd-17.0.0", fileName);
            if (!File.Exists(p))
                Assert.Ignore($"Conformance data file missing: {p}. See Conformance~/README.md to download it.");
            return p;
        }

        public static void EnsureUnicode()
        {
            UnicodeData.EnsureInitialized();
            if (!UnicodeData.IsInitialized)
                Assert.Ignore("UnicodeData could not be initialized (Resources/UnicodeData.bytes missing).");
        }

        public static string Cps(ReadOnlySpan<int> cps)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < cps.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(cps[i].ToString("X4", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static int ParseHex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        /// <summary>Parses "XXXX" or "XXXX..YYYY".</summary>
        public static void ParseRange(string s, out int lo, out int hi)
        {
            var d = s.IndexOf("..", StringComparison.Ordinal);
            if (d < 0) { lo = hi = ParseHex(s.Trim()); }
            else { lo = ParseHex(s.Substring(0, d).Trim()); hi = ParseHex(s.Substring(d + 2).Trim()); }
        }

        /// <summary>
        /// Parses a UCD break-test line ("÷ 0020 × 0308 ÷ ...") into code points and per-boundary break flags.
        /// breaks[i] = true when a break is expected before code point i (breaks[n] = after the last).
        /// Returns false if the line is a comment/blank.
        /// </summary>
        public static bool ParseBreakLine(string line, List<int> cps, List<bool> breaks)
        {
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);
            line = line.Trim();
            if (line.Length == 0) return false;
            cps.Clear(); breaks.Clear();
            foreach (var tok in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok == "÷") breaks.Add(true);
                else if (tok == "×") breaks.Add(false);
                else cps.Add(ParseHex(tok));
            }
            return true;
        }
    }
}
