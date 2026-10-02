using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>
    /// GPU pixel regression for the "leading glyph right-side vertical wall" bug (OpenGlyph issue:
    /// rendering "sac"/"sx" at a large size through the UNIFIED <c>UniText/Uber</c> shader with the
    /// default appearance — which carries an outline/underlay with Dilate = 1 — chopped the first
    /// glyph's right curves behind a hard half-coverage (~0.5) vertical band).
    ///
    /// Root cause (fixed in <c>Shaders/UniText_Uber.shader</c>): the Alpha8/MSDF atlas clamps the
    /// distance field at 0 outside the encoded spread, so a dilated layer bias makes
    /// <c>saturate(0 - bias)</c> FLOOR at a positive coverage — the layer paints a solid half-coverage
    /// rectangle over the whole padded cell. In the unified single merged mesh adjacent glyph quads
    /// (ink + 2*padding wide) overlap, so one glyph's floored layer darkens its neighbour's ink into a
    /// vertical wall. The shader now gates every SDF/MSDF layer by the raw field presence so the
    /// clamped-0 exterior contributes zero coverage.
    ///
    /// The test renders through the REAL production path (a <see cref="UniText"/> component, unified
    /// forced ON, default appearance) to an offscreen RenderTexture on a real GPU, then asserts the
    /// inter-glyph region carries NO sustained mid-grey ("wall") plateau. Requires a GPU: skips under
    /// -nographics / null device.
    /// </summary>
    public class LeadingGlyphWallTests
    {
        const int W = 1280, H = 720;
        const float FontSize = 400f;
        const string AppearancePkg = "Packages/com.openglyph.text/Defaults/UniTextAppearance_Default.asset";
        const string FontStackPkg  = "Packages/com.openglyph.text/Defaults/UniTextFonts_Default.asset";

        [TestCase("sac", 0.25f)]
        [TestCase("sac", 0.10f)]
        [TestCase("sx", 0.10f)]
        public void UnifiedRun_LeadingGlyph_HasNoHalfCoverageWall(string text, float spread)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("No GPU (null graphics device) — this is a real-GPU pixel test.");

            var fonts = UnityEditor.AssetDatabase.LoadAssetAtPath<UniTextFontStack>(FontStackPkg);
            if (fonts == null || fonts.fonts == null || fonts.fonts.Count == 0) Assert.Ignore("Default font stack missing.");
#pragma warning disable 618
            var app = UnityEditor.AssetDatabase.LoadAssetAtPath<UniTextAppearance>(AppearancePkg);
#pragma warning restore 618
            if (app == null) Assert.Ignore("Default appearance missing.");

            // Set the atlas spread on the first font (same reflection the evidence harness uses).
            var f0 = fonts.fonts[0];
            var fld = typeof(UniTextFont).GetField("spreadStrength",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fld != null) { fld.SetValue(f0, spread); f0.ClearDynamicData(); }

            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
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
            var rtf = (RectTransform)go.transform; rtf.sizeDelta = new Vector2(1200, 600); rtf.localPosition = Vector3.zero;
            var ut = go.AddComponent<UniText>();
            ut.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOn;
            ut.FontStack = fonts;
            ut.Appearance = app;
            ut.color = Color.white; ut.FontSize = FontSize; ut.Text = text;

            Canvas.ForceUpdateCanvases(); cam.Render(); cam.Render();

            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();

            // Ink bounds (non-black) to locate the glyph run.
            int minX = W, maxX = 0, minY = H, maxY = 0, nonBg = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var c = px[y * W + x];
                    if (c.r > 20 || c.g > 20 || c.b > 20) { nonBg++; if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                }
            Assert.Greater(nonBg, 1000, $"{text}@{spread}: nothing rendered (nonBg={nonBg}).");

            // The "wall" is a 2D band: WIDE (many adjacent columns) AND TALL (tens of px) of
            // FULLY-OPAQUE mid-grey (~0.5 => r in [96,160]). A near-vertical glyph edge is also a tall
            // mid-grey column but only 1-2 px WIDE; a near-horizontal edge is wide but only 1-2 px
            // tall. Requiring BOTH (>=8 adjacent columns that each have a >=20px tall mid-grey run)
            // isolates the overlapping-neighbour floored-layer wall from all legitimate edge AA.
            const int TallThresh = 20;
            int bandWidth = 0, worstBand = 0, worstX = -1;
            for (int x = minX; x <= maxX; x++)
            {
                int run = 0, tallest = 0;
                for (int y = minY; y <= maxY; y++)
                {
                    var c = px[y * W + x];
                    bool midGrey = c.a > 200 && c.r >= 96 && c.r <= 160 && Mathf.Abs(c.r - c.g) < 14 && Mathf.Abs(c.r - c.b) < 14;
                    if (midGrey) { run++; if (run > tallest) tallest = run; }
                    else run = 0;
                }
                if (tallest >= TallThresh) { bandWidth++; if (bandWidth > worstBand) { worstBand = bandWidth; worstX = x; } }
                else bandWidth = 0;
            }
            int worstRun = worstBand;

            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(canvasGo);
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(camGo);
            rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
            f0.ClearDynamicData();

            // A legitimate vertical glyph edge is only 1-2 columns wide; the wall is several px wide.
            // 4 sits above the fixed render's residual (0) and below the wall (>=5).
            Assert.LessOrEqual(worstRun, 4,
                $"{text}@{spread}: leading glyph shows a {worstRun}px-wide x >={TallThresh}px-tall mid-grey (~0.5) " +
                $"vertical band near x={worstX} — the half-coverage 'wall' regression (overlapping-neighbour floored SDF layer).");
        }
    }
}
