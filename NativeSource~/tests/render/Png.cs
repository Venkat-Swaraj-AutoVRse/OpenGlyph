// Png.cs — minimal RGBA PNG encoder (no NuGet), a canvas, and a tiny built-in label font.
using System;
using System.IO;
using System.IO.Compression;

static class Png
{
    static readonly uint[] Crc = BuildCrc();
    static uint[] BuildCrc(){var t=new uint[256];for(uint n=0;n<256;n++){uint c=n;for(int k=0;k<8;k++)c=((c&1)!=0)?0xEDB88320u^(c>>1):c>>1;t[n]=c;}return t;}
    static uint Crc32(byte[] d,int off,int len){uint c=0xFFFFFFFFu;for(int i=0;i<len;i++)c=Crc[(c^d[off+i])&0xFF]^(c>>8);return c^0xFFFFFFFFu;}
    static void Chunk(Stream s, string type, byte[] data)
    {
        var len=data.Length; s.Write(new[]{(byte)(len>>24),(byte)(len>>16),(byte)(len>>8),(byte)len},0,4);
        var tb=System.Text.Encoding.ASCII.GetBytes(type);
        var buf=new byte[4+data.Length]; Array.Copy(tb,0,buf,0,4); Array.Copy(data,0,buf,4,data.Length);
        s.Write(buf,0,buf.Length);
        uint crc=Crc32(buf,0,buf.Length); s.Write(new[]{(byte)(crc>>24),(byte)(crc>>16),(byte)(crc>>8),(byte)crc},0,4);
    }
    // rgba = w*h*4 bytes
    public static void Write(string path, byte[] rgba, int w, int h)
    {
        using var fs=new FileStream(path,FileMode.Create);
        fs.Write(new byte[]{0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A},0,8);
        var ihdr=new byte[13];
        ihdr[0]=(byte)(w>>24);ihdr[1]=(byte)(w>>16);ihdr[2]=(byte)(w>>8);ihdr[3]=(byte)w;
        ihdr[4]=(byte)(h>>24);ihdr[5]=(byte)(h>>16);ihdr[6]=(byte)(h>>8);ihdr[7]=(byte)h;
        ihdr[8]=8; ihdr[9]=6; ihdr[10]=0; ihdr[11]=0; ihdr[12]=0; // 8-bit RGBA
        Chunk(fs,"IHDR",ihdr);
        // filtered scanlines (filter 0) then zlib
        var raw=new byte[h*(1+w*4)];
        for(int y=0;y<h;y++){raw[y*(1+w*4)]=0; Array.Copy(rgba,y*w*4,raw,y*(1+w*4)+1,w*4);}
        byte[] comp;
        using(var ms=new MemoryStream()){ using(var z=new ZLibStream(ms,CompressionLevel.Optimal,true)) z.Write(raw,0,raw.Length); comp=ms.ToArray(); }
        Chunk(fs,"IDAT",comp);
        Chunk(fs,"IEND",Array.Empty<byte>());
    }
}

// Simple RGBA canvas with blit + tiny text.
class Canvas
{
    public int W,H; public byte[] Px;
    public Canvas(int w,int h,byte r=32,byte g=32,byte b=32){W=w;H=h;Px=new byte[w*h*4];for(int i=0;i<w*h;i++){Px[i*4]=r;Px[i*4+1]=g;Px[i*4+2]=b;Px[i*4+3]=255;}}
    public void Set(int x,int y,byte r,byte g,byte b){if(x<0||y<0||x>=W||y>=H)return;int i=(y*W+x)*4;Px[i]=r;Px[i+1]=g;Px[i+2]=b;Px[i+3]=255;}
    // blit a grayscale tile (val 0..255) as white-on-bg at (ox,oy)
    public void BlitGray(byte[] g,int gw,int gh,int ox,int oy){for(int y=0;y<gh;y++)for(int x=0;x<gw;x++){byte v=g[y*gw+x];Set(ox+x,oy+y,v,v,v);}}
    // blit RGBA tile
    public void BlitRGBA(byte[] t,int tw,int th,int ox,int oy){for(int y=0;y<th;y++)for(int x=0;x<tw;x++){int i=(y*tw+x)*4;Set(ox+x,oy+y,t[i],t[i+1],t[i+2]);}}
    public void Rect(int x,int y,int w,int h,byte r,byte g,byte b){for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++)Set(xx,yy,r,g,b);}
    public void Text(string s,int x,int y,byte r=230,byte g=230,byte b=230){int cx=x;foreach(char c in s){Font5x7.Draw(this,c,cx,y,r,g,b);cx+=6;}}
    public void Save(string p){Png.Write(p,Px,W,H);}
}

// Tiny 5x7 bitmap font: digits, A-Z, a-z (subset), and a few symbols. Each glyph 5 cols x 7 rows.
static class Font5x7
{
    public static void Draw(Canvas c,char ch,int x,int y,byte r,byte g,byte b)
    {
        string[] rows = Glyph(ch);
        for(int j=0;j<rows.Length;j++)for(int i=0;i<rows[j].Length;i++)if(rows[j][i]=='#')c.Set(x+i,y+j,r,g,b);
    }
    static string[] Glyph(char ch)
    {
        switch(char.ToUpper(ch))
        {
            case '0':return new[]{"01110","10001","10011","10101","11001","10001","01110"};
            case '1':return new[]{"00100","01100","00100","00100","00100","00100","01110"};
            case '2':return new[]{"01110","10001","00001","00010","00100","01000","11111"};
            case '3':return new[]{"11111","00010","00100","00010","00001","10001","01110"};
            case '4':return new[]{"00010","00110","01010","10010","11111","00010","00010"};
            case '5':return new[]{"11111","10000","11110","00001","00001","10001","01110"};
            case '6':return new[]{"00110","01000","10000","11110","10001","10001","01110"};
            case '7':return new[]{"11111","00001","00010","00100","01000","01000","01000"};
            case '8':return new[]{"01110","10001","10001","01110","10001","10001","01110"};
            case '9':return new[]{"01110","10001","10001","01111","00001","00010","01100"};
            case 'A':return new[]{"01110","10001","10001","11111","10001","10001","10001"};
            case 'B':return new[]{"11110","10001","10001","11110","10001","10001","11110"};
            case 'C':return new[]{"01110","10001","10000","10000","10000","10001","01110"};
            case 'D':return new[]{"11100","10010","10001","10001","10001","10010","11100"};
            case 'E':return new[]{"11111","10000","10000","11110","10000","10000","11111"};
            case 'F':return new[]{"11111","10000","10000","11110","10000","10000","10000"};
            case 'G':return new[]{"01110","10001","10000","10111","10001","10001","01111"};
            case 'H':return new[]{"10001","10001","10001","11111","10001","10001","10001"};
            case 'I':return new[]{"01110","00100","00100","00100","00100","00100","01110"};
            case 'K':return new[]{"10001","10010","10100","11000","10100","10010","10001"};
            case 'L':return new[]{"10000","10000","10000","10000","10000","10000","11111"};
            case 'M':return new[]{"10001","11011","10101","10101","10001","10001","10001"};
            case 'N':return new[]{"10001","11001","10101","10011","10001","10001","10001"};
            case 'O':return new[]{"01110","10001","10001","10001","10001","10001","01110"};
            case 'P':return new[]{"11110","10001","10001","11110","10000","10000","10000"};
            case 'R':return new[]{"11110","10001","10001","11110","10100","10010","10001"};
            case 'S':return new[]{"01111","10000","10000","01110","00001","00001","11110"};
            case 'T':return new[]{"11111","00100","00100","00100","00100","00100","00100"};
            case 'U':return new[]{"10001","10001","10001","10001","10001","10001","01110"};
            case 'V':return new[]{"10001","10001","10001","10001","10001","01010","00100"};
            case 'W':return new[]{"10001","10001","10001","10101","10101","11011","10001"};
            case 'X':return new[]{"10001","10001","01010","00100","01010","10001","10001"};
            case 'Y':return new[]{"10001","10001","01010","00100","00100","00100","00100"};
            case 'Z':return new[]{"11111","00001","00010","00100","01000","10000","11111"};
            case '+':return new[]{"00000","00100","00100","11111","00100","00100","00000"};
            case '&':return new[]{"01100","10010","10010","01100","10101","10010","01101"};
            case '@':return new[]{"01110","10001","10111","10101","10111","10000","01110"};
            case '.':return new[]{"00000","00000","00000","00000","00000","01100","01100"};
            case ':':return new[]{"00000","01100","01100","00000","01100","01100","00000"};
            case '/':return new[]{"00001","00010","00010","00100","01000","01000","10000"};
            case '-':return new[]{"00000","00000","00000","11111","00000","00000","00000"};
            case '_':return new[]{"00000","00000","00000","00000","00000","00000","11111"};
            case '#':return new[]{"01010","11111","01010","01010","11111","01010","00000"};
            case ' ':return new[]{"00000","00000","00000","00000","00000","00000","00000"};
            default:return new[]{"11111","10001","10001","10001","10001","10001","11111"}; // box
        }
    }
}
