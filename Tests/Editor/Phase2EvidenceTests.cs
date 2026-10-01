using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// STEP 3 EVIDENCE. Rasterizes glyphs through OpenGlyph's own native FreeType raster path
    /// (<see cref="FT.RenderGlyph"/> + <see cref="FT.GetBitmapData"/>) — NOT PIL/Skia — and tiles
    /// them into labelled PNG sheets:
    /// <list type="bullet">
    /// <item><c>phase2_family_real_vs_synthetic.png</c>: Regular/Bold/Italic/BoldItalic from the real
    /// Noto family, beside synthetic bold (SDF-free faux via a horizontal dilate of Regular) and
    /// synthetic italic (shear of Regular).</item>
    /// <item><c>phase2_robotoflex_axes.png</c>: RobotoFlex at wght 100/400/700/1000 and wdth
    /// 25/100/151, each applied via the variation ABI before rasterization.</item>
    /// </list>
    /// Written to the host project's <c>Phase2Evidence/</c> folder; path is logged for inspection.
    /// </summary>
    public class Phase2EvidenceTests
    {
        private const int Ppem = 64;
        private const int Cell = 96;   // cell is larger than ppem to leave margins
        private const string Sample = "Rgae"; // a few shapes that show weight/slant clearly

        private static string OutDir()
        {
            string dir = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "Phase2Evidence");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string NotoFamilyDir()
        {
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) return null;
            string pkgRoot = Directory.GetParent(Path.GetDirectoryName(noto))?.FullName;
            string dir = pkgRoot == null ? null : Path.Combine(pkgRoot, "Tests", "Editor", "Fonts", "NotoSansFamily");
            return (dir != null && Directory.Exists(dir)) ? dir : null;
        }

        [Test]
        public void RealVsSynthetic_FamilySheet_Png()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string dir = NotoFamilyDir();
            if (dir == null) Assert.Ignore("NotoSansFamily fixtures not present.");

            var rows = new List<(string label, byte[] bytes, float shear, bool dilate)>
            {
                ("Real Regular",     File.ReadAllBytes(Path.Combine(dir, "NotoSans-Regular.ttf")),    0f,    false),
                ("Real Bold",        File.ReadAllBytes(Path.Combine(dir, "NotoSans-Bold.ttf")),        0f,    false),
                ("Real Italic",      File.ReadAllBytes(Path.Combine(dir, "NotoSans-Italic.ttf")),      0f,    false),
                ("Real BoldItalic",  File.ReadAllBytes(Path.Combine(dir, "NotoSans-BoldItalic.ttf")),  0f,    false),
                ("Synthetic Bold (dilate Regular)",   File.ReadAllBytes(Path.Combine(dir, "NotoSans-Regular.ttf")), 0f,    true),
                ("Synthetic Italic (shear Regular)",  File.ReadAllBytes(Path.Combine(dir, "NotoSans-Regular.ttf")), 0.25f, false),
            };

            int cols = Sample.Length;
            var sheet = NewSheet(cols, rows.Count, out int cw, out int ch);
            int imgW = cols * cw;
            for (int r = 0; r < rows.Count; r++)
            {
                var face = FT.LoadFace(rows[r].bytes, 0);
                try
                {
                    FT.SetPixelSize(face, Ppem);
                    for (int c = 0; c < Sample.Length; c++)
                        BlitGlyph(sheet, imgW, cw, ch, c, r, face, Sample[c], rows[r].shear, rows[r].dilate);
                }
                finally { FT.UnloadFace(face); }
            }

            string path = Path.Combine(OutDir(), "phase2_family_real_vs_synthetic.png");
            WriteSheet(sheet, cw, ch, cols, rows.Count, path);
            Debug.Log($"[Phase2Evidence] family sheet -> {path}");
            Assert.IsTrue(File.Exists(path));
        }

        [Test]
        public void RobotoFlex_AxisSweep_Sheet_Png()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");

            byte[] bytes = File.ReadAllBytes(vf);
            var face = FT.LoadFace(bytes, 0);
            Assert.AreNotEqual(IntPtr.Zero, face);
            try
            {
                var map = VariationMapper.Read(face);
                Assert.IsTrue(map.isVariable);

                // Rows: weight sweep (wdth=100) then width sweep (wght=400).
                var specs = new List<(string label, FontStyleSpec spec)>
                {
                    ("wght 100",  new FontStyleSpec(100, 100, StyleAxis.Normal)),
                    ("wght 400",  new FontStyleSpec(400, 100, StyleAxis.Normal)),
                    ("wght 700",  new FontStyleSpec(700, 100, StyleAxis.Normal)),
                    ("wght 1000", new FontStyleSpec(1000, 100, StyleAxis.Normal)),
                    ("wdth 25",   new FontStyleSpec(400, 25,  StyleAxis.Normal)),
                    ("wdth 100",  new FontStyleSpec(400, 100, StyleAxis.Normal)),
                    ("wdth 151",  new FontStyleSpec(400, 151, StyleAxis.Normal)),
                };

                int cols = Sample.Length;
                var sheet = NewSheet(cols, specs.Count, out int cw, out int ch);
                int imgW = cols * cw;
                FT.SetPixelSize(face, Ppem);
                for (int r = 0; r < specs.Count; r++)
                {
                    map.Map(specs[r].spec, 0f, out _, out float[] coords);
                    FTVar.SetDesignCoordinates(face, coords);
                    for (int c = 0; c < Sample.Length; c++)
                        BlitGlyph(sheet, imgW, cw, ch, c, r, face, Sample[c], 0f, false);
                }

                string path = Path.Combine(OutDir(), "phase2_robotoflex_axes.png");
                WriteSheet(sheet, cw, ch, cols, specs.Count, path);
                Debug.Log($"[Phase2Evidence] robotoflex sheet -> {path}");
                Assert.IsTrue(File.Exists(path));
            }
            finally { FT.UnloadFace(face); }
        }

        // ---- tiny raster compositor --------------------------------------------

        private static Color32[] NewSheet(int cols, int rows, out int cw, out int ch)
        {
            cw = Cell; ch = Cell;
            var px = new Color32[cols * cw * rows * ch];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(24, 24, 28, 255);
            return px;
        }

        // Render one glyph (optionally synthetic-sheared/dilated) into cell (col,row).
        private static void BlitGlyph(Color32[] sheet, int imgW, int cw, int ch, int col, int row, IntPtr face, char ch2, float shear, bool dilate)
        {
            uint gi = FT.GetCharIndex(face, ch2);
            if (gi == 0) return;
            if (!FT.LoadGlyph(face, gi, FT.LOAD_DEFAULT | FT.LOAD_NO_HINTING)) return;
            if (!FT.RenderGlyph(face, FT.RENDER_MODE_NORMAL)) return;

            var bmp = FT.GetBitmapData(face);
            if (bmp.width <= 0 || bmp.height <= 0 || bmp.buffer == IntPtr.Zero) return;

            var cov = ReadGray(bmp);

            // Place with a margin; a nominal baseline 24px up from the cell bottom.
            int originX = col * cw + 10;
            int originY = row * ch + ch - 24; // baseline from top-of-sheet
            int top = FT.GetBitmapTop(face);
            int left = FT.GetBitmapLeft(face);

            for (int y = 0; y < bmp.height; y++)
            for (int x = 0; x < bmp.width; x++)
            {
                byte a = cov[y * bmp.width + x];
                if (a == 0) continue;
                int sx = originX + left + x + Mathf.RoundToInt(shear * (bmp.height - y));
                int sy = originY - top + y;
                PlacePixel(sheet, imgW, sx, sy, a);
                if (dilate) PlacePixel(sheet, imgW, sx + 1, sy, a); // faux-bold: 1px horizontal dilate
            }
        }

        private static byte[] ReadGray(FT.BitmapData bmp)
        {
            var outp = new byte[bmp.width * bmp.height];
            unsafe
            {
                byte* src = (byte*)bmp.buffer;
                for (int y = 0; y < bmp.height; y++)
                {
                    byte* rowp = src + y * bmp.pitch;
                    for (int x = 0; x < bmp.width; x++)
                        outp[y * bmp.width + x] = rowp[x];
                }
            }
            return outp;
        }

        private static void PlacePixel(Color32[] sheet, int imgW, int x, int y, byte a)
        {
            if (x < 0 || y < 0 || x >= imgW) return;
            int idx = y * imgW + x;
            if (idx < 0 || idx >= sheet.Length) return;
            byte v = (byte)Mathf.Clamp(a + 24, 0, 255);
            sheet[idx] = new Color32(v, v, v, 255);
        }

        private static void WriteSheet(Color32[] sheet, int cw, int ch, int cols, int rows, string path)
        {
            int imgW = cols * cw, imgH = rows * ch;
            var tex = new Texture2D(imgW, imgH, TextureFormat.RGBA32, false);
            // Unity texture origin is bottom-left; our compose used top-left, so flip rows.
            var flipped = new Color32[sheet.Length];
            for (int y = 0; y < imgH; y++)
                Array.Copy(sheet, y * imgW, flipped, (imgH - 1 - y) * imgW, imgW);
            tex.SetPixels32(flipped);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
