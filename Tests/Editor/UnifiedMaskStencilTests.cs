using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression for B9: with the unified renderer (<see cref="UniText.UnifiedRendererMode.ForceOn"/>)
    /// text inside a stencil <see cref="Mask"/> rendered NOTHING (RectMask2D was fine).
    ///
    /// Why it fails without the fix: <c>UniText.SetSubMeshRendererData</c> routes the shared Uber
    /// material through <c>StencilMaterial.Add</c>, which copies the material ONCE and caches the copy.
    /// <c>UnifiedRenderBuilder.Build</c> then re-binds <c>_MainTexArray</c>, <c>_StyleTex</c>,
    /// <c>_StyleTexWidth/Height</c> and <c>_AtlasSize</c> on the SHARED material on every build (the
    /// array is reallocated as atlas pages are added, the style texture as style rows are added). The
    /// cached stencil copy keeps the stale (possibly destroyed) array/style bindings and samples
    /// nothing, so the masked glyphs vanish.
    ///
    /// Scenario: (1) render a masked text once so the stencil copy exists and captures the bindings;
    /// (2) add NON-masked unified texts with many new glyphs at several sizes (grows/reallocates the
    /// atlas array) and a per-span outline (adds a style row, reallocating the style texture), so the
    /// shared bindings change after the copy was made; (3) re-render the masked text after a text
    /// change and assert it still has ink inside the mask. The test relies on the style-texture
    /// reallocation (new style row) and, where the atlas grows past its current page capacity, the array
    /// reallocation; either one leaves the unfixed stencil copy stale.
    /// Requires a GPU: skips under a null graphics device.
    /// </summary>
    public class UnifiedMaskStencilTests
    {
        const int W = 1280, H = 720;
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

        UniText MakeText(Transform parent, UniTextFontStack stack, string name, Vector2 pos, Vector2 size, float fontSize, string text)
        {
            var go = new GameObject(name, typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform; rt.sizeDelta = size; rt.anchoredPosition = pos;
            var t = go.AddComponent<UniText>();
            t.SetUnifiedRendererModeForTests(UniText.UnifiedRendererMode.ForceOn);
            t.RegisterModifier(new ModRegister { Modifier = new SpanStyleModifier(SpanStyleModifier.Kind.Outline), Rule = new OutlineParseRule() });
            t.FontStack = stack;
            t.color = Color.white;
            t.FontSize = fontSize;
            t.Text = text;
            return t;
        }

        // Counts bright pixels (clearly above the black background) in [x0,x1) x [y0,y1).
        static int CountInk(Color32[] px, int x0, int y0, int x1, int y1)
        {
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = px[y * W + x];
                    if (c.r > 128 && c.g > 128 && c.b > 128) n++;
                }
            return n;
        }

        static Color32[] Capture(Camera cam, RenderTexture rt)
        {
            Canvas.ForceUpdateCanvases();
            cam.Render(); cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }

        [Test]
        public void UnifiedRenderer_TextInsideStencilMask_SurvivesSharedBindingChange()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("No GPU (null graphics device) - this is a real-GPU pixel test.");
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
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

            var camGo = new GameObject("Cam", typeof(Camera)); _junk.Add(camGo);
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.orthographic = true; cam.orthographicSize = H / 2f;
            cam.transform.position = new Vector3(0, 0, -10);
            var rt = new RenderTexture(W, H, 24) { antiAliasing = 1 };
            _junk.Add(rt);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("C", typeof(Canvas)); _junk.Add(canvasGo);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = cam;
            var crt = (RectTransform)canvas.transform; crt.sizeDelta = new Vector2(W, H); crt.position = Vector3.zero;

            // Mask parent: 600x160 white Image, hidden by showMaskGraphic=false, centred on the canvas.
            var maskGo = new GameObject("Mask", typeof(RectTransform), typeof(Image), typeof(Mask)); _junk.Add(maskGo);
            maskGo.transform.SetParent(canvas.transform, false);
            var mrt = (RectTransform)maskGo.transform; mrt.sizeDelta = new Vector2(600, 160); mrt.anchoredPosition = Vector2.zero;
            var img = maskGo.GetComponent<Image>(); img.color = Color.white; img.raycastTarget = false;
            maskGo.GetComponent<Mask>().showMaskGraphic = false;

            var masked = MakeText(maskGo.transform, stack, "Masked", Vector2.zero, new Vector2(580, 150), 64f, "Masked HHH");

            // (1) first render: creates the stencil copy of the shared Uber material.
            var px0 = Capture(cam, rt);
            if (masked.GetDrawnVerticesForTests().Count == 0) Assert.Ignore("Pipeline produced no geometry in this environment.");

            // Mask rect in pixels (canvas is 1:1 with the RT, centred): x 340..940, y 280..440.
            const int MX0 = 340, MX1 = 940, MY0 = 280, MY1 = 440;
            int inkBefore = CountInk(px0, MX0, MY0, MX1, MY1);

            // (2) change the shared bindings AFTER the stencil copy exists. Every distinct UniTextFont
            // contributes its own legacy atlas page => a new slice in the shared Texture2DArray, which
            // makes GlyphAtlasArray destroy and reallocate the array the stencil copy still points at.
            // So the control texts use OTHER fonts (Thai/Khmer/Myanmar fixtures + extra Noto instances),
            // one with a per-span outline (new style row as well). Kept in bands clear of the mask.
            var arraysBefore = ArrayIds();
            string fixtures = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(noto)), "Tests", "Editor", "Fixtures");
            var specs = new List<(string ttf, string txtFile, int sample)>
            {
                (Path.Combine(fixtures, "NotoSansThai-Regular.ttf"), "Thai.txt", 48),
                (Path.Combine(fixtures, "NotoSansKhmer-Regular.ttf"), "Khmer.txt", 48),
                (Path.Combine(fixtures, "NotoSansMyanmar-Regular.ttf"), "Myanmar.txt", 48),
                (noto, null, 32), (noto, null, 40), (noto, null, 56), (noto, null, 64), (noto, null, 72),
            };
            // Pixel-row centres of the control texts: top band (y >= 480) and bottom band (y < 240).
            int[] rows = { 690, 646, 602, 558, 514, 200, 156, 112 };
            string latin = "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz 0123456789";
            for (int i = 0; i < specs.Count; i++)
            {
                var sp = specs[i];
                if (!File.Exists(sp.ttf)) continue;
                var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(sp.ttf), sp.sample);
                if (f == null) continue;
                _junk.Add(f);
                var st = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(st);
                st.fonts.Add(f);
                string txt = latin.Substring((i * 7) % 20, 40);
                if (sp.txtFile != null)
                {
                    string tp = Path.Combine(fixtures, sp.txtFile);
                    if (File.Exists(tp))
                    {
                        string raw = File.ReadAllText(tp, System.Text.Encoding.UTF8).Replace("\r", "").Replace("\n", " ");
                        txt = raw.Length > 40 ? raw.Substring(0, 40) : raw;
                    }
                }
                if (i == 3) txt = "<outline=#FF0000,0.2>" + txt + "</outline>";
                MakeText(canvas.transform, st, "Ctl" + i, new Vector2(0, rows[i] - H / 2), new Vector2(1240, 44), 36f, txt);
            }
            Capture(cam, rt);

            // Precondition: the shared array the stencil copy captured must really have been
            // reallocated (an old array instance destroyed). Otherwise this test proves nothing.
            var arraysAfter = ArrayIds();
            bool reallocated = false;
            foreach (int id in arraysBefore) if (!arraysAfter.Contains(id)) { reallocated = true; break; }
            if (!reallocated)
                Assert.Inconclusive("Shared Texture2DArray was not reallocated by the control texts; cannot exercise the stale-binding case.");

            // (3) rebuild the masked text, render, and read pixels inside the mask.
            masked.Text = "Masked HHH!";
            var px = Capture(cam, rt);
            int inkMasked = CountInk(px, MX0, MY0, MX1, MY1);

            // Control: the non-masked text really rendered (outside the mask), so a pass cannot be vacuous.
            int inkControl = CountInk(px, 0, 480, W, H) + CountInk(px, 0, 0, W, 240);

            TestContext.WriteLine($"ink before={inkBefore} masked-after={inkMasked} control={inkControl} reallocated={reallocated}");
            Assert.Greater(inkBefore, 300, $"masked text did not render on the first pass (ink={inkBefore}).");
            Assert.Greater(inkControl, 500, $"control: non-masked unified text rendered nothing (ink={inkControl}).");
            Assert.GreaterOrEqual(inkMasked, (int)(0.8f * inkBefore),
                $"text inside the stencil Mask lost its ink after the shared array was reallocated " +
                $"(masked after={inkMasked}, before={inkBefore}, control={inkControl}) - the cached " +
                $"StencilMaterial copy still points at the destroyed _MainTexArray / stale _StyleTex (B9).");
        }

        static HashSet<int> ArrayIds()
        {
            var set = new HashSet<int>();
            foreach (var a in Resources.FindObjectsOfTypeAll<Texture2DArray>()) if (a != null) set.Add(a.GetInstanceID());
            return set;
        }
    }
}
