using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Checks Plugins/WebGL/libunitext_native.a, the static wasm archive linked into every Web player
    /// (WebGL 2 and WebGPU), against the two ways it once broke Unity's player link:
    /// <list type="bullet">
    /// <item>it defined plain FreeType / HarfBuzz / libpng / zlib symbols, which Unity's own
    /// TextRenderingModule, libpng.a and zlib.a also define ("wasm-ld: error: duplicate symbol: FT_Get_Advance");</item>
    /// <item>it was compiled by a newer emscripten than Unity's (3.1.39), so it referenced
    /// __wasm_setjmp / __wasm_setjmp_test, which Unity's runtime lacks ("undefined symbol: __wasm_setjmp").</item>
    /// </list>
    /// The archive is parsed directly (ar container + wasm "linking" symbol table), so no toolchain is needed.
    /// </summary>
    public class WebNativeArchiveTests
    {
        private const string ArchivePath = "Packages/com.openglyph.text/Plugins/WebGL/libunitext_native.a";

        private sealed class WasmObject
        {
            public string Member;
            public readonly List<string> DefinedGlobals = new();
            public readonly List<string> ImportFields = new();
            public string Producer = "";
        }

        private static List<WasmObject> cached;

        private static List<WasmObject> Objects()
        {
            if (cached != null) return cached;
            var path = Path.GetFullPath(ArchivePath);
            Assert.IsTrue(File.Exists(path), "missing " + path);
            cached = ParseArchive(File.ReadAllBytes(path));
            return cached;
        }

        [Test]
        public void ArchiveMembers_AreWasmObjects()
        {
            var objs = Objects();
            Assert.Greater(objs.Count, 0, "no wasm members in the archive");
            Assert.Greater(objs.Sum(o => o.DefinedGlobals.Count), 1000,
                "the archive should be self-contained (FreeType + HarfBuzz + libpng + zlib bundled)");
        }

        [Test]
        public void EveryGlobalSymbol_IsPublicUtOrPrefixed()
        {
            var bad = Objects().SelectMany(o => o.DefinedGlobals.Select(s => (o.Member, s)))
                .Where(p => !p.s.StartsWith("ut_", StringComparison.Ordinal) && !p.s.StartsWith("__ut_", StringComparison.Ordinal))
                .ToList();
            Assert.IsEmpty(bad.Take(30).Select(p => p.s + " (" + p.Member + ")"),
                $"{bad.Count} global symbols are not ut_* / __ut_*; they clash with the FreeType/HarfBuzz/libpng/zlib " +
                "copies inside Unity's Web player at link time (duplicate symbol). Rebuild with Tools~/wasm_prefix_symbols.py.");
        }

        [Test]
        public void NoImportOfSymbolsMissingFromUnitysEmscripten()
        {
            var bad = Objects().SelectMany(o => o.ImportFields.Where(f => f.StartsWith("__wasm_setjmp", StringComparison.Ordinal))
                    .Select(f => f + " (" + o.Member + ")"))
                .Distinct().ToList();
            Assert.IsEmpty(bad, "the archive was built by an emscripten newer than Unity's 3.1.39; " +
                                "the player link fails with 'undefined symbol: __wasm_setjmp'");
        }

        [Test]
        public void ProducedByUnitysClangMajor()
        {
            // Unity 2023.2 / 6000.x link with emscripten 3.1.39 (LLVM 17).
            var producers = Objects().Select(o => o.Producer).Where(p => p.Length > 0).Distinct().ToList();
            Assert.IsNotEmpty(producers, "no producers section found");
            foreach (var p in producers)
                StringAssert.StartsWith("17.", p, "objects must come from Unity's emscripten (clang 17): " + string.Join(" | ", producers));
        }

        [Test]
        public void EveryNativeEntryPoint_IsDefinedInArchive()
        {
            var defined = new HashSet<string>(Objects().SelectMany(o => o.DefinedGlobals));
            var asm = typeof(UniTextFont).Assembly;
            var missing = new List<string>();
            int count = 0;
            foreach (var type in asm.GetTypes())
                foreach (var m in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var di = m.GetCustomAttribute<DllImportAttribute>();
                    if (di == null) continue;
                    var entry = string.IsNullOrEmpty(di.EntryPoint) ? m.Name : di.EntryPoint;
                    if (!entry.StartsWith("ut_", StringComparison.Ordinal)) continue; // e.g. WebGL emoji jslib
                    if (di.Value != "unitext_native" && di.Value != "__Internal") continue;   // editor-only library
                    count++;
                    if (!defined.Contains(entry)) missing.Add(type.Name + "." + entry);
                }
            Assert.Greater(count, 50, "expected the FT/HB/BL P/Invoke surface");
            Assert.IsEmpty(missing, "P/Invoke entry points not defined by the Web archive");
        }

        // ------------------------------------------------------------------ parsing

        private static List<WasmObject> ParseArchive(byte[] a)
        {
            const string magic = "!<arch>\n";
            Assert.AreEqual(magic, Encoding.ASCII.GetString(a, 0, 8), "not an ar archive");
            var result = new List<WasmObject>();
            string longNames = null;
            int p = 8;
            while (p + 60 <= a.Length)
            {
                var name = Encoding.ASCII.GetString(a, p, 16).TrimEnd();
                var size = int.Parse(Encoding.ASCII.GetString(a, p + 48, 10).Trim());
                int data = p + 60;
                if (name == "//")
                    longNames = Encoding.ASCII.GetString(a, data, size);
                else if (name != "/" && name != "/SYM64/" && size >= 8 &&
                         a[data] == 0 && a[data + 1] == (byte)'a' && a[data + 2] == (byte)'s' && a[data + 3] == (byte)'m')
                {
                    if (name.StartsWith("/") && longNames != null && int.TryParse(name.Substring(1), out var off))
                    {
                        var end = longNames.IndexOf('\n', off);
                        name = longNames.Substring(off, (end < 0 ? longNames.Length : end) - off);
                    }
                    var obj = ParseWasm(a, data, size);
                    obj.Member = name.TrimEnd('/');
                    result.Add(obj);
                }
                p = data + size + (size & 1);
            }
            return result;
        }

        private static uint Leb(byte[] b, ref int p)
        {
            uint r = 0; int shift = 0;
            while (true)
            {
                byte x = b[p++];
                r |= (uint)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) return r;
                shift += 7;
            }
        }

        private static string Str(byte[] b, ref int p)
        {
            int n = (int)Leb(b, ref p);
            var s = Encoding.UTF8.GetString(b, p, n);
            p += n;
            return s;
        }

        private static void Limits(byte[] b, ref int p)
        {
            var flags = Leb(b, ref p);
            Leb(b, ref p);
            if ((flags & 1) != 0) Leb(b, ref p);
        }

        private static WasmObject ParseWasm(byte[] b, int start, int size)
        {
            var obj = new WasmObject();
            int end = start + size, p = start + 8;
            int linkStart = -1, linkEnd = -1;
            while (p < end)
            {
                byte id = b[p++];
                int len = (int)Leb(b, ref p);
                int body = p, bodyEnd = p + len;
                if (id == 2) // imports
                {
                    int q = body;
                    var n = Leb(b, ref q);
                    for (uint i = 0; i < n; i++)
                    {
                        Str(b, ref q);
                        obj.ImportFields.Add(Str(b, ref q));
                        byte kind = b[q++];
                        switch (kind)
                        {
                            case 0: Leb(b, ref q); break;                  // func: type index
                            case 1: q++; Limits(b, ref q); break;           // table: reftype + limits
                            case 2: Limits(b, ref q); break;                // memory
                            case 3: q += 2; break;                          // global: valtype + mut
                            case 4: q++; Leb(b, ref q); break;              // tag
                            default: throw new InvalidDataException("unknown import kind " + kind);
                        }
                    }
                }
                else if (id == 0)
                {
                    int q = body;
                    var secName = Str(b, ref q);
                    if (secName == "linking") { linkStart = q; linkEnd = bodyEnd; }
                    else if (secName == "producers")
                    {
                        var fields = Leb(b, ref q);
                        for (uint f = 0; f < fields; f++)
                        {
                            var field = Str(b, ref q);
                            var values = Leb(b, ref q);
                            for (uint v = 0; v < values; v++)
                            {
                                var tool = Str(b, ref q);
                                var ver = Str(b, ref q);
                                if (field == "processed-by" && tool == "clang") obj.Producer = ver;
                            }
                        }
                    }
                }
                p = bodyEnd;
            }
            if (linkStart >= 0) ParseLinking(b, linkStart, linkEnd, obj);
            return obj;
        }

        private static void ParseLinking(byte[] b, int p, int end, WasmObject obj)
        {
            Leb(b, ref p); // version
            while (p < end)
            {
                byte sub = b[p++];
                int len = (int)Leb(b, ref p);
                int subEnd = p + len;
                if (sub == 8) // WASM_SYMBOL_TABLE
                {
                    int q = p;
                    var n = Leb(b, ref q);
                    for (uint i = 0; i < n; i++)
                    {
                        byte kind = b[q++];
                        var flags = Leb(b, ref q);
                        bool undefined = (flags & 0x10) != 0, local = (flags & 0x2) != 0;
                        string name = null;
                        if (kind == 0 || kind == 2 || kind == 4 || kind == 5) // function, global, tag, table
                        {
                            Leb(b, ref q);
                            if (!undefined || (flags & 0x40) != 0) name = Str(b, ref q);
                        }
                        else if (kind == 1) // data
                        {
                            name = Str(b, ref q);
                            if (!undefined) { Leb(b, ref q); Leb(b, ref q); Leb(b, ref q); }
                        }
                        else if (kind == 3) Leb(b, ref q); // section
                        else throw new InvalidDataException("unknown symbol kind " + kind);
                        if (name != null && !undefined && !local) obj.DefinedGlobals.Add(name);
                    }
                }
                p = subEnd;
            }
        }
    }
}
