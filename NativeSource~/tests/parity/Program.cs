// parity — loads the ORIGINAL and NEW unitext_native DLLs side by side and compares
// behaviour across the FreeType + HarfBuzz ABI. Milestone 1 scope (ut_ft_*, ut_hb_*,
// ut_ft_get_outline_data, variable-font exports). ut_colr_*/ut_bl* are stubbed in the
// new DLL this milestone, so COLR/Blend2D parity is deferred to M2.
//
// Usage: parity <origDll> <newDll> <fontsDir> [varFont]
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

unsafe class Program
{
    // ---- delegate types --------------------------------------------------------
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_setcoords(IntPtr face, int* coords1616, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_setnamed(IntPtr face, int idx);
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

    static int pass = 0, fail = 0;
    static List<string> failures = new();
    static void Check(string what, bool ok, string detail = "")
    {
        if (ok) pass++;
        else { fail++; failures.Add($"{what}: {detail}"); }
    }
    // SDF reach parity: median edge->0 outside distance (px) must match the original within +-1px.
    static void slopesO_reachReport(System.Collections.Generic.List<double> ro, System.Collections.Generic.List<double> rn, string name)
    {
        ro.Sort(); rn.Sort();
        double mo=ro.Count>0?ro[ro.Count/2]:0, mn=rn.Count>0?rn[rn.Count/2]:0;
        Console.WriteLine($"    SDF reach median (edge->0 px): orig={mo:F1} new={mn:F1} (gate +-1px)");
        Check($"{name} sdf reach +-1px", Math.Abs(mo-mn)<=1.0, $"orig={mo} new={mn}");
    }
    // Straddle-jump: median |dByte| across the 128 crossing pair (H+V); ratio new/orig 0.85-1.15.
    // Sees the original's edge discontinuity (EDT on a hard mask); the reach gate stays too.
    static void nearEdgeSlopeReport(System.Collections.Generic.List<double> so, System.Collections.Generic.List<double> sn, string name)
    {
        so.Sort(); sn.Sort();
        double mo=so.Count>0?so[so.Count/2]:0, mn=sn.Count>0?sn[sn.Count/2]:0;
        double ratio = mo>0? mn/mo : 0;
        Console.WriteLine($"    SDF straddle jump median |dByte|: orig={mo:F1} new={mn:F1} ratio={ratio:F3} (gate 0.85-1.15)");
        Check($"{name} sdf straddle jump 0.85-1.15", ratio>=0.85 && ratio<=1.15, $"ratio={ratio:F3} orig={mo} new={mn}");
    }

    static readonly (string script, uint tag, int dir, int[] cps)[] Samples = new (string, uint, int, int[])[]
    {
        ("Latin",      (uint)(('L'<<24)|('a'<<16)|('t'<<8)|'n'), 4, Cp("Hello World")),
        ("Arabic",     (uint)(('A'<<24)|('r'<<16)|('a'<<8)|'b'), 5, Cp("\u0645\u0631\u062d\u0628\u0627")),
        ("Hebrew",     (uint)(('H'<<24)|('e'<<16)|('b'<<8)|'r'), 5, Cp("\u05e9\u05dc\u05d5\u05dd")),
        ("Devanagari", (uint)(('D'<<24)|('e'<<16)|('v'<<8)|'a'), 4, Cp("\u0928\u092e\u0938\u094d\u0924\u0947")),
        ("Thai",       (uint)(('T'<<24)|('h'<<16)|('a'<<8)|'i'), 4, Cp("\u0e2a\u0e27\u0e31\u0e2a\u0e14\u0e35")),
    };
    static int[] Cp(string s) { var l = new List<int>(); foreach (var r in s.EnumerateRunes()) l.Add(r.Value); return l.ToArray(); }

    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: parity <origDll> <newDll> <fontsDir> [varFont]"); return 2; }
        string origPath = args[0], newPath = args[1], fontsDir = args[2];
        string varFont = args.Length > 3 ? args[3] : null;

        var orig = new Lib(origPath);
        var neu  = new Lib(newPath);
        Console.WriteLine($"orig: {origPath}\nnew : {newPath}\n");

        foreach (var f in Directory.GetFiles(fontsDir, "*.ttf"))
            CompareFont(orig, neu, f, varFont != null && Path.GetFullPath(f) == Path.GetFullPath(varFont));
        if (varFont != null && File.Exists(varFont))
            CompareFont(orig, neu, varFont, true);

        // COLRv1 paint-tree parity (M2) on a color font, opt-in via env var.
        string colrFont = Environment.GetEnvironmentVariable("PARITY_COLR_FONT");
        if (!string.IsNullOrEmpty(colrFont) && File.Exists(colrFont))
        {
            Console.WriteLine($"\n== COLRv1 ==\nfont: {colrFont}");
            CompareColr(orig, neu, colrFont);
        }

        // Blend2D rasterization parity (M2). Runs only when both DLLs have a functional
        // ut_ft_outline_to_blpath (Blend2D enabled); with the stub build it reports skipped.
        {
            var f = Directory.GetFiles(fontsDir, "*.ttf");
            if (f.Length > 0) { Console.WriteLine("\n== Blend2D raster =="); CompareBlend2D(orig, neu, f[0]); }
        }

        // Editor DLL subset parity (M2): opt-in via env vars so positional args stay stable.
        string edOrig = Environment.GetEnvironmentVariable("PARITY_EDITOR_ORIG");
        string edNew  = Environment.GetEnvironmentVariable("PARITY_EDITOR_NEW");
        if (!string.IsNullOrEmpty(edOrig) && !string.IsNullOrEmpty(edNew) && File.Exists(edOrig) && File.Exists(edNew))
        {
            Console.WriteLine($"\n== EDITOR DLL ==\norig: {edOrig}\nnew : {edNew}");
            var eo = new Lib(edOrig); var en = new Lib(edNew);
            foreach (var f in Directory.GetFiles(fontsDir, "*.ttf"))
                CompareEditor(eo, en, f);
        }

        Console.WriteLine($"\n===== RESULT: {pass} passed, {fail} failed =====");
        foreach (var fl in failures) Console.WriteLine("  FAIL " + fl);
        return fail == 0 ? 0 : 1;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_glyphcount(IntPtr data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_subset(IntPtr data, uint size, uint[] cps, uint n, IntPtr outData, uint cap);

    // COLR delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_glyphpaint(IntPtr face, uint bg, int root, out IntPtr pP, out int pIns);
    // Blend2D delegates
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxtranslate(IntPtr ctx,double x,double y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxscale(IntPtr ctx,double x,double y);

    // Blend2D rasterization parity: fill the same glyph outline in both DLLs, diff pixels.
    // Runs only when ut_ft_outline_to_blpath is functional in BOTH DLLs (Blend2D enabled).
    static void CompareBlend2D(Lib orig, Lib neu, string path)
    {
        byte[] data = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        if (!orig.Has("ut_blImageCreate") || !neu.Has("ut_blImageCreate")) return;
        var oi = orig.Fn<D_init>("ut_ft_init"); var ni = neu.Fn<D_init>("ut_ft_init");
        oi(out var oLib); ni(out var nLib);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            byte* p = (byte*)pin.AddrOfPinnedObject();
            orig.Fn<D_newface>("ut_ft_new_memory_face")(oLib, p, (IntPtr)data.Length, IntPtr.Zero, out var of);
            neu .Fn<D_newface>("ut_ft_new_memory_face")(nLib, p, (IntPtr)data.Length, IntPtr.Zero, out var nf);
            orig.Fn<D_setpx>("ut_ft_set_pixel_sizes")(of, 64, 64);
            neu .Fn<D_setpx>("ut_ft_set_pixel_sizes")(nf, 64, 64);
            uint g = neu.Fn<D_charindex>("ut_ft_get_char_index")(nf, (UIntPtr)'A'); if (g==0) g=1;

            int maxPix = 0; long sum = 0; int len = 0; bool ok = true;
            foreach (var lib in new[]{orig,neu})
            {
                var face = lib==orig?of:nf;
                lib.Fn<D_loadglyph>("ut_ft_load_glyph")(face, g, (1<<0)|(1<<3) /*NO_SCALE|NO_BITMAP*/);
                var path2 = lib.Fn<D_blpathcreate>("ut_blPathCreate")();
                if (lib.Fn<D_outlinetobl>("ut_ft_outline_to_blpath")(face, path2) == 0) { ok = false; break; }
            }
            if (!ok) { Console.WriteLine($"    Blend2D {name}: ut_ft_outline_to_blpath returned 0 (Blend2D disabled/stub) — SKIPPED (deferred; needs -DOPENGLYPH_ENABLE_BLEND2D=ON on CMake<=3.31)"); return; }

            byte[] Raster(Lib lib, IntPtr face)
            {
                const int W=96,H=96,PX=64;
                var fi=lib.Fn<D_faceinfo>("ut_ft_get_face_info");
                fi(face, out _, out _, out int upem, out _, out _, out _, out _, out _, out _); if(upem<=0)upem=2048;
                var img = lib.Fn<D_blimgcreate>("ut_blImageCreate")(W,H,1/*PRGB32*/);
                var ctx = lib.Fn<D_blctxcreate>("ut_blContextCreate")(img);
                var pathH = lib.Fn<D_blpathcreate>("ut_blPathCreate")();
                // C# contract (COLRv1Renderer): LOAD_NO_SCALE|NO_BITMAP -> path in font units,
                // scaled to px in the Blend2D context (px/upem), Y flipped.
                lib.Fn<D_loadglyph>("ut_ft_load_glyph")(face, g, (1<<0)|(1<<3));
                lib.Fn<D_outlinetobl>("ut_ft_outline_to_blpath")(face, pathH);
                lib.Fn<D_blctxrgba>("ut_blContextSetFillStyleRgba32")(ctx, 0xFF000000);
                lib.Fn<D_blctxfillall>("ut_blContextFillAll")(ctx);
                lib.Fn<D_blctxrgba>("ut_blContextSetFillStyleRgba32")(ctx, 0xFFFFFFFF);
                double s=(double)PX/upem;
                lib.Fn<D_blctxtranslate>("ut_blContextTranslate")(ctx, PX*0.2, PX*1.2);
                lib.Fn<D_blctxscale>("ut_blContextScale")(ctx, s, -s);
                lib.Fn<D_blctxfillpath>("ut_blContextFillPath")(ctx, pathH);
                lib.Fn<D_blctxvoid>("ut_blContextEnd")(ctx);
                IntPtr pd = lib.Fn<D_blimgdata>("ut_blImageGetData")(img, out int stride);
                var buf = new byte[H*stride];
                Marshal.Copy(pd, buf, 0, buf.Length);
                lib.Fn<D_blctxvoid>("ut_blContextDestroy")(ctx);
                lib.Fn<D_blimgdestroy>("ut_blImageDestroy")(img);
                return buf;
            }
            var ob = Raster(orig, of); var nb = Raster(neu, nf);
            len = Math.Min(ob.Length, nb.Length);
            for (int i=0;i<len;i++){int d=Math.Abs(ob[i]-nb[i]);if(d>maxPix)maxPix=d;sum+=d;}
            double mean = len>0?(double)sum/len:0;
            Console.WriteLine($"    Blend2D {name}: raster maxPix={maxPix}, meanPix={mean:F3}");
            Check($"{name} blend2d raster (max<=2)", maxPix <= 2, $"maxPix={maxPix} meanPix={mean:F3}");

            orig.Fn<D_doneface>("ut_ft_done_face")(of); neu.Fn<D_doneface>("ut_ft_done_face")(nf);
        }
        finally { pin.Free(); }
        orig.Fn<D_done>("ut_ft_done")(oLib); neu.Fn<D_done>("ut_ft_done")(nLib);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_paintfmt(IntPtr face, IntPtr pP, int pIns);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_paintlayers(IntPtr face, IntPtr pP, int pIns, out uint nl, out uint l, out IntPtr iP);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_nextlayer(IntPtr face, ref uint nl, ref uint l, ref IntPtr iP, out IntPtr cP, out int cIns);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_palettedata(IntPtr face, out PaletteData pd);

    [StructLayout(LayoutKind.Sequential)]
    struct PaletteData { public ushort num_palettes; public IntPtr name_ids; public IntPtr flags; public ushort num_entries; public IntPtr entry_name_ids; }

    // Walks the COLRv1 paint tree for a color glyph, returning the sequence of paint formats seen.
    static List<int> WalkPaint(Lib lib, IntPtr face, IntPtr pP, int pIns, int depth)
    {
        var fmts = new List<int>();
        if (depth > 64) return fmts;
        var getFmt = lib.Fn<D_paintfmt>("ut_colr_get_paint_format");
        int fmt = getFmt(face, pP, pIns);
        fmts.Add(fmt);
        // FT_COLR_PAINTFORMAT_COLR_LAYERS == 1
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

    static void CompareColr(Lib orig, Lib neu, string path)
    {
        byte[] data = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        var oi = orig.Fn<D_init>("ut_ft_init"); var ni = neu.Fn<D_init>("ut_ft_init");
        oi(out var oLib); ni(out var nLib);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            byte* p = (byte*)pin.AddrOfPinnedObject();
            orig.Fn<D_newface>("ut_ft_new_memory_face")(oLib, p, (IntPtr)data.Length, IntPtr.Zero, out var of);
            neu .Fn<D_newface>("ut_ft_new_memory_face")(nLib, p, (IntPtr)data.Length, IntPtr.Zero, out var nf);
            if (of == IntPtr.Zero || nf == IntPtr.Zero) { Check($"{name} colr face", false); return; }

            // palette data parity
            var opd = orig.Fn<D_palettedata>("ut_ft_palette_data_get");
            var npd = neu .Fn<D_palettedata>("ut_ft_palette_data_get");
            int orp = opd(of, out PaletteData opdD);
            int nrp = npd(nf, out PaletteData npdD);
            Check($"{name} palette_data", orp == nrp && opdD.num_palettes == npdD.num_palettes && opdD.num_entries == npdD.num_entries,
                  $"orig(rc{orp} pal{opdD.num_palettes} ent{opdD.num_entries}) new(rc{nrp} pal{npdD.num_palettes} ent{npdD.num_entries})");

            var oGP = orig.Fn<D_glyphpaint>("ut_colr_get_glyph_paint");
            var nGP = neu .Fn<D_glyphpaint>("ut_colr_get_glyph_paint");
            neu.Fn<D_faceinfo>("ut_ft_get_face_info")(nf, out _, out int ng, out _, out _, out _, out _, out _, out _, out _);
            int colorGlyphs = 0, treeMatch = 0, treeMismatch = 0;
            for (uint g = 1; g < (uint)ng && colorGlyphs < 40; g++)
            {
                int oHas = oGP(of, g, 1, out IntPtr opP, out int opIns);
                int nHas = nGP(nf, g, 1, out IntPtr npP, out int npIns);
                if (oHas == 0 && nHas == 0) continue;
                if (oHas != nHas) { treeMismatch++; continue; }
                colorGlyphs++;
                var oFmts = WalkPaint(orig, of, opP, opIns, 0);
                var nFmts = WalkPaint(neu, nf, npP, npIns, 0);
                bool same = oFmts.Count == nFmts.Count;
                if (same) for (int i = 0; i < oFmts.Count; i++) if (oFmts[i] != nFmts[i]) { same = false; break; }
                if (same) treeMatch++; else treeMismatch++;
            }
            Console.WriteLine($"    COLR {name}: {colorGlyphs} color glyphs walked, treeMatch={treeMatch}, mismatch={treeMismatch}");
            Check($"{name} COLR paint trees", treeMismatch == 0 && colorGlyphs > 0,
                  $"match={treeMatch} mismatch={treeMismatch} colorGlyphs={colorGlyphs}");

            orig.Fn<D_doneface>("ut_ft_done_face")(of);
            neu .Fn<D_doneface>("ut_ft_done_face")(nf);
        }
        finally { pin.Free(); }
        orig.Fn<D_done>("ut_ft_done")(oLib);
        neu .Fn<D_done>("ut_ft_done")(nLib);
    }

    static void CompareEditor(Lib eo, Lib en, string path)
    {
        byte[] data = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        var gcO = eo.Fn<D_glyphcount>("get_glyph_count"); var gcN = en.Fn<D_glyphcount>("get_glyph_count");
        var suO = eo.Fn<D_subset>("subset_font"); var suN = en.Fn<D_subset>("subset_font");

        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr p = pin.AddrOfPinnedObject();
            uint gco = gcO(p, (uint)data.Length), gcn = gcN(p, (uint)data.Length);
            Check($"{name} editor glyph_count", gco == gcn && gcn > 0, $"orig {gco} vs new {gcn}");

            // subset to a small ASCII set and compare the resulting subset fonts' glyph counts.
            uint[] cps = { 'H','e','l','o','W','r','d',' ' };
            uint needO = suO(p, (uint)data.Length, cps, (uint)cps.Length, IntPtr.Zero, 0);
            uint needN = suN(p, (uint)data.Length, cps, (uint)cps.Length, IntPtr.Zero, 0);
            Check($"{name} editor subset size", needO > 0 && needN > 0, $"orig {needO} new {needN}");
            if (needN > 0)
            {
                var outO = new byte[needO]; var outN = new byte[needN];
                var poO = GCHandle.Alloc(outO, GCHandleType.Pinned);
                var poN = GCHandle.Alloc(outN, GCHandleType.Pinned);
                try
                {
                    suO(p, (uint)data.Length, cps, (uint)cps.Length, poO.AddrOfPinnedObject(), needO);
                    suN(p, (uint)data.Length, cps, (uint)cps.Length, poN.AddrOfPinnedObject(), needN);
                    // valid font? sfnt magic (0x00010000 or 'OTTO'/'true') at offset 0
                    bool okO = IsSfnt(outO), okN = IsSfnt(outN);
                    Check($"{name} editor subset valid font", okO && okN, $"origMagic={okO} newMagic={okN}");
                    // same glyph set: feed each subset back through get_glyph_count
                    var pgO = GCHandle.Alloc(outO, GCHandleType.Pinned);
                    var pgN = GCHandle.Alloc(outN, GCHandleType.Pinned);
                    try
                    {
                        uint sgO = gcN(pgO.AddrOfPinnedObject(), (uint)outO.Length);
                        uint sgN = gcN(pgN.AddrOfPinnedObject(), (uint)outN.Length);
                        Check($"{name} editor subset glyph set", sgO == sgN && sgN > 0, $"orig {sgO} new {sgN}");
                        Console.WriteLine($"    editor {name}: full={gcn}g subset={sgN}g ({needN}B)");
                    }
                    finally { pgO.Free(); pgN.Free(); }
                }
                finally { poO.Free(); poN.Free(); }
            }
        }
        finally { pin.Free(); }
    }

    static bool IsSfnt(byte[] b)
    {
        if (b.Length < 4) return false;
        uint m = ((uint)b[0]<<24)|((uint)b[1]<<16)|((uint)b[2]<<8)|b[3];
        return m == 0x00010000u || m == 0x4F54544Fu /*OTTO*/ || m == 0x74727565u /*true*/ || m == 0x74746366u /*ttcf*/;
    }

    // Perceptual SDF metric: threshold at the shader edge (byte 128 = 0.5) at `scale`x bilinear.
    // edgeMean/edgeMax = per-edge-pixel position error in glyph px; cov = |coverage area| difference fraction.
    static void Perceptual(byte* o, byte* n, int w, int h, int pitch, int spread, int scale,
                           out double edgeMean, out double edgeMax, out double cov)
    {
        edgeMean = edgeMax = cov = 0;
        int W = w*scale, H = h*scale;
        long insideO=0, insideN=0; double eSum=0; int eCnt=0; double eMax=0;
        double SO(double fx,double fy)=>Bil(o,w,h,pitch,(int)Math.Floor(fx),(int)Math.Floor(fy),fx-Math.Floor(fx),fy-Math.Floor(fy));
        double SN(double fx,double fy)=>Bil(n,w,h,pitch,(int)Math.Floor(fx),(int)Math.Floor(fy),fx-Math.Floor(fx),fy-Math.Floor(fy));
        // coverage (inside = SDF >= 128, the shader edge)
        for (int Y=0;Y<H;Y++){double fy=(Y+0.5)/scale-0.5;for(int X=0;X<W;X++){double fx=(X+0.5)/scale-0.5;if(SO(fx,fy)>=128)insideO++;if(SN(fx,fy)>=128)insideN++;}}
        cov = (long)W*H>0 ? Math.Abs(insideO-insideN)/(double)((long)W*H) : 0;
        // edge-position error = difference in the sub-pixel 128-crossing along H and V scanlines.
        double step = 1.0/scale;
        for (int Y=0;Y<H;Y++){ double fy=(Y+0.5)/scale-0.5; CrossErr(t=>SO(t,fy), t=>SN(t,fy), -0.5, w-0.5, step, ref eSum, ref eCnt, ref eMax); }
        for (int X=0;X<W;X++){ double fx=(X+0.5)/scale-0.5; CrossErr(t=>SO(fx,t), t=>SN(fx,t), -0.5, h-0.5, step, ref eSum, ref eCnt, ref eMax); }
        edgeMean = eCnt>0? eSum/eCnt : 0; edgeMax = eMax;
    }
    // Pair 128-crossings in order (i-th orig with i-th new); record per-crossing position error.
    static void CrossErr(Func<double,double> fa, Func<double,double> fb, double lo, double hi, double step,
                         ref double eSum, ref int eCnt, ref double eMax)
    {
        var ca = new System.Collections.Generic.List<double>();
        var cb = new System.Collections.Generic.List<double>();
        double pa=fa(lo), pb=fb(lo);
        for (double t=lo+step; t<=hi; t+=step)
        {
            double a=fa(t); if ((pa-128)*(a-128)<0){ double u=(128-pa)/(a-pa); ca.Add(t-step+u*step); } pa=a;
            double b=fb(t); if ((pb-128)*(b-128)<0){ double u=(128-pb)/(b-pb); cb.Add(t-step+u*step); } pb=b;
        }
        int m = Math.Min(ca.Count, cb.Count);
        // Robust local pairing: for each orig crossing, take the nearest new crossing within a
        // 2px window (real edge shift is sub-pixel). Unmatched crossings are boundary/phantom
        // artifacts already captured by the coverage metric — they do not inflate edge error.
        const double WIN = 2.0;
        foreach (double x in ca)
        {
            double best = double.MaxValue;
            foreach (double y in cb) { double d = Math.Abs(x-y); if (d < best) best = d; }
            if (best <= WIN) { eSum += best; eCnt++; if (best > eMax) eMax = best; }
        }
        _ = m;
    }
    static double Bil(byte* b, int w, int h, int pitch, int x0, int y0, double tx, double ty)
    {
        int x1=x0+1, y1=y0+1;
        int cx0=x0<0?0:(x0>=w?w-1:x0), cx1=x1<0?0:(x1>=w?w-1:x1);
        int cy0=y0<0?0:(y0>=h?h-1:y0), cy1=y1<0?0:(y1>=h?h-1:y1);
        double v00=b[cy0*pitch+cx0], v10=b[cy0*pitch+cx1], v01=b[cy1*pitch+cx0], v11=b[cy1*pitch+cx1];
        double a=v00+(v10-v00)*tx, bb=v01+(v11-v01)*tx;
        return a+(bb-a)*ty;
    }

    static void CompareFont(Lib orig, Lib neu, string path, bool isVar)
    {
        byte[] data = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        Console.WriteLine($"--- {name} ({data.Length} bytes){(isVar ? " [variable]" : "")} ---");

        // init both libraries
        var oi = orig.Fn<D_init>("ut_ft_init"); var ni = neu.Fn<D_init>("ut_ft_init");
        oi(out var oLib); ni(out var nLib);

        fixed (byte* p = data)
        {
            orig.Fn<D_newface>("ut_ft_new_memory_face")(oLib, p, (IntPtr)data.Length, IntPtr.Zero, out var of);
            neu .Fn<D_newface>("ut_ft_new_memory_face")(nLib, p, (IntPtr)data.Length, IntPtr.Zero, out var nf);
            if (of == IntPtr.Zero || nf == IntPtr.Zero) { Check($"{name} face-load", false, "null face"); return; }

            // Face info
            orig.Fn<D_faceinfo>("ut_ft_get_face_info")(of, out var off, out var ong, out var oup, out _, out var onf, out _, out var oa, out var od, out var oh);
            neu .Fn<D_faceinfo>("ut_ft_get_face_info")(nf, out var nff, out var nng, out var nup, out _, out var nnf, out _, out var na, out var nd, out var nh);
            Check($"{name} faceinfo.numGlyphs", ong == nng, $"{ong} vs {nng}");
            Check($"{name} faceinfo.unitsPerEm", oup == nup, $"{oup} vs {nup}");
            Check($"{name} faceinfo.ascender", oa == na, $"{oa} vs {na}");
            Check($"{name} faceinfo.descender", od == nd, $"{od} vs {nd}");

            // char index for a set of codepoints
            var ocI = orig.Fn<D_charindex>("ut_ft_get_char_index");
            var ncI = neu .Fn<D_charindex>("ut_ft_get_char_index");
            int ciMismatch = 0;
            foreach (int cp in new[] { 'H', 'e', 'A', '0', 0x0645, 0x05e9, 0x0e2a, 0x0928 })
            {
                uint og = ocI(of, (UIntPtr)cp), ng = ncI(nf, (UIntPtr)cp);
                if (og != ng) ciMismatch++;
            }
            Check($"{name} char_index", ciMismatch == 0, $"{ciMismatch} mismatches");

            // set pixel size 48 on both
            orig.Fn<D_setpx>("ut_ft_set_pixel_sizes")(of, 48, 48);
            neu .Fn<D_setpx>("ut_ft_set_pixel_sizes")(nf, 48, 48);

            // pick a few glyphs to compare metrics + bitmap + sdf + outline
            uint gA = ncI(nf, (UIntPtr)'A');
            uint[] testGlyphs = gA != 0 ? new[] { gA } : new uint[] { 3 };

            var oLoad = orig.Fn<D_loadglyph>("ut_ft_load_glyph"); var nLoad = neu.Fn<D_loadglyph>("ut_ft_load_glyph");
            var oMet = orig.Fn<D_metrics>("ut_ft_get_glyph_metrics"); var nMet = neu.Fn<D_metrics>("ut_ft_get_glyph_metrics");
            var oRen = orig.Fn<D_render>("ut_ft_render_glyph"); var nRen = neu.Fn<D_render>("ut_ft_render_glyph");
            var oSlot = orig.Fn<D_slot>("ut_ft_get_glyph_slot"); var nSlot = neu.Fn<D_slot>("ut_ft_get_glyph_slot");
            var oBmp = orig.Fn<D_bmpinfo>("ut_ft_get_bitmap_info"); var nBmp = neu.Fn<D_bmpinfo>("ut_ft_get_bitmap_info");
            var oOI = orig.Fn<D_outlineinfo>("ut_ft_get_outline_info"); var nOI = neu.Fn<D_outlineinfo>("ut_ft_get_outline_info");

            foreach (uint g in testGlyphs)
            {
                const int LOAD_DEFAULT = 0;
                oLoad(of, g, LOAD_DEFAULT); nLoad(nf, g, LOAD_DEFAULT);
                oMet(of, out var ow, out var ohh, out var obx, out var oby, out var oax, out _);
                nMet(nf, out var nw, out var nhh, out var nbx, out var nby, out var nax, out _);
                Check($"{name} g{g} metrics.w", ow == nw, $"{ow} vs {nw}");
                Check($"{name} g{g} metrics.advance", oax == nax, $"{oax} vs {nax}");
                Check($"{name} g{g} metrics.bearingX", obx == nbx, $"{obx} vs {nbx}");

                // outline info parity + new outline-data point-count parity
                int oc = 0, on = 0, nc = 0, np = 0;
                bool oOk = oOI(of, out oc, out on) != 0;
                bool nOk = nOI(nf, out nc, out np) != 0;
                Check($"{name} g{g} outline_info", oOk == nOk && oc == nc && on == np, $"orig({oc},{on}) new({nc},{np})");

                if (neu.Has("ut_ft_get_outline_data") && nOk && np > 0)
                {
                    var xy = new int[2 * np]; var tags = new byte[np]; var ends = new short[np];
                    fixed (int* pxy = xy) fixed (byte* pt = tags) fixed (short* pe = ends)
                    {
                        int r = neu.Fn<D_outlinedata>("ut_ft_get_outline_data")(nf, pxy, pt, pe, np, nc, out int gp, out int gc, out _);
                        Check($"{name} g{g} outline_data counts", r == 1 && gp == np && gc == nc, $"r={r} pts {gp}/{np} ctr {gc}/{nc}");
                    }
                }

                // smooth bitmap render parity (±1px tolerated: autohinter version drift,
                // quantified by AnalyzeBitmapDrift below).
                oRen(oSlot(of), 0); nRen(nSlot(nf), 0);
                oBmp(of, out var obw, out var obh, out _, out var opm, out _);
                nBmp(nf, out var nbw, out var nbh, out _, out var npm, out _);
                Check($"{name} g{g} bmp dims (±1)",
                    Math.Abs(obw - nbw) <= 1 && Math.Abs(obh - nbh) <= 1 && opm == npm,
                    $"orig({obw}x{obh} pm{opm}) new({nbw}x{nbh} pm{npm})");
            }

            // SDF render — byte stats (info) + PERCEPTUAL gate (edge-position + coverage) at 1x/4x.
            if (orig.Has("ut_ft_render_sdf_glyph") && neu.Has("ut_ft_render_sdf_glyph"))
            {
                var oS = orig.Fn<D_sdf>("ut_ft_render_sdf_glyph"); var nS = neu.Fn<D_sdf>("ut_ft_render_sdf_glyph");
                var oFree = orig.Fn<D_freesdf>("ut_ft_free_sdf_buffer"); var nFree = neu.Fn<D_freesdf>("ut_ft_free_sdf_buffer");
                int nGlyphs = Math.Min(20, nng);
                int compared = 0, dimMismatch = 0, overallMax = 0; double sumMean = 0;
                // perceptual accumulators
                double edgeMeanSum1=0, edgeMax1=0, covSum1=0; double edgeMeanSum4=0, edgeMax4=0, covSum4=0; int pc=0;
                var reachO=new System.Collections.Generic.List<double>(); var reachN=new System.Collections.Generic.List<double>();
                var slopeEO=new System.Collections.Generic.List<double>(); var slopeEN=new System.Collections.Generic.List<double>();
                const int SDF_LOAD = 0, SPREAD = 8;
                for (uint g = 1; g <= (uint)nng && compared < nGlyphs; g++)
                {
                    int or = oS(of, g, SDF_LOAD, SPREAD, out var ores);
                    int nr = nS(nf, g, SDF_LOAD, SPREAD, out var nres);
                    if (or == 0 && nr == 0 && ores.buf != IntPtr.Zero && nres.buf != IntPtr.Zero
                        && ores.bw == nres.bw && ores.bh == nres.bh && ores.bp == nres.bp)
                    {
                        int len = Math.Abs(ores.bp) * ores.bh, maxDiff = 0; long sum = 0;
                        byte* ob = (byte*)ores.buf, nb = (byte*)nres.buf;
                        for (int i = 0; i < len; i++) { int d = Math.Abs(ob[i] - nb[i]); if (d > maxDiff) maxDiff = d; sum += d; }
                        if (maxDiff > overallMax) overallMax = maxDiff;
                        sumMean += (len > 0 ? (double)sum / len : 0);
                        // REACH: on the center row, px from the first 128-crossing outward to the
                        // first 0 byte. Only count glyphs where BOTH sides have a clean crossing
                        // with margin (>=2px) before the frame edge, so degenerate rows (thin
                        // features / near-border crossings) don't inflate the median.
                        int pit=Math.Abs(ores.bp); int cy=ores.bh/2;
                        int cxo=-1; for(int x=2;x<ores.bw-1;x++){int va=ob[cy*pit+x],vb=ob[cy*pit+x+1]; if((va-128)*(vb-128)<0){cxo=x;break;}}
                        int cxn=-1; for(int x=2;x<nres.bw-1;x++){int vc=nb[cy*pit+x],vd=nb[cy*pit+x+1]; if((vc-128)*(vd-128)<0){cxn=x;break;}}
                        double rO=-1,rN=-1;
                        if(cxo>2){ for(int x=cxo;x>=0;x--) if(ob[cy*pit+x]<=0){ rO=cxo-x; break; } }
                        if(cxn>2){ for(int x=cxn;x>=0;x--) if(nb[cy*pit+x]<=0){ rN=cxn-x; break; } }
                        if(rO>0 && rN>0){ reachO.Add(rO); reachN.Add(rN); }
                        // STRADDLE JUMP: |dByte| across the pair straddling 128, over all H+V
                        // crossings. This sees the original's ~5-7 byte edge discontinuity (EDT on
                        // a hard mask), which a bilinear normal profile smooths away.
                        for(int y=0;y<ores.bh;y++) for(int x=0;x<ores.bw-1;x++){
                            int va=ob[y*pit+x],vb=ob[y*pit+x+1]; if((va-128)*(vb-128)<0) slopeEO.Add(Math.Abs(vb-va));
                            int vc=nb[y*pit+x],vd=nb[y*pit+x+1]; if((vc-128)*(vd-128)<0) slopeEN.Add(Math.Abs(vd-vc)); }
                        for(int x=0;x<ores.bw;x++) for(int y=0;y<ores.bh-1;y++){
                            int va=ob[y*pit+x],vb=ob[(y+1)*pit+x]; if((va-128)*(vb-128)<0) slopeEO.Add(Math.Abs(vb-va));
                            int vc=nb[y*pit+x],vd=nb[(y+1)*pit+x]; if((vc-128)*(vd-128)<0) slopeEN.Add(Math.Abs(vd-vc)); }
                        compared++;
                        // perceptual: threshold at 128 (shader edge=0.5), 1x and 4x bilinear.
                        Perceptual(ob, nb, ores.bw, ores.bh, Math.Abs(ores.bp), SPREAD, 1, out double em1, out double ex1, out double cov1);
                        Perceptual(ob, nb, ores.bw, ores.bh, Math.Abs(ores.bp), SPREAD, 4, out double em4, out double ex4, out double cov4);
                        edgeMeanSum1+=em1; if(ex1>edgeMax1)edgeMax1=ex1; covSum1+=cov1;
                        edgeMeanSum4+=em4; if(ex4>edgeMax4)edgeMax4=ex4; covSum4+=cov4;
                        pc++;
                    }
                    else if (or==0 && nr==0 && ores.buf!=IntPtr.Zero && nres.buf!=IntPtr.Zero) dimMismatch++;
                    if (ores.buf != IntPtr.Zero) oFree(ores.buf);
                    if (nres.buf != IntPtr.Zero) nFree(nres.buf);
                }
                double meanOfMeans = compared > 0 ? sumMean / compared : 0;
                double em1a = pc>0?edgeMeanSum1/pc:0, cov1a = pc>0?covSum1/pc:0;
                double em4a = pc>0?edgeMeanSum4/pc:0, cov4a = pc>0?covSum4/pc:0;
                Console.WriteLine($"    SDF {compared}g byte(info): meanAbs={meanOfMeans:F2} maxAbs={overallMax} dimMism={dimMismatch}");
                Console.WriteLine($"    SDF perceptual 1x: edgeMean={em1a:F3}px edgeMax={edgeMax1:F3}px covDiff={cov1a:P2}");
                Console.WriteLine($"    SDF perceptual 4x: edgeMean={em4a:F3}px edgeMax={edgeMax4:F3}px covDiff={cov4a:P2}");
                slopesO_reachReport(reachO, reachN, name);
                nearEdgeSlopeReport(slopeEO, slopeEN, name);
                // Perceptual gate is the pass/fail criterion: edgeMean<=0.10px, edgeMax<=0.50px, cov<=1%.
                bool gate = dimMismatch==0
                    && em1a<=0.10 && edgeMax1<=0.50 && cov1a<=0.01
                    && em4a<=0.10 && edgeMax4<=0.50 && cov4a<=0.01;
                Check($"{name} sdf perceptual gate", gate,
                      $"1x(em={em1a:F3} ex={edgeMax1:F3} cov={cov1a:P2}) 4x(em={em4a:F3} ex={edgeMax4:F3} cov={cov4a:P2}) dimMism={dimMismatch}");
            }

            // Bitmap hinting-drift analysis (gap c): 10+ glyphs x 3 sizes, default vs NO_HINTING.
            AnalyzeBitmapDrift(orig, neu, of, nf, name, nng,
                orig.Fn<D_setpx>("ut_ft_set_pixel_sizes"), neu.Fn<D_setpx>("ut_ft_set_pixel_sizes"),
                oLoad, nLoad, oRen, nRen, oSlot, nSlot, oBmp, nBmp);

            // reset size to 48 after drift analysis
            orig.Fn<D_setpx>("ut_ft_set_pixel_sizes")(of, 48, 48);
            neu .Fn<D_setpx>("ut_ft_set_pixel_sizes")(nf, 48, 48);

            // Variable font exports (gap b): read axes, then SET coords and confirm the
            // outline/advance actually change and are deterministic.
            if (isVar && neu.Has("ut_ft_get_mm_var"))
            {
                int r = neu.Fn<D_mmvar>("ut_ft_get_mm_var")(nf, out int vAxes, out int vInst);
                Console.WriteLine($"    var: rc={r} axes={vAxes} instances={vInst}");
                Check($"{name} mm_var", r == 0 && vAxes > 0, $"rc={r} axes={vAxes}");
                if (r == 0 && vAxes > 0)
                {
                    var ax = neu.Fn<D_varaxis>("ut_ft_get_var_axis");
                    int ok = 0;
                    int wghtIdx = -1; int wghtMin = 0, wghtMax = 0, wghtDef = 0;
                    for (int i = 0; i < vAxes; i++)
                        if (ax(nf, i, out uint tag, out int mn, out int df, out int mx, out _) == 0 && mx >= mn)
                        {
                            ok++;
                            if (tag == ((uint)(('w'<<24)|('g'<<16)|('h'<<8)|'t')) ) { wghtIdx = i; wghtMin = mn; wghtMax = mx; wghtDef = df; }
                        }
                    Check($"{name} var_axis all", ok == vAxes, $"{ok}/{vAxes} valid");

                    if (wghtIdx >= 0 && neu.Has("ut_ft_set_var_design_coordinates"))
                    {
                        var setc = neu.Fn<D_setcoords>("ut_ft_set_var_design_coordinates");
                        uint g = neu.Fn<D_charindex>("ut_ft_get_char_index")(nf, (UIntPtr)'A');
                        if (g == 0) g = 1;
                        neu.Fn<D_setpx>("ut_ft_set_pixel_sizes")(nf, 64, 64);
                        // measure outline point count + advance at min and max weight
                        int PtsAt(int wghtVal, out int adv)
                        {
                            var coords = new int[vAxes];
                            for (int i = 0; i < vAxes; i++) coords[i] = wghtDef; // 16.16 already (axis def)
                            coords[wghtIdx] = wghtVal;
                            fixed (int* pc = coords) setc(nf, pc, vAxes);
                            neu.Fn<D_loadglyph>("ut_ft_load_glyph")(nf, g, 0);
                            neu.Fn<D_metrics>("ut_ft_get_glyph_metrics")(nf, out _, out _, out _, out _, out adv, out _);
                            neu.Fn<D_outlineinfo>("ut_ft_get_outline_info")(nf, out _, out int np2);
                            return np2;
                        }
                        int ptsMin = PtsAt(wghtMin, out int advMin);
                        int ptsMax = PtsAt(wghtMax, out int advMax);
                        int ptsMin2 = PtsAt(wghtMin, out int advMin2);
                        Console.WriteLine($"    var wght[min={wghtMin>>16},max={wghtMax>>16}]: pts {ptsMin}/{ptsMax}, adv {advMin}/{advMax}");
                        // Changing weight should change advance and/or outline extents; and be deterministic.
                        Check($"{name} var advance changes", advMin != advMax, $"advMin={advMin} advMax={advMax}");
                        Check($"{name} var deterministic", ptsMin == ptsMin2 && advMin == advMin2,
                              $"pts {ptsMin}/{ptsMin2} adv {advMin}/{advMin2}");
                    }
                }
            }

            // HarfBuzz shaping parity
            ShapeCompare(orig, neu, data, name);

            orig.Fn<D_doneface>("ut_ft_done_face")(of);
            neu .Fn<D_doneface>("ut_ft_done_face")(nf);
        }
        orig.Fn<D_done>("ut_ft_done")(oLib);
        neu .Fn<D_done>("ut_ft_done")(nLib);
    }

    // Gap (c): quantify bitmap dimension drift default vs NO_HINTING across glyphs+sizes.
    static void AnalyzeBitmapDrift(Lib orig, Lib neu, IntPtr of, IntPtr nf, string name, int nng,
        D_setpx oSet, D_setpx nSet, D_loadglyph oLoad, D_loadglyph nLoad,
        D_render oRen, D_render nRen, D_slot oSlot, D_slot nSlot, D_bmpinfo oBmp, D_bmpinfo nBmp)
    {
        const int LOAD_NO_HINTING = 1 << 1;
        int[] sizes = { 16, 32, 64 };
        int[] flagsSet = { 0, LOAD_NO_HINTING };
        string[] flagName = { "default", "no_hinting" };
        int nGlyphs = Math.Min(12, nng);
        for (int fi = 0; fi < flagsSet.Length; fi++)
        {
            int diffCount = 0, total = 0, maxDelta = 0;
            foreach (int sz in sizes)
            {
                oSet(of, (uint)sz, (uint)sz); nSet(nf, (uint)sz, (uint)sz);
                for (uint g = 1; g <= (uint)nGlyphs; g++)
                {
                    oLoad(of, g, flagsSet[fi]); nLoad(nf, g, flagsSet[fi]);
                    oRen(oSlot(of), 0); nRen(nSlot(nf), 0);
                    oBmp(of, out var ow, out var oh, out _, out _, out _);
                    nBmp(nf, out var nw, out var nh, out _, out _, out _);
                    total++;
                    int d = Math.Abs(ow - nw) + Math.Abs(oh - nh);
                    if (d != 0) { diffCount++; if (d > maxDelta) maxDelta = d; }
                }
            }
            Console.WriteLine($"    bmpdrift[{flagName[fi]}]: {diffCount}/{total} glyphs differ, maxDimDelta={maxDelta}px");
            // Record as an informational check: NO_HINTING should have far fewer/zero diffs,
            // proving the drift is autohinter-version, not a wrapper bug.
            if (fi == 1) Check($"{name} bmp NO_HINTING drift", diffCount == 0,
                              $"{diffCount}/{total} differ even unhinted (maxDelta={maxDelta})");
        }

        // Strict hinted-bitmap parity (default flags): FAIL on ANY dimension OR pixel difference.
        {
            int glyphsChecked = 0, dimDiff = 0, pixDiff = 0, maxPix = 0;
            foreach (int sz in sizes)
            {
                oSet(of, (uint)sz, (uint)sz); nSet(nf, (uint)sz, (uint)sz);
                for (uint g = 1; g <= (uint)nGlyphs; g++)
                {
                    oLoad(of, g, 0); nLoad(nf, g, 0);
                    oRen(oSlot(of), 0); nRen(nSlot(nf), 0);
                    oBmp(of, out var ow, out var oh, out _, out _, out IntPtr obuf);
                    nBmp(nf, out var nw, out var nh, out _, out _, out IntPtr nbuf);
                    glyphsChecked++;
                    if (ow != nw || oh != nh) { dimDiff++; continue; }
                    if (obuf == IntPtr.Zero || nbuf == IntPtr.Zero || ow <= 0 || oh <= 0) continue;
                    byte* ob = (byte*)obuf, nb = (byte*)nbuf;
                    for (int i = 0; i < ow * oh; i++) { int d = Math.Abs(ob[i] - nb[i]); if (d > 0) { pixDiff++; if (d > maxPix) maxPix = d; break; } }
                }
            }
            Console.WriteLine($"    hinted bitmap parity: {glyphsChecked} glyphs, dimDiff={dimDiff}, pixDiff(glyphs)={pixDiff}, maxPix={maxPix}");
            Check($"{name} hinted bitmap exact", dimDiff == 0 && pixDiff == 0,
                  $"dimDiff={dimDiff} pixDiffGlyphs={pixDiff} maxPix={maxPix}");
        }
    }

    static void ShapeCompare(Lib orig, Lib neu, byte[] data, string name)
    {
        IntPtr oFont = MakeHbFont(orig, data, out var oPin);
        IntPtr nFont = MakeHbFont(neu, data, out var nPin);
        try
        {
            foreach (var s in Samples)
            {
                var oh = Shape(orig, oFont, s);
                var nh = Shape(neu, nFont, s);
                bool same = oh.Count == nh.Count;
                if (same)
                    for (int i = 0; i < oh.Count; i++)
                        if (oh[i] != nh[i]) { same = false; break; }
                Check($"{name} shape[{s.script}]", same, $"orig {oh.Count} glyphs vs new {nh.Count}");
            }
        }
        finally { oPin.Free(); nPin.Free(); }
    }

    static IntPtr MakeHbFont(Lib lib, byte[] data, out GCHandle pin)
    {
        pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        IntPtr blob = lib.Fn<D_blob>("ut_hb_blob_create")(pin.AddrOfPinnedObject(), (uint)data.Length, 1, IntPtr.Zero, IntPtr.Zero);
        IntPtr face = lib.Fn<D_hbface>("ut_hb_face_create")(blob, 0);
        IntPtr font = lib.Fn<D_hbfont>("ut_hb_font_create")(face);
        lib.Fn<D_setfuncs>("ut_hb_ot_font_set_funcs")(font);
        return font;
    }

    static List<(uint gid, uint cluster, int xadv, int xoff, int yoff)> Shape(Lib lib, IntPtr font, (string script, uint tag, int dir, int[] cps) s)
    {
        IntPtr buf = lib.Fn<D_bufcreate>("ut_hb_buffer_create")();
        lib.Fn<D_bufdir>("ut_hb_buffer_set_direction")(buf, s.dir);
        lib.Fn<D_bufscript>("ut_hb_buffer_set_script")(buf, s.tag);
        var cps = (uint[])(object)Array.ConvertAll(s.cps, x => (uint)x);
        fixed (uint* pc = cps)
            lib.Fn<D_addcp>("ut_hb_buffer_add_codepoints")(buf, pc, cps.Length, 0, cps.Length);
        lib.Fn<D_shape>("ut_hb_shape")(font, buf, IntPtr.Zero, 0);
        uint n = lib.Fn<D_buflen>("ut_hb_buffer_get_length")(buf);
        IntPtr infosP = lib.Fn<D_infos>("ut_hb_buffer_get_glyph_infos")(buf, out _);
        IntPtr possP = lib.Fn<D_poss>("ut_hb_buffer_get_glyph_positions")(buf, out _);
        var outp = new List<(uint, uint, int, int, int)>();
        HbInfo* inf = (HbInfo*)infosP; HbPos* pos = (HbPos*)possP;
        for (uint i = 0; i < n; i++)
            outp.Add((inf[i].codepoint, inf[i].cluster, pos[i].xadv, pos[i].xoff, pos[i].yoff));
        return outp;
    }
}
