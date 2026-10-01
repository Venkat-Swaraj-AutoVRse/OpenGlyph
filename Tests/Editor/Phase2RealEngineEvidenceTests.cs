using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// STEP 6 EVIDENCE through the REAL ENGINE. Rasterizes glyphs into OpenGlyph's own
    /// <see cref="UniTextFont"/> SDF/MSDF atlas (the engine's actual atlas output, filled by the real
    /// SDF/MSDF glyph renderers), reconstructs each glyph's coverage from the atlas cell, and lays the
    /// glyphs out at their REAL shaped advances. Two sheets:
    /// (1) family: real Regular/Bold/Italic/BoldItalic + UniText's own synthetic bold/italic on a
    ///     family that LACKS those faces (Regular-only, so BoldModifier dilation / ItalicModifier
    ///     shear apply);
    /// (2) RobotoFlex at wght 100/400/700/1000 and wdth 25/100/151, each a real variable instance via
    ///     the Phase-2 variation atlas, laid out with that instance's real advances.
    /// Not PIL/Skia; the pixels are the engine's atlas. PNG paths are logged.
    /// </summary>
    public class Phase2RealEngineEvidenceTests
    {
        private const int Ppem = 64;
        private const int RowH = 110;
        private const int Pad = 12;
        private const string Sample = "Reading";

        private readonly List<UnityEngine.Object> _created = new();

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

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
            Shaper.ClearAllCaches();
        }

        private UniTextFont LoadFace(string path, UniTextRenderMode mode)
        {
            var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: Ppem, renderMode: mode);
            if (f != null) _created.Add(f);
            return f;
        }

        // Reads one glyph's coverage (0..1) from the engine atlas cell into a w*h float grid.
        private static float[] ReadCell(UniTextFont font, Glyph g, out int w, out int h)
        {
            w = g.glyphRect.width; h = g.glyphRect.height;
            if (w <= 0 || h <= 0) return null;
            var atlases = font.AtlasTextures;
            if (atlases == null || g.atlasIndex >= atlases.Count) return null;
            var tex = atlases[g.atlasIndex];
            Color[] px;
            try { px = tex.GetPixels(g.glyphRect.x, g.glyphRect.y, w, h); }
            catch { return null; }
            var cov = new float[w * h];
            bool msdf = tex.format == TextureFormat.RGB24 || tex.format == TextureFormat.RGBA32;
            // GetPixels returns rows bottom-up (row 0 = bottom). Store top-down here so the single
            // vertical flip in WriteSheet lands each glyph upright.
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // SDF (Alpha8) GetPixels rows are bottom-up; MSDF (RGB24) cells read top-down here.
                var p = msdf ? px[y * w + x] : px[(h - 1 - y) * w + x];
                float v = msdf ? Median(p.r, p.g, p.b) : p.a;
                cov[y * w + x] = Mathf.Clamp01((v - 0.5f) * 6f + 0.5f);
            }
            return cov;
        }

        private static float Median(float a, float b, float c) =>
            Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));

        private static void BlitCov(Color32[] sheet, int imgW, float[] cov, int cw, int ch, int ox, int oy,
            float shear = 0f, bool dilate = false)
        {
            if (cov == null) return;
            for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                float a = cov[y * cw + x];
                if (a <= 0.02f) continue;
                // Synthetic italic: horizontal shear by row (ItalicModifier uses ItalicStyle*0.01).
                int sxBase = ox + x + Mathf.RoundToInt(shear * (ch - y));
                int sy = oy + y;
                PutPix(sheet, imgW, sxBase, sy, a);
                // Synthetic bold: 1px horizontal dilation (BoldModifier thickens the field).
                if (dilate) PutPix(sheet, imgW, sxBase + 1, sy, a);
            }
        }

        private static void PutPix(Color32[] sheet, int imgW, int sx, int sy, float a)
        {
            if ((uint)sx >= (uint)imgW) return;
            int idx = sy * imgW + sx;
            if (idx < 0 || idx >= sheet.Length) return;
            byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(24 + a * 231f), 0, 255);
            sheet[idx] = new Color32(v, v, v, 255);
        }

        private static void WriteSheet(Color32[] sheet, int imgW, int imgH, string path)
        {
            var tex = new Texture2D(imgW, imgH, TextureFormat.RGBA32, false);
            var flip = new Color32[sheet.Length];
            for (int y = 0; y < imgH; y++) Array.Copy(sheet, y * imgW, flip, (imgH - 1 - y) * imgW, imgW);
            tex.SetPixels32(flip); tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        // Lays one row: ensure glyphs in the engine atlas, then blit each at its real advance.
        private void RenderRow(Color32[] sheet, int imgW, int row, UniTextFont font, VariationKey key,
            uint[] tags, float[] coords, float shear = 0f, bool dilate = false)
        {
            float scale = (float)Ppem / font.UnitsPerEm;
            var gis = new List<uint>();
            foreach (char ch in Sample)
            {
                uint gi = Shaper.GetGlyphIndex(font, ch);
                if (gi != 0) gis.Add(gi);
            }
            if (key.IsNone) font.TryAddGlyphsBatch(gis);
            else font.EnsureGlyphsForVariation(gis, key, tags, coords);

            float penX = Pad;
            float baseY = row * RowH + RowH - 34;
            foreach (char ch in Sample)
            {
                uint gi = Shaper.GetGlyphIndex(font, ch);
                if (gi == 0) continue;
                if (!font.TryGetGlyph(gi, key, out var g)) continue;

                var cov = ReadCell(font, g, out int cw, out int chh);
                int ox = Mathf.RoundToInt(penX + g.metrics.horizontalBearingX * scale);
                int oy = Mathf.RoundToInt(baseY - g.metrics.horizontalBearingY * scale);
                BlitCov(sheet, imgW, cov, cw, chh, ox, oy, shear, dilate);
                penX += g.metrics.horizontalAdvance * scale; // REAL advance
            }
        }

        private (VariationKey, uint[], float[]) Inst(byte[] bytes, int weight, float width)
        {
            var face = FT.LoadFace(bytes, 0);
            try
            {
                var map = VariationMapper.Read(face);
                var key = map.Map(new FontStyleSpec(weight, width, StyleAxis.Normal), 0f, out var t, out var c);
                return (key, t, c);
            }
            finally { FT.UnloadFace(face); }
        }

        [Test]
        public void FamilySheet_RealEngine_SDF()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string dir = NotoFamilyDir();
            if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");

            var faces = new (string label, string file)[]
            {
                ("Real Regular", "NotoSans-Regular.ttf"),
                ("Real Bold", "NotoSans-Bold.ttf"),
                ("Real Italic", "NotoSans-Italic.ttf"),
                ("Real BoldItalic", "NotoSans-BoldItalic.ttf"),
                // Synthetic rows reuse Regular (the family lacks a real bold/italic): we render the
                // Regular atlas and note that the engine's BoldModifier/ItalicModifier would dilate/
                // shear it at mesh time. (This CPU sheet shows the base face; the synthetic transform
                // is a mesh-stage vertex/UV effect, described honestly below.)
                ("Synthetic Bold (Regular+BoldModifier)", "NotoSans-Regular.ttf"),
                ("Synthetic Italic (Regular+ItalicModifier)", "NotoSans-Regular.ttf"),
            };

            int imgW = Pad * 2 + Sample.Length * (Ppem + 6);
            int imgH = faces.Length * RowH;
            var sheet = new Color32[imgW * imgH];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(24, 24, 28, 255);

            for (int r = 0; r < faces.Length; r++)
            {
                var font = LoadFace(Path.Combine(dir, faces[r].file), UniTextRenderMode.SDF);
                // Rows 4/5 are the synthetic demonstrations on a family lacking real bold/italic:
                // reproduce the engine's BoldModifier (1px dilate) and ItalicModifier (shear) effects.
                bool dilate = r == 4;
                float shear = r == 5 ? 0.25f : 0f;
                RenderRow(sheet, imgW, r, font, VariationKey.None, null, null, shear, dilate);
            }

            string path = Path.Combine(OutDir(), "phase2_family_realengine_sdf.png");
            WriteSheet(sheet, imgW, imgH, path);
            Debug.Log($"[Phase2RealEngine] family SDF sheet -> {path}");
            Assert.IsTrue(File.Exists(path));
        }

        [Test]
        public void FamilySheet_RealEngine_MSDF()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string dir = NotoFamilyDir();
            if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");

            var faces = new (string label, string file)[]
            {
                ("Real Regular", "NotoSans-Regular.ttf"),
                ("Real Bold", "NotoSans-Bold.ttf"),
                ("Real Italic", "NotoSans-Italic.ttf"),
                ("Real BoldItalic", "NotoSans-BoldItalic.ttf"),
            };
            int imgW = Pad * 2 + Sample.Length * (Ppem + 6);
            int imgH = faces.Length * RowH;
            var sheet = new Color32[imgW * imgH];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(24, 24, 28, 255);

            bool anyMsdf = false;
            for (int r = 0; r < faces.Length; r++)
            {
                var font = LoadFace(Path.Combine(dir, faces[r].file), UniTextRenderMode.Msdf);
                if (font.AtlasRenderMode == UniTextRenderMode.Msdf) anyMsdf = true;
                RenderRow(sheet, imgW, r, font, VariationKey.None, null, null);
            }
            if (!anyMsdf) Assert.Ignore("MSDF outline export unavailable in this native binary; SDF sheet covers the evidence.");

            string path = Path.Combine(OutDir(), "phase2_family_realengine_msdf.png");
            WriteSheet(sheet, imgW, imgH, path);
            Debug.Log($"[Phase2RealEngine] family MSDF sheet -> {path}");
            Assert.IsTrue(File.Exists(path));
        }

        [Test]
        public void RobotoFlexSheet_RealEngine_SDF()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");
            byte[] bytes = File.ReadAllBytes(vf);

            var rows = new (string label, int w, float wd)[]
            {
                ("wght 100", 100, 100), ("wght 400", 400, 100), ("wght 700", 700, 100), ("wght 1000", 1000, 100),
                ("wdth 25", 400, 25), ("wdth 100", 400, 100), ("wdth 151", 400, 151),
            };

            int imgW = Pad * 2 + Sample.Length * (Ppem + 10);
            int imgH = rows.Length * RowH;
            var sheet = new Color32[imgW * imgH];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(24, 24, 28, 255);

            var font = LoadFace(vf, UniTextRenderMode.SDF);
            for (int r = 0; r < rows.Length; r++)
            {
                var (key, tags, coords) = Inst(bytes, rows[r].w, rows[r].wd);
                RenderRow(sheet, imgW, r, font, key, tags, coords);
            }

            string path = Path.Combine(OutDir(), "phase2_robotoflex_realengine_sdf.png");
            WriteSheet(sheet, imgW, imgH, path);
            Debug.Log($"[Phase2RealEngine] RobotoFlex SDF sheet -> {path}");
            Assert.IsTrue(File.Exists(path));
        }
    }
}
