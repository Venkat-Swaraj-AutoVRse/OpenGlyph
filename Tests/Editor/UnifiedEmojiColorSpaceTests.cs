using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression for B6: in LINEAR colour space the unified renderer drew colour emoji washed out
    /// (a red square came out pink).
    ///
    /// Why it fails without the fix: the unified renderer's shared RGBA32 <c>Texture2DArray</c> is
    /// linear (it also holds MSDF data) but the colour-emoji pixels written into it are sRGB-encoded,
    /// so sampling returned sRGB values that were then treated as linear and brightened by the final
    /// sRGB encode. <c>UniText_Uber.shader</c> mode 3 now (when not UNITY_COLORSPACE_GAMMA)
    /// un-premultiplies, applies <c>GammaToLinearSpace</c> and re-premultiplies, matching the legacy
    /// sRGB page. For a red emoji, G and B rise markedly without the fix.
    ///
    /// Renders U+1F7E5 (large red square) with the legacy path (ForceOff) and the unified path
    /// (ForceOn) onto black and compares the mean colour of the ink pixels per channel.
    /// Requires a GPU, LINEAR colour space and an emoji font (otherwise ignored).
    /// </summary>
    public class UnifiedEmojiColorSpaceTests
    {
        const int W = 512, H = 512;
        const string Emoji = "\U0001F7E5"; // large red square
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

        // Renders the emoji and returns the mean RGB (0..255) of its ink pixels; inkCount = pixels used.
        Vector3 RenderMean(UniText.UnifiedRendererMode mode, UniTextFontStack stack, out int inkCount)
        {
            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.orthographic = true; cam.orthographicSize = H / 2f;
            cam.transform.position = new Vector3(0, 0, -10);
            var rt = new RenderTexture(W, H, 24) { antiAliasing = 1 };
            cam.targetTexture = rt;

            var canvasGo = new GameObject("C", typeof(Canvas));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = cam;
            var crt = (RectTransform)canvas.transform; crt.sizeDelta = new Vector2(W, H); crt.position = Vector3.zero;

            var go = new GameObject("UT", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            var rtf = (RectTransform)go.transform; rtf.sizeDelta = new Vector2(W, H); rtf.anchoredPosition = Vector2.zero;
            var t = go.AddComponent<UniText>();
            t.SetUnifiedRendererModeForTests(mode);
            t.FontStack = stack;
            t.color = Color.white;
            t.FontSize = 300f;
            t.Text = Emoji;

            Canvas.ForceUpdateCanvases();
            cam.Render(); cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            cam.targetTexture = null; rt.Release();
            Object.DestroyImmediate(rt); Object.DestroyImmediate(canvasGo); Object.DestroyImmediate(camGo);

            // Ink = pixels at least half as bright as the brightest pixel (drops anti-aliased edges that
            // blend toward the black background).
            int peak = 0;
            foreach (var p in px) peak = Mathf.Max(peak, Mathf.Max(p.r, Mathf.Max(p.g, p.b)));
            double r = 0, g = 0, b = 0; inkCount = 0;
            if (peak < 40) return Vector3.zero;
            int cut = peak / 2;
            foreach (var p in px)
            {
                if (Mathf.Max(p.r, Mathf.Max(p.g, p.b)) < cut) continue;
                r += p.r; g += p.g; b += p.b; inkCount++;
            }
            return inkCount == 0 ? Vector3.zero : new Vector3((float)(r / inkCount), (float)(g / inkCount), (float)(b / inkCount));
        }

        [Test]
        public void UnifiedEmoji_LinearColorSpace_MatchesLegacy()
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                Assert.Ignore("Test targets LINEAR colour space (project is in Gamma).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("No GPU (null graphics device) - this is a real-GPU pixel test.");
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            if (!EmojiFont.IsAvailable) Assert.Ignore("No system emoji font available on this host.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");

            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();

            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (font == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(font);

            var legacy = RenderMean(UniText.UnifiedRendererMode.ForceOff, stack, out int legacyInk);
            UnifiedRenderBuilder.ResetShared();
            var unified = RenderMean(UniText.UnifiedRendererMode.ForceOn, stack, out int unifiedInk);

            string msg = $"emoji ink mean RGB (0..255): legacy=({legacy.x:F1},{legacy.y:F1},{legacy.z:F1}) n={legacyInk}  " +
                         $"unified=({unified.x:F1},{unified.y:F1},{unified.z:F1}) n={unifiedInk}";
            TestContext.WriteLine(msg);

            if (legacyInk < 500)
                Assert.Ignore("Legacy emoji path produced no visible ink in this environment (no colour emoji glyph / offscreen UGUI). " + msg);
            Assert.GreaterOrEqual(unifiedInk, 500, "unified renderer drew no emoji ink. " + msg);

            const float tol = 6f;
            Assert.AreEqual(legacy.x, unified.x, tol, "R mismatch (unified vs legacy) - " + msg);
            Assert.AreEqual(legacy.y, unified.y, tol, "G mismatch: unified emoji is washed out in LINEAR space (B6) - " + msg);
            Assert.AreEqual(legacy.z, unified.z, tol, "B mismatch: unified emoji is washed out in LINEAR space (B6) - " + msg);
        }
    }
}
