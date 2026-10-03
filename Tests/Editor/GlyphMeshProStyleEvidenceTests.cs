// Round 2 — Priority 1 (font styles) render evidence: GlyphMeshProUGUI fontStyle
// Underline / Strikethrough vs a plain run, alongside a real TextMeshProUGUI driving
// the SAME <u>/<s> via its own fontStyle, on a WorldSpace Canvas rendered by an
// orthographic camera to a RenderTexture and saved as a PNG for visual confirmation.
//
// Clean-room: no TMP code copied; the TMP component is driven only through its public
// API for comparison. Requires a real graphics device + TMP resources; under
// -nographics/NullGfxDevice the GPU test ignores (headless suite stays deterministic).
// See Documentation/GlyphMeshPro-Parity.md.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using TMPro;
using OpenGlyph;

namespace LightSide.Tests
{
    [TestFixture]
    public class GlyphMeshProStyleEvidenceTests
    {
        private const int Width = 1280, Height = 720;
        private const float FontSize = 54f;

        private static string EvidenceDir()
        {
            string dir = @"D:\OpenGlyphWork\scratch\gmp-round2\evidence";
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void FontStyle_Underline_Strikethrough_SideBySide()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No real graphics device (-nographics/NullGfxDevice); style render evidence skipped.");

            string notoPath = FindNoto();
            if (notoPath == null) Assert.Ignore("NotoSans-Regular.ttf fixture not found.");

            var ogFont = UniTextFont.CreateFontAsset(File.ReadAllBytes(notoPath));
            if (ogFont == null) Assert.Ignore("UniTextFont build failed (native backend).");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(ogFont);

            TMP_FontAsset tmpFont = null;
            Font unityFont = null;
#if UNITY_EDITOR
            unityFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>("Assets/ParityFonts/NotoSans-Regular.ttf");
#endif
            if (unityFont == null) unityFont = Font.CreateDynamicFontFromOSFont("Noto Sans", (int)FontSize);
            if (unityFont != null)
            {
                try
                {
                    tmpFont = TMP_FontAsset.CreateFontAsset(unityFont, 90, 9,
                        UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024,
                        AtlasPopulationMode.Dynamic, true);
                }
                catch (System.Exception ex) { Debug.LogWarning($"[StyleEvidence] TMP font build failed: {ex.Message}"); }
            }

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var bg = new Color(0.10f, 0.10f, 0.12f, 1f);
            var gos = new System.Collections.Generic.List<GameObject>();
            try
            {
                var canvasGo = new GameObject("StyleCanvas", typeof(Canvas));
                gos.Add(canvasGo);
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(Width, Height);
                canvasGo.transform.position = Vector3.zero;

                var camGo = new GameObject("StyleCam", typeof(Camera));
                gos.Add(camGo);
                var cam = camGo.GetComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = Height / 2f; cam.aspect = (float)Width / Height;
                cam.transform.position = new Vector3(0, 0, -10);
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = bg;
                cam.targetTexture = rt;
                canvas.worldCamera = cam;

                // Four stacked GlyphMeshPro rows on the left, mirrored TMP rows on the right.
                float rowH = (Height - 80) / 4f;
                System.Func<string, float, OpenGlyph.FontStyles, GlyphMeshProUGUI> makeOg = (label, yTop, style) =>
                {
                    var go = new GameObject("og_" + label, typeof(RectTransform));
                    go.transform.SetParent(canvasGo.transform, false);
                    var rtf = (RectTransform)go.transform;
                    rtf.sizeDelta = new Vector2(Width / 2f - 40, rowH - 10);
                    rtf.anchoredPosition = new Vector2(-Width / 4f, Height / 2f - 40 - yTop - rowH / 2f);
                    var og = go.AddComponent<GlyphMeshProUGUI>();
                    og.FontStack = stack; og.fontSize = FontSize; og.color = Color.white;
                    og.alignment = OpenGlyph.TextAlignmentOptions.Left;
                    og.fontStyle = style;
                    og.text = label;
                    og.ForceMeshUpdate(ignoreActiveState: true);
                    gos.Add(go);
                    return og;
                };

                makeOg("Plain Agjy", 0 * rowH, OpenGlyph.FontStyles.Normal);
                makeOg("Underline Agjy", 1 * rowH, OpenGlyph.FontStyles.Underline);
                makeOg("Strike Agjy", 2 * rowH, OpenGlyph.FontStyles.Strikethrough);
                makeOg("Both Agjy", 3 * rowH, OpenGlyph.FontStyles.Underline | OpenGlyph.FontStyles.Strikethrough);

                if (tmpFont != null)
                {
                    System.Action<string, float, OpenGlyph.FontStyles> makeTmp = (label, yTop, style) =>
                    {
                        var go = new GameObject("tmp_" + label, typeof(RectTransform));
                        go.transform.SetParent(canvasGo.transform, false);
                        var rtf = (RectTransform)go.transform;
                        rtf.sizeDelta = new Vector2(Width / 2f - 40, rowH - 10);
                        rtf.anchoredPosition = new Vector2(Width / 4f, Height / 2f - 40 - yTop - rowH / 2f);
                        var tmp = go.AddComponent<TextMeshProUGUI>();
                        tmp.font = tmpFont; tmp.fontSize = FontSize; tmp.color = Color.white;
                        tmp.alignment = TMPro.TextAlignmentOptions.Left;
                        tmp.fontStyle = (TMPro.FontStyles)style; // TMP flag bit values match ours
                        tmp.text = label;
                        tmp.ForceMeshUpdate();
                        gos.Add(go);
                    };
                    makeTmp("Plain Agjy", 0 * rowH, OpenGlyph.FontStyles.Normal);
                    makeTmp("Underline Agjy", 1 * rowH, OpenGlyph.FontStyles.Underline);
                    makeTmp("Strike Agjy", 2 * rowH, OpenGlyph.FontStyles.Strikethrough);
                    makeTmp("Both Agjy", 3 * rowH, OpenGlyph.FontStyles.Underline | OpenGlyph.FontStyles.Strikethrough);
                }

                Canvas.ForceUpdateCanvases();
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                string path = Path.Combine(EvidenceDir(), "fontstyle_underline_strike.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[RenderEvidence] fontStyle underline/strike -> {path}");

                // Pixel sanity: the underline row must have MORE foreground pixels than the plain row
                // (the decoration line adds pixels). Measure the left (GlyphMeshPro) half per row band.
                int plainFg = CountForeground(tex, 0, Width / 2, BandTop(0, rowH), BandBottom(0, rowH), bg);
                int underFg = CountForeground(tex, 0, Width / 2, BandTop(1, rowH), BandBottom(1, rowH), bg);
                int strikeFg = CountForeground(tex, 0, Width / 2, BandTop(2, rowH), BandBottom(2, rowH), bg);
                Object.DestroyImmediate(tex);

                Debug.Log($"[StyleEvidence] fg pixels left-half: plain={plainFg} underline={underFg} strike={strikeFg}");
                Assert.Greater(plainFg, 0, "Plain row drew nothing.");
                Assert.Greater(underFg, plainFg, "Underline must add foreground pixels vs plain (the line).");
                Assert.Greater(strikeFg, plainFg, "Strikethrough must add foreground pixels vs plain (the line).");
                Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length > 1000, "Evidence PNG not written.");
            }
            finally
            {
                foreach (var g in gos) { if (g != null) { var c = g.GetComponent<Camera>(); if (c != null) c.targetTexture = null; } }
                rt.Release(); Object.DestroyImmediate(rt);
                foreach (var g in gos) if (g != null) Object.DestroyImmediate(g);
                if (stack != null) Object.DestroyImmediate(stack);
                if (ogFont != null) Object.DestroyImmediate(ogFont);
                if (tmpFont != null) Object.DestroyImmediate(tmpFont);
#if UNITY_EDITOR
                if (unityFont != null && !UnityEditor.AssetDatabase.Contains(unityFont)) Object.DestroyImmediate(unityFont);
#endif
            }
        }

        private static int BandTop(int row, float rowH) => Mathf.RoundToInt(40 + row * rowH);
        private static int BandBottom(int row, float rowH) => Mathf.RoundToInt(40 + (row + 1) * rowH);

        private static int CountForeground(Texture2D tex, int x0, int x1, int yTopFromTop, int yBotFromTop, Color bg)
        {
            // Texture coords are bottom-up; convert the top-down band to bottom-up rows.
            int h = tex.height;
            int yA = Mathf.Clamp(h - yBotFromTop, 0, h);
            int yB = Mathf.Clamp(h - yTopFromTop, 0, h);
            int n = 0;
            var px = tex.GetPixels(x0, yA, Mathf.Max(1, x1 - x0), Mathf.Max(1, yB - yA));
            foreach (var p in px)
                if (Mathf.Abs(p.r - bg.r) + Mathf.Abs(p.g - bg.g) + Mathf.Abs(p.b - bg.b) > 0.25f) n++;
            return n;
        }

        [Test]
        public void Alignment_Justified_Flush_SideBySide()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No real graphics device; justified render evidence skipped.");
            string notoPath = FindNoto();
            if (notoPath == null) Assert.Ignore("NotoSans-Regular.ttf fixture not found.");

            var ogFont = UniTextFont.CreateFontAsset(File.ReadAllBytes(notoPath));
            if (ogFont == null) Assert.Ignore("UniTextFont build failed.");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(ogFont);

            TMP_FontAsset tmpFont = null; Font unityFont = null;
#if UNITY_EDITOR
            unityFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>("Assets/ParityFonts/NotoSans-Regular.ttf");
#endif
            if (unityFont == null) unityFont = Font.CreateDynamicFontFromOSFont("Noto Sans", 32);
            if (unityFont != null)
                try { tmpFont = TMP_FontAsset.CreateFontAsset(unityFont, 90, 9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true); }
                catch (System.Exception ex) { Debug.LogWarning($"[JustifyEvidence] TMP build failed: {ex.Message}"); }

            const string para =
                "The quick brown fox jumps over the lazy dog while five boxing wizards jump quickly to vex the gymnast.";
            float colW = Width / 2f - 60;
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var bg = new Color(0.10f, 0.10f, 0.12f, 1f);
            var gos = new System.Collections.Generic.List<GameObject>();
            try
            {
                var canvasGo = new GameObject("JCanvas", typeof(Canvas)); gos.Add(canvasGo);
                var canvas = canvasGo.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
                ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(Width, Height);
                var camGo = new GameObject("JCam", typeof(Camera)); gos.Add(camGo);
                var cam = camGo.GetComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = Height / 2f; cam.aspect = (float)Width / Height;
                cam.transform.position = new Vector3(0, 0, -10);
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = bg; cam.targetTexture = rt;
                canvas.worldCamera = cam;

                System.Action<string, float, float, OpenGlyph.TextAlignmentOptions> og =
                    (label, cx, cy, al) =>
                {
                    var go = new GameObject("og_" + label, typeof(RectTransform));
                    go.transform.SetParent(canvasGo.transform, false);
                    var r = (RectTransform)go.transform; r.sizeDelta = new Vector2(colW, 180); r.anchoredPosition = new Vector2(cx, cy);
                    var c = go.AddComponent<GlyphMeshProUGUI>();
                    c.FontStack = stack; c.fontSize = 30; c.color = Color.white; c.enableWordWrapping = true;
                    c.alignment = al; c.text = label + "\n" + para;
                    c.ForceMeshUpdate(ignoreActiveState: true); gos.Add(go);
                };
                // Left column: GlyphMeshPro Left / Justified / Flush stacked.
                og("GMP Left", -Width / 4f, 200, OpenGlyph.TextAlignmentOptions.TopLeft);
                og("GMP Justified", -Width / 4f, -40, OpenGlyph.TextAlignmentOptions.TopJustified);
                og("GMP Flush", -Width / 4f, -280, OpenGlyph.TextAlignmentOptions.TopFlush);

                if (tmpFont != null)
                {
                    System.Action<string, float, float, TMPro.TextAlignmentOptions> tm =
                        (label, cx, cy, al) =>
                    {
                        var go = new GameObject("tmp_" + label, typeof(RectTransform));
                        go.transform.SetParent(canvasGo.transform, false);
                        var r = (RectTransform)go.transform; r.sizeDelta = new Vector2(colW, 180); r.anchoredPosition = new Vector2(cx, cy);
                        var t = go.AddComponent<TextMeshProUGUI>();
                        t.font = tmpFont; t.fontSize = 30; t.color = Color.white; t.enableWordWrapping = true;
                        t.alignment = al; t.text = label + "\n" + para; t.ForceMeshUpdate(); gos.Add(go);
                    };
                    tm("TMP Left", Width / 4f, 200, TMPro.TextAlignmentOptions.TopLeft);
                    tm("TMP Justified", Width / 4f, -40, TMPro.TextAlignmentOptions.TopJustified);
                    tm("TMP Flush", Width / 4f, -280, TMPro.TextAlignmentOptions.TopFlush);
                }

                Canvas.ForceUpdateCanvases(); cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); tex.Apply(); RenderTexture.active = prev;
                string path = Path.Combine(EvidenceDir(), "alignment_justified_flush.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Debug.Log($"[RenderEvidence] justified/flush -> {path}");
                Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length > 1000, "Evidence PNG not written.");
            }
            finally
            {
                foreach (var g in gos) { if (g != null) { var c = g.GetComponent<Camera>(); if (c != null) c.targetTexture = null; } }
                rt.Release(); Object.DestroyImmediate(rt);
                foreach (var g in gos) if (g != null) Object.DestroyImmediate(g);
                if (stack != null) Object.DestroyImmediate(stack);
                if (ogFont != null) Object.DestroyImmediate(ogFont);
                if (tmpFont != null) Object.DestroyImmediate(tmpFont);
#if UNITY_EDITOR
                if (unityFont != null && !UnityEditor.AssetDatabase.Contains(unityFont)) Object.DestroyImmediate(unityFont);
#endif
            }
        }

        [Test]
        public void FontStyle_Upper_Lower_SideBySide()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No real graphics device; case render evidence skipped.");
            string notoPath = FindNoto();
            if (notoPath == null) Assert.Ignore("NotoSans-Regular.ttf fixture not found.");
            var ogFont = UniTextFont.CreateFontAsset(File.ReadAllBytes(notoPath));
            if (ogFont == null) Assert.Ignore("UniTextFont build failed.");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(ogFont);

            TMP_FontAsset tmpFont = null; Font unityFont = null;
#if UNITY_EDITOR
            unityFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>("Assets/ParityFonts/NotoSans-Regular.ttf");
#endif
            if (unityFont == null) unityFont = Font.CreateDynamicFontFromOSFont("Noto Sans", (int)FontSize);
            if (unityFont != null)
                try { tmpFont = TMP_FontAsset.CreateFontAsset(unityFont, 90, 9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true); }
                catch (System.Exception ex) { Debug.LogWarning($"[CaseEvidence] TMP build failed: {ex.Message}"); }

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var bg = new Color(0.10f, 0.10f, 0.12f, 1f);
            var gos = new System.Collections.Generic.List<GameObject>();
            try
            {
                var canvasGo = new GameObject("CCanvas", typeof(Canvas)); gos.Add(canvasGo);
                var canvas = canvasGo.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
                ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(Width, Height);
                var camGo = new GameObject("CCam", typeof(Camera)); gos.Add(camGo);
                var cam = camGo.GetComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = Height / 2f; cam.aspect = (float)Width / Height;
                cam.transform.position = new Vector3(0, 0, -10);
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = bg; cam.targetTexture = rt;
                canvas.worldCamera = cam;

                const string sample = "MixedCase Text";
                float rowH = (Height - 80) / 3f;
                System.Action<string, float, OpenGlyph.FontStyles> ogRow = (lbl, yTop, st) =>
                {
                    var go = new GameObject("og_" + lbl, typeof(RectTransform)); go.transform.SetParent(canvasGo.transform, false);
                    var r = (RectTransform)go.transform; r.sizeDelta = new Vector2(Width/2f-40, rowH-10);
                    r.anchoredPosition = new Vector2(-Width/4f, Height/2f-40-yTop-rowH/2f);
                    var c = go.AddComponent<GlyphMeshProUGUI>(); c.FontStack = stack; c.fontSize = FontSize; c.color = Color.white;
                    c.alignment = OpenGlyph.TextAlignmentOptions.Left; c.fontStyle = st; c.text = lbl + ": " + sample;
                    c.ForceMeshUpdate(ignoreActiveState: true); gos.Add(go);
                };
                ogRow("Plain", 0*rowH, OpenGlyph.FontStyles.Normal);
                ogRow("Upper", 1*rowH, OpenGlyph.FontStyles.UpperCase);
                ogRow("Lower", 2*rowH, OpenGlyph.FontStyles.LowerCase);
                if (tmpFont != null)
                {
                    System.Action<string, float, TMPro.FontStyles> tmRow = (lbl, yTop, st) =>
                    {
                        var go = new GameObject("tmp_" + lbl, typeof(RectTransform)); go.transform.SetParent(canvasGo.transform, false);
                        var r = (RectTransform)go.transform; r.sizeDelta = new Vector2(Width/2f-40, rowH-10);
                        r.anchoredPosition = new Vector2(Width/4f, Height/2f-40-yTop-rowH/2f);
                        var t = go.AddComponent<TextMeshProUGUI>(); t.font = tmpFont; t.fontSize = FontSize; t.color = Color.white;
                        t.alignment = TMPro.TextAlignmentOptions.Left; t.fontStyle = st; t.text = lbl + ": " + sample; t.ForceMeshUpdate(); gos.Add(go);
                    };
                    tmRow("Plain", 0*rowH, TMPro.FontStyles.Normal);
                    tmRow("Upper", 1*rowH, TMPro.FontStyles.UpperCase);
                    tmRow("Lower", 2*rowH, TMPro.FontStyles.LowerCase);
                }
                Canvas.ForceUpdateCanvases(); cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0,0,Width,Height),0,0); tex.Apply(); RenderTexture.active = prev;
                string path = Path.Combine(EvidenceDir(), "fontstyle_upper_lower.png");
                File.WriteAllBytes(path, tex.EncodeToPNG()); Object.DestroyImmediate(tex);
                Debug.Log($"[RenderEvidence] fontStyle upper/lower -> {path}");
                Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length > 1000, "Evidence PNG not written.");
            }
            finally
            {
                foreach (var g in gos) { if (g != null) { var c = g.GetComponent<Camera>(); if (c != null) c.targetTexture = null; } }
                rt.Release(); Object.DestroyImmediate(rt);
                foreach (var g in gos) if (g != null) Object.DestroyImmediate(g);
                if (stack != null) Object.DestroyImmediate(stack);
                if (ogFont != null) Object.DestroyImmediate(ogFont);
                if (tmpFont != null) Object.DestroyImmediate(tmpFont);
#if UNITY_EDITOR
                if (unityFont != null && !UnityEditor.AssetDatabase.Contains(unityFont)) Object.DestroyImmediate(unityFont);
#endif
            }
        }

        [Test]
        public void Markup_Sup_Sub_SideBySide()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No real graphics device; sup/sub render evidence skipped.");
            string notoPath = FindNoto();
            if (notoPath == null) Assert.Ignore("NotoSans-Regular.ttf fixture not found.");
            var ogFont = UniTextFont.CreateFontAsset(File.ReadAllBytes(notoPath));
            if (ogFont == null) Assert.Ignore("UniTextFont build failed.");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(ogFont);

            TMP_FontAsset tmpFont = null; Font unityFont = null;
#if UNITY_EDITOR
            unityFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>("Assets/ParityFonts/NotoSans-Regular.ttf");
#endif
            if (unityFont == null) unityFont = Font.CreateDynamicFontFromOSFont("Noto Sans", (int)FontSize);
            if (unityFont != null)
                try { tmpFont = TMP_FontAsset.CreateFontAsset(unityFont, 90, 9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true); }
                catch (System.Exception ex) { Debug.LogWarning($"[SupSubEvidence] TMP build failed: {ex.Message}"); }

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var bg = new Color(0.10f, 0.10f, 0.12f, 1f);
            var gos = new System.Collections.Generic.List<GameObject>();
            try
            {
                var canvasGo = new GameObject("SSCanvas", typeof(Canvas)); gos.Add(canvasGo);
                var canvas = canvasGo.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
                ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(Width, Height);
                var camGo = new GameObject("SSCam", typeof(Camera)); gos.Add(camGo);
                var cam = camGo.GetComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = Height / 2f; cam.aspect = (float)Width / Height;
                cam.transform.position = new Vector3(0, 0, -10);
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = bg; cam.targetTexture = rt;
                canvas.worldCamera = cam;

                // Chemistry/math style sample exercises both sup and sub.
                const string sample = "E=mc<sup>2</sup>  H<sub>2</sub>O  x<sup>n</sup>+y<sub>i</sub>";
                System.Action<string, float, float, bool> row = (label, cx, cy, tmp) =>
                {
                    var go = new GameObject((tmp ? "tmp_" : "og_") + label, typeof(RectTransform));
                    go.transform.SetParent(canvasGo.transform, false);
                    var r = (RectTransform)go.transform; r.sizeDelta = new Vector2(Width/2f-40, 200);
                    r.anchoredPosition = new Vector2(cx, cy);
                    if (tmp)
                    {
                        var t = go.AddComponent<TextMeshProUGUI>(); t.font = tmpFont; t.fontSize = FontSize;
                        t.color = Color.white; t.richText = true; t.alignment = TMPro.TextAlignmentOptions.Left;
                        t.text = label + "\n" + sample; t.ForceMeshUpdate();
                    }
                    else
                    {
                        var c = go.AddComponent<GlyphMeshProUGUI>(); c.FontStack = stack; c.fontSize = FontSize;
                        c.color = Color.white; c.richText = true; c.alignment = OpenGlyph.TextAlignmentOptions.Left;
                        c.text = label + "\n" + sample; c.ForceMeshUpdate(ignoreActiveState: true);
                    }
                    gos.Add(go);
                };
                row("GMP sup/sub", -Width/4f, 60, false);
                if (tmpFont != null) row("TMP sup/sub", Width/4f, 60, true);

                Canvas.ForceUpdateCanvases(); cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0,0,Width,Height),0,0); tex.Apply(); RenderTexture.active = prev;
                string path = Path.Combine(EvidenceDir(), "markup_sup_sub.png");
                File.WriteAllBytes(path, tex.EncodeToPNG()); Object.DestroyImmediate(tex);
                Debug.Log($"[RenderEvidence] sup/sub -> {path}");
                Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length > 1000, "Evidence PNG not written.");
            }
            finally
            {
                foreach (var g in gos) { if (g != null) { var c = g.GetComponent<Camera>(); if (c != null) c.targetTexture = null; } }
                rt.Release(); Object.DestroyImmediate(rt);
                foreach (var g in gos) if (g != null) Object.DestroyImmediate(g);
                if (stack != null) Object.DestroyImmediate(stack);
                if (ogFont != null) Object.DestroyImmediate(ogFont);
                if (tmpFont != null) Object.DestroyImmediate(tmpFont);
#if UNITY_EDITOR
                if (unityFont != null && !UnityEditor.AssetDatabase.Contains(unityFont)) Object.DestroyImmediate(unityFont);
#endif
            }
        }

        [Test]
        public void Markup_VOffset_SideBySide()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No real graphics device; voffset render evidence skipped.");
            string notoPath = FindNoto();
            if (notoPath == null) Assert.Ignore("NotoSans-Regular.ttf fixture not found.");
            var ogFont = UniTextFont.CreateFontAsset(File.ReadAllBytes(notoPath));
            if (ogFont == null) Assert.Ignore("UniTextFont build failed.");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(ogFont);

            TMP_FontAsset tmpFont = null; Font unityFont = null;
#if UNITY_EDITOR
            unityFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>("Assets/ParityFonts/NotoSans-Regular.ttf");
#endif
            if (unityFont == null) unityFont = Font.CreateDynamicFontFromOSFont("Noto Sans", (int)FontSize);
            if (unityFont != null)
                try { tmpFont = TMP_FontAsset.CreateFontAsset(unityFont, 90, 9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true); }
                catch (System.Exception ex) { Debug.LogWarning($"[VOffsetEvidence] TMP build failed: {ex.Message}"); }

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var bg = new Color(0.10f, 0.10f, 0.12f, 1f);
            var gos = new System.Collections.Generic.List<GameObject>();
            try
            {
                var canvasGo = new GameObject("VCanvas", typeof(Canvas)); gos.Add(canvasGo);
                var canvas = canvasGo.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
                ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(Width, Height);
                var camGo = new GameObject("VCam", typeof(Camera)); gos.Add(camGo);
                var cam = camGo.GetComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = Height / 2f; cam.aspect = (float)Width / Height;
                cam.transform.position = new Vector3(0, 0, -10);
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = bg; cam.targetTexture = rt;
                canvas.worldCamera = cam;

                const string sample = "base <voffset=0.4em>UP</voffset> mid <voffset=-0.3em>DOWN</voffset> end";
                System.Action<float, bool> row = (cx, tmp) =>
                {
                    var go = new GameObject((tmp ? "tmp" : "og"), typeof(RectTransform)); go.transform.SetParent(canvasGo.transform, false);
                    var r = (RectTransform)go.transform; r.sizeDelta = new Vector2(Width/2f-40, 200); r.anchoredPosition = new Vector2(cx, 40);
                    if (tmp) { var t = go.AddComponent<TextMeshProUGUI>(); t.font = tmpFont; t.fontSize = FontSize; t.color = Color.white; t.richText = true; t.alignment = TMPro.TextAlignmentOptions.Left; t.text = "TMP\n" + sample; t.ForceMeshUpdate(); }
                    else { var c = go.AddComponent<GlyphMeshProUGUI>(); c.FontStack = stack; c.fontSize = FontSize; c.color = Color.white; c.richText = true; c.alignment = OpenGlyph.TextAlignmentOptions.Left; c.text = "GMP\n" + sample; c.ForceMeshUpdate(ignoreActiveState: true); }
                    gos.Add(go);
                };
                row(-Width/4f, false);
                if (tmpFont != null) row(Width/4f, true);

                Canvas.ForceUpdateCanvases(); cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0,0,Width,Height),0,0); tex.Apply(); RenderTexture.active = prev;
                string path = Path.Combine(EvidenceDir(), "markup_voffset.png");
                File.WriteAllBytes(path, tex.EncodeToPNG()); Object.DestroyImmediate(tex);
                Debug.Log($"[RenderEvidence] voffset -> {path}");
                Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length > 1000, "Evidence PNG not written.");
            }
            finally
            {
                foreach (var g in gos) { if (g != null) { var c = g.GetComponent<Camera>(); if (c != null) c.targetTexture = null; } }
                rt.Release(); Object.DestroyImmediate(rt);
                foreach (var g in gos) if (g != null) Object.DestroyImmediate(g);
                if (stack != null) Object.DestroyImmediate(stack);
                if (ogFont != null) Object.DestroyImmediate(ogFont);
                if (tmpFont != null) Object.DestroyImmediate(tmpFont);
#if UNITY_EDITOR
                if (unityFont != null && !UnityEditor.AssetDatabase.Contains(unityFont)) Object.DestroyImmediate(unityFont);
#endif
            }
        }

        private static string FindNoto()
        {
            string[] candidates =
            {
                "Packages/com.openglyph.text/Tests/Editor/Fonts/NotoSansFamily/NotoSans-Regular.ttf",
                "Packages/com.openglyph.text/Defaults/NotoSans-Regular.ttf",
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }
    }
}
