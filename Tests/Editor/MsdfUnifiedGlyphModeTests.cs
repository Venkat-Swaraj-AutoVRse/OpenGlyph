using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// QA B18: GlyphMeshProUGUI with an MSDF font reportedly drew RGB-coloured squares. Suspected:
    /// an MSDF page being classified as colour (glyph mode Colr) because MSDF and colour emoji share
    /// the RGBA32 shared array. These tests assert, for plain UniText (ForceOn) and GlyphMeshProUGUI,
    /// that MSDF glyphs carry the MSDF glyph mode in UV1.z and that rendered ink is near-white.
    /// </summary>
    public class MsdfUnifiedGlyphModeTests
    {
        const int W = 512, H = 256;
        readonly List<Object> _junk = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        private UniTextFontStack MakeMsdfStack()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48, 0.25f, UniTextRenderMode.Msdf);
            if (f == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(f);
            if (f.AtlasRenderMode != UniTextRenderMode.Msdf) Assert.Ignore("MSDF outline export unavailable.");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(f);
            return stack;
        }

        // Builds the component under a world-space canvas; returns it (caller renders).
        private T Build<T>(UniTextFontStack stack, Camera cam, out GameObject canvasGo) where T : UniText
        {
            canvasGo = new GameObject("C", typeof(Canvas)); _junk.Add(canvasGo);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = cam;
            var crt = (RectTransform)canvas.transform; crt.sizeDelta = new Vector2(W, H); crt.position = Vector3.zero;
            var go = new GameObject("T", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(canvas.transform, false);
            var rtf = (RectTransform)go.transform; rtf.sizeDelta = new Vector2(W, H); rtf.anchoredPosition = Vector2.zero;
            var t = go.AddComponent<T>();
            t.SetUnifiedRendererModeForTests(UniText.UnifiedRendererMode.ForceOn);
            t.FontStack = stack;
            t.color = Color.white;
            t.FontSize = 120f;
            t.Text = "HIM";
            return t;
        }

        private Camera MakeCamera(out RenderTexture rt)
        {
            var camGo = new GameObject("Cam", typeof(Camera)); _junk.Add(camGo);
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.orthographic = true; cam.orthographicSize = H / 2f;
            cam.transform.position = new Vector3(0, 0, -10);
            rt = new RenderTexture(W, H, 24) { antiAliasing = 1 };
            cam.targetTexture = rt;
            return cam;
        }

        private void Check(System.Func<UniTextFontStack, Camera, UniText> make, string label)
        {
            var stack = MakeMsdfStack();
            var cam = MakeCamera(out var rt);
            var t = make(stack, cam);
            Canvas.ForceUpdateCanvases();
            if (t.GetDrawnVerticesForTests().Count == 0) Assert.Ignore("Pipeline produced no geometry in this environment.");

            // (1) glyph mode in UV1.z of the built unified mesh must be MSDF, never colour.
            var items = t.GetDrawnMeshMaterialsForTests();
            Assert.Greater(items.Count, 0, label + ": no unified mesh");
            var uv1 = new List<Vector4>();
            int verts = 0;
            foreach (var (mesh, _, _) in items)
            {
                uv1.Clear(); mesh.GetUVs(1, uv1);
                foreach (var v in uv1)
                {
                    verts++;
                    Assert.AreEqual((int)UberDrawGroup.GlyphMode.Msdf, (int)(v.z + 0.5f),
                        $"{label}: MSDF glyph carries glyph mode {v.z} (3 = Colr) instead of Msdf.");
                }
            }
            Assert.Greater(verts, 0, label + ": no vertices");

            // (2) GPU: rendered ink must be near-white with no strong per-channel colour.
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                cam.Render(); cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
                RenderTexture.active = prev;
                var px = tex.GetPixels32();
                Object.DestroyImmediate(tex);
                int ink = 0; double sr = 0, sg = 0, sb = 0; int maxSpread = 0;
                foreach (var p in px)
                {
                    int mx = Mathf.Max(p.r, Mathf.Max(p.g, p.b));
                    if (mx < 200) continue; // solid interior only
                    ink++; sr += p.r; sg += p.g; sb += p.b;
                    maxSpread = Mathf.Max(maxSpread, mx - Mathf.Min(p.r, Mathf.Min(p.g, p.b)));
                }
                TestContext.WriteLine($"{label}: ink={ink} mean=({sr / System.Math.Max(1, ink):F0},{sg / System.Math.Max(1, ink):F0},{sb / System.Math.Max(1, ink):F0}) maxChannelSpread={maxSpread}");
                if (ink > 200)
                {
                    Assert.Less(maxSpread, 60, $"{label}: ink pixels carry strong per-channel colour (RGB squares).");
                    Assert.Greater(sr / ink, 230, $"{label}: ink is not near-white.");
                }
            }
            cam.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt);
        }

        [Test]
        public void UniText_ForceOn_MsdfFont_UsesMsdfMode_AndNearWhiteInk()
        {
            Check((stack, cam) => Build<UniText>(stack, cam, out _), "UniText");
        }

        [Test]
        public void GlyphMeshProUGUI_MsdfFont_UsesMsdfMode_AndNearWhiteInk()
        {
            Check((stack, cam) => Build<OpenGlyph.GlyphMeshProUGUI>(stack, cam, out _), "GlyphMeshProUGUI");
        }
    }
}
