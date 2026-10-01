using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using LightSide;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// Produces VISUAL EVIDENCE for the real-export MSDF pipeline by rendering glyph atlas cells
    /// THROUGH THE SHIPPED Unity shaders (<c>UniText/MSDF SSD</c> and <c>UniText/SDF SSD</c>), not a
    /// CPU re-implementation of them. Each glyph's MSDF (RGB24) and SDF (Alpha8) atlas is uploaded to
    /// a texture, drawn on a world-space quad with the real shader material, and captured by an
    /// orthographic camera into a RenderTexture, then read back and tiled into one labelled PNG per
    /// run under <c>Tests/Editor/Output</c>.
    ///
    /// There is deliberately NO silhouette "flip" fallback here: contour orientation is now resolved
    /// at the source by <see cref="Shape.OrientContours"/>, so a glyph that renders inside-out is a
    /// real defect the evidence must show, not something the harness papers over.
    ///
    /// WindowsEditor only (depends on the real ut_ft_get_outline_data export). Requires a graphics
    /// device: run batchmode WITHOUT -nographics. Under a Null device the test is INCONCLUSIVE (it
    /// says so and still writes the raw atlas montage) rather than silently decoding on the CPU.
    /// </summary>
    [TestFixture]
    public class MsdfVisualEvidenceTests
    {
        private const string Text = "AMWNkx&g@";
        private const int Ppem = 64;
        private const int Spread = 8;
        private const int Up = 8;

        private static string OutputDir()
        {
            // Tests/Editor/Output inside the package, discovered by walking up from the assembly.
            string start = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath ?? ".", ".."));
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string tests = Path.Combine(dir.FullName, "Tests", "Editor");
                if (Directory.Exists(tests))
                {
                    string outp = Path.Combine(tests, "Output");
                    Directory.CreateDirectory(outp);
                    return outp;
                }
                // Also try the embedded-package layout Packages/com.openglyph.text/Tests/Editor.
                string pkg = Path.Combine(dir.FullName, "Packages", "com.openglyph.text", "Tests", "Editor");
                if (Directory.Exists(pkg))
                {
                    string outp = Path.Combine(pkg, "Output");
                    Directory.CreateDirectory(outp);
                    return outp;
                }
                dir = dir.Parent;
            }
            string fallback = Environment.GetEnvironmentVariable("KIROCREW_SCRATCH");
            if (string.IsNullOrEmpty(fallback)) fallback = Path.GetTempPath();
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void Render_Glyphs_SDF_vs_MSDF_ThroughRealShaders_Png()
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
                    if (gi == 0) { TestContext.WriteLine($"[VISUAL] '{ch}' missing from font — skipped."); continue; }
                    var outline = source.GetOutline(gi, Ppem);
                    if (outline == null) { TestContext.WriteLine($"[VISUAL] '{ch}' outline null — skipped."); continue; }

                    var msdf = MsdfBuilder.Build(outline, Spread);
                    Assert.IsTrue(msdf.IsValid, $"MSDF build failed for '{ch}'.");
                    float[] sdf = MsdfBuilder.BuildSdf(outline, Spread, out int sw, out int sh, out double range);
                    Assert.IsNotNull(sdf, $"SDF build failed for '{ch}'.");
                    Assert.AreEqual(msdf.Width, sw); Assert.AreEqual(msdf.Height, sh);

                    cells.Add(new Cell { ch = ch, w = msdf.Width, h = msdf.Height, msdfField = msdf.Field, sdf = sdf });
                }
            }
            finally { FT.UnloadFace(face); }

            Assert.GreaterOrEqual(cells.Count, 5, "Too few glyphs built for visual evidence.");

            bool gpu = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            Material msdfMat = gpu ? TryMaterial("UniText/MSDF SSD", "UniText/MSDF") : null;
            Material sdfMat = gpu ? TryMaterial("UniText/SDF SSD", "UniText/SDF") : null;
            bool realShaders = gpu && msdfMat != null && sdfMat != null;

            string mode = realShaders
                ? $"GPU through real shaders (MSDF='{msdfMat.shader.name}', SDF='{sdfMat.shader.name}')"
                : (gpu ? "GPU available but UniText shaders not found — raw atlas montage only"
                       : "Null graphics device (-nographics) — raw atlas montage only, INCONCLUSIVE for shader output");
            TestContext.WriteLine($"[VISUAL] mode = {mode}");

            // Build the montage: two rows (top=SDF, bottom=MSDF), one column per glyph.
            int cellW = 0, cellH = 0;
            foreach (var c in cells) { cellW = Math.Max(cellW, c.w * Up); cellH = Math.Max(cellH, c.h * Up); }
            int pad = 8, cols = cells.Count;
            int imgW = cols * cellW + (cols + 1) * pad;
            int imgH = 2 * cellH + 3 * pad;
            var img = new Color32[imgW * imgH];
            for (int i = 0; i < img.Length; i++) img[i] = new Color32(24, 24, 28, 255);

            for (int col = 0; col < cells.Count; col++)
            {
                var c = cells[col];
                int cx = pad + col * (cellW + pad);
                float[] sdfLum, msdfLum;

                if (realShaders)
                {
                    sdfLum = RenderThroughShader(SdfAtlas(c), sdfMat, c.w * Up, c.h * Up);
                    msdfLum = RenderThroughShader(MsdfAtlas(c), msdfMat, c.w * Up, c.h * Up);
                }
                else
                {
                    // No shader output available: show the raw atlas median/alpha, upscaled nearest,
                    // so the montage still carries information. NOT a shader reconstruction.
                    sdfLum = RawUpscaleScalar(c.sdf, c.w, c.h);
                    msdfLum = RawUpscaleMedian(c.msdfField, c.w, c.h);
                }

                Blit(img, imgW, imgH, sdfLum, c.w * Up, c.h * Up, cx, pad + (cellH - c.h * Up) / 2);
                Blit(img, imgW, imgH, msdfLum, c.w * Up, c.h * Up, cx, 2 * pad + cellH + (cellH - c.h * Up) / 2);
            }

            if (msdfMat != null) UnityEngine.Object.DestroyImmediate(msdfMat);
            if (sdfMat != null) UnityEngine.Object.DestroyImmediate(sdfMat);

            var tex = new Texture2D(imgW, imgH, TextureFormat.RGB24, false);
            tex.SetPixels32(img); tex.Apply();
            string outDir = OutputDir();
            string name = realShaders ? "glyphs_sdf_vs_msdf_realshaders_8x.png" : "glyphs_sdf_vs_msdf_atlas_8x.png";
            string outPath = Path.Combine(outDir, name);
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            TestContext.WriteLine($"[VISUAL] text='{Text}' ppem={Ppem} spread={Spread} up={Up}x");
            TestContext.WriteLine($"[VISUAL] top row = SDF, bottom row = MSDF");
            TestContext.WriteLine($"[VISUAL] png = {outPath}");
            Assert.IsTrue(File.Exists(outPath), "Visual PNG was not written.");

            if (!realShaders)
                Assert.Inconclusive($"Shader-rendered evidence unavailable ({mode}). Raw-atlas montage written to {outPath}. " +
                    "Re-run batchmode WITHOUT -nographics to capture the real shader output.");
        }

        private struct Cell { public char ch; public int w, h; public float[] msdfField; public float[] sdf; }

        // ---- real-shader render: textured quad -> orthographic camera -> RenderTexture readback ----

        private static Material TryMaterial(params string[] shaderNames)
        {
            foreach (var n in shaderNames)
            {
                var sh = Shader.Find(n);
                if (sh != null) return new Material(sh) { hideFlags = HideFlags.DontSave };
            }
            return null;
        }

        private static Texture2D MsdfAtlas(Cell c)
        {
            var t = new Texture2D(c.w, c.h, TextureFormat.RGB24, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            t.LoadRawTextureData(MsdfGenerator.EncodeRgb24(c.msdfField, c.w, c.h));
            t.Apply();
            return t;
        }

        private static Texture2D SdfAtlas(Cell c)
        {
            var bytes = new byte[c.w * c.h];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(c.sdf[i] * 255f), 0, 255);
            var t = new Texture2D(c.w, c.h, TextureFormat.Alpha8, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            t.LoadRawTextureData(bytes);
            t.Apply();
            return t;
        }

        /// <summary>
        /// Draws a unit quad textured with <paramref name="atlas"/> and the real shader material into
        /// a <paramref name="w"/>x<paramref name="h"/> RenderTexture via an orthographic camera, then
        /// reads back the luminance. Vertex colour is white and the UGUI Z-test is forced to Always so
        /// the TMP-derived shaders draw outside a Canvas. Destroys the atlas when done.
        /// </summary>
        private static float[] RenderThroughShader(Texture2D atlas, Material mat, int w, int h)
        {
            // Feed the TMP-derived shader the uniforms it needs for a non-zero face. The critical
            // ones are the per-vertex gradientScale (TEXCOORD0.z) and xScaleVal (TEXCOORD0.w): if
            // they are zero the shader's `scale` is zero and nothing is drawn. gradientScale is the
            // atlas pxRange (2*Spread); xScaleVal is the glyph pixel size.
            mat.SetTexture("_MainTex", atlas);
            if (mat.HasProperty("_TextureSampleAdd")) mat.SetFloat("_TextureSampleAdd", 0f);
            if (mat.HasProperty("_FaceColor")) mat.SetColor("_FaceColor", Color.white);
            if (mat.HasProperty("_FaceDilate")) mat.SetFloat("_FaceDilate", 0f);
            if (mat.HasProperty("_OutlineWidth")) mat.SetFloat("_OutlineWidth", 0f);
            if (mat.HasProperty("_Sharpness")) mat.SetFloat("_Sharpness", 0f);
            if (mat.HasProperty("_ScaleRatioA")) mat.SetFloat("_ScaleRatioA", 1f);
            if (mat.HasProperty("_ScaleRatioB")) mat.SetFloat("_ScaleRatioB", 1f);
            if (mat.HasProperty("_ScaleRatioC")) mat.SetFloat("_ScaleRatioC", 1f);
            if (mat.HasProperty("_WeightNormal")) mat.SetFloat("_WeightNormal", 0f);
            if (mat.HasProperty("_WeightBold")) mat.SetFloat("_WeightBold", 0f);
            if (mat.HasProperty("_ClipRect")) mat.SetVector("_ClipRect", new Vector4(-1e6f, -1e6f, 1e6f, 1e6f));
            // Force the UGUI global Z-test so the quad is not depth-rejected off-canvas.
            mat.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);

            float gradientScale = 2f * Spread;  // atlas pxRange
            float xScaleVal = Ppem;             // glyph size in px (TMP's xScale)
            float spreadRatio = (2f * Spread) / (float)Ppem;

            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();

            var camGo = new GameObject("VisualEvidenceCam") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 0.5f;          // view height = 1 unit == the quad
            cam.aspect = 1f;
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.transform.position = new Vector3(0, 0, -1);
            cam.transform.rotation = Quaternion.identity;
            cam.targetTexture = rt;

            var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.hideFlags = HideFlags.DontSave;
            UnityEngine.Object.DestroyImmediate(quadGo.GetComponent<Collider>());
            quadGo.transform.position = Vector3.zero;
            quadGo.transform.localScale = Vector3.one;
            var mr = quadGo.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;

            var mf = quadGo.GetComponent<MeshFilter>();
            var white32 = (Color32)Color.white;
            var mesh = new Mesh { hideFlags = HideFlags.DontSave };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0),
                new Vector3(0.5f, 0.5f, 0), new Vector3(0.5f, -0.5f, 0)
            };
            // TEXCOORD0 = (u, v, gradientScale, xScaleVal); TEXCOORD1 = (spreadRatio, u, v, 0).
            var uv0 = new List<Vector4>
            {
                new Vector4(0, 0, gradientScale, xScaleVal),
                new Vector4(0, 1, gradientScale, xScaleVal),
                new Vector4(1, 1, gradientScale, xScaleVal),
                new Vector4(1, 0, gradientScale, xScaleVal),
            };
            var uv1 = new List<Vector4>
            {
                new Vector4(spreadRatio, 0, 0, 0),
                new Vector4(spreadRatio, 0, 1, 0),
                new Vector4(spreadRatio, 1, 1, 0),
                new Vector4(spreadRatio, 1, 0, 0),
            };
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.colors32 = new Color32[] { white32, white32, white32, white32 };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;

            float[] lum;
            var prevActive = RenderTexture.active;
            try
            {
                cam.Render();
                RenderTexture.active = rt;
                var read = new Texture2D(w, h, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                read.Apply();
                var px = read.GetPixels();
                lum = new float[w * h];
                for (int i = 0; i < lum.Length; i++)
                {
                    // The shader uses Blend One OneMinusSrcAlpha and outputs PREMULTIPLIED colour over
                    // a transparent clear, so RGB already encodes coverage*faceColor (white face =>
                    // RGB ~= coverage). Use the max channel as the glyph's luminance.
                    var c = px[i];
                    lum[i] = Mathf.Clamp01(Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
                }
                UnityEngine.Object.DestroyImmediate(read);
            }
            finally
            {
                RenderTexture.active = prevActive;
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(quadGo);
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(mesh);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(atlas);
            }
            return lum;
        }

        // ---- raw-atlas upscales used ONLY when no shader output is available ----

        private static float[] RawUpscaleMedian(float[] field, int w, int h)
        {
            int W = w * Up, H = h * Up; var o = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int sx = Mathf.Clamp(x / Up, 0, w - 1), sy = Mathf.Clamp(y / Up, 0, h - 1);
                    int i = (sy * w + sx) * 3;
                    o[y * W + x] = MsdfGenerator.Median(field[i], field[i + 1], field[i + 2]) > 0.5f ? 1f : 0f;
                }
            return o;
        }

        private static float[] RawUpscaleScalar(float[] field, int w, int h)
        {
            int W = w * Up, H = h * Up; var o = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int sx = Mathf.Clamp(x / Up, 0, w - 1), sy = Mathf.Clamp(y / Up, 0, h - 1);
                    o[y * W + x] = field[sy * w + sx] > 0.5f ? 1f : 0f;
                }
            return o;
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
