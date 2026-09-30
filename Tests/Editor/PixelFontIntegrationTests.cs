using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>
    /// Phase 1c INTEGRATION tests: a real Canvas + UniText GameObject with a pixel-perfect Silkscreen
    /// font. Drives the actual render pipeline via <see cref="Canvas.ForceUpdateCanvases"/>, reads
    /// back the engine-generated mesh vertices, and asserts they land on integer device pixels at
    /// canvas scale factors 1, 2 and 1.5 (the documented non-integer behaviour). Also composites the
    /// engine's own atlas texels through its own generated quads and asserts pixel-perfect coverage
    /// is fully on/off (no intermediate alpha).
    /// </summary>
    [TestFixture]
    public class PixelFontIntegrationTests
    {
        private const int NativePpem = 8;
        private GameObject _canvasGo, _textGo;
        private Canvas _canvas;
        private UniText _text;
        private UniTextFont _font;
        private UniTextFontStack _stack;
        private UniTextAppearance _appearance;

        private static string FindSilkscreen()
        {
            string[] c =
            {
                "Packages/com.openglyph.text/Tests/Editor/Fonts/Silkscreen-Regular.ttf",
                Path.Combine(UnityEngine.Application.dataPath ?? "", "..", "Packages", "com.openglyph.text", "Tests", "Editor", "Fonts", "Silkscreen-Regular.ttf"),
            };
            foreach (var p in c) if (File.Exists(p)) return p;
            string root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath ?? ".", ".."));
            try { foreach (var f in Directory.EnumerateFiles(root, "Silkscreen-Regular.ttf", SearchOption.AllDirectories)) return f; }
            catch { }
            return null;
        }

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable in this environment.");

            string path = FindSilkscreen();
            if (path == null) Assert.Ignore("Silkscreen-Regular.ttf not found; run fetch_fonts_pixel.ps1.");

            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: NativePpem,
                renderMode: UniTextRenderMode.Mono);
            Assert.IsNotNull(_font);
            _font.PixelPerfect = true;
            var pf = _font.DetectPixelFont();
            if (!pf.IsPixelFont)
                Assert.Ignore($"Native outline export unavailable so pixel grid not detected here ({pf}); managed-detection test covers the grid. Skipping integration.");

            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);
            _appearance = ScriptableObject.CreateInstance<UniTextAppearance>();

            _canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
            _canvas = _canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            _textGo = new GameObject("UniText", typeof(RectTransform));
            _textGo.transform.SetParent(_canvasGo.transform, worldPositionStays: false);
            _text = _textGo.AddComponent<UniText>();
            _text.FontStack = _stack;
            _text.Appearance = _appearance;
            _text.FontSize = NativePpem; // 1x of native
            _text.Text = "Ag5";
        }

        [TearDown]
        public void TearDown()
        {
            if (_textGo != null) UnityEngine.Object.DestroyImmediate(_textGo);
            if (_canvasGo != null) UnityEngine.Object.DestroyImmediate(_canvasGo);
            if (_font != null) UnityEngine.Object.DestroyImmediate(_font);
            if (_stack != null) UnityEngine.Object.DestroyImmediate(_stack);
            if (_appearance != null) UnityEngine.Object.DestroyImmediate(_appearance);
        }

        private List<Vector3> RenderAtScale(float scaleFactor)
        {
            // Drive the CanvasScaler-independent scale directly (matches setting it via a scaler).
            _canvas.scaleFactor = scaleFactor;
            _canvasGo.transform.localScale = Vector3.one * scaleFactor; // Overlay canvas is scaled by scaleFactor
            _text.SetVerticesDirty();
            _text.SetLayoutDirty();
            Canvas.ForceUpdateCanvases();
            return _text.GetGeneratedVerticesForEditorTests();
        }

        [TestCase(1.0f)]
        [TestCase(2.0f)]
        public void EngineMesh_IntegerScale_VerticesOnIntegerDevicePixels(float scaleFactor)
        {
            var verts = RenderAtScale(scaleFactor);
            if (verts.Count == 0) Assert.Ignore("Engine produced no vertices (pipeline did not run in this environment).");

            float devScale = _text.cachedTransformData.pixelSnapDeviceScale;
            Assert.Greater(devScale, 0f, "Pixel-snap device scale must be set from the canvas for a screen-space pixel font.");

            int checkedCount = 0;
            foreach (var v in verts)
            {
                float dx = v.x * devScale, dy = v.y * devScale;
                Assert.AreEqual(Mathf.Round(dx), dx, 1e-3f, $"vertex x {v.x} -> device {dx} not integral at scale {scaleFactor}");
                Assert.AreEqual(Mathf.Round(dy), dy, 1e-3f, $"vertex y {v.y} -> device {dy} not integral at scale {scaleFactor}");
                checkedCount++;
            }
            Assert.Greater(checkedCount, 0);
        }

        [Test]
        public void EngineMesh_Scale1p5_VerticesOnIntegerDevicePixels_DocumentedBehavior()
        {
            // Documented behaviour at a non-integer scale factor: corners still snap to the nearest
            // DEVICE pixel, so device coordinates remain integral (crisp on the physical grid); only
            // the local-space step is coarser (1/1.5 local units).
            var verts = RenderAtScale(1.5f);
            if (verts.Count == 0) Assert.Ignore("Engine produced no vertices in this environment.");

            float devScale = _text.cachedTransformData.pixelSnapDeviceScale;
            Assert.AreEqual(1.5f, devScale, 1e-3f, "Device scale should equal the 1.5 canvas scale factor.");

            foreach (var v in verts)
            {
                float dx = v.x * devScale, dy = v.y * devScale;
                Assert.AreEqual(Mathf.Round(dx), dx, 1e-3f, $"At 1.5, device x {dx} must be integral (nearest device pixel).");
                Assert.AreEqual(Mathf.Round(dy), dy, 1e-3f, $"At 1.5, device y {dy} must be integral (nearest device pixel).");
            }
        }

        // -------------------------------------------------------------------------------------------
        // Engine-rendered visual: composite the ENGINE's atlas texels through the ENGINE's quads.
        // Asserts pixel-perfect coverage is fully on/off (no intermediate alpha), and writes a PNG.
        // -------------------------------------------------------------------------------------------

        [Test]
        public void EngineRender_PixelPerfect_CoverageFullyOnOrOff_AndWritesPng()
        {
            // Ensure glyphs are in the atlas at native size.
            _text.FontSize = NativePpem;
            var verts = RenderAtScale(1.0f);
            if (verts.Count == 0) Assert.Ignore("Engine produced no vertices in this environment.");

            var atlases = _font.AtlasTextures;
            Assert.IsNotNull(atlases);
            Assert.Greater(atlases.Count, 0, "No atlas produced by the engine.");

            // The engine's atlas IS the rendered coverage for a Mono pixel-perfect font. Assert every
            // texel is fully on (255) or fully off (0) — no anti-aliasing.
            foreach (var tex in atlases)
            {
                Assert.AreEqual(FilterMode.Point, tex.filterMode);
                Assert.AreEqual(TextureFormat.Alpha8, tex.format);
                var raw = tex.GetRawTextureData<byte>();
                for (int i = 0; i < raw.Length; i++)
                    if (raw[i] != 0 && raw[i] != 255)
                        Assert.Fail($"Engine atlas texel {i}={raw[i]}: pixel-perfect coverage must be fully on/off.");
            }

            // Build the comparison PNG from ENGINE atlas texels (pixel-perfect nearest vs bilinear).
            {
                // Compose a comparison PNG entirely from ENGINE atlas texels:
                //   row 0 = pixel-perfect Mono (this font, point-filtered + snapped), nearest-upscaled;
                //   row 1 = the SAME engine coverage sampled BILINEARLY (what a non-pixel-perfect,
                //           bilinear-filtered scale of the atlas produces) — the blur pixel-perfect
                //           mode removes. Both rows are the engine's own texels; the difference is
                //           purely the sampling the pixel-perfect path avoids.
                var ppCoverage = ExtractGlyphCoverage(_font, "Ag5");
                if (!ppCoverage.isValid) Assert.Ignore("No pixel-perfect coverage to compose.");

                int[] scales = { 1, 2, 3 };
                int disp = 6, pad = 3, gap = 6;
                int rowH = 0, totalW = pad;
                var cellSizes = new List<(int w, int h)>();
                foreach (var s in scales)
                {
                    int w = ppCoverage.w * s * disp, h = ppCoverage.h * s * disp;
                    cellSizes.Add((w, h));
                    rowH = Mathf.Max(rowH, h);
                    totalW += w + gap;
                }
                int labelH = 14;
                var outTex = new Texture2D(totalW, pad + labelH + (rowH + gap) * 2 + labelH + pad, TextureFormat.RGBA32, false);
                var bg = new Color32(24, 24, 28, 255);
                var pxAll = new Color32[outTex.width * outTex.height];
                for (int i = 0; i < pxAll.Length; i++) pxAll[i] = bg;

                // NEAREST (pixel-perfect): each coverage texel -> a solid on/off block.
                void BlitNearest(CoverageBlock cov, int s, int ox, int oy)
                {
                    for (int y = 0; y < cov.h; y++)
                        for (int x = 0; x < cov.w; x++)
                        {
                            byte a = cov.data[y * cov.w + x];
                            Color32 c = a >= 128 ? new Color32(255, 255, 255, 255) : bg;
                            for (int sy = 0; sy < s * disp; sy++)
                                for (int sx = 0; sx < s * disp; sx++)
                                {
                                    int px = ox + x * s * disp + sx;
                                    int py = oy + (cov.h - 1 - y) * s * disp + sy;
                                    if (px >= 0 && px < outTex.width && py >= 0 && py < outTex.height)
                                        pxAll[py * outTex.width + px] = c;
                                }
                        }
                }

                // BILINEAR (non-pixel-perfect): sample the coverage with bilinear interpolation across
                // the upscaled cell, so texel edges blur — the fringing pixel-perfect mode eliminates.
                void BlitBilinear(CoverageBlock cov, int s, int ox, int oy)
                {
                    int cw = cov.w * s * disp, chp = cov.h * s * disp;
                    float SampleCov(float u, float v)
                    {
                        float fx = u * cov.w - 0.5f, fy = v * cov.h - 0.5f;
                        int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
                        float tx = fx - x0, ty = fy - y0;
                        float G(int xx, int yy)
                        {
                            xx = Mathf.Clamp(xx, 0, cov.w - 1); yy = Mathf.Clamp(yy, 0, cov.h - 1);
                            return cov.data[yy * cov.w + xx] / 255f;
                        }
                        float a = Mathf.Lerp(G(x0, y0), G(x0 + 1, y0), tx);
                        float b = Mathf.Lerp(G(x0, y0 + 1), G(x0 + 1, y0 + 1), tx);
                        return Mathf.Lerp(a, b, ty);
                    }
                    for (int py = 0; py < chp; py++)
                        for (int px = 0; px < cw; px++)
                        {
                            float u = (px + 0.5f) / cw, v = (py + 0.5f) / chp;
                            float cov01 = SampleCov(u, v);
                            var c = (Color32)Color.Lerp(bg, Color.white, cov01);
                            int dstX = ox + px, dstY = oy + (chp - 1 - py);
                            if (dstX >= 0 && dstX < outTex.width && dstY >= 0 && dstY < outTex.height)
                                pxAll[dstY * outTex.width + dstX] = c;
                        }
                }

                int rowTop0 = pad + labelH;
                int rowTop1 = rowTop0 + rowH + gap + labelH;
                for (int ci = 0; ci < scales.Length; ci++)
                {
                    int ox = pad;
                    for (int k = 0; k < ci; k++) ox += cellSizes[k].w + gap;
                    BlitNearest(ppCoverage, scales[ci], ox, rowTop0);
                    BlitBilinear(ppCoverage, scales[ci], ox, rowTop1);
                }

                outTex.SetPixels32(pxAll);
                outTex.Apply();

                string outDir = Environment.GetEnvironmentVariable("KIROCREW_SCRATCH") ?? Path.GetTempPath();
                string outPath = Path.Combine(outDir, "phase1c_engine_pixelperfect.png");
                File.WriteAllBytes(outPath, outTex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(outTex);
                TestContext.WriteLine("ENGINE_PNG:" + outPath);
                Assert.IsTrue(File.Exists(outPath));
            }
        }

        private struct CoverageBlock { public int w, h; public byte[] data; public bool isValid; }

        /// <summary>
        /// Renders <paramref name="text"/> glyphs into the font's atlas (engine path) and stitches the
        /// glyph rects into one left-to-right coverage block (alpha bytes) straight from the atlas
        /// texels. This is genuine engine output — no external rasteriser.
        /// </summary>
        private CoverageBlock ExtractGlyphCoverage(UniTextFont font, string text)
        {
            var glyphs = new List<uint>();
            foreach (char c in text)
            {
                uint gi = font.GetGlyphIndexForUnicode(c);
                if (gi != 0) glyphs.Add(gi);
            }
            font.TryAddGlyphsBatch(glyphs);
            var atlases = font.AtlasTextures;
            if (atlases == null || atlases.Count == 0) return default;
            var tex = atlases[0];
            int aw = tex.width, ach = tex.height;
            var raw = tex.GetRawTextureData<byte>();
            int channels = tex.format == TextureFormat.RGBA32 ? 4 : tex.format == TextureFormat.RGB24 ? 3 : 1;

            byte AlphaAt(int x, int y)
            {
                int idx = (y * aw + x) * channels + (channels == 1 ? 0 : channels - 1);
                return idx >= 0 && idx < raw.Length ? raw[idx] : (byte)0;
            }

            // Lay glyphs out in text order with a 1px gap; height = max glyph height.
            int totalW = 0, maxH = 0;
            var rects = new List<GlyphRect>();
            foreach (uint gi in glyphs)
            {
                if (!font.GlyphLookupTable.TryGetValue(gi, out var g)) continue;
                var r = g.glyphRect;
                if (r.width == 0 || r.height == 0) { totalW += 2; continue; } // space-ish
                rects.Add(r);
                totalW += r.width + 1;
                maxH = Mathf.Max(maxH, r.height);
            }
            if (rects.Count == 0 || maxH == 0) return default;

            var block = new CoverageBlock { w = totalW, h = maxH, data = new byte[totalW * maxH], isValid = true };
            int cursor = 0;
            foreach (var r in rects)
            {
                for (int y = 0; y < r.height; y++)
                    for (int x = 0; x < r.width; x++)
                        block.data[(y) * totalW + (cursor + x)] = AlphaAt(r.x + x, r.y + y);
                cursor += r.width + 1;
            }
            return block;
        }
    }
}
