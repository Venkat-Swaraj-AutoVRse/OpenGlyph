using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenGlyph.DictGen
{
    /// <summary>
    /// Offline generator: reads pinned ICU dictionary text files and emits the
    /// compact OGSD trie binaries consumed by OpenGlyph's DictionarySegmenter.
    ///
    /// Usage:
    ///   openglyph-dictgen &lt;icuDir&gt; &lt;outDir&gt;
    ///
    ///   icuDir  directory holding thaidict.txt, laodict.txt, khmerdict.txt, burmesedict.txt
    ///   outDir  directory to write ThaiDict.bytes, LaoDict.bytes, KhmerDict.bytes, MyanmarDict.bytes
    ///           (the package ships these in Dictionaries/, OUTSIDE any Resources folder, so they
    ///           are only included in a build when referenced from UniTextSettings)
    ///
    /// Source data: unicode-org/icu, tag release-74-2,
    ///   icu4c/source/data/brkitr/dictionaries/*.txt  (Unicode License V3).
    /// </summary>
    internal static class Program
    {
        // UnicodeScript enum values must match LightSide.UnicodeScript.
        // (Informational only — stored in the header for sanity checks.)
        private const int ScriptThai = 21;
        private const int ScriptLao = 22;
        private const int ScriptMyanmar = 24;
        private const int ScriptKhmer = 32;

        private readonly struct DictSpec
        {
            public readonly string InputFile;
            public readonly string OutputFile;
            public readonly int ScriptId;
            public readonly int RangeStart;
            public readonly int RangeEnd;

            public DictSpec(string inputFile, string outputFile, int scriptId, int rangeStart, int rangeEnd)
            {
                InputFile = inputFile;
                OutputFile = outputFile;
                ScriptId = scriptId;
                RangeStart = rangeStart;
                RangeEnd = rangeEnd;
            }
        }

        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: openglyph-dictgen <icuDir> <outDir>");
                return 2;
            }

            var icuDir = args[0];
            var outDir = args[1];
            Directory.CreateDirectory(outDir);

            // Codepoint ranges are used only to reject stray non-script codepoints
            // (a defensive filter — ICU files are already single-script). Combining
            // marks that belong to each script fall inside these ranges.
            var specs = new[]
            {
                new DictSpec("thaidict.txt",     "ThaiDict.bytes",    ScriptThai,    0x0E00, 0x0E7F),
                new DictSpec("laodict.txt",      "LaoDict.bytes",     ScriptLao,     0x0E80, 0x0EFF),
                new DictSpec("khmerdict.txt",    "KhmerDict.bytes",   ScriptKhmer,   0x1780, 0x17FF),
                new DictSpec("burmesedict.txt",  "MyanmarDict.bytes", ScriptMyanmar, 0x1000, 0x109F),
            };

            var ok = true;
            foreach (var spec in specs)
                ok &= BuildOne(icuDir, outDir, spec);

            return ok ? 0 : 1;
        }

        private static bool BuildOne(string icuDir, string outDir, DictSpec spec)
        {
            var inPath = Path.Combine(icuDir, spec.InputFile);
            if (!File.Exists(inPath))
            {
                Console.Error.WriteLine($"MISSING: {inPath}");
                return false;
            }

            var builder = new TrieBuilder();
            var buffer = new List<int>(64);
            int accepted = 0, skipped = 0, longest = 0;

            foreach (var raw in File.ReadLines(inPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                // ICU dictionary files are one word per line. Some entries can carry
                // a trailing tab-separated numeric weight; keep only the first field.
                var tab = line.IndexOf('\t');
                if (tab >= 0)
                    line = line.Substring(0, tab).Trim();
                if (line.Length == 0)
                    continue;

                buffer.Clear();
                var inRange = true;
                for (int i = 0; i < line.Length;)
                {
                    int cp = char.ConvertToUtf32(line, i);
                    i += char.IsSurrogatePair(line, i) ? 2 : 1;
                    buffer.Add(cp);
                    if (cp < spec.RangeStart || cp > spec.RangeEnd)
                        inRange = false;
                }

                if (!inRange)
                {
                    skipped++;
                    continue;
                }

                builder.Insert(buffer.ToArray());
                accepted++;
                if (buffer.Count > longest) longest = buffer.Count;
            }

            var bytes = builder.Serialize(spec.ScriptId, spec.RangeStart);
            var outPath = Path.Combine(outDir, spec.OutputFile);
            File.WriteAllBytes(outPath, bytes);

            Console.WriteLine(
                $"{spec.OutputFile}: words={builder.WordCount} accepted={accepted} skipped={skipped} " +
                $"longestWord={longest} size={bytes.Length} bytes ({(bytes.Length / 1024.0).ToString("F1", CultureInfo.InvariantCulture)} KiB)");
            return true;
        }
    }
}
