using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 3, item 4: N = 100 / 500 world labels (UniTextWorld, MeshRenderer, no Canvas) against the same
    /// labels on one World Space Canvas each and on one shared World Space Canvas, in Play Mode with real
    /// frames. Measures create + first build, a full text rebuild, a steady frame, and the frame's draw
    /// calls / batches / SetPass calls (ProfilerRecorder, or UnityStats in the Editor). Writes
    /// world_labels_benchmark.json to OPENGLYPH_BENCH_OUT (or the temp cache).
    /// </summary>
    public class Wave3WorldLabelsPlayTests
    {
        private readonly List<Object> _junk = new();
        private UniTextFontStack _stack;
        private Camera _cam;

        private static string FindFont()
        {
            string[] candidates =
            {
                "Packages/com.openglyph.text/Defaults/NotoSans-Regular.ttf",
                Path.Combine(Application.dataPath ?? "", "..", "Packages", "com.openglyph.text", "Defaults", "NotoSans-Regular.ttf"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return Path.GetFullPath(c);
            var root = Path.GetFullPath(Path.Combine(Application.dataPath ?? ".", ".."));
            try { return Directory.EnumerateFiles(root, "NotoSans-Regular.ttf", SearchOption.AllDirectories).FirstOrDefault(); }
            catch { return null; }
        }

        private static string OutDir()
        {
            var env = System.Environment.GetEnvironmentVariable("OPENGLYPH_BENCH_OUT");
            var dir = !string.IsNullOrEmpty(env) ? env : Application.temporaryCachePath;
            try { Directory.CreateDirectory(dir); return dir; } catch { return Application.temporaryCachePath; }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        private struct Frame { public long draws, batches, setpass; }

        // Editor stats (filled for the last rendered frame) when the profiler counters read 0.
        private static long EditorStat(string name)
        {
            var t = System.Type.GetType("UnityEditor.UnityStats, UnityEditor");
            var p = t?.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return p == null ? 0 : System.Convert.ToInt64(p.GetValue(null));
        }

        private IEnumerator MeasureFrame(Frame[] result)
        {
            using var draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            using var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            using var setpass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            // Batchmode does not present frames, so render the camera explicitly inside each frame.
            for (var i = 0; i < 4; i++) { if (_cam != null) _cam.Render(); yield return null; }
            var f = new Frame
            {
                draws = draws.Valid ? draws.LastValue : 0,
                batches = batches.Valid ? batches.LastValue : 0,
                setpass = setpass.Valid ? setpass.LastValue : 0,
            };
            if (f.draws == 0 && f.batches == 0)
                f = new Frame { draws = EditorStat("drawCalls"), batches = EditorStat("batches"), setpass = EditorStat("setPassCalls") };
            result[0] = f;
        }

        private UniText MakeLabel(System.Type worldType, int i, Transform sharedCanvas)
        {
            GameObject go;
            if (worldType != null)
            {
                go = new GameObject("world", typeof(RectTransform));
                go.transform.position = new Vector3(i % 25 * 0.8f - 10f, i / 25 * 0.3f - 3f, 10f);
                go.transform.localScale = Vector3.one * 0.0025f;
            }
            else if (sharedCanvas != null)
            {
                go = new GameObject("shared", typeof(RectTransform));
                go.transform.SetParent(sharedCanvas, false);
                go.transform.localPosition = new Vector3(i % 25 * 320f - 4000f, i / 25 * 120f - 1200f, 0f);
            }
            else
            {
                var canvasGo = new GameObject("canvas", typeof(Canvas));
                _junk.Add(canvasGo);
                var c = canvasGo.GetComponent<Canvas>();
                c.renderMode = RenderMode.WorldSpace;
                c.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal;
                canvasGo.transform.position = new Vector3(i % 25 * 0.8f - 10f, i / 25 * 0.3f - 3f, 10f);
                canvasGo.transform.localScale = Vector3.one * 0.0025f;
                go = new GameObject("label", typeof(RectTransform));
                go.transform.SetParent(canvasGo.transform, false);
            }
            _junk.Add(go);
            ((RectTransform)go.transform).sizeDelta = new Vector2(300, 60);
            var t = (UniText)go.AddComponent(worldType ?? typeof(UniText));
            t.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOn;
            t.FontStack = _stack;
            t.FontSize = 32f;
            t.Text = "Label " + i;
            return t;
        }

        private IEnumerator Scenario(string name, int n, System.Type worldType, bool shared, StringBuilder json, Dictionary<string, Frame> frames)
        {
            Transform sharedCanvas = null;
            if (shared)
            {
                var cgo = new GameObject("sharedCanvas", typeof(Canvas));
                _junk.Add(cgo);
                var c = cgo.GetComponent<Canvas>();
                c.renderMode = RenderMode.WorldSpace;
                c.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal;
                cgo.transform.position = new Vector3(0, 0, 10f);
                cgo.transform.localScale = Vector3.one * 0.0025f;
                sharedCanvas = cgo.transform;
            }

            var sw = Stopwatch.StartNew();
            var labels = new List<UniText>(n);
            for (var i = 0; i < n; i++) labels.Add(MakeLabel(worldType, i, sharedCanvas));
            Canvas.ForceUpdateCanvases();
            var create = sw.Elapsed.TotalMilliseconds;

            yield return null;
            sw.Restart();
            for (var i = 0; i < n; i++) labels[i].Text = "Value " + (i * 7);
            Canvas.ForceUpdateCanvases();
            var rebuild = sw.Elapsed.TotalMilliseconds;

            var fr = new Frame[1];
            yield return MeasureFrame(fr);

            // Steady frame cost with nothing dirty (Unity's own per-frame work for these objects).
            yield return null;
            sw.Restart();
            for (var k = 0; k < 10; k++) Canvas.ForceUpdateCanvases();
            var idle = sw.Elapsed.TotalMilliseconds / 10.0;

            var mats = worldType != null
                ? labels.SelectMany(t => t.GetComponent<MeshRenderer>().sharedMaterials).Distinct().Count()
                : -1;
            frames[name] = fr[0];
            var line = $"\"{name}_{n}\": {{ \"createMs\": {create:0.0}, \"rebuildMs\": {rebuild:0.0}, \"idleUpdateMs\": {idle:0.00}, " +
                       $"\"draws\": {fr[0].draws}, \"batches\": {fr[0].batches}, \"setpass\": {fr[0].setpass}, \"worldMaterials\": {mats} }}";
            json.Append("  ").Append(line).Append(",\n");
            Debug.Log("[W3] " + line);

            if (worldType != null) Assert.AreEqual(1, mats, $"{n} world labels share one material");
            foreach (var t in labels) if (t != null) Object.DestroyImmediate(t.gameObject);
            if (sharedCanvas != null) Object.DestroyImmediate(sharedCanvas.gameObject);
            foreach (var o in _junk.ToArray()) if (o is GameObject g && g != null && g.GetComponent<Canvas>() != null) Object.DestroyImmediate(g);
            yield return null;
        }

        [UnityTest]
        public IEnumerator WorldLabels_vs_WorldSpaceCanvas_N100_N500()
        {
            var worldType = typeof(UniText).Assembly.GetType("LightSide.UniTextWorld");
            Assert.IsNotNull(worldType, "LightSide.UniTextWorld missing");
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) { Assert.Ignore("FreeType native unavailable."); yield break; }
            var path = FindFont();
            if (path == null) { Assert.Ignore("NotoSans-Regular.ttf not found."); yield break; }
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), 48);
            _junk.Add(font);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(font);
            _junk.Add(_stack);

            var camGo = new GameObject("Cam", typeof(Camera));
            _junk.Add(camGo);
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView = 60f;
            cam.targetTexture = new RenderTexture(1280, 720, 24);
            _junk.Add(cam.targetTexture);
            _cam = cam;

            var json = new StringBuilder("{\n");
            var frames = new Dictionary<string, Frame>();
            // Warm-up (atlas, shaders, pools).
            yield return Scenario("warmup", 20, worldType, false, new StringBuilder(), frames);
            foreach (var n in new[] { 100, 500 })
            {
                yield return Scenario("world", n, worldType, false, json, frames);
                yield return Scenario("canvasPerLabel", n, null, false, json, frames);
                yield return Scenario("sharedCanvas", n, null, true, json, frames);

                var w = frames["world"]; var c = frames["canvasPerLabel"];
                if (w.batches > 0 && c.batches > 0)
                    Assert.LessOrEqual(w.batches, c.batches, $"N={n}: world labels must not need more batches than one canvas per label");
            }
            json.Append("  \"note\": \"draws/batches/setpass: last rendered frame (ProfilerRecorder, or UnityStats in the Editor). Built-in pipeline, Editor, Windows.\"\n}\n");
            var outPath = Path.Combine(OutDir(), "world_labels_benchmark.json");
            File.WriteAllText(outPath, json.ToString());
            Debug.Log("[W3] wrote " + outPath + "\n" + json);
        }
    }
}
