// arabicprobe — list hinted-bitmap glyphs that differ between orig and new, with gid,
// codepoint, dim/pixel deltas, at 16/32/64px. Tests new under force-autohint (default) and
// force+TARGET_LIGHT. Usage: arabicprobe <origDll> <newDll> <font.ttf>
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

unsafe class ArabicProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_init(out IntPtr lib);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_newface(IntPtr lib, byte* d, IntPtr sz, IntPtr idx, out IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_setpx(IntPtr face, uint w, uint h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_charindex(IntPtr face, UIntPtr cp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_loadglyph(IntPtr face, uint gid, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_render(IntPtr slot, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_slot(IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_bmpinfo(IntPtr face, out int w, out int h, out int pitch, out int pm, out IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_faceinfo(IntPtr face, out long ff, out int ng, out int upem, out int nfs, out int nf, out int fi, out short a, out short d, out short h);

    class Lib { public IntPtr H; public Lib(string p){H=NativeLibrary.Load(p);} public T Fn<T>(string n) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(H,n)); }

    static int Main(string[] a)
    {
        var data = File.ReadAllBytes(a[2]);
        var o = new Lib(a[0]); var n = new Lib(a[1]);
        o.Fn<D_init>("ut_ft_init")(out var oLib); n.Fn<D_init>("ut_ft_init")(out var nLib);
        // build gid->codepoint map (first cp that maps to each gid) via reverse cmap scan
        var gid2cp = new Dictionary<uint,int>();
        fixed (byte* p = data)
        {
            o.Fn<D_newface>("ut_ft_new_memory_face")(oLib,p,(IntPtr)data.Length,IntPtr.Zero,out var of0);
            var ci = o.Fn<D_charindex>("ut_ft_get_char_index");
            for (int cp=0x20; cp<0x10000; cp++){uint g=ci(of0,(UIntPtr)cp); if(g!=0 && !gid2cp.ContainsKey(g)) gid2cp[g]=cp;}

            n.Fn<D_newface>("ut_ft_new_memory_face")(nLib,p,(IntPtr)data.Length,IntPtr.Zero,out var of);
            n.Fn<D_newface>("ut_ft_new_memory_face")(nLib,p,(IntPtr)data.Length,IntPtr.Zero,out var nf);
            o.Fn<D_newface>("ut_ft_new_memory_face")(oLib,p,(IntPtr)data.Length,IntPtr.Zero,out var ofc);
            o.Fn<D_faceinfo>("ut_ft_get_face_info")(ofc, out _, out int ng, out _,out _,out _,out _,out _,out _,out _);

            var oLoad=o.Fn<D_loadglyph>("ut_ft_load_glyph"); var nLoad=n.Fn<D_loadglyph>("ut_ft_load_glyph");
            var oRen=o.Fn<D_render>("ut_ft_render_glyph"); var nRen=n.Fn<D_render>("ut_ft_render_glyph");
            var oSlot=o.Fn<D_slot>("ut_ft_get_glyph_slot"); var nSlot=n.Fn<D_slot>("ut_ft_get_glyph_slot");
            var oBmp=o.Fn<D_bmpinfo>("ut_ft_get_bitmap_info"); var nBmp=n.Fn<D_bmpinfo>("ut_ft_get_bitmap_info");
            var oSet=o.Fn<D_setpx>("ut_ft_set_pixel_sizes"); var nSet=n.Fn<D_setpx>("ut_ft_set_pixel_sizes");
            const int LIGHT=1<<16; // FT_LOAD_TARGET_LIGHT

            foreach (int flags in new[]{0, LIGHT})
            {
                Console.WriteLine($"=== new flags = {(flags==0?"force-autohint (wrapper default)":"force-autohint + TARGET_LIGHT")} ===");
                int totalDiff=0;
                foreach (int sz in new[]{16,32,64})
                {
                    oSet(ofc,(uint)sz,(uint)sz); nSet(nf,(uint)sz,(uint)sz);
                    for (uint g=1; g<(uint)Math.Min(ng,400); g++)
                    {
                        oLoad(ofc,g,0); nLoad(nf,g,flags);
                        oRen(oSlot(ofc),0); nRen(nSlot(nf),0);
                        oBmp(ofc,out int ow,out int oh,out int op,out _,out IntPtr ob);
                        nBmp(nf,out int nw,out int nh,out int np,out _,out IntPtr nb);
                        int dimD = Math.Abs(ow-nw)+Math.Abs(oh-nh);
                        int pixMax=0;
                        if(dimD==0 && ow>0 && oh>0 && ob!=IntPtr.Zero && nb!=IntPtr.Zero){byte* obb=(byte*)ob,nbb=(byte*)nb;for(int y=0;y<oh;y++)for(int x=0;x<ow;x++){int d=Math.Abs(obb[y*op+x]-nbb[y*np+x]);if(d>pixMax)pixMax=d;}}
                        if(dimD!=0 || pixMax>0){ totalDiff++; int cp=gid2cp.TryGetValue(g,out int c)?c:-1;
                            if(totalDiff<=20) Console.WriteLine($"  gid {g} U+{(cp>=0?cp.ToString("X4"):"????")} sz{sz}: orig {ow}x{oh} new {nw}x{nh} dimDelta={dimD} pixMax={pixMax}"); }
                    }
                }
                Console.WriteLine($"  total differing (glyph,size) pairs: {totalDiff}");
            }
        }
        return 0;
    }
}
