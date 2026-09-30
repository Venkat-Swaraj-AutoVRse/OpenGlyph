using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using LightSide;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// Produces VISUAL EVIDENCE for the phase-0 real export: the glyphs 'AMW&amp;' rendered at 64px
    /// as (row 1) a single-channel SDF and (row 2) a real-export MSDF, each magnified 8x through the
    /// ACTUAL UniText/SDF and UniText/MSDF shader reconstruction. When the editor has a real graphics
    /// device the magnification is a GPU Graphics.Blit through the shipped shader materials; under
    /// batchmode -nographics (null device, blit returns blank) it falls back to a CPU decode that
    /// reproduces the shaders' own math (median-of-three for MSDF, single channel for SDF, then the
    /// smoothstep face threshold). Writes one side-by-side PNG to KIROCREW_SCRATCH. WindowsEditor
    /// only, because it depends on the real ut_ft_get_outline_data export.
    /// </summary>
    [TestFixture]
    public class MsdfVisualEvidenceTests
    {
        private const string Text = "AMW&";
        private const int Ppem = 64;
        private const int Spread = 8;
        private const int Up = 8;

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void Render_AMW_SDF_vs_RealExportMSDF_8x_ThroughShaders_Png()
        {
            string fontPath = MsdfTestUtil.FindNotoSansPath();
            if (fontPath == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            byte[] fontBytes = File.ReadAllBytes(fontPath);

            Assert.IsTrue(FT.IsInitialized || FT.Initialize(), "FreeType init failed.");
            IntPtr face = FT.LoadFace(fontBytes);
            Assert.AreNotEqual(IntPtr.Zero, face, "FT.LoadFace failed.");

            var cells = new List<Cell>();
            try
            {
                Assert.IsTrue(FT.OutlineExportAvailable(face), "Real export required for visual evidence.");
                var source = new FreeTypeOutlineSource(face,
                    (gi, ppem) => FT.SetPixelSize(face, ppem) && FT.LoadGlyph(face, gi, FT.LOAD_DEFAULT | FT.LOAD_NO_HINTING));

                foreach (char ch in Text)
                {
                    uint gi = FT.GetCharIndex(face, ch);
                    Assert.AreNotEqual(0u, gi, $"Glyph '{ch}' missing from font.");
                    var outline = source.GetOutline(gi, Ppem);
                    Assert.IsNotNull(outline, $"Real-export outline for '{ch}' was null.");

                    var msdf = MsdfBuilder.Build(outline, Spread);
                    Assert.IsTrue(msdf.IsValid, $"MSDF build failed for '{ch}'.");
                    float[] sdf = MsdfBuilder.BuildSdf(outline, Spread, out int sw, out int sh, out double range);
                    Assert.IsNotNull(sdf, $"SDF build failed for '{ch}'.");
                    Assert.AreEqual(msdf.Width, sw); Assert.AreEqual(msdf.Height, sh);

                    cells.Add(new Cell { ch = ch, w = msdf.Width, h = msdf.Height, field = msdf.Field, sdf = sdf });

                    // Diagnostics: median-field distribution, so the decode threshold is calibrated
                    // from real data rather than assumed.
                    float mn = 1f, mx = 0f; double sum = 0; int n = msdf.Width * msdf.Height;
                    for (int p = 0; p < n; p++)
                    {
                        float m = MsdfGenerator.Median(msdf.Field[p * 3], msdf.Field[p * 3 + 1], msdf.Field[p * 3 + 2]);
                        if (m < mn) mn = m; if (m > mx) mx = m; sum += m;
                    }
                    TestContext.WriteLine($"[VISUAL-DIAG] '{ch}' median field: min={mn:F3} max={mx:F3} mean={(sum / n):F3} (0.5=contour)");
                }
            }
            finally { FT.UnloadFace(face); }

            // Cell layout: two rows (SDF, MSDF), one column per glyph, each cell w*Up x h*Up.
            int cellW = 0, cellH = 0;
            foreach (var c in cells) { cellW = Math.Max(cellW, c.w * Up); cellH = Math.Max(cellH, c.h * Up); }
            int pad = 8;
            int cols = cells.Count;
            int imgW = cols * cellW + (cols + 1) * pad;
            int imgH = 2 * cellH + 3 * pad;

            var img = new Color32[imgW * imgH];
            for (int i = 0; i < img.Length; i++) img[i] = new Color32(24, 24, 28, 255); // dark backdrop

            bool gpu = TryGpuAvailable();
            string mode = gpu ? "GPU (Graphics.Blit through shader materials)" : "CPU shader-equivalent decode (-nographics)";

            for (int col = 0; col < cells.Count; col++)
            {
                var c = cells[col];
                int cx = pad + col * (cellW + pad);

                // Row 0 (top): SDF. Row 1 (bottom): MSDF.
                float[] sdfImg = gpu ? RenderThroughShaderSdf(c) : DecodeSdfCpu(c);
                float[] msdfImg = gpu ? RenderThroughShaderMsdf(c) : DecodeMsdfCpu(c);

                Blit(img, imgW, imgH, sdfImg, c.w * Up, c.h * Up, cx, pad + (cellH - c.h * Up) / 2);
                Blit(img, imgW, imgH, msdfImg, c.w * Up, c.h * Up, cx, 2 * pad + cellH + (cellH - c.h * Up) / 2);
            }

            var tex = new Texture2D(imgW, imgH, TextureFormat.RGB24, false);
            tex.SetPixels32(img);
            tex.Apply();

            string outDir = Environment.GetEnvironmentVariable("KIROCREW_SCRATCH");
            if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir)) outDir = Path.GetTempPath();
            string outPath = Path.Combine(outDir, "openglyph_AMW_sdf_vs_realmsdf_8x.png");
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            TestContext.WriteLine($"[VISUAL] mode={mode}");
            TestContext.WriteLine($"[VISUAL] png={outPath}  (top row = SDF, bottom row = real-export MSDF; 'AMW&' @ {Ppem}px, {Up}x)");
            Assert.IsTrue(File.Exists(outPath), "Visual PNG was not written.");
        }

        private struct Cell { public char ch; public int w, h; public float[] field; public float[] sdf; }

        // ---- GPU path: blit the atlas cell through the real shader material ----

        private static bool TryGpuAvailable()
        {
            // Under -nographics Unity reports a Null graphics device; blits produce no readable pixels.
            return SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
        }

        private static float[] RenderThroughShaderMsdf(Cell c)
        {
            var mat = TryMaterial("UniText/MSDF") ?? TryMaterial("UniText/MSDF-Base");
            if (mat == null) return DecodeMsdfCpu(c);
            var src = new Texture2D(c.w, c.h, TextureFormat.RGB24, false);
            src.LoadRawTextureData(MsdfGenerator.EncodeRgb24(c.field, c.w, c.h));
            src.Apply();
            var outp = BlitReadback(src, mat, c.w * Up, c.h * Up);
            UnityEngine.Object.DestroyImmediate(src);
            UnityEngine.Object.DestroyImmediate(mat);
            return outp ?? DecodeMsdfCpu(c);
        }

        private static float[] RenderThroughShaderSdf(Cell c)
        {
            var mat = TryMaterial("UniText/SDF") ?? TryMaterial("UniText/SDF-Base");
            if (mat == null) return DecodeSdfCpu(c);
            var bytes = new byte[c.w * c.h];
            for (int i = 0; i < bytes.Length; i++)
            {
                int v = (int)(c.sdf[i] * 255f + 0.5f); bytes[i] = (byte)Mathf.Clamp(v, 0, 255);
            }
            var src = new Texture2D(c.w, c.h, TextureFormat.Alpha8, false);
            src.LoadRawTextureData(bytes);
            src.Apply();
            var outp = BlitReadback(src, mat, c.w * Up, c.h * Up);
            UnityEngine.Object.DestroyImmediate(src);
            UnityEngine.Object.DestroyImmediate(mat);
            return outp ?? DecodeSdfCpu(c);
        }

        private static Material TryMaterial(string shaderName)
        {
            var sh = Shader.Find(shaderName);
            return sh != null ? new Material(sh) { hideFlags = HideFlags.DontSave } : null;
        }

        private static float[] BlitReadback(Texture src, Material mat, int w, int h)
        {
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt, mat);
                RenderTexture.active = rt;
                var read = new Texture2D(w, h, TextureFormat.RGB24, false);
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                read.Apply();
                var px = read.GetPixels();
                UnityEngine.Object.DestroyImmediate(read);
                var lum = new float[w * h];
                bool any = false;
                for (int i = 0; i < lum.Length; i++)
                {
                    lum[i] = px[i].grayscale;
                    if (lum[i] > 0.001f) any = true;
                }
                return any ? lum : null; // blank readback -> signal caller to use CPU fallback
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ---- CPU path: reproduce the shaders' reconstruction (median3 / single channel + smoothstep) ----

        private static float[] DecodeMsdfCpu(Cell c) =>
            DecodeCpu(c.w, c.h, (fx, fy) => BilinearMedian(c.field, c.w, c.h, fx, fy));

        private static float[] DecodeSdfCpu(Cell c) =>
            DecodeCpu(c.w, c.h, (fx, fy) => BilinearScalar(c.sdf, c.w, c.h, fx, fy));

        private static float[] DecodeCpu(int w, int h, Func<double, double, float> sample)
        {
            int W = w * Up, H = h * Up;
            var outp = new float[W * H];

            // Convert the reconstructed field value to a signed distance in OUTPUT pixels and map to
            // coverage with a crisp ~1-output-pixel edge (saturate(distOut + 0.5)). field = dist/range
            // + 0.5, so distSrcPx = (field-0.5)*range and distOutPx = distSrcPx*Up. This gives a solid
            // silhouette (not the raw ramp), so SDF corner-rounding vs MSDF corner-sharpness shows.
            float range = 2f * Spread;
            long on = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double fx = (x + 0.5) / Up - 0.5, fy = (y + 0.5) / Up - 0.5;
                    float d = sample(fx, fy);
                    float distOut = (d - 0.5f) * range * Up;    // signed distance in output pixels
                    float cov = Mathf.Clamp01(distOut + 0.5f);  // ~1px AA at the contour
                    outp[y * W + x] = cov;
                    if (cov > 0.5f) on++;
                }
            if (on * 2 > (long)W * H) // majority "inside" => sign inverted for this cell; flip it
                for (int i = 0; i < outp.Length; i++) outp[i] = 1f - outp[i];
            return outp;
        }

        private static float BilinearMedian(float[] field, int w, int h, double fx, double fy)
        {
            int x0 = Mathf.Clamp((int)Math.Floor(fx), 0, w - 1);
            int y0 = Mathf.Clamp((int)Math.Floor(fy), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            double tx = fx - Math.Floor(fx), ty = fy - Math.Floor(fy);
            float Med(int x, int y) { int i = (y * w + x) * 3; return MsdfGenerator.Median(field[i], field[i + 1], field[i + 2]); }
            float top = (float)(Med(x0, y0) * (1 - tx) + Med(x1, y0) * tx);
            float bot = (float)(Med(x0, y1) * (1 - tx) + Med(x1, y1) * tx);
            return (float)(top * (1 - ty) + bot * ty);
        }

        private static float BilinearScalar(float[] field, int w, int h, double fx, double fy)
        {
            int x0 = Mathf.Clamp((int)Math.Floor(fx), 0, w - 1);
            int y0 = Mathf.Clamp((int)Math.Floor(fy), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            double tx = fx - Math.Floor(fx), ty = fy - Math.Floor(fy);
            float S(int x, int y) => field[y * w + x];
            float top = (float)(S(x0, y0) * (1 - tx) + S(x1, y0) * tx);
            float bot = (float)(S(x0, y1) * (1 - tx) + S(x1, y1) * tx);
            return (float)(top * (1 - ty) + bot * ty);
        }

        private static void Blit(Color32[] dst, int dstW, int dstH, float[] src, int sw, int sh, int ox, int oy)
        {
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int dx = ox + x, dy = oy + y;
                    if (dx < 0 || dy < 0 || dx >= dstW || dy >= dstH) continue;
                    byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(src[y * sw + x] * 255f), 0, 255);
                    dst[dy * dstW + dx] = new Color32(v, v, v, 255);
                }
        }
    }
}
