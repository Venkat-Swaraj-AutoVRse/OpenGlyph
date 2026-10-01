// msdfbench — times OpenGlyph MSDF generation (MsdfBuilder.Build) over a glyph set, median of
// several runs, for the perf comparison old (9e4af98) vs new generator. Loads real FreeType
// outlines via the native ut_ft_* exports at ppem 64 spread 6 (production framing). Compiles the
// live Runtime/FontCore/Msdf sources — point the csproj at the OLD or NEW tree to compare.
//
// Usage: msdfbench <nativeDll> <font.ttf> [runs]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using LightSide.Msdf;

static class MsdfBench
{
    const int Ppem = 64, Spread = 6;
    const double Angle = 3.0;
    const int LOAD_NO_HINTING = 2;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_init(out IntPtr lib);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] unsafe delegate int D_newface(IntPtr lib, byte* d, IntPtr sz, IntPtr idx, out IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_setpx(IntPtr face, uint w, uint h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_charindex(IntPtr face, UIntPtr cp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_loadglyph(IntPtr face, uint gid, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] unsafe delegate int D_outlinedata(IntPtr face, int* xy, byte* tags, short* ends, int pcap, int ccap, out int np, out int nc, out int flags);
    static IntPtr _h;
    static T Fn<T>(string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_h, n));

    static unsafe GlyphOutline GetOutline(IntPtr face, uint gid)
    {
        if (Fn<D_setpx>("ut_ft_set_pixel_sizes")(face, Ppem, Ppem) != 0) return null;
        if (Fn<D_loadglyph>("ut_ft_load_glyph")(face, gid, LOAD_NO_HINTING) != 0) return null;
        int cap = 2048, ccap = 128;
        var xy = new int[cap * 2]; var tags = new byte[cap]; var ends = new short[ccap];
        var dget = Fn<D_outlinedata>("ut_ft_get_outline_data");
        int rc, np, nc, flags;
        fixed (int* pxy = xy) fixed (byte* pt = tags) fixed (short* pe = ends)
            rc = dget(face, pxy, pt, pe, cap, ccap, out np, out nc, out flags);
        if (rc == 0 || np <= 0 || nc <= 0) return null;
        var o = new GlyphOutline(nc) { ReverseFill = (flags & 0x4) != 0 };
        int start = 0;
        for (int c = 0; c < nc; c++)
        {
            int end = ends[c]; int count = end - start + 1;
            if (count <= 0) { start = end + 1; continue; }
            var contour = new OutlineContour(count);
            for (int i = start; i <= end; i++)
            {
                byte tag = tags[i];
                var t = (tag & 0x1) != 0 ? OutlinePointTag.OnCurve : ((tag & 0x2) != 0 ? OutlinePointTag.CubicControl : OutlinePointTag.QuadraticControl);
                contour.Add(new OutlinePoint(xy[i * 2] / 64.0, xy[i * 2 + 1] / 64.0), t);
            }
            o.Contours.Add(contour); start = end + 1;
        }
        return o;
    }

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: msdfbench <nativeDll> <font.ttf> [runs]"); return 2; }
        int runs = args.Length > 2 ? int.Parse(args[2]) : 7;
        _h = NativeLibrary.Load(args[0]);
        Fn<D_init>("ut_ft_init")(out IntPtr lib);
        byte[] data = File.ReadAllBytes(args[1]);
        IntPtr face;
        unsafe { fixed (byte* p = data) Fn<D_newface>("ut_ft_new_memory_face")(lib, p, (IntPtr)data.Length, IntPtr.Zero, out face); }

        var sets = new (string name, List<char> chars)[]
        {
            ("9 test glyphs", new List<char> { 'A','M','W','N','k','x','&','g','@' }),
            ("95 printable ASCII", AsciiPrintable()),
        };

        // Pre-fetch outlines once (not timed — we time generation, not FreeType).
        foreach (var (name, chars) in sets)
        {
            var outlines = new List<GlyphOutline>();
            foreach (char ch in chars)
            {
                uint gid = Fn<D_charindex>("ut_ft_get_char_index")(face, (UIntPtr)ch);
                if (gid == 0) continue;
                var o = GetOutline(face, gid);
                if (o != null && !o.IsEmpty) outlines.Add(o);
            }
            // Warm up (JIT + atlas-independent).
            foreach (var o in outlines) { var _ = MsdfBuilder.Build(o, Spread, Angle, errorCorrection: true); }

            var times = new List<double>();
            var sw = new Stopwatch();
            for (int r = 0; r < runs; r++)
            {
                sw.Restart();
                foreach (var o in outlines) { var res = MsdfBuilder.Build(o, Spread, Angle, errorCorrection: true); }
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double median = times[times.Count / 2];
            double perGlyph = median / Math.Max(1, outlines.Count);
            Console.WriteLine($"{name}: {outlines.Count} glyphs, median batch {median:F2} ms over {runs} runs, {perGlyph:F3} ms/glyph");
        }
        return 0;
    }

    static List<char> AsciiPrintable()
    {
        var l = new List<char>();
        for (int c = 0x21; c <= 0x7E; c++) l.Add((char)c); // '!'..'~' = 94; add space handled as empty→skipped
        l.Add(' ');
        return l;
    }
}
