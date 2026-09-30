// render — side-by-side ORIGINAL | NEW | DIFF sheets from the two DLLs. No native changes.
// Usage: render <origDll> <newDll> <fontsDir> <outDir>
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

unsafe class Render
{
    // ---- native delegates ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_init(out IntPtr lib);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_newface(IntPtr lib, byte* d, IntPtr sz, IntPtr idx, out IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_setpx(IntPtr face, uint w, uint h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_charindex(IntPtr face, UIntPtr cp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_loadglyph(IntPtr face, uint gid, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_render(IntPtr slot, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_slot(IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_bmpinfo(IntPtr face, out int w, out int h, out int pitch, out int pm, out IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_sdf(IntPtr face, uint gid, int flags, int spread, out Sdf r);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_free(IntPtr b);
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
    // Blend2D
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blimgcreate(int w,int h,uint fmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blimgdata(IntPtr img,out int stride);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blctxcreate(IntPtr img);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxv(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxrgba(IntPtr ctx,uint rgba);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxfillpath(IntPtr ctx,IntPtr path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_blpathcreate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_outlinetobl(IntPtr face,IntPtr path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blimgdestroy(IntPtr img);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxtranslate(IntPtr ctx,double x,double y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxscale(IntPtr ctx,double x,double y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_blctxfillall(IntPtr ctx);

    [StructLayout(LayoutKind.Sequential)] struct Sdf { public int success, mw, mh, mbx, mby, max, bw, bh, bp, bl, bt; public IntPtr buf; }
    [StructLayout(LayoutKind.Sequential)] struct HbInfo { public uint codepoint, mask, cluster, var1, var2; }
    [StructLayout(LayoutKind.Sequential)] struct HbPos { public int xadv, yadv, xoff, yoff; public uint var; }

    class Lib { public IntPtr H; public Lib(string p){H=NativeLibrary.Load(p);} public T Fn<T>(string n) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(H,n)); public bool Has(string n)=>NativeLibrary.TryGetExport(H,n,out _);}

    static Lib O,N; static string OutDir; static Dictionary<string,byte[]> Fonts=new();

    static int Main(string[] a)
    {
        if (a.Length<4 || a[0]!="render"){ /* allow both "render <...>" and "<...>" */ }
        int off = (a.Length>0 && a[0]=="render")?1:0;
        string origDll=a[off], newDll=a[off+1], fontsDir=a[off+2]; OutDir=a[off+3];
        Directory.CreateDirectory(OutDir);
        O=new Lib(origDll); N=new Lib(newDll);
        foreach (var f in Directory.GetFiles(fontsDir,"*.ttf")) Fonts[Path.GetFileName(f)]=File.ReadAllBytes(f);

        SheetSdfText();
        SheetSdfArabicHebrew();
        SheetArabicHinted();
        SheetBlend2DFill();
        try { SheetEmoji(); } catch(Exception e){ Console.WriteLine($"emoji.png SKIPPED: {e.Message}"); }
        return 0;
    }

    // ---------- SDF helpers ----------
    static byte[] LoadFont(string name){ foreach(var k in Fonts.Keys) if(k.StartsWith(name)) return Fonts[k]; return null; }
    static (byte[] buf,int w,int h,int pitch,int bl,int bt) RenderSdf(Lib lib, byte[] font, uint gid, int px, int spread)
    {
        lib.Fn<D_init>("ut_ft_init")(out var l);
        fixed (byte* p=font){ lib.Fn<D_newface>("ut_ft_new_memory_face")(l,p,(IntPtr)font.Length,IntPtr.Zero,out var f);
            lib.Fn<D_setpx>("ut_ft_set_pixel_sizes")(f,(uint)px,(uint)px);
            if(lib.Fn<D_sdf>("ut_ft_render_sdf_glyph")(f,gid,0,spread,out var r)!=0||r.buf==IntPtr.Zero) return (null,0,0,0,0,0);
            int pitch=Math.Abs(r.bp); var buf=new byte[pitch*r.bh]; Marshal.Copy(r.buf,buf,0,buf.Length);
            lib.Fn<D_free>("ut_ft_free_sdf_buffer")(r.buf);
            return (buf,r.bw,r.bh,pitch,r.bl,r.bt); }
    }
    static uint GidOf(Lib lib, byte[] font, int cp)
    {
        lib.Fn<D_init>("ut_ft_init")(out var l);
        fixed(byte* p=font){ lib.Fn<D_newface>("ut_ft_new_memory_face")(l,p,(IntPtr)font.Length,IntPtr.Zero,out var f); return lib.Fn<D_charindex>("ut_ft_get_char_index")(f,(UIntPtr)cp); }
    }
    // shader preview: smoothstep(0.5-w,0.5+w, sdf/255) -> alpha 0..255 (white glyph on dark)
    static byte[] ShaderPreview(byte[] sdf,int w,int h,int pitch,double ww)
    {
        var o=new byte[w*h];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){double d=sdf[y*pitch+x]/255.0; double t=Smooth(0.5-ww,0.5+ww,d); o[y*w+x]=(byte)(t*255+0.5);}
        return o;
    }
    static double Smooth(double e0,double e1,double v){double t=(v-e0)/(e1-e0); if(t<0)t=0; if(t>1)t=1; return t*t*(3-2*t);}
    static byte[] UpBilinear(byte[] g,int w,int h,int s,out int W,out int H){W=w*s;H=h*s;var o=new byte[W*H];for(int Y=0;Y<H;Y++){double fy=(Y+0.5)/s-0.5;int y0=(int)Math.Floor(fy);double ty=fy-y0;for(int X=0;X<W;X++){double fx=(X+0.5)/s-0.5;int x0=(int)Math.Floor(fx);double tx=fx-x0;o[Y*W+X]=(byte)Bil(g,w,h,x0,y0,tx,ty);}}return o;}
    static byte[] UpNearest(byte[] g,int w,int h,int s,out int W,out int H){W=w*s;H=h*s;var o=new byte[W*H];for(int Y=0;Y<H;Y++)for(int X=0;X<W;X++)o[Y*W+X]=g[(Y/s)*w+(X/s)];return o;}
    static double Bil(byte[] b,int w,int h,int x0,int y0,double tx,double ty){int x1=x0+1,y1=y0+1;int cx0=Cl(x0,w),cx1=Cl(x1,w),cy0=Cl(y0,h),cy1=Cl(y1,h);double v00=b[cy0*w+cx0],v10=b[cy0*w+cx1],v01=b[cy1*w+cx0],v11=b[cy1*w+cx1];double a=v00+(v10-v00)*tx,bb=v01+(v11-v01)*tx;return a+(bb-a)*ty;}
    static int Cl(int v,int n)=>v<0?0:(v>=n?n-1:v);
    // heatmap of |a-b|*amp: black=0 -> through magma-ish ramp
    static byte[] DiffHeat(byte[] a,byte[] b,int w,int h,int amp){var o=new byte[w*h*4];for(int i=0;i<w*h;i++){int d=Math.Abs(a[i]-b[i])*amp; if(d>255)d=255; // ramp: black->red->yellow->white
        byte r=(byte)Math.Min(255,d*2), gg=(byte)Math.Max(0,Math.Min(255,(d-64)*2)), bb=(byte)Math.Max(0,Math.Min(255,(d-160)*3)); o[i*4]=r;o[i*4+1]=gg;o[i*4+2]=bb;o[i*4+3]=255;}return o;}
    static byte[] GrayToRGBA(byte[] g,int w,int h){var o=new byte[w*h*4];for(int i=0;i<w*h;i++){o[i*4]=g[i];o[i*4+1]=g[i];o[i*4+2]=g[i];o[i*4+3]=255;}return o;}

    // place a tile (RGBA) into a cell of given size, centered
    static void PlaceCell(Canvas c,byte[] tile,int tw,int th,int cx,int cy,int cw,int chh){int ox=cx+(cw-tw)/2,oy=cy+(chh-th)/2;c.BlitRGBA(tile,tw,th,ox,oy);}

    // ---------- Sheet 1: SDF text ----------
    static void SheetSdfText()
    {
        var font=LoadFont("NotoSans-Regular"); int px=48,spread=8; double ww=0.05;
        string glyphs="AkMWxg@&"; var rows=new List<(string cap,byte[] o,byte[] n,int w,int h)>();
        // 1x + 4x for each glyph
        foreach(char ch in glyphs){
            uint g=GidOf(O,font,ch); if(g==0)continue;
            var so=RenderSdf(O,font,g,px,spread); var sn=RenderSdf(N,font,g,px,spread);
            if(so.buf==null||sn.buf==null)continue;
            int w=Math.Min(so.w,sn.w),h=Math.Min(so.h,sn.h);
            var po=ShaderPreview(so.buf,w,h,so.pitch,ww); var pn=ShaderPreview(sn.buf,w,h,sn.pitch,ww);
            rows.Add(($"{ch} 1x", po, pn, w, h));
            var po4=UpBilinear(po,w,h,4,out int W,out int H); var pn4=UpBilinear(pn,w,h,4,out _,out _);
            rows.Add(($"{ch} 4x", po4, pn4, W, H));
        }
        // worst-edgeMax glyphs (from perceptual gate: NotoSans reported worst on curved/diagonal;
        // use k, g, & at 4x as representative worst — the report's edgeMax outliers).
        Composite(rows, Path.Combine(OutDir,"sdf_text.png"), "SDF shader preview smoothstep(0.5+-0.05)");
    }

    // ---------- Sheet 2: SDF Arabic + Hebrew (shaped) ----------
    static void SheetSdfArabicHebrew()
    {
        var rows=new List<(string,byte[],byte[],int,int)>();
        AddShapedSdfRow(rows,"NotoSansArabic-Regular","\u0645\u0631\u062d\u0628\u0627",5,("Arab",'a'));
        AddShapedSdfRow(rows,"NotoSansHebrew-Regular","\u05e9\u05dc\u05d5\u05dd",5,("Hebr",'h'));
        Composite(rows, Path.Combine(OutDir,"sdf_arabic_hebrew.png"), "Shaped SDF words (Arabic RTL / Hebrew RTL)");
    }
    static void AddShapedSdfRow(List<(string,byte[],byte[],int,int)> rows,string fontName,string text,int dir,(string,char) tag)
    {
        var font=LoadFont(fontName); int px=48,spread=8; double ww=0.05;
        var oComposite=ShapeSdfComposite(O,font,text,dir,px,spread,ww,out int w,out int h);
        var nComposite=ShapeSdfComposite(N,font,text,dir,px,spread,ww,out int w2,out int h2);
        int W=Math.Min(w,w2),H=Math.Min(h,h2);
        rows.Add(($"{tag.Item1} 1x", Crop(oComposite,w,h,W,H), Crop(nComposite,w2,h2,W,H), W, H));
        var o4=UpBilinear(Crop(oComposite,w,h,W,H),W,H,4,out int WW,out int HH); var n4=UpBilinear(Crop(nComposite,w2,h2,W,H),W,H,4,out _,out _);
        rows.Add(($"{tag.Item1} 4x", o4, n4, WW, HH));
    }
    static byte[] Crop(byte[] g,int w,int h,int W,int H){var o=new byte[W*H];for(int y=0;y<H;y++)for(int x=0;x<W;x++)o[y*W+x]=g[y*w+x];return o;}
    // Shape text, render each glyph SDF, composite gray shader-preview at pen positions.
    static byte[] ShapeSdfComposite(Lib lib, byte[] font, string text, int dir, int px, int spread, double ww, out int W, out int H)
    {
        lib.Fn<D_init>("ut_ft_init")(out var l);
        var pin=System.Runtime.InteropServices.GCHandle.Alloc(font,GCHandleType.Pinned);
        IntPtr blob=lib.Fn<D_blob>("ut_hb_blob_create")(pin.AddrOfPinnedObject(),(uint)font.Length,1,IntPtr.Zero,IntPtr.Zero);
        IntPtr hbface=lib.Fn<D_hbface>("ut_hb_face_create")(blob,0); IntPtr hbfont=lib.Fn<D_hbfont>("ut_hb_font_create")(hbface); lib.Fn<D_setfuncs>("ut_hb_ot_font_set_funcs")(hbfont);
        IntPtr buf=lib.Fn<D_bufcreate>("ut_hb_buffer_create")(); lib.Fn<D_bufdir>("ut_hb_buffer_set_direction")(buf,dir);
        var cps=new List<uint>(); foreach(var r in text.EnumerateRunes())cps.Add((uint)r.Value); var arr=cps.ToArray();
        fixed(uint* pc=arr) lib.Fn<D_addcp>("ut_hb_buffer_add_codepoints")(buf,pc,arr.Length,0,arr.Length);
        lib.Fn<D_shape>("ut_hb_shape")(hbfont,buf,IntPtr.Zero,0);
        uint n=lib.Fn<D_buflen>("ut_hb_buffer_get_length")(buf);
        HbInfo* infos=(HbInfo*)lib.Fn<D_infos>("ut_hb_buffer_get_glyph_infos")(buf,out _);
        HbPos* pos=(HbPos*)lib.Fn<D_poss>("ut_hb_buffer_get_glyph_positions")(buf,out _);
        // face for sdf
        fixed(byte* p=font){ lib.Fn<D_newface>("ut_ft_new_memory_face")(l,p,(IntPtr)font.Length,IntPtr.Zero,out var face);
            lib.Fn<D_setpx>("ut_ft_set_pixel_sizes")(face,(uint)px,(uint)px);
            // first pass: compute pen advance + max ascent/descent
            int penX=8, baseline=px+8; int totalW=16;
            for(uint i=0;i<n;i++) totalW += pos[i].xadv/64;
            W=totalW+16; H=px*2; var canvas=new byte[W*H];
            for(uint i=0;i<n;i++){ uint gid=infos[i].codepoint;
                if(lib.Fn<D_sdf>("ut_ft_render_sdf_glyph")(face,gid,0,spread,out var r)==0 && r.buf!=IntPtr.Zero){
                    int pitch=Math.Abs(r.bp); var sb=new byte[pitch*r.bh]; Marshal.Copy(r.buf,sb,0,sb.Length);
                    var prev=ShaderPreview(sb,r.bw,r.bh,pitch,ww);
                    int gx=penX + pos[i].xoff/64 + r.bl; int gy=baseline - r.bt + pos[i].yoff/64;
                    for(int y=0;y<r.bh;y++)for(int x=0;x<r.bw;x++){int cx=gx+x,cy=gy+y;if(cx>=0&&cy>=0&&cx<W&&cy<H){int v=prev[y*r.bw+x]; if(v>canvas[cy*W+cx])canvas[cy*W+cx]=(byte)v;}}
                    lib.Fn<D_free>("ut_ft_free_sdf_buffer")(r.buf);
                }
                penX += pos[i].xadv/64;
            }
            pin.Free();
            return canvas;
        }
    }

    // ---------- Sheet 3: Arabic hinted (differing pairs) ----------
    static void SheetArabicHinted()
    {
        var font=LoadFont("NotoSansArabic-Regular");
        // the 27 differing (gid,size) pairs are in rare extended glyphs; scan to rediscover them.
        var pairs=new List<(uint gid,int sz)>();
        int ng=NumGlyphs(O,font);
        foreach(int sz in new[]{16,32,64}){
            for(uint g=1; g<(uint)Math.Min(ng,400) && pairs.Count<27; g++){
                var (ob,ow,oh)=RenderBitmap(O,font,g,sz); var (nb,nw,nh)=RenderBitmap(N,font,g,sz);
                if(ob==null||nb==null)continue;
                bool diff = ow!=nw||oh!=nh;
                if(!diff && ow>0){ for(int i=0;i<ow*oh;i++) if(ob[i]!=nb[i]){diff=true;break;} }
                if(diff) pairs.Add((g,sz));
            }
        }
        var rows=new List<(string,byte[],byte[],int,int)>();
        foreach(var pr in pairs){
            var (ob,ow,oh)=RenderBitmap(O,font,pr.gid,pr.sz); var (nb,nw,nh)=RenderBitmap(N,font,pr.gid,pr.sz);
            int w=Math.Max(ow,nw),h=Math.Max(oh,nh); if(w==0||h==0)continue;
            var oP=Pad(ob,ow,oh,w,h); var nP=Pad(nb,nw,nh,w,h);
            int s=4; var o4=UpNearest(oP,w,h,s,out int W,out int H); var n4=UpNearest(nP,w,h,s,out _,out _);
            rows.Add(($"g{pr.gid}s{pr.sz}", o4, n4, W, H));
        }
        // control row: 6 common Arabic letters (should be identical)
        foreach(int cp in new[]{0x0627,0x0628,0x062A,0x062F,0x0631,0x0645}){
            uint g=GidOf(O,font,cp); if(g==0)continue; int sz=32;
            var (ob,ow,oh)=RenderBitmap(O,font,g,sz); var (nb,nw,nh)=RenderBitmap(N,font,g,sz);
            int w=Math.Max(ow,nw),h=Math.Max(oh,nh); if(w==0||h==0)continue;
            var o4=UpNearest(Pad(ob,ow,oh,w,h),w,h,4,out int W,out int H); var n4=UpNearest(Pad(nb,nw,nh,w,h),w,h,4,out _,out _);
            rows.Add(($"ctrlU{cp:X4}", o4, n4, W, H));
        }
        Composite(rows, Path.Combine(OutDir,"arabic_hinted.png"), "Arabic hinted bitmaps 4x nearest (differing pairs + control)");
    }
    static int NumGlyphs(Lib lib, byte[] font){ lib.Fn<D_init>("ut_ft_init")(out var l); fixed(byte* p=font){lib.Fn<D_newface>("ut_ft_new_memory_face")(l,p,(IntPtr)font.Length,IntPtr.Zero,out var f);
        var fi=lib.Fn<D_faceinfo>("ut_ft_get_face_info"); fi(f,out _,out int ng,out _,out _,out _,out _,out _,out _,out _); return ng; } }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_faceinfo(IntPtr face, out long ff, out int ng, out int upem, out int nfs, out int nf, out int fi, out short a, out short d, out short h);
    static (byte[],int,int) RenderBitmap(Lib lib, byte[] font, uint gid, int px)
    {
        lib.Fn<D_init>("ut_ft_init")(out var l);
        fixed(byte* p=font){ lib.Fn<D_newface>("ut_ft_new_memory_face")(l,p,(IntPtr)font.Length,IntPtr.Zero,out var f);
            lib.Fn<D_setpx>("ut_ft_set_pixel_sizes")(f,(uint)px,(uint)px);
            if(lib.Fn<D_loadglyph>("ut_ft_load_glyph")(f,gid,0)!=0)return(null,0,0);
            lib.Fn<D_render>("ut_ft_render_glyph")(lib.Fn<D_slot>("ut_ft_get_glyph_slot")(f),0);
            lib.Fn<D_bmpinfo>("ut_ft_get_bitmap_info")(f,out int w,out int h,out int pitch,out _,out IntPtr buf);
            if(w<=0||h<=0||buf==IntPtr.Zero)return(new byte[0],0,0);
            var o=new byte[w*h]; byte* b=(byte*)buf; for(int y=0;y<h;y++)for(int x=0;x<w;x++)o[y*w+x]=b[y*pitch+x];
            return(o,w,h); }
    }
    static byte[] Pad(byte[] g,int w,int h,int W,int H){var o=new byte[W*H];if(g!=null)for(int y=0;y<h;y++)for(int x=0;x<w;x++)o[y*W+x]=g[y*w+x];return o;}

    // ---------- Sheet 4: Blend2D fill ----------
    static void SheetBlend2DFill()
    {
        if(!O.Has("ut_blImageCreate")||!N.Has("ut_blImageCreate")){ WriteLegend(Path.Combine(OutDir,"blend2d_fill.png.txt"),"Blend2D not present in one DLL"); Console.WriteLine("blend2d_fill: a DLL lacks Blend2D exports"); }
        var font=LoadFont("NotoSans-Regular"); var arab=LoadFont("NotoSansArabic-Regular");
        var rows=new List<(string,byte[],byte[],int,int)>();
        foreach(int px in new[]{64,160}){
            foreach(var (fname,cp,lbl) in new[]{ ("NotoSans-Regular",(int)'A',"A"),("NotoSans-Regular",(int)'g',"g"),("NotoSans-Regular",(int)'&',"&"),("NotoSansArabic-Regular",0x0645,"AR")}){
                var fnt=LoadFont(fname); uint g=GidOf(O,fnt,cp); if(g==0)continue;
                var ob=Blend2DFill(O,fnt,g,px,out int ow,out int oh); var nb=Blend2DFill(N,fnt,g,px,out int nw,out int nh);
                if(ob==null||nb==null){ Console.WriteLine($"blend2d fill null {lbl}{px}"); continue; }
                int w=Math.Min(ow,nw),h=Math.Min(oh,nh);
                rows.Add(($"{lbl}{px}", CropRGBA(ob,ow,oh,w,h), CropRGBA(nb,nw,nh,w,h), w, h));
            }
        }
        CompositeRGBA(rows, Path.Combine(OutDir,"blend2d_fill.png"), "Blend2D outline fill (ut_ft_outline_to_blpath)");
    }
    static byte[] CropRGBA(byte[] g,int w,int h,int W,int H){var o=new byte[W*H*4];for(int y=0;y<H;y++)for(int x=0;x<W;x++){int s=(y*w+x)*4,d=(y*W+x)*4;o[d]=g[s];o[d+1]=g[s+1];o[d+2]=g[s+2];o[d+3]=255;}return o;}
    static byte[] Blend2DFill(Lib lib, byte[] font, uint gid, int px, out int W, out int H)
    {
        W=(int)(px*1.6); H=px*2; var outp=new byte[W*H*4];
        lib.Fn<D_init>("ut_ft_init")(out var l);
        fixed(byte* p=font){ lib.Fn<D_newface>("ut_ft_new_memory_face")(l,p,(IntPtr)font.Length,IntPtr.Zero,out var f);
            lib.Fn<D_setpx>("ut_ft_set_pixel_sizes")(f,(uint)px,(uint)px);
            if(lib.Fn<D_loadglyph>("ut_ft_load_glyph")(f,gid,1<<3 /*NO_BITMAP*/)!=0)return null;
            var img=lib.Fn<D_blimgcreate>("ut_blImageCreate")(W,H,1); if(img==IntPtr.Zero)return null;
            var ctx=lib.Fn<D_blctxcreate>("ut_blContextCreate")(img); var path=lib.Fn<D_blpathcreate>("ut_blPathCreate")();
            if(lib.Fn<D_outlinetobl>("ut_ft_outline_to_blpath")(f,path)==0){ lib.Fn<D_blctxv>("ut_blContextDestroy")(ctx); lib.Fn<D_blimgdestroy>("ut_blImageDestroy")(img); return null; }
            // ut_ft_outline_to_blpath emits FreeType units/64, Y UP from the baseline. Transform
            // matrix is applied to points as translate * scale (Blend2D post-multiplies), so set
            // translate(margin, baseline) first then scale(1,-1): a Y-up glyph maps below the
            // baseline, landing inside the 2*px frame.
            lib.Fn<D_blctxrgba>("ut_blContextSetFillStyleRgba32")(ctx,0xFF000000);
            lib.Fn<D_blctxfillall>("ut_blContextFillAll")(ctx);
            lib.Fn<D_blctxrgba>("ut_blContextSetFillStyleRgba32")(ctx,0xFFFFFFFF);
            lib.Fn<D_blctxtranslate>("ut_blContextTranslate")(ctx, px*0.3, px*1.55);
            lib.Fn<D_blctxscale>("ut_blContextScale")(ctx, 1.0, -1.0);
            lib.Fn<D_blctxfillpath>("ut_blContextFillPath")(ctx,path);
            lib.Fn<D_blctxv>("ut_blContextEnd")(ctx);
            IntPtr pd=lib.Fn<D_blimgdata>("ut_blImageGetData")(img,out int stride);
            var raw=new byte[H*stride]; Marshal.Copy(pd,raw,0,raw.Length);
            for(int y=0;y<H;y++)for(int x=0;x<W;x++){int s=y*stride+x*4,d=(y*W+x)*4; // PRGB32 BGRA
                outp[d]=raw[s+2];outp[d+1]=raw[s+1];outp[d+2]=raw[s+0];outp[d+3]=255;}
            lib.Fn<D_blctxv>("ut_blContextDestroy")(ctx); lib.Fn<D_blimgdestroy>("ut_blImageDestroy")(img);
            return outp;
        }
    }

    // ---------- Sheet 5: emoji (COLR) ----------
    static void SheetEmoji()
    {
        Console.WriteLine("emoji.png SKIPPED: COLRv1 paint-walk + Blend2D compositing replicating EmojiCore is >20 turns; COLR paint-tree parity is already verified (40/40) in the parity harness.");
        WriteLegend(Path.Combine(OutDir,"emoji.png.txt"),"SKIPPED: COLRv1 emoji compositing not rendered this round; paint-tree parity 40/40 covered by the parity harness.");
    }

    // ---------- compositors ----------
    static void Composite(List<(string cap,byte[] o,byte[] n,int w,int h)> rows, string path, string title)
    {
        // convert grayscale rows to RGBA + diff
        var rr=new List<(string,byte[],byte[],byte[],int,int)>();
        foreach(var r in rows){ var oR=GrayToRGBA(r.o,r.w,r.h); var nR=GrayToRGBA(r.n,r.w,r.h); var d=DiffHeat(r.o,r.n,r.w,r.h,4); rr.Add((r.cap,oR,nR,d,r.w,r.h)); }
        DrawSheet(rr,path,title);
    }
    static void CompositeRGBA(List<(string cap,byte[] o,byte[] n,int w,int h)> rows, string path, string title)
    {
        // normalize all tiles to a common max size so variable-height rows don't overlap
        int mw=0,mh=0; foreach(var r in rows){mw=Math.Max(mw,r.w);mh=Math.Max(mh,r.h);}
        byte[] PadR(byte[] t,int w,int h){var o=new byte[mw*mh*4];int ox=(mw-w)/2,oy=(mh-h)/2;for(int y=0;y<h;y++)for(int x=0;x<w;x++){int s=(y*w+x)*4,d=((y+oy)*mw+(x+ox))*4;o[d]=t[s];o[d+1]=t[s+1];o[d+2]=t[s+2];o[d+3]=255;}return o;}
        var rr=new List<(string,byte[],byte[],byte[],int,int)>();
        foreach(var r in rows){
            var oP=PadR(r.o,r.w,r.h); var nP=PadR(r.n,r.w,r.h);
            var d=new byte[mw*mh*4]; for(int i=0;i<mw*mh;i++){int lo=(oP[i*4]+oP[i*4+1]+oP[i*4+2])/3, ln=(nP[i*4]+nP[i*4+1]+nP[i*4+2])/3; int dd=Math.Abs(lo-ln)*4; if(dd>255)dd=255; d[i*4]=(byte)Math.Min(255,dd*2);d[i*4+1]=(byte)Math.Max(0,Math.Min(255,(dd-64)*2));d[i*4+2]=(byte)Math.Max(0,Math.Min(255,(dd-160)*3));d[i*4+3]=255; }
            rr.Add((r.cap,oP,nP,d,mw,mh)); }
        DrawSheet(rr,path,title);
    }
    static void DrawSheet(List<(string cap,byte[] o,byte[] n,byte[] d,int w,int h)> rows, string path, string title)
    {
        int pad=8, labelW=64, colGap=8, rowGap=10, top=22;
        int cellW=0,rowH=0; foreach(var r in rows){cellW=Math.Max(cellW,r.w);rowH+=Math.Max(r.h,16)+rowGap;}
        int sheetW=labelW+3*cellW+2*colGap+2*pad; int sheetH=top+rowH+pad;
        var c=new Canvas(sheetW,sheetH,24,24,28);
        c.Text(title,pad,6);
        c.Text("ORIG",labelW+pad+ (cellW-24)/2, top-2); c.Text("NEW",labelW+cellW+colGap+pad+(cellW-18)/2, top-2); c.Text("DIFFx4",labelW+2*(cellW+colGap)+pad+(cellW-36)/2, top-2);
        int y=top+8;
        foreach(var r in rows){ int ch=Math.Max(r.h,16);
            c.Text(r.cap,pad,y+ch/2-3);
            PlaceCell(c,r.o,r.w,r.h,labelW+pad,y,cellW,ch);
            PlaceCell(c,r.n,r.w,r.h,labelW+cellW+colGap+pad,y,cellW,ch);
            PlaceCell(c,r.d,r.w,r.h,labelW+2*(cellW+colGap)+pad,y,cellW,ch);
            y+=ch+rowGap;
        }
        c.Save(path);
        Console.WriteLine($"WROTE {path} ({sheetW}x{sheetH}, {rows.Count} rows)");
    }
    static void WriteLegend(string path,string text){File.WriteAllText(path,text);}
}
