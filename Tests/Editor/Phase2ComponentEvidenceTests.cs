using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// STEP 2 (round 5): evidence through an ACTUAL UniText component — a WorldSpace Canvas with real
    /// UniText components rendered by a dedicated orthographic camera to a RenderTexture. This runs
    /// the FULL component pipeline including the synthetic BoldModifier/ItalicModifier, so a
    /// Regular-only family under FontWeight 700 shows the engine's real faux-bold, while a full family
    /// selects the real face. Family (SDF + MSDF) and RobotoFlex wght/wdth sheets. Honest: if MSDF
    /// renders flipped here too, that is the component path (a product issue), not the harness.
    /// </summary>
    public class Phase2ComponentEvidenceTests
    {
        private const float FontSize = 40f;
        private const int RowW = 820, RowH = 86;
        private const string Sample = "Reading";

        private readonly List<GameObject> _gos = new();
        private readonly List<UnityEngine.Object> _assets = new();

        private static string OutDir()
        {
            string dir = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "Phase2Evidence");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string NotoFamilyDir()
        {
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) return null;
            string pkgRoot = Directory.GetParent(Path.GetDirectoryName(noto))?.FullName;
            string dir = pkgRoot == null ? null : Path.Combine(pkgRoot, "Tests", "Editor", "Fonts", "NotoSansFamily");
            return (dir != null && Directory.Exists(dir)) ? dir : null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var g in _gos) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            foreach (var a in _assets) if (a != null) UnityEngine.Object.DestroyImmediate(a);
            _gos.Clear(); _assets.Clear();
            Shaper.ClearAllCaches();
        }

        private UniTextFont Face(string path, UniTextRenderMode mode)
        {
            var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: 72, renderMode: mode);
            if (f != null) _assets.Add(f);
            return f;
        }

        // Builds one UniText component as a row child on the canvas.
        private void AddRow(Transform canvas, UniTextFontStack stack, int weight, StyleAxis style, int rowIndex, int rowCount)
        {
            var go = new GameObject($"row{rowIndex}", typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(RowW, RowH);
            // Rows stack top-down in the canvas; center each row vertically in its slot.
            float totalH = rowCount * RowH;
            rt.anchoredPosition = new Vector2(0, totalH / 2f - RowH / 2f - rowIndex * RowH);
            var t = go.AddComponent<UniText>();
            t.FontStack = stack;
            t.FontSize = FontSize;
            if (weight != FontStyleSpec.NormalWeight) t.FontWeight = weight;
            if (style != StyleAxis.Normal) t.FontStyleAxis = style;
            t.Text = Sample;
            _gos.Add(go);
        }

        private Texture2D FinishRender((RenderTexture rt, Camera cam) r, out int drawnPx)
        {
            Canvas.ForceUpdateCanvases();
            r.cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = r.rt;
            var tex = new Texture2D(r.rt.width, r.rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, r.rt.width, r.rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            r.cam.targetTexture = null;
            r.rt.Release(); UnityEngine.Object.DestroyImmediate(r.rt);
            var bg = new Color(0.09f, 0.09f, 0.11f, 1f);
            var px = tex.GetPixels(); int n = 0;
            foreach (var p in px) if (Mathf.Abs(p.r - bg.r) + Mathf.Abs(p.g - bg.g) + Mathf.Abs(p.b - bg.b) > 0.15f) n++;
            drawnPx = n;
            return tex;
        }

        private void FamilySheet(UniTextRenderMode mode, string outName)
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string dir = NotoFamilyDir();
            if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");

            var reg = Face(Path.Combine(dir, "NotoSans-Regular.ttf"), mode);
            var bold = Face(Path.Combine(dir, "NotoSans-Bold.ttf"), mode);
            var ital = Face(Path.Combine(dir, "NotoSans-Italic.ttf"), mode);
            var bital = Face(Path.Combine(dir, "NotoSans-BoldItalic.ttf"), mode);
            if (reg == null || bold == null || ital == null || bital == null) Assert.Ignore("Noto faces failed.");
            if (mode == UniTextRenderMode.Msdf && reg.AtlasRenderMode != UniTextRenderMode.Msdf)
                Assert.Ignore("MSDF export unavailable.");

            var full = ScriptableObject.CreateInstance<FontFamily>();
            full.familyName = "Noto"; full.AddFace(reg); full.AddFace(bold); full.AddFace(ital); full.AddFace(bital);
            _assets.Add(full);
            var fullStack = ScriptableObject.CreateInstance<UniTextFontStack>();
            fullStack.fonts.Add(reg); fullStack.family = full; _assets.Add(fullStack);

            var regOnly = ScriptableObject.CreateInstance<FontFamily>();
            regOnly.familyName = "RegOnly"; regOnly.AddFace(reg); _assets.Add(regOnly);
            var synthStack = ScriptableObject.CreateInstance<UniTextFontStack>();
            synthStack.fonts.Add(reg); synthStack.family = regOnly; _assets.Add(synthStack);

            const int rows = 6;
            var r = RenderCanvasSetup(rows, out var canvasTf);
            AddRow(canvasTf, fullStack, 400, StyleAxis.Normal, 0, rows);   // Regular
            AddRow(canvasTf, fullStack, 700, StyleAxis.Normal, 1, rows);   // real Bold
            AddRow(canvasTf, fullStack, 400, StyleAxis.Italic, 2, rows);   // real Italic
            AddRow(canvasTf, fullStack, 700, StyleAxis.Italic, 3, rows);   // real BoldItalic
            AddRow(canvasTf, synthStack, 700, StyleAxis.Normal, 4, rows);  // synthetic bold (engine modifier)
            AddRow(canvasTf, synthStack, 400, StyleAxis.Italic, 5, rows);  // synthetic italic (engine modifier)

            var tex = FinishRender(r, out int px);
            try
            {
                Assert.Greater(px, 500, "Component render produced almost nothing.");
                string path = Path.Combine(OutDir(), outName);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[Phase2Component] {outName} -> {path} ({px} px)");
                Assert.IsTrue(File.Exists(path));
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        // Overload that also returns the canvas transform for adding rows.
        private (RenderTexture rt, Camera cam) RenderCanvasSetup(int rowCount, out Transform canvasTf)
        {
            int w = RowW, h = rowCount * RowH;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var canvasGo = new GameObject("P2Canvas", typeof(Canvas));
            _gos.Add(canvasGo);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var crt = canvasGo.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(w, h);
            canvasGo.transform.position = Vector3.zero;
            var camGo = new GameObject("P2Cam", typeof(Camera));
            _gos.Add(camGo);
            var cam = camGo.GetComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = h / 2f; cam.aspect = (float)w / h;
            cam.transform.position = new Vector3(0, 0, -10);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.09f, 0.11f, 1f);
            cam.targetTexture = rt;
            canvas.worldCamera = cam;
            canvasTf = canvasGo.transform;
            return (rt, cam);
        }

        [Test] public void FamilySheet_Component_SDF() => FamilySheet(UniTextRenderMode.SDF, "phase2_family_component_sdf.png");
        [Test] public void FamilySheet_Component_MSDF() => FamilySheet(UniTextRenderMode.Msdf, "phase2_family_component_msdf.png");

        [Test]
        public void PlainRegular_NoPhase2_FirstGlyphCheck()
        {
            // Isolation: a plain Regular UniText component with NO family, NO FontWeight/Style, NO
            // variation — pure 1.0 path. If the first glyph is still broken here, the artifact is
            // pre-existing (not caused by Phase 2). Three rows of "Reading".
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans not found.");
            var font = Face(noto, UniTextRenderMode.SDF);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font); _assets.Add(stack);   // plain stack, NO family

            const int rows = 3;
            var r = RenderCanvasSetup(rows, out var tf);
            for (int i = 0; i < rows; i++)
                AddRow(tf, stack, FontStyleSpec.NormalWeight, StyleAxis.Normal, i, rows); // no style props

            var tex = FinishRender(r, out int px);
            try
            {
                Assert.Greater(px, 500, "Plain component render produced almost nothing.");
                string path = Path.Combine(OutDir(), "phase2_plain_component_sdf.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[Phase2Component] plain -> {path} ({px} px)");
                Assert.IsTrue(File.Exists(path));
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Test]
        public void RobotoFlexSheet_Component_SDF()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");
            var font = Face(vf, UniTextRenderMode.SDF);
            var fam = ScriptableObject.CreateInstance<FontFamily>(); fam.familyName="RF"; fam.AddFace(font); _assets.Add(fam);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); stack.fonts.Add(font); stack.family=fam; _assets.Add(stack);

            var specs = new (int w, StyleAxis s)[] { (100,StyleAxis.Normal),(400,StyleAxis.Normal),(700,StyleAxis.Normal),(1000,StyleAxis.Normal) };
            const int rows = 4;
            var r = RenderCanvasSetup(rows, out var tf);
            for (int i = 0; i < specs.Length; i++) AddRow(tf, stack, specs[i].w, specs[i].s, i, rows);

            var tex = FinishRender(r, out int px);
            try
            {
                Assert.Greater(px, 500, "Component render produced almost nothing.");
                string path = Path.Combine(OutDir(), "phase2_robotoflex_component_sdf.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[Phase2Component] robotoflex -> {path} ({px} px)");
                Assert.IsTrue(File.Exists(path));
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }
    }
}
