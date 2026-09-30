using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// Optional visual evidence: renders 'A' as MSDF, then dumps (a) the raw RGB MSDF atlas cell
    /// and (b) a 4x nearest vs bilinear-median upscale, to PNGs under KIROCREW_SCRATCH (or the
    /// temp folder). Not an assertion of correctness — it always passes if it can write a file.
    /// </summary>
    [TestFixture]
    public class MsdfVisualDumpTests
    {
        [Test]
        public void Dump_MSDF_A_ComparisonPng()
        {
            string path = MsdfTestUtil.FindNotoSansPath();
            if (path == null) Assert.Ignore("Font not found; skipping visual dump.");

            var reader = TtfGlyfReader.FromFile(path);
            int gid = reader.GetGlyphId('A');
            var outline = reader.ReadOutline(gid, 40);
            if (outline == null) Assert.Ignore("'A' outline unavailable.");

            int spread = 6;
            var res = MsdfBuilder.Build(outline, spread);
            Assert.IsTrue(res.IsValid);

            string outDir = Environment.GetEnvironmentVariable("KIROCREW_SCRATCH");
            if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir))
                outDir = Path.GetTempPath();

            // Raw MSDF cell (RGB), y-flip already applied by Build (top-down rows).
            var raw = new Texture2D(res.Width, res.Height, TextureFormat.RGB24, false);
            raw.LoadRawTextureData(res.Rgb);
            raw.Apply();
            string rawPath = Path.Combine(outDir, "msdf_A_raw_rgb.png");
            File.WriteAllBytes(rawPath, raw.EncodeToPNG());
            Object.DestroyImmediate(raw);

            // 4x upscales: nearest of the median (SDF-like) vs bilinear median (MSDF).
            int up = 4;
            int W = res.Width * up, H = res.Height * up;
            var sdfTex = new Texture2D(W, H, TextureFormat.RGB24, false);
            var msdfTex = new Texture2D(W, H, TextureFormat.RGB24, false);
            var field = res.Field; // bottom-up
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    double fx = (double)x / up, fy = (double)y / up;
                    float nearest = MedianAt(field, res.Width, res.Height, (int)Math.Round(fx), (int)Math.Round(fy));
                    float bil = BilinearMedian(field, res.Width, res.Height, fx, fy);
                    byte sN = (byte)(Mathf.Clamp01(nearest) * 255);
                    byte sB = (byte)(Mathf.Clamp01(bil) * 255);
                    // Map so inside (>0.5) shows bright.
                    sdfTex.SetPixel(x, y, new Color32(sN, sN, sN, 255));
                    msdfTex.SetPixel(x, y, new Color32(sB, sB, sB, 255));
                }
            }
            sdfTex.Apply(); msdfTex.Apply();
            string sdfPath = Path.Combine(outDir, "msdf_A_upscale_sdf_median_nearest.png");
            string msdfPath = Path.Combine(outDir, "msdf_A_upscale_msdf_median_bilinear.png");
            File.WriteAllBytes(sdfPath, sdfTex.EncodeToPNG());
            File.WriteAllBytes(msdfPath, msdfTex.EncodeToPNG());
            Object.DestroyImmediate(sdfTex);
            Object.DestroyImmediate(msdfTex);

            TestContext.WriteLine("[MSDF-VISUAL] raw:   " + rawPath);
            TestContext.WriteLine("[MSDF-VISUAL] sdf:   " + sdfPath);
            TestContext.WriteLine("[MSDF-VISUAL] msdf:  " + msdfPath);
            Assert.IsTrue(File.Exists(msdfPath));
        }

        private static float MedianAt(float[] field, int w, int h, int x, int y)
        {
            x = Mathf.Clamp(x, 0, w - 1); y = Mathf.Clamp(y, 0, h - 1);
            int i = (y * w + x) * 3;
            return MsdfGenerator.Median(field[i], field[i + 1], field[i + 2]);
        }

        private static float BilinearMedian(float[] field, int w, int h, double fx, double fy)
        {
            int x0 = Mathf.Clamp((int)Math.Floor(fx), 0, w - 1);
            int y0 = Mathf.Clamp((int)Math.Floor(fy), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            double tx = fx - x0, ty = fy - y0;
            float M(int x, int y) { int i = (y * w + x) * 3; return MsdfGenerator.Median(field[i], field[i + 1], field[i + 2]); }
            float top = (float)(M(x0, y0) * (1 - tx) + M(x1, y0) * tx);
            float bot = (float)(M(x0, y1) * (1 - tx) + M(x1, y1) * tx);
            return (float)(top * (1 - ty) + bot * ty);
        }
    }
}
