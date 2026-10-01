// glyphref — managed half of the OpenGlyph↔msdfgen MSDF parity test.
//
// For each of the glyphs A M W N k x & g @ at ppem 64, spread 6 (pxRange 12) — the exact set
// and framing the W-streak gate (MsdfArtifactTests) uses — glyphref:
//   1. Loads the glyph's REAL FreeType outline via the native ut_ft_* exports (same binary the
//      Unity package ships), scaled to ppem 64, LOAD_NO_HINTING, 26.6→whole-pixel y-up — the
//      exact transform FreeTypeOutlineSource applies.
//   2. Runs the production managed pipeline: Shape.FromOutline → OrientContours →
//      EdgeColoring.ColorSimple(shape, 3.0, 0) → MsdfBuilder/MsdfGenerator, producing OpenGlyph's
//      RAW field (error correction off) and CORRECTED field (on).
//   3. Serialises the SAME coloured, oriented shape to an msdfgen shape-description file (with
//      per-edge colour tokens) and the identical framing (w,h,range,scale,translate).
//   4. Invokes msdfref.exe TWICE: once consuming OpenGlyph's colours from the file (field parity),
//      once re-colouring with msdfgen's own edgeColoringSimple (independent colour-parity proof).
//   5. Compares PER TEXEL: max and mean |Δ| per channel, raw and corrected; verifies per-edge
//      colours equal between OpenGlyph and msdfgen's own colouring.
//
// Prints a table and writes PARITY_RESULTS_MSDF.md next to the harness output dir.
//
// Usage: glyphref <nativeDll> <font.ttf> <msdfrefExe> <outDir>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LightSide.Msdf;

static class GlyphRef
{
    const int Ppem = 64;
    const int Spread = 6;              // pxRange = 2*spread = 12 (matches MsdfArtifactTests)
    const double AngleThreshold = 3.0; // EdgeColoring.DefaultAngleThreshold
    const int LOAD_NO_HINTING = 2;     // FT_LOAD_NO_HINTING
    static readonly char[] Glyphs = { 'A', 'M', 'W', 'N', 'k', 'x', '&', 'g', '@' };

    // ---- Native P/Invoke (resolved dynamically from the given DLL) --------------------------
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
        if (Fn<D_setpx>("ut_ft_set_pixel_sizes")(face, (uint)Ppem, (uint)Ppem) != 0) return null;
        if (Fn<D_loadglyph>("ut_ft_load_glyph")(face, gid, LOAD_NO_HINTING) != 0) return null;

        int cap = 1024, ccap = 64;
        var xy = new int[cap * 2]; var tags = new byte[cap]; var ends = new short[ccap];
        int np, nc, flags;
        var dget = Fn<D_outlinedata>("ut_ft_get_outline_data");
        int rc;
        fixed (int* pxy = xy) fixed (byte* pt = tags) fixed (short* pe = ends)
            rc = dget(face, pxy, pt, pe, cap, ccap, out np, out nc, out flags);
        if (rc == 0 && (np > cap || nc > ccap))
        {
            cap = Math.Max(np, cap); ccap = Math.Max(nc, ccap);
            xy = new int[cap * 2]; tags = new byte[cap]; ends = new short[ccap];
            fixed (int* pxy = xy) fixed (byte* pt = tags) fixed (short* pe = ends)
                rc = dget(face, pxy, pt, pe, cap, ccap, out np, out nc, out flags);
        }
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
                double x = xy[i * 2] / 64.0, y = xy[i * 2 + 1] / 64.0;
                contour.Add(new OutlinePoint(x, y), DecodeTag(tags[i]));
            }
            o.Contours.Add(contour);
            start = end + 1;
        }
        return o;
    }

    static OutlinePointTag DecodeTag(byte tag)
    {
        if ((tag & 0x1) != 0) return OutlinePointTag.OnCurve;
        return (tag & 0x2) != 0 ? OutlinePointTag.CubicControl : OutlinePointTag.QuadraticControl;
    }

    // ---- Shape → msdfgen shape-description (bottom-up, y-up, with per-edge colour tokens) ----
    static char ColorToken(EdgeColor c) => c switch
    {
        EdgeColor.Cyan => 'c',
        EdgeColor.Magenta => 'm',
        EdgeColor.Yellow => 'y',
        EdgeColor.White => 'w',
        EdgeColor.Red => 'w',    // msdfgen writer only emits c/m/y/w; non-standard colours cannot
        EdgeColor.Green => 'w',  // round-trip through the text format, but after edgeColoringSimple
        EdgeColor.Blue => 'w',   // every edge is CYAN/MAGENTA/YELLOW/WHITE, so these never occur.
        _ => 'w',
    };

    static string Coord(Vector2D p) =>
        p.X.ToString("R", CultureInfo.InvariantCulture) + ", " + p.Y.ToString("R", CultureInfo.InvariantCulture);

    static string SerializeShape(Shape shape)
    {
        var sb = new StringBuilder();
        foreach (var contour in shape.Contours)
        {
            if (contour.Edges.Count == 0) continue;
            sb.Append("{\n");
            foreach (var e in contour.Edges)
            {
                char col = ColorToken(e.Color);
                // Each edge: its start point, then the colour token, then control points for curves.
                // The next edge's start point is this edge's endpoint (contour is closed).
                sb.Append('\t').Append(Coord(e.Point0)).Append(";\n");
                sb.Append("\t\t").Append(col);
                if (e is QuadraticSegment q)
                    sb.Append('(').Append(Coord(q.Control)).Append(')');
                else if (e is CubicSegment cu)
                    sb.Append('(').Append(Coord(cu.Control1)).Append("; ").Append(Coord(cu.Control2)).Append(')');
                sb.Append(";\n");
            }
            sb.Append("\t#\n}\n");
        }
        return sb.ToString();
    }

    // Per-edge colours of OpenGlyph's own colouring, in the SAME (contour,edge) iteration order
    // msdfref.dumpColors uses, so the two files line up for comparison.
    static List<(int c, int e, int color)> ColorList(Shape shape)
    {
        var list = new List<(int, int, int)>();
        int ci = 0;
        foreach (var contour in shape.Contours)
        {
            int ei = 0;
            foreach (var edge in contour.Edges) { list.Add((ci, ei, (int)edge.Color)); ei++; }
            ci++;
        }
        return list;
    }

    static float[] ReadField(string path, out int w, out int h, out int n)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        w = br.ReadInt32(); h = br.ReadInt32(); n = br.ReadInt32();
        var f = new float[(long)w * h * n];
        for (long i = 0; i < f.Length; i++) f[i] = br.ReadSingle();
        return f;
    }

    struct Diff { public float maxR, maxG, maxB, meanR, meanG, meanB; }

    static Diff Compare(float[] a, float[] b, int w, int h)
    {
        double sr = 0, sg = 0, sb = 0; float mr = 0, mg = 0, mbx = 0;
        int px = w * h;
        for (int i = 0; i < px; i++)
        {
            int o = i * 3;
            float dr = Math.Abs(a[o] - b[o]), dg = Math.Abs(a[o + 1] - b[o + 1]), db = Math.Abs(a[o + 2] - b[o + 2]);
            sr += dr; sg += dg; sb += db;
            if (dr > mr) mr = dr; if (dg > mg) mg = dg; if (db > mbx) mbx = db;
        }
        return new Diff { maxR = mr, maxG = mg, maxB = mbx, meanR = (float)(sr / px), meanG = (float)(sg / px), meanB = (float)(sb / px) };
    }

    static int Main(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: glyphref <nativeDll> <font.ttf> <msdfrefExe> <outDir>"); return 2; }
        string dll = args[0], font = args[1], msdfref = args[2], outDir = args[3];
        Directory.CreateDirectory(outDir);

        _h = NativeLibrary.Load(dll);
        Fn<D_init>("ut_ft_init")(out IntPtr lib);
        byte[] data = File.ReadAllBytes(font);
        IntPtr face;
        unsafe { fixed (byte* p = data) Fn<D_newface>("ut_ft_new_memory_face")(lib, p, (IntPtr)data.Length, IntPtr.Zero, out face); }

        var md = new StringBuilder();
        md.AppendLine("# MSDF parity vs upstream msdfgen v1.12 (85e8b3d)");
        md.AppendLine();
        md.AppendLine($"Font: `{Path.GetFileName(font)}`  ppem={Ppem} spread={Spread} (pxRange={2 * Spread}) angle={AngleThreshold} colouring=edgeColoringSimple(seed 0)");
        md.AppendLine();
        md.AppendLine("Per-texel |Δ| between OpenGlyph and msdfgen fields (channel values in [0,1]; 1/255≈0.0039).");
        md.AppendLine();
        md.AppendLine("| glyph | WxH | colours eq | RAW max | RAW mean | CORRECTED(no-dist) max | CORRECTED(no-dist) mean | CORR default max |");
        md.AppendLine("|-------|-----|-----------|---------|----------|---------------|----------------|------|");

        Console.WriteLine($"{"gly",-4} {"WxH",-9} {"colEq",-6} {"rawMax",-8} {"rawMean",-9} {"corrMax",-9} {"corrMean",-9}");
        int failures = 0;

        foreach (char ch in Glyphs)
        {
            uint gid = Fn<D_charindex>("ut_ft_get_char_index")(face, (UIntPtr)ch);
            if (gid == 0) { Console.WriteLine($"{ch,-4} (no glyph)"); md.AppendLine($"| {ch} | — | — | — | — | — | — |"); continue; }
            var outline = GetOutline(face, gid);
            if (outline == null || outline.IsEmpty) { Console.WriteLine($"{ch,-4} (empty outline)"); md.AppendLine($"| {ch} | — | — | — | — | — | — |"); continue; }

            // OpenGlyph fields (production path).
            var rawRes = MsdfBuilder.Build(outline, Spread, AngleThreshold, errorCorrection: false);
            var corrRes = MsdfBuilder.Build(outline, Spread, AngleThreshold, errorCorrection: true);
            int w = rawRes.Width, h = rawRes.Height;

            // Rebuild the coloured+oriented shape exactly as MsdfBuilder did, to serialise + list colours.
            var shape = Shape.FromOutline(outline);
            shape.OrientContours();
            EdgeColoring.ColorSimple(shape, AngleThreshold, 0);

            // Framing params identical to MsdfBuilder.
            shape.Bounds(out double l, out double b, out double r, out double t);
            int gx0 = (int)Math.Floor(l), gy0 = (int)Math.Floor(b);
            double tx = Spread - gx0, ty = Spread - gy0, range = 2.0 * Spread;

            string pfxOur = Path.Combine(outDir, ch == '@' ? "at" : ch == '&' ? "amp" : ch.ToString());
            // Write OpenGlyph fields (bottom-up float[w*h*3]).
            WriteFieldF32(pfxOur + ".our_raw.f32", rawRes.Field, w, h);
            WriteFieldF32(pfxOur + ".our_corrected.f32", corrRes.Field, w, h);

            // Serialise shape + OpenGlyph colours.
            string shapePath = pfxOur + ".shape.txt";
            File.WriteAllText(shapePath, SerializeShape(shape));
            var ourColors = ColorList(shape);

            // msdfref pass 1: use the colours in the file (field parity).
            RunMsdfref(msdfref, shapePath, w, h, range, tx, ty, pfxOur + ".ref", recolor: false);
            // msdfref pass 2: re-colour with msdfgen's own edgeColoringSimple (colour parity).
            RunMsdfref(msdfref, shapePath, w, h, range, tx, ty, pfxOur + ".refrecolor", recolor: true);

            var refRaw = ReadField(pfxOur + ".ref.raw.f32", out _, out _, out _);
            var refCorr = ReadField(pfxOur + ".ref.corrected.f32", out _, out _, out _);
            var refCorrNoDist = ReadField(pfxOur + ".ref.corrected_nodist.f32", out _, out _, out _);

            var dRaw = Compare(rawRes.Field, refRaw, w, h);
            // The C# port implements the DO_NOT_CHECK_DISTANCE classifier path, so the apples-to-apples
            // corrected reference is msdfgen's own DO_NOT_CHECK_DISTANCE output. We also report the
            // delta vs msdfgen's shipping default (CHECK_DISTANCE_AT_EDGE) for context.
            var dCorr = Compare(corrRes.Field, refCorrNoDist, w, h);
            var dCorrDefault = Compare(corrRes.Field, refCorr, w, h);

            // Colour parity: compare OpenGlyph colours vs msdfgen's own colouring of the SAME shape.
            bool colEq = CompareColors(ourColors, pfxOur + ".refrecolor.msdfgen_colors.txt", out int nEdges, out int nDiff);

            Console.WriteLine($"{ch,-4} {w}x{h,-6} {(colEq ? "yes" : nDiff + "/" + nEdges),-6} {dRaw.maxR.ToString("F5"),-8} {dRaw.meanR.ToString("F6"),-9} {dCorr.maxR.ToString("F5"),-9} {dCorr.meanR.ToString("F6"),-9}");
            md.AppendLine($"| {ch} | {w}x{h} | {(colEq ? "yes" : $"{nDiff}/{nEdges}")} | R{dRaw.maxR:F5} G{dRaw.maxG:F5} B{dRaw.maxB:F5} | R{dRaw.meanR:F6} G{dRaw.meanG:F6} B{dRaw.meanB:F6} | R{dCorr.maxR:F5} G{dCorr.maxG:F5} B{dCorr.maxB:F5} | R{dCorr.meanR:F6} G{dCorr.meanG:F6} B{dCorr.meanB:F6} | R{dCorrDefault.maxR:F4} |");

            // Raw target: identical within 1e-4 per channel (task). Track for the summary.
            float rawWorst = Math.Max(dRaw.maxR, Math.Max(dRaw.maxG, dRaw.maxB));
            if (rawWorst > 1e-4f) failures++;
            if (!colEq) failures++;
        }

        md.AppendLine();
        md.AppendLine(failures == 0
            ? "RAW parity within 1e-4 on every channel and per-edge colours equal for all glyphs."
            : $"{failures} glyph/colour checks exceed the raw-1e-4 / colour-equal bar (see rows above). Corrected-field tolerance is characterised, not yet gated.");
        string mdPath = Path.Combine(outDir, "PARITY_RESULTS_MSDF.md");
        File.WriteAllText(mdPath, md.ToString());
        Console.WriteLine($"\nWrote {mdPath}");
        return failures == 0 ? 0 : 1;
    }

    static void WriteFieldF32(string path, float[] field, int w, int h)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(w); bw.Write(h); bw.Write(3);
        for (int i = 0; i < w * h * 3; i++) bw.Write(field[i]);
    }

    static void RunMsdfref(string exe, string shape, int w, int h, double range, double tx, double ty, string outPfx, bool recolor)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(shape);
        psi.ArgumentList.Add(w.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(h.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(range.ToString("R", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("1"); psi.ArgumentList.Add("1"); // scaleX, scaleY
        psi.ArgumentList.Add(tx.ToString("R", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(ty.ToString("R", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(outPfx);
        if (recolor) psi.ArgumentList.Add(AngleThreshold.ToString("R", CultureInfo.InvariantCulture));
        var p = Process.Start(psi);
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new Exception($"msdfref failed ({p.ExitCode}): {p.StandardError.ReadToEnd()}");
    }

    static bool CompareColors(List<(int c, int e, int color)> ours, string msdfgenColorFile, out int nEdges, out int nDiff)
    {
        var theirs = new Dictionary<(int, int), int>();
        foreach (var line in File.ReadAllLines(msdfgenColorFile))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
                theirs[(int.Parse(parts[0]), int.Parse(parts[1]))] = int.Parse(parts[2]);
        }
        nEdges = ours.Count; nDiff = 0;
        foreach (var (c, e, color) in ours)
            if (!theirs.TryGetValue((c, e), out int tc) || tc != color) nDiff++;
        return nDiff == 0;
    }
}
