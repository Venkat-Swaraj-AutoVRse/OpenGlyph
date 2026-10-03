using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>
    /// GPU pixel regression for the LEGACY renderer's "faint rectangle behind every glyph" bug (B4).
    /// Same root cause as <see cref="LeadingGlyphWallTests"/> (see the EXTERIOR-FLOOR GATE comment in
    /// <c>Shaders/UniText_Uber.shader</c>): the default material (<c>UniText/Mobile/SDF</c>, UNDERLAY_ON,
    /// <c>_OutlineDilate = 1</c>, <c>_UnderlayDilate = 1</c>) makes the layer bias
    /// <c>(0.5 - normEffect)*scale - 0.5</c> negative, so <c>saturate(dist*scale - bias)</c> floors at
    /// ~0.5 where the clamped atlas has <c>dist == 0</c>. The legacy shaders had no gate, so the whole
    /// padded glyph quad was painted with a 50% BLACK outline/underlay layer.
    ///
    /// Why this FAILS on the ungated shaders: the quad's exterior (field == 0) is painted with the
    /// floored black underlay/outline layers; measured on the ungated shaders the top quad corners
    /// read (0,0,0) on the (128,128,128) background. The gated shaders give the background exactly.
    ///
    /// A single tall glyph ("l") is rendered; the drawn mesh's vertices give the quad rect, and pixels
    /// just inside the TOP quad corners (diagonally beyond the SDF spread from the ink, so field == 0)
    /// must equal the background within 2/255.
    /// </summary>
    public class LegacyPaddingBoxTests
    {
        const int W = 1280, H = 720;
        const string AppearancePkg = "Packages/com.openglyph.text/Defaults/UniTextAppearance_Default.asset";
        const string FontStackPkg  = "Packages/com.openglyph.text/Defaults/UniTextFonts_Default.asset";

        [Test]
        public void LegacyRun_DefaultMaterial_PaintsNoBoxInGlyphQuad()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("No GPU (null graphics device) - this is a real-GPU pixel test.");

            var fonts = UnityEditor.AssetDatabase.LoadAssetAtPath<UniTextFontStack>(FontStackPkg);
            if (fonts == null || fonts.fonts == null || fonts.fonts.Count == 0) Assert.Ignore("Default font stack missing.");
#pragma warning disable 618
            var app = UnityEditor.AssetDatabase.LoadAssetAtPath<UniTextAppearance>(AppearancePkg);
#pragma warning restore 618
            if (app == null) Assert.Ignore("Default appearance missing.");

            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color32(128, 128, 128, 255);
            cam.orthographic = true; cam.orthographicSize = H / 2f;
            cam.transform.position = new Vector3(0, 0, -10);
            var rt = new RenderTexture(W, H, 24) { antiAliasing = 1 };
            cam.targetTexture = rt;

            var canvasGo = new GameObject("C", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = cam;
            var crt = (RectTransform)canvas.transform; crt.sizeDelta = new Vector2(W, H); crt.position = Vector3.zero;

            var go = new GameObject("U", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            var rtf = (RectTransform)go.transform; rtf.sizeDelta = new Vector2(600, 400); rtf.localPosition = Vector3.zero;
            var ut = go.AddComponent<UniText>();
            ut.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOff;   // legacy per-glyph-quad path
            ut.FontStack = fonts;
            ut.Appearance = app;
            ut.color = Color.white; ut.FontSize = 160f; ut.Text = "l";

            Canvas.ForceUpdateCanvases(); cam.Render(); cam.Render();

            // Quad rect in pixels from the actually drawn mesh (world -> camera RT pixels, origin bottom-left).
            float qx0 = float.MaxValue, qy0 = float.MaxValue, qx1 = float.MinValue, qy1 = float.MinValue;
            var items = ut.GetDrawnMeshMaterialsForTests();
            foreach (var (mesh, _, _) in items)
            {
                if (mesh == null) continue;
                foreach (var v in mesh.vertices)
                {
                    var sp = cam.WorldToScreenPoint(ut.transform.TransformPoint(v));
                    qx0 = Mathf.Min(qx0, sp.x); qx1 = Mathf.Max(qx1, sp.x);
                    qy0 = Mathf.Min(qy0, sp.y); qy1 = Mathf.Max(qy1, sp.y);
                }
            }
            Assert.Less(qx0, qx1, "no drawn mesh for 'l'.");

            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();

            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(canvasGo);
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(camGo);
            rt.Release(); UnityEngine.Object.DestroyImmediate(rt);

            var bg = px[5 * W + 5];   // far corner, outside the quad
            Assert.AreEqual(128, bg.r, 2, "background readback unexpected.");

            const int Inset = 3, Tol = 2;
            int ix0 = Mathf.RoundToInt(qx0), ix1 = Mathf.RoundToInt(qx1) - 1;
            int iy0 = Mathf.RoundToInt(qy0), iy1 = Mathf.RoundToInt(qy1) - 1;
            // Only the TOP corners: the default underlay is offset downward and the dilate-1 outline
            // reaches the quad sides, so the bottom corners and side midpoints are legitimately inked.
            var samples = new (string name, int x, int y)[]
            {
                ("corner TL", ix0 + Inset, iy1 - Inset), ("corner TR", ix1 - Inset, iy1 - Inset),
            };
            var report = new System.Text.StringBuilder();
            report.Append($"quad px [{ix0},{iy0}]-[{ix1},{iy1}], bg=({bg.r},{bg.g},{bg.b}); ");
            bool ok = true;
            foreach (var (name, x, y) in samples)
            {
                var c = px[y * W + x];
                bool pass = Mathf.Abs(c.r - bg.r) <= Tol && Mathf.Abs(c.g - bg.g) <= Tol && Mathf.Abs(c.b - bg.b) <= Tol;
                ok &= pass;
                report.Append($"{name}({x},{y})=({c.r},{c.g},{c.b}){(pass ? "" : " FAIL")}; ");
            }
            Assert.IsTrue(ok, "glyph quad exterior is not background (exterior-floor 'box' regression): " + report);
        }
    }
}
