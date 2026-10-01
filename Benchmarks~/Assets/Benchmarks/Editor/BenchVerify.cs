// SPDX-License-Identifier: MIT
// Fresh-project render proof: import TMP essentials the official way, then render
// a TextMeshProUGUI, an OpenGlyph UniText, and a UI Toolkit Label to PNGs and
// assert each produced non-background pixels. Run BEFORE any benchmark build:
//   Unity -batchmode -projectPath <v2> -executeMethod OpenGlyph.Benchmarks.Editor.BenchVerify.RenderProofCI -logFile ...
// (NOT -nographics: we need a GPU/readback. Exits 0 only if all three show text.)
using System.IO;
using LightSide;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace OpenGlyph.Benchmarks.Editor
{
    public static class BenchVerify
    {
        private const string EvidenceDir = @"D:/OpenGlyphWork/scratch/benchmarks/evidence";

        public static void RenderProofCI()
        {
            try { RenderProof(); EditorApplication.Exit(0); }
            catch (System.Exception ex) { Debug.LogError("[Verify] FAILED: " + ex); EditorApplication.Exit(7); }
        }

        // Import-only entry: run FIRST in its own session (ImportPackage assets are not
        // fully usable until the next session), then run RenderProofCI.
        public static void ImportTmpEssentialsCI()
        {
            try { ImportTmpEssentials(); EditorApplication.Exit(0); }
            catch (System.Exception ex) { Debug.LogError("[Verify] import FAILED: " + ex); EditorApplication.Exit(9); }
        }

        // Play-mode render proof: build a scene with RenderProofBehaviour (TMP font asset
        // + OpenGlyph stack wired) and enter play mode; the behaviour writes the 3 PNGs
        // and calls EditorApplication.Exit. More reliable than editor-batch cam.Render.
        public static void RenderProofPlayCI()
        {
            TMPro.TMP_FontAsset fa = null;
            try
            {
                TMP_Settings.LoadDefaultSettings();
                var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/StreamingAssets/Fonts/NotoSans-Regular.ttf");
                if (font == null)
                {
                    // import a Font copy outside StreamingAssets
                    const string dst = "Assets/Benchmarks/Generated/NotoSans-Regular.ttf";
                    Directory.CreateDirectory("Assets/Benchmarks/Generated");
                    File.Copy(Path.Combine(Application.streamingAssetsPath, "Fonts", "NotoSans-Regular.ttf"), dst, true);
                    AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
                    font = AssetDatabase.LoadAssetAtPath<Font>(dst);
                }
                if (TMP_Settings.instance != null && font != null)
                    fa = TMPro.TMP_FontAsset.CreateFontAsset(font);
            }
            catch (System.Exception ex) { Debug.LogWarning("[Verify] TMP font asset build: " + ex.Message); }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var go = new GameObject("RenderProof");
            var b = go.AddComponent<RenderProofBehaviour>();
            b.openGlyphFonts = AssetDatabase.LoadAssetAtPath<UniTextFontStack>("Packages/com.openglyph.text/Defaults/UniTextFonts_Default.asset");
            b.openGlyphAppearance = AssetDatabase.LoadAssetAtPath<UniTextAppearance>("Packages/com.openglyph.text/Defaults/UniTextAppearance_Default.asset");
            b.tmpFontAsset = fa;
            b.themeStyleSheet = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>("Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
            string scenePath = "Assets/Benchmarks/Scenes/RenderProof.unity";
            Directory.CreateDirectory("Assets/Benchmarks/Scenes");
            EditorSceneManager.SaveScene(scene, scenePath);
            EditorSceneManager.OpenScene(scenePath);
            Debug.Log("[Verify] entering play mode for render proof");
            EditorApplication.EnterPlaymode();
        }

        // Import TMP Essential Resources from the ugui package (official path).
        public static void ImportTmpEssentials()
        {
            if (File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset")) { Debug.Log("[Verify] TMP essentials already present"); return; }
            string pkg = Path.Combine(EditorApplication.applicationContentsPath,
                "Resources/PackageManager/BuiltInPackages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage");
            if (!File.Exists(pkg)) { Debug.LogError("[Verify] TMP essentials unitypackage not found: " + pkg); return; }
            AssetDatabase.ImportPackage(pkg, false);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("[Verify] imported TMP Essential Resources");
        }

        private static void RenderProof()
        {
            Directory.CreateDirectory(EvidenceDir);
            ImportTmpEssentials();
            // After import, resolve the settings singleton explicitly.
            var tmpSettings = Resources.Load<TMP_Settings>("TMP Settings");
            if (tmpSettings != null) TMP_Settings.LoadDefaultSettings();
            bool instOk = TMP_Settings.instance != null;
            bool fontOk = instOk && TMP_Settings.defaultFontAsset != null;
            Debug.Log($"[Verify] TMP_Settings.instance={instOk} defaultFontAsset={fontOk}");

            int ok = 0;
            ok += RenderUGUI("tmp", (go) =>
            {
                var t = go.AddComponent<TextMeshProUGUI>();
                t.text = "TMP renders OK 123";
                t.fontSize = 48; t.color = Color.white; t.alignment = TextAlignmentOptions.Center;
                t.ForceMeshUpdate(true, true);
            }) ? 1 : 0;

            ok += RenderUGUI("openglyph", (go) =>
            {
                var ut = go.AddComponent<UniText>();
                ut.FontStack = LoadOpenGlyphStack();
                ut.Appearance = AssetDatabase.LoadAssetAtPath<UniTextAppearance>("Packages/com.openglyph.text/Defaults/UniTextAppearance_Default.asset");
                ut.color = Color.white;
                ut.Text = "OpenGlyph renders OK 123";
                Canvas.ForceUpdateCanvases();
            }) ? 1 : 0;

            ok += RenderUITK("uitoolkit") ? 1 : 0;

            Debug.Log($"[Verify] render proof: {ok}/3 systems rendered non-background pixels");
            if (ok < 3) { Debug.LogError("[Verify] NOT all three rendered — do not build."); EditorApplication.Exit(8); }
        }

        private static UniTextFontStack LoadOpenGlyphStack()
        {
            var s = AssetDatabase.LoadAssetAtPath<UniTextFontStack>("Packages/com.openglyph.text/Defaults/UniTextFonts_Default.asset");
            return s;
        }

        // Render a uGUI graphic (TMP or UniText) under a screen-space camera to a PNG.
        private static bool RenderUGUI(string name, System.Action<GameObject> addText)
        {
            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.orthographic = true;

            var canvasGo = new GameObject("C", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = cam;

            var txtGo = new GameObject("T", typeof(RectTransform));
            txtGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)txtGo.transform; rt.sizeDelta = new Vector2(600, 200);
            addText(txtGo);

            bool pass = RenderAndCheck(cam, name);
            Object.DestroyImmediate(txtGo); Object.DestroyImmediate(canvasGo); Object.DestroyImmediate(camGo);
            return pass;
        }

        private static bool RenderUITK(string name)
        {
            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;

            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.scaleMode = PanelScaleMode.ConstantPixelSize;
            var theme = Resources.Load<ThemeStyleSheet>("unity-default-runtime-theme") ?? Resources.Load<ThemeStyleSheet>("Default Theme Style Sheet");
            if (theme != null) ps.themeStyleSheet = theme;
            ps.targetTexture = new RenderTexture(512, 256, 24);

            var docGo = new GameObject("UIDoc", typeof(UIDocument));
            var doc = docGo.GetComponent<UIDocument>();
            doc.panelSettings = ps;
            var lbl = new Label("UITK renders OK 123");
            lbl.style.color = Color.white; lbl.style.fontSize = 48;
            doc.rootVisualElement.Add(lbl);
            doc.rootVisualElement.MarkDirtyRepaint();

            // Force the panel to repaint into its target texture.
            var mi = typeof(IPanel).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
            // Fallback: just read the panel's target texture after a manual update.
            var method = ps.GetType().GetMethod("SetScreenToPanelSpaceFunction"); // no-op touch
            bool pass = ReadTextureCheck(ps.targetTexture, name);
            Object.DestroyImmediate(docGo); Object.DestroyImmediate(camGo);
            return pass;
        }

        private static bool RenderAndCheck(Camera cam, string name)
        {
            var rt = new RenderTexture(512, 256, 24);
            cam.targetTexture = rt;
            cam.Render();
            bool pass = ReadTextureCheck(rt, name);
            cam.targetTexture = null;
            return pass;
        }

        private static bool ReadTextureCheck(RenderTexture rt, string name)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            int nonBg = 0;
            var px = tex.GetPixels32();
            foreach (var c in px) if (c.r > 20 || c.g > 20 || c.b > 20) nonBg++;

            string path = Path.Combine(EvidenceDir, "tmp_render_check.png".Replace("tmp", name));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"[Verify] {name}: nonBackgroundPixels={nonBg} -> {path}");
            Object.DestroyImmediate(tex);
            return nonBg > 0;
        }
    }
}
