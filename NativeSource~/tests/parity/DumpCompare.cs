// DumpCompare — cross-platform (vs Windows reference) parity for OpenGlyph native.
//
// WHY THIS EXISTS
// ---------------
// The side-by-side harness in Program.cs loads TWO unitext_native libraries in ONE process
// and diffs them. That only works for libraries the running OS can dlopen, so in CI each OS
// compares a fresh build against ITS OWN committed binary. Nothing there proves the Linux .so
// or the macOS .dylib produce the SAME output as the Windows x64 unitext_native.dll the user
// visually accepted — you cannot load a .dll on Linux.
//
// This module closes that gap WITHOUT cross-loading. Windows runs `dump`: it drives the
// committed Windows DLL and serialises every reference output (shaping, metrics, outlines,
// face metrics, SDF bytes, hinted bitmaps, Blend2D fills, variable-font axes/instances, and
// COLR paint trees when a color font is supplied) into a deterministic, versioned, OS-neutral
// directory. That directory is uploaded as a CI artifact. Linux and macOS run `compare`: they
// load the dump and diff it against THEIR committed Plugins binary with per-category tolerances.
//
// FORMAT (v1): a directory with manifest.txt + one text file per category. Byte buffers are
// hex. Text is intentional — every difference is greppable and a human can read the dump. All
// records are emitted in a fixed order (ascending glyph id, fixed font order, fixed script
// order) so two dumps of the same library are byte-identical.
//
// USAGE
//   parity dump    <dll> <fontsDir> <varFont> <outDir>
//   parity compare <dll> <fontsDir> <varFont> <dumpDir> [--gate]
// `compare` is report-only by default (exit 0) and prints a per-category table; with --gate it
// returns non-zero when any category exceeds its tolerance.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

unsafe static class DumpCompare
{
    public const int FORMAT_VERSION = 1;

    // ---- delegate types (mirror Program.cs; kept local so this file is self-contained) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_init(out IntPtr lib);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_done(IntPtr lib);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_newface(IntPtr lib, byte* data, IntPtr size, IntPtr idx, out IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_doneface(IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_charindex(IntPtr face, UIntPtr cp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_setpx(IntPtr face, uint w, uint h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_loadglyph(IntPtr face, uint gid, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_render(IntPtr slot, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_slot(IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_faceinfo(IntPtr face, out long ff, out int ng, out int upem, out int nfs, out int nf, out int fi, out short a, out short d, out short h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_metrics(IntPtr face, out int w, out int h, out int bx, out int by, out int ax, out int ay);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_bmpinfo(IntPtr face, out int w, out int h, out int pitch, out int pm, out IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_sdf(IntPtr face, uint gid, int flags, int spread, out SdfResult r);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_freesdf(IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_outlineinfo(IntPtr face, out int nc, out int np);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_outlinedata(IntPtr face, int* xy, byte* tags, short* ends, int pc, int cc, out int np, out int ncOut, out int fl);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_mmvar(IntPtr face, out int na, out int ni);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_varaxis(IntPtr face, int i, out uint tag, out int mn, out int df, out int mx, out uint nid);
    // HarfBuzz
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blob(IntPtr data, uint len, int mode, IntPtr u, IntPtr d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_hbface(IntPtr blob, uint idx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_hbfont(IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_setfuncs(IntPtr font);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_bufcreate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_bufdir(IntPtr b, int d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_bufscript(IntPtr b, uint s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_addcp(IntPtr b, uint* cps, int len, uint off, int ilen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_shape(IntPtr font, IntPtr b, IntPtr feat, uint nf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_buflen(IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_infos(IntPtr b, out uint n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_poss(IntPtr b, out uint n);
    // COLR
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_glyphpaint(IntPtr face, uint bg, int root, out IntPtr pP, out int pIns);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_paintfmt(IntPtr face, IntPtr pP, int pIns);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_paintlayers(IntPtr face, IntPtr pP, int pIns, out uint nl, out uint l, out IntPtr iP);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_nextlayer(IntPtr face, ref uint nl, ref uint l, ref IntPtr iP, out IntPtr cP, out int cIns);
    // Blend2D
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blimgcreate(int w, int h, uint fmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blimgdata(IntPtr img, out int stride);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blctxcreate(IntPtr img);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxvoid(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxrgba(IntPtr ctx, uint rgba);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxfillpath(IntPtr ctx, IntPtr path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blpathcreate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_outlinetobl(IntPtr face, IntPtr path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blimgdestroy(IntPtr img);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxfillall(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxtranslate(IntPtr ctx, double x, double y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxscale(IntPtr ctx, double x, double y);

    [StructLayout(LayoutKind.Sequential)]
    struct SdfResult { public int success, mw, mh, mbx, mby, max, bw, bh, bp, bl, bt; public IntPtr buf; }
    [StructLayout(LayoutKind.Sequential)]
    struct HbInfo { public uint codepoint, mask, cluster, var1, var2; }
    [StructLayout(LayoutKind.Sequential)]
    struct HbPos { public int xadv, yadv, xoff, yoff; public uint var; }

    class Lib
    {
        public IntPtr H;
        public Lib(string path) { H = NativeLibrary.Load(path); }
        public T Fn<T>(string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(H, name));
        public bool Has(string name) => NativeLibrary.TryGetExport(H, name, out _);
    }

    // Fixed parameters shared by dump & compare so both sides measure identically.
    const int SDF_SPREAD = 8, SDF_PX = 48, SDF_GLYPHS = 20;
    const int BMP_PX_A = 16, BMP_PX_B = 32, BMP_PX_C = 64, BMP_GLYPHS = 12;
    const int OUTLINE_GLYPHS = 12, METRIC_GLYPHS = 12;
    const int BL_PX = 64, BL_W = 96, BL_H = 96;

    static readonly (string script, uint tag, int dir, int[] cps)[] Samples = new (string, uint, int, int[])[]
    {
        ("Latin",      (uint)(('L'<<24)|('a'<<16)|('t'<<8)|'n'), 4, Cp("Hello World")),
        ("Arabic",     (uint)(('A'<<24)|('r'<<16)|('a'<<8)|'b'), 5, Cp("\u0645\u0631\u062d\u0628\u0627")),
        ("Hebrew",     (uint)(('H'<<24)|('e'<<16)|('b'<<8)|'r'), 5, Cp("\u05e9\u05dc\u05d5\u05dd")),
        ("Devanagari", (uint)(('D'<<24)|('e'<<16)|('v'<<8)|'a'), 4, Cp("\u0928\u092e\u0938\u094d\u0924\u0947")),
        ("Thai",       (uint)(('T'<<24)|('h'<<16)|('a'<<8)|'i'), 4, Cp("\u0e2a\u0e27\u0e31\u0e2a\u0e14\u0e35")),
    };
    static int[] Cp(string s) { var l = new List<int>(); foreach (var r in s.EnumerateRunes()) l.Add(r.Value); return l.ToArray(); }

    // Fonts are enumerated in a FIXED (ordinal) order so dumps are deterministic.
    static string[] Fonts(string fontsDir, string varFont)
    {
        var l = new List<string>(Directory.GetFiles(fontsDir, "*.ttf"));
        l.Sort(StringComparer.Ordinal); // by full path; file names are unique within a dir
        // Append the explicit var font if it is not already inside fontsDir.
        if (!string.IsNullOrEmpty(varFont) && File.Exists(varFont))
        {
            string vf = Path.GetFullPath(varFont);
            bool present = false;
            foreach (var f in l) if (Path.GetFullPath(f) == vf) { present = true; break; }
            if (!present) l.Add(varFont);
        }
        return l.ToArray();
    }

    static bool IsVar(string path, string varFont)
        => !string.IsNullOrEmpty(varFont) && File.Exists(varFont)
           && Path.GetFullPath(path) == Path.GetFullPath(varFont);

    // ======================================================================= ENTRY
    public static int Run(string mode, string[] rest)
    {
        if (mode == "dump")
        {
            if (rest.Length < 4) { Console.Error.WriteLine("usage: parity dump <dll> <fontsDir> <varFont> <outDir>"); return 2; }
            return Dump(rest[0], rest[1], rest[2], rest[3]);
        }
        if (mode == "compare")
        {
            if (rest.Length < 4) { Console.Error.WriteLine("usage: parity compare <dll> <fontsDir> <varFont> <dumpDir> [--gate]"); return 2; }
            bool gate = Array.IndexOf(rest, "--gate") >= 0;
            return Compare(rest[0], rest[1], rest[2], rest[3], gate);
        }
        Console.Error.WriteLine($"unknown mode '{mode}'"); return 2;
    }

    // ======================================================================= DUMP
    static int Dump(string dllPath, string fontsDir, string varFont, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var lib = new Lib(dllPath);
        var fonts = Fonts(fontsDir, varFont);

        using var man = new StreamWriter(Path.Combine(outDir, "manifest.txt"));
        man.NewLine = "\n";
        man.WriteLine($"openglyph-parity-dump v{FORMAT_VERSION}");
        man.WriteLine($"source-dll {Path.GetFileName(dllPath)}");
        man.WriteLine($"sdf spread={SDF_SPREAD} px={SDF_PX} glyphs={SDF_GLYPHS}");
        man.WriteLine($"bmp px={BMP_PX_A},{BMP_PX_B},{BMP_PX_C} glyphs={BMP_GLYPHS}");
        man.WriteLine($"outline glyphs={OUTLINE_GLYPHS}");
        man.WriteLine($"blend2d px={BL_PX} img={BL_W}x{BL_H}");
        foreach (var f in fonts) man.WriteLine($"font {Path.GetFileName(f)}{(IsVar(f, varFont) ? " [var]" : "")}");

        using var w = new StreamWriter(Path.Combine(outDir, "reference.txt"));
        w.NewLine = "\n";
        var oi = lib.Fn<D_init>("ut_ft_init"); oi(out var ulib);
        foreach (var font in fonts) DumpFont(lib, ulib, w, font, IsVar(font, varFont));
        lib.Fn<D_done>("ut_ft_done")(ulib);

        // COLR (opt-in via PARITY_COLR_FONT, matching Program.cs). Separate init so a bad color
        // font can't corrupt the main pass.
        string colrFont = Environment.GetEnvironmentVariable("PARITY_COLR_FONT");
        if (!string.IsNullOrEmpty(colrFont) && File.Exists(colrFont))
            DumpColr(lib, w, colrFont);

        w.Flush(); man.Flush();
        Console.WriteLine($"dump written: {outDir} ({fonts.Length} fonts)");
        return 0;
    }

    static void DumpFont(Lib lib, IntPtr ulib, StreamWriter w, string path, bool isVar)
    {
        byte[] data = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            byte* p = (byte*)pin.AddrOfPinnedObject();
            lib.Fn<D_newface>("ut_ft_new_memory_face")(ulib, p, (IntPtr)data.Length, IntPtr.Zero, out var face);
            if (face == IntPtr.Zero) { w.WriteLine($"FONT {name} FACE-NULL"); return; }
            w.WriteLine($"FONT {name}");

            lib.Fn<D_faceinfo>("ut_ft_get_face_info")(face, out var ff, out int ng, out int upem, out _, out int nf, out _, out short a, out short d, out short h);
            w.WriteLine($"  faceinfo numGlyphs={ng} upem={upem} ascender={a} descender={d} height={h} numFaces={nf}");

            var cI = lib.Fn<D_charindex>("ut_ft_get_char_index");
            var sb = new StringBuilder("  charindex");
            foreach (int cp in new[] { 'H', 'e', 'A', '0', 0x0645, 0x05e9, 0x0e2a, 0x0928 })
                sb.Append($" {cp:X4}:{cI(face, (UIntPtr)cp)}");
            w.WriteLine(sb.ToString());

            // Metrics + outline data at px 48, default load.
            lib.Fn<D_setpx>("ut_ft_set_pixel_sizes")(face, SDF_PX, SDF_PX);
            var load = lib.Fn<D_loadglyph>("ut_ft_load_glyph");
            var met = lib.Fn<D_metrics>("ut_ft_get_glyph_metrics");
            var oiI = lib.Fn<D_outlineinfo>("ut_ft_get_outline_info");
            int glyphCap = Math.Min(METRIC_GLYPHS, ng);
            for (uint g = 1; g <= (uint)glyphCap; g++)
            {
                load(face, g, 0);
                met(face, out int mw, out int mh, out int bx, out int by, out int ax, out int ay);
                w.WriteLine($"  metrics g{g} w={mw} h={mh} bx={bx} by={by} ax={ax} ay={ay}");
            }
            // Outline data in font units (NO_SCALE) — deterministic, resolution-independent.
            if (lib.Has("ut_ft_get_outline_data"))
            {
                var odata = lib.Fn<D_outlinedata>("ut_ft_get_outline_data");
                int ocap = Math.Min(OUTLINE_GLYPHS, ng);
                for (uint g = 1; g <= (uint)ocap; g++)
                {
                    load(face, g, (1<<0)); // LOAD_NO_SCALE
                    if (oiI(face, out int nc, out int np) == 0 || np <= 0) { w.WriteLine($"  outline g{g} empty"); continue; }
                    var xy = new int[2*np]; var tags = new byte[np]; var ends = new short[nc <= 0 ? 1 : nc];
                    fixed (int* pxy = xy) fixed (byte* pt = tags) fixed (short* pe = ends)
                    {
                        int r = odata(face, pxy, pt, pe, np, nc, out int gp, out int gc, out int fl);
                        if (r != 1) { w.WriteLine($"  outline g{g} rc={r}"); continue; }
                        var ob = new StringBuilder($"  outline g{g} np={gp} nc={gc} fl={fl} xy=");
                        for (int i = 0; i < gp; i++) { ob.Append(xy[2*i]); ob.Append(','); ob.Append(xy[2*i+1]); ob.Append(';'); }
                        ob.Append(" tags="); for (int i = 0; i < gp; i++) ob.Append(((int)tags[i]).ToString()).Append(',');
                        ob.Append(" ends="); for (int i = 0; i < gc; i++) ob.Append(((int)ends[i]).ToString()).Append(',');
                        w.WriteLine(ob.ToString());
                    }
                }
            }

            // SDF bytes.
            if (lib.Has("ut_ft_render_sdf_glyph"))
            {
                var S = lib.Fn<D_sdf>("ut_ft_render_sdf_glyph");
                var Free = lib.Fn<D_freesdf>("ut_ft_free_sdf_buffer");
                int done = 0;
                for (uint g = 1; g <= (uint)ng && done < SDF_GLYPHS; g++)
                {
                    if (S(face, g, 0, SDF_SPREAD, out var r) == 0 && r.buf != IntPtr.Zero && r.bw > 0 && r.bh > 0)
                    {
                        int len = Math.Abs(r.bp) * r.bh;
                        var buf = new byte[len];
                        Marshal.Copy(r.buf, buf, 0, len);
                        w.WriteLine($"  sdf g{g} bw={r.bw} bh={r.bh} bp={r.bp} bl={r.bl} bt={r.bt} bytes={Hex(buf)}");
                        done++;
                    }
                    if (r.buf != IntPtr.Zero) Free(r.buf);
                }
            }

            // Hinted bitmaps (default flags) at three sizes.
            DumpBitmaps(lib, face, w, ng);

            // Blend2D rasterization (only when enabled in this build).
            DumpBlend2D(lib, ulib, w, path);

            // Variable-font axes/instances.
            if (isVar && lib.Has("ut_ft_get_mm_var"))
            {
                int r = lib.Fn<D_mmvar>("ut_ft_get_mm_var")(face, out int na, out int ni);
                w.WriteLine($"  var rc={r} axes={na} instances={ni}");
                if (r == 0 && na > 0)
                {
                    var ax = lib.Fn<D_varaxis>("ut_ft_get_var_axis");
                    for (int i = 0; i < na; i++)
                        if (ax(face, i, out uint tag, out int mn, out int df, out int mx, out uint nid) == 0)
                            w.WriteLine($"  varaxis {i} tag={tag:X8} min={mn} def={df} max={mx} nameid={nid}");
                }
            }

            // Shaping — reset size first (shaping is unaffected by pixel size here, but keep it tidy).
            DumpShaping(lib, data, name, w);

            lib.Fn<D_doneface>("ut_ft_done_face")(face);
        }
        finally { pin.Free(); }
    }

    static void DumpBitmaps(Lib lib, IntPtr face, StreamWriter w, int ng)
    {
        var set = lib.Fn<D_setpx>("ut_ft_set_pixel_sizes");
        var load = lib.Fn<D_loadglyph>("ut_ft_load_glyph");
        var ren = lib.Fn<D_render>("ut_ft_render_glyph");
        var slot = lib.Fn<D_slot>("ut_ft_get_glyph_slot");
        var bmp = lib.Fn<D_bmpinfo>("ut_ft_get_bitmap_info");
        int cap = Math.Min(BMP_GLYPHS, ng);
        foreach (int sz in new[] { BMP_PX_A, BMP_PX_B, BMP_PX_C })
        {
            set(face, (uint)sz, (uint)sz);
            for (uint g = 1; g <= (uint)cap; g++)
            {
                load(face, g, 0);
                ren(slot(face), 0);
                bmp(face, out int bw, out int bh, out int pitch, out int pm, out IntPtr buf);
                if (buf == IntPtr.Zero || bw <= 0 || bh <= 0) { w.WriteLine($"  bmp sz{sz} g{g} empty bw={bw} bh={bh} pm={pm}"); continue; }
                int len = Math.Abs(pitch) * bh;
                var b = new byte[len]; Marshal.Copy(buf, b, 0, len);
                w.WriteLine($"  bmp sz{sz} g{g} bw={bw} bh={bh} pitch={pitch} pm={pm} bytes={Hex(b)}");
            }
        }
    }

    static void DumpBlend2D(Lib lib, IntPtr ulib, StreamWriter w, string path)
    {
        if (!lib.Has("ut_blImageCreate")) { w.WriteLine("  blend2d SKIP (no ut_blImageCreate)"); return; }
        byte[] data = File.ReadAllBytes(path);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            byte* p = (byte*)pin.AddrOfPinnedObject();
            lib.Fn<D_newface>("ut_ft_new_memory_face")(ulib, p, (IntPtr)data.Length, IntPtr.Zero, out var face);
            if (face == IntPtr.Zero) { w.WriteLine("  blend2d FACE-NULL"); return; }
            uint g = lib.Fn<D_charindex>("ut_ft_get_char_index")(face, (UIntPtr)'A'); if (g == 0) g = 1;
            // Probe: does outline_to_blpath actually produce a path (Blend2D real, not stub)?
            lib.Fn<D_setpx>("ut_ft_set_pixel_sizes")(face, BL_PX, BL_PX);
            lib.Fn<D_loadglyph>("ut_ft_load_glyph")(face, g, (1<<0)|(1<<3));
            var probe = lib.Fn<D_blpathcreate>("ut_blPathCreate")();
            if (lib.Fn<D_outlinetobl>("ut_ft_outline_to_blpath")(face, probe) == 0)
            { w.WriteLine("  blend2d SKIP (outline_to_blpath=0, stub/disabled)"); lib.Fn<D_doneface>("ut_ft_done_face")(face); return; }

            var fi = lib.Fn<D_faceinfo>("ut_ft_get_face_info");
            fi(face, out _, out _, out int upem, out _, out _, out _, out _, out _, out _); if (upem <= 0) upem = 2048;
            var img = lib.Fn<D_blimgcreate>("ut_blImageCreate")(BL_W, BL_H, 1);
            var ctx = lib.Fn<D_blctxcreate>("ut_blContextCreate")(img);
            var pathH = lib.Fn<D_blpathcreate>("ut_blPathCreate")();
            lib.Fn<D_loadglyph>("ut_ft_load_glyph")(face, g, (1<<0)|(1<<3));
            lib.Fn<D_outlinetobl>("ut_ft_outline_to_blpath")(face, pathH);
            lib.Fn<D_blctxrgba>("ut_blContextSetFillStyleRgba32")(ctx, 0xFF000000);
            lib.Fn<D_blctxfillall>("ut_blContextFillAll")(ctx);
            lib.Fn<D_blctxrgba>("ut_blContextSetFillStyleRgba32")(ctx, 0xFFFFFFFF);
            double s = (double)BL_PX / upem;
            lib.Fn<D_blctxtranslate>("ut_blContextTranslate")(ctx, BL_PX*0.2, BL_PX*1.2);
            lib.Fn<D_blctxscale>("ut_blContextScale")(ctx, s, -s);
            lib.Fn<D_blctxfillpath>("ut_blContextFillPath")(ctx, pathH);
            lib.Fn<D_blctxvoid>("ut_blContextEnd")(ctx);
            IntPtr pd = lib.Fn<D_blimgdata>("ut_blImageGetData")(img, out int stride);
            var buf = new byte[BL_H*stride]; Marshal.Copy(pd, buf, 0, buf.Length);
            lib.Fn<D_blctxvoid>("ut_blContextDestroy")(ctx);
            lib.Fn<D_blimgdestroy>("ut_blImageDestroy")(img);
            w.WriteLine($"  blend2d g{g} stride={stride} bytes={Hex(buf)}");
            lib.Fn<D_doneface>("ut_ft_done_face")(face);
        }
        finally { pin.Free(); }
    }

    static void DumpShaping(Lib lib, byte[] data, string name, StreamWriter w)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr blob = lib.Fn<D_blob>("ut_hb_blob_create")(pin.AddrOfPinnedObject(), (uint)data.Length, 1, IntPtr.Zero, IntPtr.Zero);
            IntPtr hbface = lib.Fn<D_hbface>("ut_hb_face_create")(blob, 0);
            IntPtr font = lib.Fn<D_hbfont>("ut_hb_font_create")(hbface);
            lib.Fn<D_setfuncs>("ut_hb_ot_font_set_funcs")(font);
            foreach (var sdef in Samples)
            {
                var sh = Shape(lib, font, sdef);
                var sb = new StringBuilder($"  shape[{sdef.script}] n={sh.Count}");
                foreach (var t in sh) sb.Append($" {t.gid}/{t.cluster}/{t.xadv}/{t.xoff}/{t.yoff}");
                w.WriteLine(sb.ToString());
            }
        }
        finally { pin.Free(); }
    }

    static List<(uint gid, uint cluster, int xadv, int xoff, int yoff)> Shape(Lib lib, IntPtr font, (string script, uint tag, int dir, int[] cps) s)
    {
        IntPtr buf = lib.Fn<D_bufcreate>("ut_hb_buffer_create")();
        lib.Fn<D_bufdir>("ut_hb_buffer_set_direction")(buf, s.dir);
        lib.Fn<D_bufscript>("ut_hb_buffer_set_script")(buf, s.tag);
        var cps = Array.ConvertAll(s.cps, x => (uint)x);
        fixed (uint* pc = cps) lib.Fn<D_addcp>("ut_hb_buffer_add_codepoints")(buf, pc, cps.Length, 0, cps.Length);
        lib.Fn<D_shape>("ut_hb_shape")(font, buf, IntPtr.Zero, 0);
        uint n = lib.Fn<D_buflen>("ut_hb_buffer_get_length")(buf);
        IntPtr infosP = lib.Fn<D_infos>("ut_hb_buffer_get_glyph_infos")(buf, out _);
        IntPtr possP = lib.Fn<D_poss>("ut_hb_buffer_get_glyph_positions")(buf, out _);
        var outp = new List<(uint, uint, int, int, int)>();
        HbInfo* inf = (HbInfo*)infosP; HbPos* pos = (HbPos*)possP;
        for (uint i = 0; i < n; i++) outp.Add((inf[i].codepoint, inf[i].cluster, pos[i].xadv, pos[i].xoff, pos[i].yoff));
        return outp;
    }

    static void DumpColr(Lib lib, StreamWriter w, string path)
    {
        byte[] data = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        var oi = lib.Fn<D_init>("ut_ft_init"); oi(out var ulib);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            byte* p = (byte*)pin.AddrOfPinnedObject();
            lib.Fn<D_newface>("ut_ft_new_memory_face")(ulib, p, (IntPtr)data.Length, IntPtr.Zero, out var face);
            if (face == IntPtr.Zero) { w.WriteLine($"COLR {name} FACE-NULL"); return; }
            w.WriteLine($"COLR {name}");
            var gp = lib.Fn<D_glyphpaint>("ut_colr_get_glyph_paint");
            lib.Fn<D_faceinfo>("ut_ft_get_face_info")(face, out _, out int ng, out _, out _, out _, out _, out _, out _, out _);
            int colorGlyphs = 0;
            for (uint g = 1; g < (uint)ng && colorGlyphs < 40; g++)
            {
                if (gp(face, g, 1, out IntPtr pP, out int pIns) == 0) continue;
                var fmts = WalkPaint(lib, face, pP, pIns, 0);
                w.WriteLine($"  colrglyph g{g} fmts={string.Join(",", fmts)}");
                colorGlyphs++;
            }
            w.WriteLine($"  colrsummary colorGlyphs={colorGlyphs}");
            lib.Fn<D_doneface>("ut_ft_done_face")(face);
        }
        finally { pin.Free(); }
        lib.Fn<D_done>("ut_ft_done")(ulib);
    }

    static List<int> WalkPaint(Lib lib, IntPtr face, IntPtr pP, int pIns, int depth)
    {
        var fmts = new List<int>();
        if (depth > 64) return fmts;
        int fmt = lib.Fn<D_paintfmt>("ut_colr_get_paint_format")(face, pP, pIns);
        fmts.Add(fmt);
        if (fmt == 1)
        {
            var getLayers = lib.Fn<D_paintlayers>("ut_colr_get_paint_layers");
            var nextLayer = lib.Fn<D_nextlayer>("ut_colr_get_next_layer");
            if (getLayers(face, pP, pIns, out uint nl, out uint l, out IntPtr iP) != 0)
            {
                int guard = 0;
                while (nextLayer(face, ref nl, ref l, ref iP, out IntPtr cP, out int cIns) != 0 && guard++ < 512)
                    fmts.AddRange(WalkPaint(lib, face, cP, cIns, depth + 1));
            }
        }
        return fmts;
    }

    static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    static byte[] UnHex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(s.AsSpan(2*i, 2), NumberStyles.HexNumber);
        return b;
    }

    // ======================================================================= COMPARE
    // Per-category tolerances. Exact for shaping/metrics/outlines/COLR/axes; measured bands for
    // SDF/bitmap/Blend2D. Report-only unless --gate. Thresholds are DERIVED FROM EVIDENCE: start
    // generous, tighten once a report run shows the real cross-platform spread.
    class Cat
    {
        public string name;
        public bool exact;           // exact-match category
        public double maxAbsTol;     // allowed max |byte diff| for raster categories
        public double meanAbsTol;    // allowed mean |byte diff|
        public long records, mismatchRecords;
        public int worstMax; public double worstMean; public string worstWhere = "";
        public long diffBytes, totalBytes; public long sumAbs;
    }

    static int Compare(string dllPath, string fontsDir, string varFont, string dumpDir, bool gate)
    {
        string refPath = Path.Combine(dumpDir, "reference.txt");
        if (!File.Exists(refPath)) { Console.Error.WriteLine($"no reference.txt in {dumpDir}"); return 2; }

        // Build the LOCAL reference the same way, into memory, keyed by line prefix.
        string localDir = Path.Combine(Path.GetTempPath(), "parity-local-" + Guid.NewGuid().ToString("N"));
        int dr = Dump(dllPath, fontsDir, varFont, localDir);
        if (dr != 0) { Console.Error.WriteLine("local dump failed"); return dr; }
        string localRef = Path.Combine(localDir, "reference.txt");

        var cats = new Dictionary<string, Cat>();
        Cat C(string n, bool exact, double maxT = 0, double meanT = 0)
        {
            if (!cats.TryGetValue(n, out var c)) { c = new Cat { name = n, exact = exact, maxAbsTol = maxT, meanAbsTol = meanT }; cats[n] = c; }
            return c;
        }
        // register in a stable order
        C("faceinfo", true); C("charindex", true); C("metrics", true); C("outline", true);
        C("shape", true); C("var", true); C("varaxis", true); C("colrglyph", true); C("colrsummary", true);
        C("sdf", false, 8, 1.0); C("bmp", false, 4, 0.5); C("blend2d", false, 2, 0.3);

        var refLines = File.ReadAllLines(refPath);
        var locLines = File.ReadAllLines(localRef);
        // Pair by position: both dumps emit identical record order, so index i matches index i.
        int n = Math.Min(refLines.Length, locLines.Length);
        int structuralDrift = Math.Abs(refLines.Length - locLines.Length);

        for (int i = 0; i < n; i++)
        {
            string rl = refLines[i].TrimStart(), ll = locLines[i].TrimStart();
            string key = CatKey(rl);
            if (!cats.ContainsKey(key)) continue; // FONT/COLR headers etc.
            var c = cats[key];
            c.records++;
            if (c.exact)
            {
                if (rl != ll) { c.mismatchRecords++; if (c.worstWhere == "") c.worstWhere = $"@line{i}: ref='{Trunc(rl)}' loc='{Trunc(ll)}'"; }
            }
            else
            {
                // raster category: parse the trailing bytes=... hex and diff.
                CompareBytes(c, rl, ll, i);
            }
        }

        // ---- report ----
        Console.WriteLine($"\n===== CROSS-PLATFORM COMPARE =====");
        Console.WriteLine($"ref dump : {refPath}");
        Console.WriteLine($"local dll: {dllPath}");
        Console.WriteLine($"ref lines={refLines.Length} local lines={locLines.Length} structuralDrift={structuralDrift}");
        Console.WriteLine($"{"category",-12} {"records",8} {"mismatch",9} {"maxAbs",7} {"meanAbs",8} {"diffBytes%",11}  worst");
        bool fail = structuralDrift != 0;
        foreach (var key in new[] { "faceinfo","charindex","metrics","outline","shape","var","varaxis","colrglyph","colrsummary","sdf","bmp","blend2d" })
        {
            var c = cats[key];
            if (c.exact)
            {
                bool ok = c.mismatchRecords == 0;
                Console.WriteLine($"{c.name,-12} {c.records,8} {c.mismatchRecords,9} {"-",7} {"-",8} {"-",11}  {c.worstWhere}");
                if (!ok) fail = true;
            }
            else
            {
                double meanAll = c.totalBytes > 0 ? (double)c.sumAbs / c.totalBytes : 0;
                double diffPct = c.totalBytes > 0 ? 100.0 * c.diffBytes / c.totalBytes : 0;
                bool ok = c.worstMax <= c.maxAbsTol && c.worstMean <= c.meanAbsTol;
                Console.WriteLine($"{c.name,-12} {c.records,8} {c.mismatchRecords,9} {c.worstMax,7} {meanAll,8:F3} {diffPct,10:F3}%  {c.worstWhere} (tol max<={c.maxAbsTol} mean<={c.meanAbsTol})");
                if (!ok) fail = true;
            }
        }
        Console.WriteLine($"=====  VERDICT: {(fail ? "DIFFERENCES EXCEED TOLERANCE" : "WITHIN TOLERANCE")}  =====");
        try { Directory.Delete(localDir, true); } catch { }
        if (!gate) { Console.WriteLine("(report-only; pass --gate to enforce)"); return 0; }
        return fail ? 1 : 0;
    }

    static void CompareBytes(Cat c, string rl, string ll, int lineIdx)
    {
        int ri = rl.IndexOf("bytes="), li = ll.IndexOf("bytes=");
        // compare the record HEADER (everything before bytes=) exactly first
        string rh = ri >= 0 ? rl.Substring(0, ri) : rl;
        string lh = li >= 0 ? ll.Substring(0, li) : ll;
        if (rh != lh) { c.mismatchRecords++; if (c.worstWhere == "") c.worstWhere = $"@line{lineIdx} header: ref='{Trunc(rh)}' loc='{Trunc(lh)}'"; return; }
        if (ri < 0 || li < 0) return; // e.g. "empty"/"SKIP" lines: header matched, no bytes
        byte[] rb = UnHex(rl.Substring(ri + 6).Trim());
        byte[] lb = UnHex(ll.Substring(li + 6).Trim());
        int len = Math.Min(rb.Length, lb.Length);
        if (rb.Length != lb.Length) { c.mismatchRecords++; if (c.worstWhere == "") c.worstWhere = $"@line{lineIdx} len {rb.Length}!={lb.Length}"; }
        int localMax = 0; long localSum = 0; long localDiff = 0;
        for (int i = 0; i < len; i++) { int d = Math.Abs(rb[i] - lb[i]); if (d > 0) localDiff++; if (d > localMax) localMax = d; localSum += d; }
        c.totalBytes += len; c.sumAbs += localSum; c.diffBytes += localDiff;
        double localMean = len > 0 ? (double)localSum / len : 0;
        if (localMax > c.worstMax) { c.worstMax = localMax; c.worstWhere = $"@line{lineIdx} max={localMax} mean={localMean:F3} ({Trunc(rh)})"; }
        if (localMean > c.worstMean) c.worstMean = localMean;
        if (localMax > 0) c.mismatchRecords++;
    }

    static string FirstToken(string s) { int sp = s.IndexOf(' '); return sp < 0 ? s : s.Substring(0, sp); }
    // Category key = first token with any trailing "[…]" qualifier stripped, so "shape[Latin]"
    // and "shape[Arabic]" both land in the "shape" category.
    static string CatKey(string s) { string t = FirstToken(s); int b = t.IndexOf('['); return b < 0 ? t : t.Substring(0, b); }
    static string Trunc(string s) => s.Length <= 80 ? s : s.Substring(0, 80) + "…";
}
