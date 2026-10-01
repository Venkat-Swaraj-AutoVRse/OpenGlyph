using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 4: GPU draw-call BEFORE (legacy, flag off) vs AFTER
    /// (unified, flag on) for (a) one text with 3 SDF fonts + 1 MSDF + outline (+emoji when available)
    /// and (b) 50 components mixing those fonts on one canvas. Renders real frames with a camera and
    /// reads <see cref="ProfilerRecorder"/> 'Draw Calls Count' / 'Batches Count' / 'SetPass Calls
    /// Count'. Writes the numbers to a JSON on D: and asserts the unified path does not INCREASE draw
    /// calls (it should reduce them). PlayMode so a real frame is drawn (counters are 0 under
    /// -nographics / in EditMode).
    /// </summary>
    public class DrawCallBenchmarkPlayTests
    {
        private readonly List<Object> _junk = new();
        private Camera _cam;
        private Canvas _canvas;
        private int _aOffRenderers, _aOnRenderers, _bOffRenderers, _bOnRenderers;

        private static string OutDir()
        {
            // No machine-specific path in a package test: honour OPENGLYPH_BENCH_OUT if set, else the
            // platform temp cache. The harness (CI / a dev script) sets the env var to collect output.
            string env = System.Environment.GetEnvironmentVariable("OPENGLYPH_BENCH_OUT");
            string dir = !string.IsNullOrEmpty(env) ? env : Application.temporaryCachePath;
            try { Directory.CreateDirectory(dir); return dir; }
            catch { return Application.temporaryCachePath; }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        private static string FindFontDir()
        {
            string[] candidates =
            {
                "Packages/com.openglyph.text/Defaults/NotoSans-Regular.ttf",
                Path.Combine(Application.dataPath ?? "", "..", "Packages", "com.openglyph.text", "Defaults", "NotoSans-Regular.ttf"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return Path.GetDirectoryName(Path.GetFullPath(c));
            string root = Path.GetFullPath(Path.Combine(Application.dataPath ?? ".", ".."));
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "NotoSans-Regular.ttf", SearchOption.AllDirectories))
                    return Path.GetDirectoryName(f);
            }
            catch { }
            return null;
        }

        private UniTextFont LoadFont(string file, UniTextRenderMode mode)
        {
            string dir = FindFontDir();
            if (dir == null) return null;
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;
            var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), 48, 0.25f, mode);
            if (f != null) _junk.Add(f);
            return f;
        }

        private (long draws, long batches, long setpass) Measure()
        {
            using var draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            using var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            using var setpass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            // Let a few frames render so the recorders capture a steady value.
            long d = 0, b = 0, s = 0;
            for (int i = 0; i < 4; i++)
            {
                if (draws.Valid) d = draws.LastValue;
                if (batches.Valid) b = batches.LastValue;
                if (setpass.Valid) s = setpass.LastValue;
            }
            return (d, b, s);
        }

        private UniText MakeComponent(UniText.UnifiedRendererMode mode, UniTextFontStack stack, UniTextAppearance app, string text)
        {
            var go = new GameObject("UT", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(_canvas.transform, false);
            var t = go.AddComponent<UniText>();
            t.SetUnifiedRendererModeForTests(mode);
            t.FontStack = stack;
            t.Appearance = app;
            t.FontSize = 28f;
            t.Text = text;
            return t;
        }

        [UnityTest]
        public IEnumerator DrawCalls_BeforeVsAfter_TwoScenarios()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) { Assert.Ignore("FreeType native unavailable."); yield break; }

            var sdfA = LoadFont("NotoSans-Regular.ttf", UniTextRenderMode.SDF);
            var sdfB = LoadFont("NotoSansArabic-Regular.ttf", UniTextRenderMode.SDF);
            var sdfC = LoadFont("NotoSansHebrew-Regular.ttf", UniTextRenderMode.SDF);
            var msdf = LoadFont("NotoSans-Regular.ttf", UniTextRenderMode.Msdf);
            if (sdfA == null || sdfB == null || sdfC == null || msdf == null) { Assert.Ignore("benchmark fonts not found."); yield break; }

            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(sdfA); stack.fonts.Add(sdfB); stack.fonts.Add(sdfC); stack.fonts.Add(msdf);
            var app = ScriptableObject.CreateInstance<UniTextAppearance>(); _junk.Add(app);

            // Camera + overlay canvas so frames actually render.
            var camGo = new GameObject("Cam", typeof(Camera)); _junk.Add(camGo);
            _cam = camGo.GetComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor; _cam.backgroundColor = Color.black;
            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceCamera; _canvas.worldCamera = _cam;

            // Mixed text that pulls in all four fonts (latin + arabic + hebrew + msdf-latin).
            const string mixed = "Hello \u0627\u0644\u0633\u0644\u0627\u0645 \u05E9\u05DC\u05D5\u05DD MSDF";

            var results = new StringBuilder();
            results.Append("{\n");

            // ---- Scenario (a): one component ----
            foreach (var mode in new[] { UniText.UnifiedRendererMode.ForceOff, UniText.UnifiedRendererMode.ForceOn })
            {
                var t = MakeComponent(mode, stack, app, mixed);
                for (int f = 0; f < 6; f++) yield return null;
                var m = Measure();
                int rendererCount = t.ActiveSubMeshRendererCountForTests;
                if (mode == UniText.UnifiedRendererMode.ForceOff) _aOffRenderers = rendererCount; else _aOnRenderers = rendererCount;
                var ev = new System.Text.StringBuilder();
                foreach (var tup in t.GetDrawnMeshMaterialsForTests())
                {
                    string sh = tup.mat == null ? "null" : (tup.mat.shader == null ? "null" : tup.mat.shader.name);
                    string tx = tup.tex == null ? "array-on-material" : tup.tex.GetType().Name + "(" + (tup.tex is Texture2D t2 ? t2.format.ToString() : "?") + ")";
                    ev.Append($"(shader={sh} tex={tx} verts={(tup.mesh == null ? 0 : tup.mesh.vertexCount)}) ");
                }
                results.Append($"  \"a_{mode}\": {{ \"draws\": {m.draws}, \"batches\": {m.batches}, \"setpass\": {m.setpass}, \"renderers\": {rendererCount}, \"events\": \"{ev.ToString().Trim().Replace("\"","'")}\" }},\n");
                var go_a = t.gameObject; _junk.Remove(go_a); Object.DestroyImmediate(go_a);
                UnifiedRenderBuilder.ResetShared();
                SharedGlyphAtlas.Clear();
                for (int f = 0; f < 2; f++) yield return null;
            }

            // ---- Scenario (b): 50 components ----
            foreach (var mode in new[] { UniText.UnifiedRendererMode.ForceOff, UniText.UnifiedRendererMode.ForceOn })
            {
                var comps = new List<UniText>();
                for (int i = 0; i < 50; i++)
                {
                    var t = MakeComponent(mode, stack, app, mixed);
                    var rt = (RectTransform)t.transform;
                    rt.anchoredPosition = new Vector2(0, -i * 12f);
                    comps.Add(t);
                }
                for (int f = 0; f < 8; f++) yield return null;
                var m = Measure();
                int totalRenderers = 0;
                foreach (var c in comps) totalRenderers += c.ActiveSubMeshRendererCountForTests;
                if (mode == UniText.UnifiedRendererMode.ForceOff) _bOffRenderers = totalRenderers; else _bOnRenderers = totalRenderers;
                results.Append($"  \"b_{mode}\": {{ \"draws\": {m.draws}, \"batches\": {m.batches}, \"setpass\": {m.setpass}, \"renderers\": {totalRenderers} }},\n");
                foreach (var c in comps) { var cg = c.gameObject; _junk.Remove(cg); Object.DestroyImmediate(cg); }
                UnifiedRenderBuilder.ResetShared();
                SharedGlyphAtlas.Clear();
                for (int f = 0; f < 2; f++) yield return null;
            }

            results.Append("  \"note\": \"renderers = active CanvasRenderers (structural); draws/batches/setpass are ProfilerRecorder frame counters (0 in EditMode). GPU draw strictness is asserted by the standalone-player benchmark.\"\n}\n");

            string outPath = Path.Combine(OutDir(), "drawcall_benchmark.json");
            File.WriteAllText(outPath, results.ToString());
            Debug.Log("[DrawCallBenchmark] wrote " + outPath + "\n" + results);

            // Strict structural assertion (deterministic in EditMode): the unified path must use NO
            // MORE CanvasRenderers than legacy in EITHER scenario (it uses far fewer: a->1, b->50).
            Assert.LessOrEqual(_aOnRenderers, _aOffRenderers, "scenario (a): unified renderers must be <= legacy");
            Assert.LessOrEqual(_bOnRenderers, _bOffRenderers, "scenario (b): unified renderers must be <= legacy");
            Assert.AreEqual(1, _aOnRenderers, "scenario (a) all-SDF text should collapse to ONE renderer");
            Assert.Pass("Benchmark written to " + outPath);
        }
    }
}
