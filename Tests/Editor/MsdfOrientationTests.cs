using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace LightSide.Tests
{
    /// <summary>
    /// Bug A (round 7): MSDF glyphs render vertically flipped IN PLACE in the component path while
    /// SDF is upright. 'P' has its ink mass in the TOP half; this renders 'P' through a real UniText
    /// component in SDF and in MSDF and asserts both put their ink in the SAME half. A failure on the
    /// MSDF side localizes the flip to the MSDF atlas/UV/shader orientation (not a capture flip).
    /// </summary>
    public class MsdfOrientationTests
    {
        private GameObject _canvas, _go;
        private UniTextFont _font;
        private UniTextFontStack _stack;

        [TearDown]
        public void TearDown()
        {
            if (_canvas != null) Object.DestroyImmediate(_canvas);
            if (_stack != null) Object.DestroyImmediate(_stack);
            if (_font != null) Object.DestroyImmediate(_font);
            Shaper.ClearAllCaches();
        }

        // Renders a single 'P' through a real component and returns (topHalfInk, bottomHalfInk).
        private (int top, int bottom) RenderP(UniTextRenderMode mode)
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans not found.");
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 72, renderMode: mode);
            if (_font == null) Assert.Ignore("Face failed.");
            if (mode == UniTextRenderMode.Msdf && _font.AtlasRenderMode != UniTextRenderMode.Msdf)
                Assert.Ignore("MSDF export unavailable.");
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);

            int w = 128, h = 128;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            _canvas = new GameObject("C", typeof(Canvas));
            var canvas = _canvas.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            ((RectTransform)_canvas.transform).sizeDelta = new Vector2(w, h);
            _canvas.transform.position = Vector3.zero;
            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = h / 2f; cam.aspect = (float)w / h;
            cam.transform.position = new Vector3(0, 0, -10);
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.targetTexture = rt;
            canvas.worldCamera = cam;

            _go = new GameObject("T", typeof(RectTransform));
            _go.transform.SetParent(_canvas.transform, false);
            ((RectTransform)_go.transform).sizeDelta = new Vector2(w, h);
            var t = _go.AddComponent<UniText>();
            t.FontStack = _stack; t.FontSize = 90f; t.Text = "P";

            Canvas.ForceUpdateCanvases();
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);

            // GetPixels row 0 = bottom. Count ink (non-black) in top vs bottom half.
            var px = tex.GetPixels();
            int top = 0, bottom = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = px[y * w + x];
                    if (p.r + p.g + p.b > 0.3f) { if (y >= h / 2) top++; else bottom++; }
                }
            Object.DestroyImmediate(tex);
            return (top, bottom);
        }

        // Reads the 'P' atlas cell and reports (inkInTopRows, inkInBottomRows) in ATLAS space
        // (row 0 = bottom via GetPixels). Isolates whether the atlas DATA is top-down.
        private (int top, int bottom) AtlasCellInk(UniTextRenderMode mode)
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans not found.");
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 72, renderMode: mode);
            if (_font == null) Assert.Ignore("Face failed.");
            if (mode == UniTextRenderMode.Msdf && _font.AtlasRenderMode != UniTextRenderMode.Msdf)
                Assert.Ignore("MSDF export unavailable.");
            uint gi = Shaper.GetGlyphIndex(_font, 'P');
            _font.TryAddGlyphsBatch(new System.Collections.Generic.List<uint> { gi });
            Assert.IsTrue(_font.TryGetGlyph(gi, VariationKey.None, out var g));
            var tex = _font.AtlasTextures[g.atlasIndex];
            var px = tex.GetPixels(g.glyphRect.x, g.glyphRect.y, g.glyphRect.width, g.glyphRect.height);
            bool msdf = tex.format == TextureFormat.RGB24 || tex.format == TextureFormat.RGBA32;
            int top = 0, bottom = 0, w = g.glyphRect.width, h = g.glyphRect.height;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = px[y * w + x];
                    float v = msdf ? Mathf.Max(Mathf.Min(p.r, p.g), Mathf.Min(Mathf.Max(p.r, p.g), p.b)) : p.a;
                    bool ink = msdf ? (v > 0.5f) : (v > 0.5f);
                    if (ink) { if (y >= h / 2) top++; else bottom++; }
                }
            return (top, bottom);
        }

        [Test]
        public void AtlasCell_P_SdfAndMsdf_SameOrientation()
        {
            var sdf = AtlasCellInk(UniTextRenderMode.SDF);
            TearDown();
            var msdf = AtlasCellInk(UniTextRenderMode.Msdf);
            // In GetPixels space (row 0 = bottom), 'P' ink is bottom-heavy for a top-down atlas.
            Assert.AreEqual(sdf.top > sdf.bottom, msdf.top > msdf.bottom,
                $"Atlas-cell 'P' orientation must match: SDF(top={sdf.top},bot={sdf.bottom}) MSDF(top={msdf.top},bot={msdf.bottom}). Mismatch -> atlas DATA flip (MsdfBuilder/Encode); match -> UV/shader flip.");
        }

        [Test]
        public void P_SdfAndMsdf_InkInSameHalf()
        {
            var sdf = RenderP(UniTextRenderMode.SDF);
            TearDown(); // reset for the second render
            var msdf = RenderP(UniTextRenderMode.Msdf);

            Assert.Greater(sdf.top + sdf.bottom, 20, "SDF 'P' drew too little.");
            Assert.Greater(msdf.top + msdf.bottom, 20, "MSDF 'P' drew too little.");
            bool sdfTopHeavy = sdf.top > sdf.bottom;
            bool msdfTopHeavy = msdf.top > msdf.bottom;
            Assert.AreEqual(sdfTopHeavy, msdfTopHeavy,
                $"'P' ink half must match between SDF (top={sdf.top},bot={sdf.bottom}) and MSDF (top={msdf.top},bot={msdf.bottom}) -- a mismatch means MSDF is flipped.");
        }
    }
}
