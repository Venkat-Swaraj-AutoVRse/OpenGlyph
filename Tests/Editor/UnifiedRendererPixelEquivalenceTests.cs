using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 3 acceptance: PIXEL equivalence of the legacy per-segment
    /// path and the unified <c>UniText/Uber</c> path for the same text. Both paths' actual meshes are
    /// rendered through MATCHED NEUTRAL display shaders over the same glyph atlas (legacy: the
    /// Texture2D SDF/MSDF reconstruction from <see cref="EngineRenderHarness"/>; unified: an identical
    /// Texture2DArray reconstruction), via a <see cref="CommandBuffer"/> into a RenderTexture, then
    /// compared per pixel. Matching the neutral shaders holds the AA math constant so the diff
    /// isolates what the unified path actually changes — geometry, UVs, and atlas-content fidelity
    /// (pages-as-slices) — rather than production-shader constant differences. Side-by-side + diff
    /// PNGs for SDF and MSDF are written under <c>Documentation/Design/evidence/</c>.
    /// </summary>
    /// <remarks>
    /// This neutral-shader comparison is the reliable one in a batchmode editor: rendering the real
    /// legacy UGUI multi-CanvasRenderer path to an offscreen RenderTexture yields a black image (the
    /// per-child UI submission does not reproduce offscreen), so a production-shader comparison is not
    /// trustworthy here. The geometry equivalence test proves the meshes are identical; this proves the
    /// same atlas texels are addressed through the array indirection.
    /// </remarks>
    public class UnifiedRendererPixelEquivalenceTests
    {
        private const int W = 256, H = 64;
        private readonly List<Object> _junk = new();
        private Material _legacyMat, _arrayMat;
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexArray = Shader.PropertyToID("_MainTexArray");

        private static string EvidenceDir()
        {
            string noto = MsdfTestUtil.FindNotoSansPath();
            string pkgRoot = noto != null ? Directory.GetParent(Path.GetDirectoryName(noto))?.FullName : null;
            string dir = pkgRoot != null
                ? Path.Combine(pkgRoot, "Documentation", "Design", "evidence")
                : Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "evidence");
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            if (_legacyMat != null) Object.DestroyImmediate(_legacyMat);
            if (_arrayMat != null) Object.DestroyImmediate(_arrayMat);
            _legacyMat = _arrayMat = null;
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        private UniTextFont MakeFont(string file, UniTextRenderMode mode)
        {
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) return null;
            string p = Path.Combine(Path.GetDirectoryName(noto), file);
            if (!File.Exists(p)) return null;
            var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(p), 48, 0.25f, mode);
            if (f != null) _junk.Add(f);
            return f;
        }

        // Renders the component's actual meshes through NEUTRAL shaders into a WxH RGBA texture.
        private Texture2D Render(UniText.UnifiedRendererMode mode, UniTextFontStack stack, UniTextAppearance app, string text, bool msdf, out int nonBg)
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(W, H);
            var go = new GameObject("UT", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(canvasGo.transform, false);
            var rtf = (RectTransform)go.transform; rtf.anchorMin = Vector2.zero; rtf.anchorMax = Vector2.one; rtf.offsetMin = rtf.offsetMax = Vector2.zero;
            var t = go.AddComponent<UniText>();
            t.UnifiedRenderer = mode; t.FontStack = stack; t.Appearance = app; t.FontSize = 36f; t.Text = text;
            Canvas.ForceUpdateCanvases();

            var items = t.GetDrawnMeshMaterialsForTests();
            var min = new Vector3(float.MaxValue, float.MaxValue, 0);
            var max = new Vector3(float.MinValue, float.MinValue, 0);
            foreach (var (mesh, _, _) in items) { if (mesh == null) continue; min = Vector3.Min(min, mesh.bounds.min); max = Vector3.Max(max, mesh.bounds.max); }

            var rt = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32); rt.Create();
            var cb = new CommandBuffer { name = "PixelEquiv" };
            cb.SetRenderTarget(rt);
            cb.ClearRenderTarget(true, true, Color.black);
            cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0, W, 0, H, -100f, 100f));
            if (min.x <= max.x)
            {
                float gw = Mathf.Max(1e-3f, max.x - min.x), gh = Mathf.Max(1e-3f, max.y - min.y);
                float scale = Mathf.Min((W - 8) / gw, (H - 8) / gh);
                var m = Matrix4x4.TRS(new Vector3(4, 4, 0), Quaternion.identity, new Vector3(scale, scale, 1))
                      * Matrix4x4.Translate(new Vector3(-min.x, -min.y, 0));
                bool unified = mode == UniText.UnifiedRendererMode.ForceOn;
                foreach (var (mesh, mat, atlasTex) in items)
                {
                    if (mesh == null) continue;
                    if (unified)
                    {
                        // Neutral ARRAY shader; copy the uber material's bound array.
                        _arrayMat ??= new Material(Shader.Find("Hidden/OpenGlyphArrayPreview"));
                        var arrTex = mat != null ? mat.GetTexture(MainTexArray) : null;
                        var mpb = new MaterialPropertyBlock();
                        if (arrTex != null) mpb.SetTexture(MainTexArray, arrTex);
                        cb.DrawMesh(mesh, m, _arrayMat, 0, -1, mpb);
                    }
                    else
                    {
                        // Neutral Texture2D shader (SDF or MSDF to match the entry's atlas), atlas via MPB.
                        _legacyMat ??= EngineRenderHarness.BuildAtlasMaterial(msdf);
                        var mpb = new MaterialPropertyBlock();
                        if (atlasTex != null) mpb.SetTexture(MainTex, atlasTex);
                        cb.DrawMesh(mesh, m, _legacyMat, 0, -1, mpb);
                    }
                }
            }
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();

            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            rt.Release(); Object.DestroyImmediate(rt);

            var px = tex.GetPixels32(); nonBg = 0;
            foreach (var p in px) if (p.r > 8 || p.g > 8 || p.b > 8) nonBg++;

            Object.DestroyImmediate(go); _junk.Remove(go);
            Object.DestroyImmediate(canvasGo); _junk.Remove(canvasGo);
            UnifiedRenderBuilder.ResetShared();
            return tex;
        }

        private (UniTextFontStack stack, UniTextAppearance app) Setup(UniTextFont font)
        {
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(font);
            var app = ScriptableObject.CreateInstance<UniTextAppearance>(); _junk.Add(app);
            return (stack, app);
        }

        private void RunCase(string name, UniTextFont font, string text, bool msdf)
        {
            var (stack, app) = Setup(font);
            var off = Render(UniText.UnifiedRendererMode.ForceOff, stack, app, text, msdf, out int nbOff);
            // Reset shared material so the OFF render's atlas publish doesn't contaminate ON.
            UnifiedRenderBuilder.ResetShared(); SharedGlyphAtlas.Clear();
            _legacyMat = null; _arrayMat = null; // force fresh neutral mats
            var on = Render(UniText.UnifiedRendererMode.ForceOn, stack, app, text, msdf, out int nbOn);

            // Compare + diff.
            var pa = off.GetPixels32(); var pb = on.GetPixels32();
            int maxD = 0, over = 0;
            var dpx = new Color32[pa.Length];
            for (int i = 0; i < pa.Length; i++)
            {
                int d = Mathf.Max(Mathf.Abs(pa[i].r - pb[i].r), Mathf.Max(Mathf.Abs(pa[i].g - pb[i].g), Mathf.Abs(pa[i].b - pb[i].b)));
                if (d > maxD) maxD = d; if (d > 2) over++;
                byte dv = (byte)Mathf.Min(255, d * 8); dpx[i] = new Color32(dv, dv, dv, 255);
            }
            float frac = (float)over / pa.Length;
            var diff = new Texture2D(W, H, TextureFormat.RGBA32, false); diff.SetPixels32(dpx); diff.Apply();

            // Side-by-side PNG: off | on | diff.
            var combo = new Texture2D(W * 3 + 8, H, TextureFormat.RGBA32, false);
            var clr = new Color32[(W * 3 + 8) * H]; for (int i = 0; i < clr.Length; i++) clr[i] = new Color32(32, 32, 32, 255);
            combo.SetPixels32(clr);
            combo.SetPixels(0, 0, W, H, off.GetPixels());
            combo.SetPixels(W + 4, 0, W, H, on.GetPixels());
            combo.SetPixels(W * 2 + 8, 0, W, H, diff.GetPixels());
            combo.Apply();
            string pngPath = Path.Combine(EvidenceDir(), $"unified_vs_legacy_{name}.png");
            File.WriteAllBytes(pngPath, combo.EncodeToPNG());
            Debug.Log($"[PixelEquiv:{name}] maxDelta={maxD} fracOver2={frac:P3} nbOff={nbOff} nbOn={nbOn} png={pngPath}");

            Object.DestroyImmediate(off); Object.DestroyImmediate(on); Object.DestroyImmediate(diff); Object.DestroyImmediate(combo);

            // Both neutral renders must produce visible glyphs, and the diff must be a thin AA edge ring.
            Assert.Greater(nbOff, 0, $"{name}: legacy neutral render produced visible glyphs");
            Assert.Greater(nbOn, 0, $"{name}: unified neutral render produced visible glyphs");
            // Justification: identical reconstruction math + vertex-identical meshes; the only residual
            // is edge AA from fwidth on slightly different interpolated derivatives at glyph borders.
            Assert.LessOrEqual(maxD, 48, $"{name}: max channel delta {maxD}/255 — larger than an AA-edge difference");
            Assert.LessOrEqual(frac, 0.05f, $"{name}: {frac:P2} pixels differ > 2/255 (expected only the thin AA edge ring)");
        }

        [Test] public void Sdf_PixelEquivalent()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            var f = MakeFont("NotoSans-Regular.ttf", UniTextRenderMode.SDF);
            if (f == null) Assert.Ignore("NotoSans not found.");
            RunCase("sdf", f, "Reading 123", msdf: false);
        }

        [Test] public void Msdf_PixelEquivalent()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            var f = MakeFont("NotoSans-Regular.ttf", UniTextRenderMode.Msdf);
            if (f == null) Assert.Ignore("NotoSans not found.");
            if (f.AtlasRenderMode != UniTextRenderMode.Msdf) Assert.Ignore("MSDF outline export unavailable.");
            RunCase("msdf", f, "Reading 123", msdf: true);
        }

        [Test] public void ColoredSpan_PixelEquivalent()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            var f = MakeFont("NotoSans-Regular.ttf", UniTextRenderMode.SDF);
            if (f == null) Assert.Ignore("NotoSans not found.");
            RunCase("color_span", f, "A<color=#ff0000>B</color>C", msdf: false);
        }
    }
}
