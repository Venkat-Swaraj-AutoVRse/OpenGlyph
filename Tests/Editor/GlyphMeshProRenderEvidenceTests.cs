// Side-by-side GPU render evidence: a real TextMeshProUGUI and a GlyphMeshProUGUI
// with identical settings, on a WorldSpace Canvas (1280x720), rendered by an
// orthographic camera to a RenderTexture and saved as PNGs. Visual confirmation
// that GlyphMeshProUGUI renders TMP-equivalent text through the OpenGlyph engine.
//
// Requires a REAL graphics device + TMP resources. Under -nographics / NullGfxDevice
// it ignores (the headless suite stays deterministic). Clean-room: no TMP code copied;
// the TMP component is driven only through its public API for comparison.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using TMPro;
using OpenGlyph;

namespace LightSide.Tests
{
    [TestFixture]
    public class GlyphMeshProRenderEvidenceTests
    {
        private const int Width = 1280, Height = 720;
        private const float FontSize = 48f;
        private const string Sample = "OpenGlyph GlyphMeshPro\nvs TextMeshPro parity";

        private static string EvidenceDir()
        {
            string dir = @"D:\OpenGlyphWork\scratch\glyphmeshpro\evidence";
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void SideBySide_TMP_vs_GlyphMeshPro_1280x720()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No real graphics device (-nographics/NullGfxDevice); GPU render evidence skipped.");

            string notoPath = FindNoto();
            if (notoPath == null) Assert.Ignore("NotoSans-Regular.ttf fixture not found.");

            // OpenGlyph font
            var ogFont = UniTextFont.CreateFontAsset(File.ReadAllBytes(notoPath));
            if (ogFont == null) Assert.Ignore("UniTextFont build failed (native backend).");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(ogFont);

            // TMP font (dynamic SDF) from the same TTF
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
                catch (System.Exception ex) { Debug.LogWarning($"[RenderEvidence] TMP font build failed: {ex.Message}"); }
            }
            if (tmpFont == null)
                Assert.Ignore("TMP font asset unavailable (TMP Essential Resources / TMP_Settings missing in host).");

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var created = new System.Collections.Generic.List<Object>();
            var gos = new System.Collections.Generic.List<GameObject>();
            try
            {
                var canvasGo = new GameObject("EvidenceCanvas", typeof(Canvas));
                gos.Add(canvasGo);
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var crt = canvasGo.GetComponent<RectTransform>();
                crt.sizeDelta = new Vector2(Width, Height);
                canvasGo.transform.position = Vector3.zero;

                var camGo = new GameObject("EvidenceCam", typeof(Camera));
                gos.Add(camGo);
                var cam = camGo.GetComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = Height / 2f; cam.aspect = (float)Width / Height;
                cam.transform.position = new Vector3(0, 0, -10);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
                cam.targetTexture = rt;
                canvas.worldCamera = cam;

                // Left half: TMP. Right half: GlyphMeshPro. Same font size, color, wrapping.
                var tmpGo = new GameObject("TMP", typeof(RectTransform));
                tmpGo.transform.SetParent(canvasGo.transform, false);
                var tmpRt = (RectTransform)tmpGo.transform;
                tmpRt.sizeDelta = new Vector2(Width / 2f - 40, Height - 80);
                tmpRt.anchoredPosition = new Vector2(-Width / 4f, 0);
                var tmp = tmpGo.AddComponent<TextMeshProUGUI>();
                tmp.font = tmpFont; tmp.fontSize = FontSize; tmp.color = Color.white;
                tmp.alignment = TMPro.TextAlignmentOptions.TopLeft;
                tmp.text = "[TMP]\n" + Sample;
                tmp.ForceMeshUpdate();
                gos.Add(tmpGo);

                var ogGo = new GameObject("GlyphMeshPro", typeof(RectTransform));
                ogGo.transform.SetParent(canvasGo.transform, false);
                var ogRt = (RectTransform)ogGo.transform;
                ogRt.sizeDelta = new Vector2(Width / 2f - 40, Height - 80);
                ogRt.anchoredPosition = new Vector2(Width / 4f, 0);
                var og = ogGo.AddComponent<GlyphMeshProUGUI>();
                og.FontStack = stack; og.fontSize = FontSize; og.color = Color.white;
                og.alignment = OpenGlyph.TextAlignmentOptions.TopLeft;
                og.text = "[GlyphMeshPro]\n" + Sample;
                og.ForceMeshUpdate(ignoreActiveState: true);
                gos.Add(ogGo);

                Canvas.ForceUpdateCanvases();
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                string path = Path.Combine(EvidenceDir(), "tmp_vs_glyphmeshpro_1280x720.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Debug.Log($"[RenderEvidence] side-by-side -> {path}");
                Assert.IsTrue(File.Exists(path), "Side-by-side PNG was not written.");
                Assert.Greater(new FileInfo(path).Length, 1000, "PNG looks empty.");
            }
            finally
            {
                // Clear camera targets BEFORE releasing the RT (releasing an active targetTexture logs an error).
                foreach (var g in gos)
                {
                    if (g != null)
                    {
                        var c = g.GetComponent<Camera>();
                        if (c != null) c.targetTexture = null;
                    }
                }
                rt.Release(); Object.DestroyImmediate(rt);
                foreach (var g in gos) if (g != null) Object.DestroyImmediate(g);
                if (stack != null) Object.DestroyImmediate(stack);
                if (ogFont != null) Object.DestroyImmediate(ogFont);
                if (tmpFont != null) Object.DestroyImmediate(tmpFont);
#if UNITY_EDITOR
                if (unityFont != null && !UnityEditor.AssetDatabase.Contains(unityFont)) Object.DestroyImmediate(unityFont);
#endif
                foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
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
            string root = Path.GetFullPath(Path.Combine(Application.dataPath ?? ".", ".."));
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "NotoSans-Regular.ttf", SearchOption.AllDirectories))
                {
                    var n = f.Replace('\\', '/');
                    if (n.Contains("com.openglyph.text") && !n.Contains("Benchmarks~")) return f;
                }
            }
            catch { }
            return null;
        }
    }
}
