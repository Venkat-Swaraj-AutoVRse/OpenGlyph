// SPDX-License-Identifier: MIT
// Runtime render proof (play mode / player): renders a TextMeshProUGUI, an OpenGlyph
// UniText, and a UI Toolkit Label over REAL frames to RenderTextures, reads pixels,
// writes 3 PNGs to the evidence dir, asserts non-background pixels>0 for all three,
// then exits. Reliable where editor-batch cam.Render() was not.
using System.Collections;
using System.IO;
using LightSide;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace OpenGlyph.Benchmarks
{
    public class RenderProofBehaviour : MonoBehaviour
    {
        public string evidenceDir = @"D:/OpenGlyphWork/scratch/benchmarks/evidence";
        public UniTextFontStack openGlyphFonts;
        public UniTextAppearance openGlyphAppearance;
        public TMP_FontAsset tmpFontAsset;
        public ThemeStyleSheet themeStyleSheet;

        private void Start() { StartCoroutine(Watchdog()); StartCoroutine(Run()); }

        // Never wait unbounded: force-exit after 20 s even if a capture stalls.
        private IEnumerator Watchdog()
        {
            float t = 0f;
            while (t < 20f) { t += Time.unscaledDeltaTime; yield return null; }
            Debug.LogError("[Verify] WATCHDOG 20s — a capture stalled; FAIL");
            HardExit(1);
        }

        private IEnumerator Run()
        {
            Directory.CreateDirectory(evidenceDir);
            int ok = 0;

            // ---- TMP ----
            {
                var cam = NewCam();
                var canvas = NewCanvas(cam);
                var go = new GameObject("T", typeof(RectTransform));
                go.transform.SetParent(canvas.transform, false);
                ((RectTransform)go.transform).sizeDelta = new Vector2(500, 160);
                var t = go.AddComponent<TextMeshProUGUI>();
                if (tmpFontAsset != null) t.font = tmpFontAsset;
                t.text = "TMP renders OK 123";
                t.fontSize = 44; t.color = Color.white; t.alignment = TextAlignmentOptions.Center;
                t.ForceMeshUpdate(true, true);
                yield return null; yield return null;
                ok += Capture(cam, "tmp");
                Destroy(cam.gameObject); Destroy(canvas.gameObject);
                yield return null;
            }

            // ---- OpenGlyph ----
            {
                var cam = NewCam();
                var canvas = NewCanvas(cam);
                var go = new GameObject("U", typeof(RectTransform));
                go.transform.SetParent(canvas.transform, false);
                ((RectTransform)go.transform).sizeDelta = new Vector2(500, 160);
                var ut = go.AddComponent<UniText>();
                if (openGlyphFonts != null) ut.FontStack = openGlyphFonts;
                if (openGlyphAppearance != null) ut.Appearance = openGlyphAppearance;
                ut.color = Color.white; ut.Text = "OpenGlyph renders OK 123";
                Canvas.ForceUpdateCanvases();
                yield return null; yield return null;
                ok += Capture(cam, "openglyph");
                Destroy(cam.gameObject); Destroy(canvas.gameObject);
                yield return null;
            }

            // ---- UI Toolkit ----
            {
                var cam = NewCam();
                var ps = ScriptableObject.CreateInstance<PanelSettings>();
                ps.scaleMode = PanelScaleMode.ConstantPixelSize;
                if (themeStyleSheet != null) ps.themeStyleSheet = themeStyleSheet;
                var rtTex = new RenderTexture(512, 256, 24); ps.targetTexture = rtTex;
                var docGo = new GameObject("UIDoc", typeof(UIDocument));
                var doc = docGo.GetComponent<UIDocument>();
                doc.panelSettings = ps;
                var lbl = new Label("UITK renders OK 123");
                lbl.style.color = Color.white; lbl.style.fontSize = 44;
                lbl.style.position = Position.Absolute; lbl.style.left = 40; lbl.style.top = 90;
                doc.rootVisualElement.Add(lbl);
                // Bounded: at most 10 plain frames (no unbounded WaitForEndOfFrame).
                for (int f = 0; f < 10; f++) { doc.rootVisualElement.MarkDirtyRepaint(); yield return null; }
                ok += ReadRt(rtTex, "uitoolkit");
                Destroy(docGo); Destroy(cam.gameObject);
                yield return null;
            }

            Debug.Log($"[Verify] render proof: {ok}/3 systems rendered non-background pixels");
            HardExit(ok >= 3 ? 0 : 8);
        }

        private static Camera NewCam()
        {
            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.orthographic = true;
            return cam;
        }

        private static Canvas NewCanvas(Camera cam)
        {
            var canvasGo = new GameObject("C", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = cam; canvas.planeDistance = 1f;
            var rt = new RenderTexture(512, 256, 24); cam.targetTexture = rt;
            return canvas;
        }

        private int Capture(Camera cam, string name)
        {
            cam.Render();
            return ReadRt(cam.targetTexture, name);
        }

        private int ReadRt(RenderTexture rt, string name)
        {
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            int nonBg = 0; foreach (var c in tex.GetPixels32()) if (c.r > 20 || c.g > 20 || c.b > 20) nonBg++;
            File.WriteAllBytes(Path.Combine(evidenceDir, name + "_render_check.png"), tex.EncodeToPNG());
            Debug.Log($"[Verify] {name}: nonBackgroundPixels={nonBg}");
            Destroy(tex);
            return nonBg > 0 ? 1 : 0;
        }

        private void HardExit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
    }
}
