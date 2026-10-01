// sdfdiag — dump orig vs new SDF buffers for a few glyphs and characterize the difference.
// Writes PGM (portable graymap) images to the scratch dir and prints per-row samples so the
// SDF encoding (spread mapping, midpoint, sign) can be reverse-engineered.
// Usage: sdfdiag <origDll> <newDll> <font.ttf> <outDir> [pixelSize] [spread]
using System;
using System.IO;
using System.Runtime.InteropServices;

unsafe class SdfDiag
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_init(out IntPtr lib);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_newface(IntPtr lib, byte* d, IntPtr sz, IntPtr idx, out IntPtr face);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_setpx(IntPtr face, uint w, uint h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_charindex(IntPtr face, UIntPtr cp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_setspread(IntPtr lib, int spread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_sdf(IntPtr face, uint gid, int flags, int spread, out Sdf r);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_free(IntPtr b);
    [StructLayout(LayoutKind.Sequential)] struct Sdf { public int success, mw, mh, mbx, mby, max, bw, bh, bp, bl, bt; public IntPtr buf; }

    class Lib { public IntPtr H; public Lib(string p){H=NativeLibrary.Load(p);} public T Fn<T>(string n) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(H,n)); public bool Has(string n)=>NativeLibrary.TryGetExport(H,n,out _);}

    static int Main(string[] a)
    {
        if (a.Length < 4) { Console.Error.WriteLine("usage: sdfdiag <orig> <new> <font> <outDir> [px] [spread]"); return 2; }
        int px = a.Length>4?int.Parse(a[4]):48, spread=a.Length>5?int.Parse(a[5]):8;
        Directory.CreateDirectory(a[3]);
        byte[] data = File.ReadAllBytes(a[2]);
        var o = new Lib(a[0]); var n = new Lib(a[1]);
        foreach (var lib in new[]{o,n}) lib.Fn<D_init>("ut_ft_init")(out _);
        fixed (byte* p = data)
        {
            IntPtr olib, nlib; o.Fn<D_init>("ut_ft_init")(out olib); n.Fn<D_init>("ut_ft_init")(out nlib);
            o.Fn<D_newface>("ut_ft_new_memory_face")(olib,p,(IntPtr)data.Length,IntPtr.Zero,out var of);
            n.Fn<D_newface>("ut_ft_new_memory_face")(nlib,p,(IntPtr)data.Length,IntPtr.Zero,out var nf);
            o.Fn<D_setpx>("ut_ft_set_pixel_sizes")(of,(uint)px,(uint)px);
            n.Fn<D_setpx>("ut_ft_set_pixel_sizes")(nf,(uint)px,(uint)px);
            if (o.Has("ut_ft_set_sdf_spread")) o.Fn<D_setspread>("ut_ft_set_sdf_spread")(olib,spread);
            if (n.Has("ut_ft_set_sdf_spread")) n.Fn<D_setspread>("ut_ft_set_sdf_spread")(nlib,spread);

            foreach (char ch in new[]{'A','o','g'})
            {
                uint g = o.Fn<D_charindex>("ut_ft_get_char_index")(of,(UIntPtr)ch);
                if (g==0) continue;
                o.Fn<D_sdf>("ut_ft_render_sdf_glyph")(of,g,2,spread,out var ors);   // LOAD_NO_HINTING
                n.Fn<D_sdf>("ut_ft_render_sdf_glyph")(nf,g,2,spread,out var nrs);
                Dump(a[3], $"orig_{ch}", ors);
                Dump(a[3], $"new_{ch}",  nrs);
                Console.WriteLine($"'{ch}' g{g}: orig {ors.bw}x{ors.bh} p{ors.bp}  new {nrs.bw}x{nrs.bh} p{nrs.bp}");
                if (ors.buf!=IntPtr.Zero && nrs.buf!=IntPtr.Zero && ors.bw==nrs.bw && ors.bh==nrs.bh)
                {
                    byte* ob=(byte*)ors.buf, nb=(byte*)nrs.buf;
                    int pO=Math.Abs(ors.bp), pN=Math.Abs(nrs.bp);
                    if (ch=='o'||ch=='A') CrossEdgeProfile(ob,nb,ors.bw,ors.bh,pO,pN,spread,ch);
                    // center row samples
                    int row = ors.bh/2, pitchO=Math.Abs(ors.bp), pitchN=Math.Abs(nrs.bp);
                    Console.Write("   orig row: "); for(int x=0;x<Math.Min(ors.bw,16);x++) Console.Write($"{ob[row*pitchO+x],4}"); Console.WriteLine();
                    Console.Write("   new  row: "); for(int x=0;x<Math.Min(nrs.bw,16);x++) Console.Write($"{nb[row*pitchN+x],4}"); Console.WriteLine();
                    // min/max
                    int mnO=255,mxO=0,mnN=255,mxN=0, maxD=0, mdx=0, mdy=0, mdo=0, mdn=0;
                    for(int y=0;y<ors.bh;y++)for(int x=0;x<ors.bw;x++){int vo=ob[y*pitchO+x],vn=nb[y*pitchN+x];if(vo<mnO)mnO=vo;if(vo>mxO)mxO=vo;if(vn<mnN)mnN=vn;if(vn>mxN)mxN=vn; int d=Math.Abs(vo-vn); if(d>maxD){maxD=d;mdx=x;mdy=y;mdo=vo;mdn=vn;}}
                    Console.WriteLine($"   orig[{mnO}-{mxO}] new[{mnN}-{mxN}] maxDiff={maxD} at({mdx},{mdy}) orig={mdo} new={mdn}");
                    // signed mean overall, and signed mean in the edge band (values within +-40 of 128)
                    long ssum=0; int scnt=0; long esum=0; int ecnt=0;
                    for(int y=0;y<ors.bh;y++)for(int x=0;x<ors.bw;x++){int vo=ob[y*pitchO+x],vn=nb[y*pitchN+x]; ssum+=(vo-vn); scnt++; if(Math.Abs(vo-128)<=40){esum+=(vo-vn); ecnt++;}}
                    double sMean = scnt>0?(double)ssum/scnt:0, eMean = ecnt>0?(double)esum/ecnt:0;
                    // byte-per-pixel slope at edge ~= 127/spread; px offset ~= edge signed diff / slope
                    double slope = 127.0/spread; double pxOff = slope>0? eMean/slope : 0;
                    Console.WriteLine($"   signedMean={sMean:F2} edgeSignedMean={eMean:F2} slope={slope:F1}/px  => edgePxOffset~{pxOff:F3}px");
                    // Empirical slope at the 128 crossing: |v[x+1]-v[x]| where the row crosses 128.
                    var so=new System.Collections.Generic.List<double>(); var sn=new System.Collections.Generic.List<double>();
                    for(int y=0;y<ors.bh;y++)for(int x=0;x<ors.bw-1;x++){
                        int va=ob[y*pitchO+x],vb=ob[y*pitchO+x+1]; if((va-128)*(vb-128)<0) so.Add(Math.Abs(vb-va));
                        int vc=nb[y*pitchN+x],vd=nb[y*pitchN+x+1]; if((vc-128)*(vd-128)<0) sn.Add(Math.Abs(vd-vc)); }
                    so.Sort(); sn.Sort(); double mo=so.Count>0?so[so.Count/2]:0, mn=sn.Count>0?sn[sn.Count/2]:0;
                    // byte value 3px inside/outside the first crossing on the center row
                    Console.WriteLine($"   MEASURED slope@128 (median |dByte/px|): orig={mo:F1} new={mn:F1} ratio new/orig={(mo>0?mn/mo:0):F3}");
                    // STRADDLE JUMP: |v[x+1]-v[x]| for the pair straddling 128, median over all H+V crossings.
                    var jO=new System.Collections.Generic.List<double>(); var jN=new System.Collections.Generic.List<double>();
                    for(int y=0;y<ors.bh;y++)for(int x=0;x<ors.bw-1;x++){
                        int a1=ob[y*pitchO+x],a2=ob[y*pitchO+x+1]; if((a1-128)*(a2-128)<0) jO.Add(Math.Abs(a2-a1));
                        int b1=nb[y*pitchN+x],b2=nb[y*pitchN+x+1]; if((b1-128)*(b2-128)<0) jN.Add(Math.Abs(b2-b1)); }
                    for(int x=0;x<ors.bw;x++)for(int y=0;y<ors.bh-1;y++){
                        int a1=ob[y*pitchO+x],a2=ob[(y+1)*pitchO+x]; if((a1-128)*(a2-128)<0) jO.Add(Math.Abs(a2-a1));
                        int b1=nb[y*pitchN+x],b2=nb[(y+1)*pitchN+x]; if((b1-128)*(b2-128)<0) jN.Add(Math.Abs(b2-b1)); }
                    jO.Sort(); jN.Sort(); double sjo=jO.Count>0?jO[jO.Count/2]:0, sjn=jN.Count>0?jN[jN.Count/2]:0;
                    Console.WriteLine($"   STRADDLE jump@128 (median |dByte| across crossing pair): orig={sjo:F1} new={sjn:F1} ratio={(sjo>0?sjn/sjo:0):F3}");
                    // REACH/SATURATION: on the center row, from the leftmost 128-crossing walk
                    // outward (decreasing) to the first 0, and inward (increasing) to the first 255;
                    // report px distance. Do it for orig and new.
                    { int cy=ors.bh/2;
                      int cxoO=-1; for(int x=0;x<ors.bw-1;x++){int av=ob[cy*pitchO+x],bv=ob[cy*pitchO+x+1]; if((av-128)*(bv-128)<0){cxoO=x;break;}}
                      int cxoN=-1; for(int x=0;x<nrs.bw-1;x++){int av=nb[cy*pitchN+x],bv=nb[cy*pitchN+x+1]; if((av-128)*(bv-128)<0){cxoN=x;break;}}
                      double oOut=0,oIn=0,nOut=0,nIn=0;
                      if(cxoO>=0){ for(int x=cxoO;x>=0;x--){if(ob[cy*pitchO+x]<=0){oOut=cxoO-x;break;}} for(int x=cxoO+1;x<ors.bw;x++){if(ob[cy*pitchO+x]>=255){oIn=x-cxoO;break;}} }
                      if(cxoN>=0){ for(int x=cxoN;x>=0;x--){if(nb[cy*pitchN+x]<=0){nOut=cxoN-x;break;}} for(int x=cxoN+1;x<nrs.bw;x++){if(nb[cy*pitchN+x]>=255){nIn=x-cxoN;break;}} }
                      Console.WriteLine($"   REACH px (edge->0 out / edge->255 in): orig out={oOut:F1} in={oIn:F1} | new out={nOut:F1} in={nIn:F1}");
                      // profile: orig byte at 1..8 px outside the crossing (raw FreeType, no remap effect on orig)
                      if(cxoO>=0){ var sb=new System.Text.StringBuilder("   orig outside profile: ");
                        for(int d=0;d<=8;d++){int x=cxoO-d; sb.Append(x>=0?ob[cy*pitchO+x].ToString():"--").Append(' ');}
                        Console.WriteLine(sb.ToString()); }
                    }
                    if (ch=='A') {
                        Console.WriteLine("   --- orig (>=128 = '#') ---");
                        for(int y=0;y<ors.bh;y+=2){var sb=new System.Text.StringBuilder("   ");for(int x=0;x<ors.bw;x+=2)sb.Append(ob[y*pitchO+x]>=128?'#':(ob[y*pitchO+x]>=64?'.':' '));Console.WriteLine(sb);}
                        Console.WriteLine("   --- new (>=128 = '#') ---");
                        for(int y=0;y<nrs.bh;y+=2){var sb=new System.Text.StringBuilder("   ");for(int x=0;x<nrs.bw;x+=2)sb.Append(nb[y*pitchN+x]>=128?'#':(nb[y*pitchN+x]>=64?'.':' '));Console.WriteLine(sb);}
                    }
                }
                if (ors.buf!=IntPtr.Zero) o.Fn<D_free>("ut_ft_free_sdf_buffer")(ors.buf);
                if (nrs.buf!=IntPtr.Zero) n.Fn<D_free>("ut_ft_free_sdf_buffer")(nrs.buf);
            }
        }
        return 0;
    }

    static double Bil(byte* b,int w,int h,int pit,double fx,double fy){
        int x0=(int)Math.Floor(fx),y0=(int)Math.Floor(fy); double tx=fx-x0,ty=fy-y0;
        int x1=x0+1,y1=y0+1; int cx0=x0<0?0:(x0>=w?w-1:x0),cx1=x1<0?0:(x1>=w?w-1:x1),cy0=y0<0?0:(y0>=h?h-1:y0),cy1=y1<0?0:(y1>=h?h-1:y1);
        double v00=b[cy0*pit+cx0],v10=b[cy0*pit+cx1],v01=b[cy1*pit+cx0],v11=b[cy1*pit+cx1];
        double aa=v00+(v10-v00)*tx,bb=v01+(v11-v01)*tx; return aa+(bb-aa)*ty; }
    // Sample orig+new along the outward normal at 20+ edge points, -spread..+spread @0.25px.
    // Print the averaged profile (t=distance px, negative=inside) and where they diverge.
    static void CrossEdgeProfile(byte* ob, byte* nb, int w, int h, int pO, int pN, int spread, char ch)
    {
        int N=(int)(spread/0.25); // samples each side
        int steps=2*N+1; var profO=new double[steps]; var profN=new double[steps]; int npts=0;
        // find edge points: pixels where the byte is near 128 and gradient is strong
        for (int y=2;y<h-2 && npts<40;y++) for (int x=2;x<w-2 && npts<40;x++){
            int c=ob[y*pO+x]; if (Math.Abs(c-128)>20) continue;
            double gx=(ob[y*pO+x+1]-ob[y*pO+x-1])/2.0, gy=(ob[(y+1)*pO+x]-ob[(y-1)*pO+x])/2.0;
            double gl=Math.Sqrt(gx*gx+gy*gy); if (gl<8) continue; // strong edge only
            double nx=gx/gl, ny=gy/gl; // gradient points toward inside (higher byte); normal outward = -grad
            for (int i=0;i<steps;i++){ double t=(i-N)*0.25; // t>0 outside (subtract along +grad = inside, so outside is -grad*t)
                double sx=x - nx*t, sy=y - ny*t; // move opposite gradient for +t outside
                profO[i]+=Bil(ob,w,h,pO,sx,sy); profN[i]+=Bil(nb,w,h,pN,sx,sy); }
            npts++;
        }
        if (npts==0){ Console.WriteLine($"   '{ch}': no strong edge points"); return; }
        for (int i=0;i<steps;i++){ profO[i]/=npts; profN[i]/=npts; }
        Console.WriteLine($"   '{ch}' cross-edge profile ({npts} pts), t=px (neg=inside):");
        Console.Write("     t   :"); for(int i=0;i<steps;i+=2) Console.Write($"{((i-N)*0.25),6:F1}"); Console.WriteLine();
        Console.Write("     orig:"); for(int i=0;i<steps;i+=2) Console.Write($"{profO[i],6:F0}"); Console.WriteLine();
        Console.Write("     new :"); for(int i=0;i<steps;i+=2) Console.Write($"{profN[i],6:F0}"); Console.WriteLine();
        // divergence: first |orig-new|>10 moving out from the edge center
        double firstDiv=999; for(int i=0;i<steps;i++){ if(Math.Abs(profO[i]-profN[i])>10){ firstDiv=Math.Abs((i-N)*0.25); break; } }
        // near-edge slope: median |dByte/px| within +-1.5px (samples i where |t|<=1.5)
        var so=new System.Collections.Generic.List<double>(); var sn=new System.Collections.Generic.List<double>();
        for(int i=0;i<steps-1;i++){ double t=(i-N)*0.25; if(Math.Abs(t)<=1.5){ so.Add(Math.Abs(profO[i+1]-profO[i])/0.25); sn.Add(Math.Abs(profN[i+1]-profN[i])/0.25);} }
        so.Sort(); sn.Sort(); double mo=so.Count>0?so[so.Count/2]:0, mn=sn.Count>0?sn[sn.Count/2]:0;
        Console.WriteLine($"   '{ch}' near-edge slope(+-1.5px) median |dByte/px|: orig={mo:F1} new={mn:F1} ratio={(mo>0?mn/mo:0):F3}; first divergence(|d|>10) at t~{firstDiv:F2}px");
    }

    static void Dump(string dir, string name, Sdf s)
    {
        if (s.buf==IntPtr.Zero || s.bw<=0 || s.bh<=0) return;
        int pitch=Math.Abs(s.bp);
        var px=new byte[s.bw*s.bh]; byte* b=(byte*)s.buf;
        for(int y=0;y<s.bh;y++) for(int x=0;x<s.bw;x++) px[y*s.bw+x]=b[y*pitch+x];
        using var fs=new FileStream(Path.Combine(dir,name+".pgm"),FileMode.Create);
        var hdr=System.Text.Encoding.ASCII.GetBytes($"P5\n{s.bw} {s.bh}\n255\n");
        fs.Write(hdr,0,hdr.Length); fs.Write(px,0,px.Length);
    }
}
