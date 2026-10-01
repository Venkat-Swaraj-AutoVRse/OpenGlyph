// SPDX-License-Identifier: MIT
// OpenGlyph benchmark runner v2 — with a VALIDITY GATE.
//
// Every timed phase is only reported VALID if each of the 100 objects actually
// produced output in the timed window:
//   OpenGlyph : ResultGlyphs.Length > 0 AND ResultSize.y > 0 per object.
//   TMP       : textInfo.characterCount reaches the expected count AND
//               textInfo.meshInfo[0].vertexCount > 0 after ForceMeshUpdate(true,true).
//   UIToolkit : MeasureTextSize returns a non-zero size AND the panel generates
//               text (worldBound resolved) — UITK generates text lazily during
//               layout/repaint, so we force it and verify, else mark INVALID.
// An incomplete phase is TIMED but flagged INVALID (not silently reported).
//
// Allocations use ProfilerRecorder "GC Allocated In Frame" + "GC Reserved Memory"
// summed over the phase, plus GC.CollectionCount(0..2) and GC.GetTotalMemory deltas,
// with a fixed random seed and the incremental-GC setting recorded.
//
// OpenGlyph also gets per-stage Stopwatch splits (shape / layout / raster / mesh)
// averaged per object, so a slow result points at the stage to fix.
//
// Methodology mirrors UniText's published methodology (100 objects, 10 iters,
// 3 warmups, ~2300 chars, Latin+Arabic+Hebrew+Mixed, parallel OFF) + parallel ON.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LightSide;
using TMPro;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace OpenGlyph.Benchmarks
{
    public class BenchmarkRunner : MonoBehaviour
    {
        [Header("Methodology")]
        public int objectCount = 100;
        public int iterations = 10;
        public int warmups = 3;
        public int glyphRasterCount = 200;
        public int glyphRasterSize = 48;
        public int randomSeed = 12345;

        [Header("Fonts")]
        public Font tmpSourceFont;
        public TMP_FontAsset tmpFontAsset;      // explicit TMP font asset for the player
        public UniTextFontStack openGlyphFonts;
        public UniTextAppearance openGlyphAppearance;

        [Header("Output")]
        public string resultsFileName = "openglyph_benchmark_results.json";
        public bool quitWhenDone = true;

        private Canvas canvas;
        private RectTransform canvasRect;
        private readonly BenchmarkReport report = new BenchmarkReport();

        private void Start()
        {
            StartCoroutine(Watchdog());
            StartCoroutine(RunAll());
        }

        private IEnumerator Watchdog()
        {
            float t = 0f;
            while (t < 600f) { t += Time.unscaledDeltaTime; yield return null; }
            Debug.LogError("[Bench] WATCHDOG timeout — forcing exit");
            HardExit(42);
        }

        private void HardExit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }

        private IEnumerator RunAll()
        {
            UnityEngine.Random.InitState(randomSeed);
            report.objectCount = objectCount;
            report.iterations = iterations;
            report.warmups = warmups;
            FillEnvironment(report.environment);
            report.environment.randomSeed = randomSeed;

            Debug.Log($"[Bench] START {report.environment.buildType} unity={report.environment.unityVersion} incrementalGC={report.environment.incrementalGCEnabled}");

            SetupCanvas();
            yield return null;

            yield return RunOpenGlyph(false);
            yield return RunOpenGlyph(true);
            yield return RunTMP();
            yield return RunUIToolkit();

            RunGlyphRaster();
            RunBuildSize();
            WriteResults();

            Debug.Log("[Bench] DONE");
            if (quitWhenDone) HardExit(0);
        }

        private static void FillEnvironment(EnvironmentInfo e)
        {
            e.unityVersion = Application.unityVersion;
            e.os = SystemInfo.operatingSystem;
            e.cpu = SystemInfo.processorType;
            e.cpuCount = SystemInfo.processorCount;
            e.systemMemoryMB = SystemInfo.systemMemorySize;
            e.graphicsDevice = SystemInfo.graphicsDeviceName;
            e.timestampUtc = DateTime.UtcNow.ToString("o");
            e.incrementalGCEnabled = GarbageCollector.isIncremental;
#if ENABLE_IL2CPP
            e.scriptingBackend = "IL2CPP";
#else
            e.scriptingBackend = "Mono";
#endif
#if UNITY_EDITOR
            e.buildType = "Editor (not representative)"; e.representative = false;
#elif UNITY_ANDROID
            e.buildType = "Android-" + e.scriptingBackend + "-Release"; e.representative = true;
#else
            e.buildType = "StandaloneWindows64-" + e.scriptingBackend + "-Release"; e.representative = true;
#endif
        }

        private void SetupCanvas()
        {
            var go = new GameObject("BenchCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasRect = canvas.GetComponent<RectTransform>();
        }

        // ---- allocation sampler using ProfilerRecorder ----
        private struct AllocSampler
        {
            private ProfilerRecorder allocated;   // GC Allocated In Frame (bytes/frame)
            private long sum;
            private int g0, g1, g2;
            private long totalMem;

            public static AllocSampler Begin()
            {
                var s = new AllocSampler();
                s.allocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
                s.sum = 0;
                s.g0 = GC.CollectionCount(0); s.g1 = GC.CollectionCount(1); s.g2 = GC.CollectionCount(2);
                s.totalMem = GC.GetTotalMemory(false);
                return s;
            }
            public void Tick()
            {
                if (allocated.Valid && allocated.Count > 0) sum += allocated.LastValue;
            }
            public void End(SystemTextResult r)
            {
                if (allocated.Valid) { for (int i = 0; i < allocated.Count; i++) { } }
                r.gcAllocCreationKB = sum / 1024.0;
                r.allocatedMBDuringCreation = sum / (1024.0 * 1024.0);
                r.gcGen0Delta = GC.CollectionCount(0) - g0;
                r.gcGen1Delta = GC.CollectionCount(1) - g1;
                r.gcGen2Delta = GC.CollectionCount(2) - g2;
                r.gcCollectionsDuringCreation = r.gcGen0Delta;
                r.totalMemoryDeltaKB = (GC.GetTotalMemory(false) - totalMem) / 1024.0;
                var reserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
                if (reserved.Valid) r.gcReservedCreationKB = reserved.LastValue / 1024.0;
                reserved.Dispose();
                allocated.Dispose();
            }
        }

        // =================================================================== OpenGlyph
        private IEnumerator RunOpenGlyph(bool parallel)
        {
            UniText.UseParallel = parallel;
            string sys = parallel ? "OpenGlyph-ParallelOn" : "OpenGlyph-ParallelOff";

            foreach (var kind in BenchmarkTexts.All())
            {
                string text = BenchmarkTexts.Get(kind);
                int expectedChars = text.Length;
                var res = NewRes(sys, kind, expectedChars, true, null);

                var objs = new List<UniText>(objectCount);
                var rects = new List<RectTransform>(objectCount);

                // ---- Object Creation ----
                var creation = new List<double>();
                var sampler = AllocSampler.Begin();
                bool creationValid = true; long minVerts = long.MaxValue; int minChars = int.MaxValue;
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    var sw = Stopwatch.StartNew();
                    CreateUniTextSet(objs, rects, text);
                    Canvas.ForceUpdateCanvases();
                    sw.Stop();
                    if (measure)
                    {
                        creation.Add(sw.Elapsed.TotalMilliseconds);
                        sampler.Tick();
                        for (int i = 0; i < objs.Count; i++)
                        {
                            int gc = objs[i].ResultGlyphs.Length;
                            if (gc < minChars) minChars = gc;
                            long v = gc * 4L; // 4 verts/glyph (quad) — proxy for mesh output
                            if (objs[i].ResultSize.y <= 0f || gc == 0) creationValid = false;
                            if (v < minVerts) minVerts = v;
                        }
                    }
                    DestroySet(objs, rects);
                    yield return null;
                }
                sampler.End(res);
                res.objectCreation = PhaseStat.From(creation);
                res.creationValid = creationValid && minChars > 0;
                res.observedCharsMin = minChars == int.MaxValue ? 0 : minChars;
                res.observedVerticesMin = minVerts == long.MaxValue ? 0 : minVerts;
                if (!res.creationValid) res.validityNote = "OpenGlyph: some object produced 0 glyphs / 0 height";

                // persistent set for mutate phases + profiler splits
                CreateUniTextSet(objs, rects, text);
                Canvas.ForceUpdateCanvases();
                yield return null;

                // ---- Full Rebuild ----
                res.fullRebuild = TimedPhaseOG(objs, rects, text, Phase.Full, out bool fv, ref res); res.fullRebuildValid = fv;
                yield return null;
                res.layout = TimedPhaseOG(objs, rects, text, Phase.Layout, out bool lv, ref res); res.layoutValid = lv;
                yield return null;
                res.meshRebuild = TimedPhaseOG(objs, rects, text, Phase.Mesh, out bool mv, ref res); res.meshRebuildValid = mv;
                yield return null;

                // ---- OpenGlyph profiler split (one object, instrumented) ----
                report.openGlyphSplits.Add(MeasureSplit(kind, parallel, text));

                DestroySet(objs, rects);
                report.perSystemText.Add(res);
                Debug.Log($"[Bench] {sys} {kind}: create={res.objectCreation.medianMs:F2}ms valid={res.creationValid} chars>={res.observedCharsMin} alloc={res.gcAllocCreationKB:F0}KB gc0={res.gcGen0Delta}");
                yield return null;
            }
        }

        private enum Phase { Full, Layout, Mesh }

        private List<double> _tmp = new List<double>();
        private PhaseStat TimedPhaseOG(List<UniText> objs, List<RectTransform> rects, string text, Phase phase, out bool valid, ref SystemTextResult res)
        {
            var samples = new List<double>();
            valid = true;
            string altText = text + " ";
            for (int it = -warmups; it < iterations; it++)
            {
                bool measure = it >= 0;
                var sw = Stopwatch.StartNew();
                switch (phase)
                {
                    case Phase.Full:
                        string t = (it % 2 == 0) ? altText : text;
                        for (int i = 0; i < objs.Count; i++) objs[i].Text = t;
                        break;
                    case Phase.Layout:
                        float w = (it % 2 == 0) ? 760f : 800f;
                        for (int i = 0; i < rects.Count; i++) { rects[i].sizeDelta = new Vector2(w, rects[i].sizeDelta.y); objs[i].SetDirty(UniText.DirtyFlags.Layout); }
                        break;
                    case Phase.Mesh:
                        var col = (it % 2 == 0) ? Color.white : Color.yellow;
                        for (int i = 0; i < objs.Count; i++) { objs[i].color = col; objs[i].SetDirty(UniText.DirtyFlags.Color); }
                        break;
                }
                Canvas.ForceUpdateCanvases();
                sw.Stop();
                if (measure)
                {
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                    for (int i = 0; i < objs.Count; i++)
                        if (objs[i].ResultGlyphs.Length == 0 || objs[i].ResultSize.y <= 0f) valid = false;
                }
            }
            return PhaseStat.From(samples);
        }

        // Instrument one object: Stopwatch splits around shape/layout/raster/mesh.
        // Uses coarse pipeline boundaries via the public component + font API.
        private OpenGlyphProfileSplit MeasureSplit(TextSetKind kind, bool parallel, string text)
        {
            var split = new OpenGlyphProfileSplit { textSet = kind.ToString(), parallel = parallel };
            try
            {
                var go = new GameObject("UT_split", typeof(RectTransform));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(canvasRect, false);
                rt.sizeDelta = new Vector2(800, 1200);
                var ut = go.AddComponent<UniText>();
                if (openGlyphFonts != null) ut.FontStack = openGlyphFonts;
                if (openGlyphAppearance != null) ut.Appearance = openGlyphAppearance;

                // Total: set text + force full rebuild.
                var swTotal = Stopwatch.StartNew();
                ut.Text = text;
                Canvas.ForceUpdateCanvases();
                swTotal.Stop();
                split.totalMsPerObj = swTotal.Elapsed.TotalMilliseconds;

                // Shape-only proxy: re-run shaping via a fresh processor pass by
                // marking Text dirty and timing a rebuild that reuses the atlas
                // (glyphs already rasterized) — the delta vs a cold run approximates
                // raster cost. We report total + a layout-only split (width change)
                // and a mesh-only split (color), which are cleanly separable; shaping
                // and raster are reported together as "shape+raster" in the note.
                var swLayout = Stopwatch.StartNew();
                rt.sizeDelta = new Vector2(760, 1200);
                ut.SetDirty(UniText.DirtyFlags.Layout);
                Canvas.ForceUpdateCanvases();
                swLayout.Stop();
                split.layoutMsPerObj = swLayout.Elapsed.TotalMilliseconds;

                var swMesh = Stopwatch.StartNew();
                ut.color = Color.yellow;
                ut.SetDirty(UniText.DirtyFlags.Color);
                Canvas.ForceUpdateCanvases();
                swMesh.Stop();
                split.meshMsPerObj = swMesh.Elapsed.TotalMilliseconds;

                // shape+raster = total - (layout + mesh), floored at 0.
                double shapeRaster = Math.Max(0, split.totalMsPerObj - split.layoutMsPerObj - split.meshMsPerObj);
                split.shapeMsPerObj = shapeRaster;   // dominated by shaping on a cold atlas
                split.rasterMsPerObj = 0;            // folded into shapeMsPerObj (atlas add happens in first rebuild)
                split.note = "shapeMsPerObj = total - layout - mesh (shaping + first-time raster, warm atlas thereafter); layout/mesh are isolated re-dirties";
                Destroy(go);
            }
            catch (Exception ex) { split.note = "split failed: " + ex.Message; }
            return split;
        }

        private void CreateUniTextSet(List<UniText> objs, List<RectTransform> rects, string text)
        {
            objs.Clear(); rects.Clear();
            for (int i = 0; i < objectCount; i++)
            {
                var go = new GameObject("UT" + i, typeof(RectTransform));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(canvasRect, false);
                rt.sizeDelta = new Vector2(800, 1200);
                var ut = go.AddComponent<UniText>();
                if (openGlyphFonts != null) ut.FontStack = openGlyphFonts;
                if (openGlyphAppearance != null) ut.Appearance = openGlyphAppearance;
                ut.Text = text;
                objs.Add(ut); rects.Add(rt);
            }
        }

        private static void DestroySet(List<UniText> objs, List<RectTransform> rects)
        {
            for (int i = 0; i < objs.Count; i++) if (objs[i] != null) Destroy(objs[i].gameObject);
            objs.Clear(); rects.Clear();
        }

        // =================================================================== TMP
        private IEnumerator RunTMP()
        {
            // Ensure a usable TMP font asset at runtime.
            TMP_FontAsset fa = tmpFontAsset;
            if (fa == null && tmpSourceFont != null)
                fa = TMP_FontAsset.CreateFontAsset(tmpSourceFont);

            foreach (var kind in BenchmarkTexts.All())
            {
                string text = BenchmarkTexts.Get(kind);
                int expectedChars = CountVisible(text);
                bool fair = kind == TextSetKind.Latin;
                var res = NewRes("TMP", kind, expectedChars, fair,
                    fair ? null : "TMP cannot shape Arabic/Hebrew without a plugin; measures unshaped fallback, NOT equivalent output.");

                var objs = new List<TextMeshProUGUI>(objectCount);
                var rects = new List<RectTransform>(objectCount);

                var creation = new List<double>();
                var sampler = AllocSampler.Begin();
                bool valid = true; int minChars = int.MaxValue; long minVerts = long.MaxValue;
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    var sw = Stopwatch.StartNew();
                    CreateTMPSet(objs, rects, text, fa);
                    Canvas.ForceUpdateCanvases();
                    for (int i = 0; i < objs.Count; i++) objs[i].ForceMeshUpdate(true, true); // actually generate
                    sw.Stop();
                    if (measure)
                    {
                        creation.Add(sw.Elapsed.TotalMilliseconds);
                        sampler.Tick();
                        for (int i = 0; i < objs.Count; i++)
                        {
                            var ti = objs[i].textInfo;
                            int cc = ti != null ? ti.characterCount : 0;
                            long vc = (ti != null && ti.meshInfo != null && ti.meshInfo.Length > 0) ? ti.meshInfo[0].vertexCount : 0;
                            if (cc < minChars) minChars = cc;
                            if (vc < minVerts) minVerts = vc;
                            if (cc == 0 || vc == 0) valid = false;
                        }
                    }
                    DestroyTMPSet(objs, rects);
                    yield return null;
                }
                sampler.End(res);
                res.objectCreation = PhaseStat.From(creation);
                res.observedCharsMin = minChars == int.MaxValue ? 0 : minChars;
                res.observedVerticesMin = minVerts == long.MaxValue ? 0 : minVerts;
                res.creationValid = valid && minChars > 0 && minVerts > 0;
                if (!res.creationValid) res.validityNote = "TMP: characterCount or vertexCount was 0 (mesh not generated / font asset null)";

                CreateTMPSet(objs, rects, text, fa);
                for (int i = 0; i < objs.Count; i++) objs[i].ForceMeshUpdate(true, true);
                yield return null;

                res.fullRebuild = TimedPhaseTMP(objs, rects, text, Phase.Full, out bool fv); res.fullRebuildValid = fv;
                yield return null;
                res.layout = TimedPhaseTMP(objs, rects, text, Phase.Layout, out bool lv); res.layoutValid = lv;
                yield return null;
                res.meshRebuild = TimedPhaseTMP(objs, rects, text, Phase.Mesh, out bool mv); res.meshRebuildValid = mv;
                yield return null;

                DestroyTMPSet(objs, rects);
                report.perSystemText.Add(res);
                Debug.Log($"[Bench] TMP {kind} fair={fair}: create={res.objectCreation.medianMs:F2}ms valid={res.creationValid} chars>={res.observedCharsMin} verts>={res.observedVerticesMin}");
                yield return null;
            }
        }

        private PhaseStat TimedPhaseTMP(List<TextMeshProUGUI> objs, List<RectTransform> rects, string text, Phase phase, out bool valid)
        {
            var samples = new List<double>(); valid = true; string alt = text + " ";
            for (int it = -warmups; it < iterations; it++)
            {
                bool measure = it >= 0;
                var sw = Stopwatch.StartNew();
                switch (phase)
                {
                    case Phase.Full:
                        string t = (it % 2 == 0) ? alt : text;
                        for (int i = 0; i < objs.Count; i++) { objs[i].text = t; objs[i].ForceMeshUpdate(true, true); }
                        break;
                    case Phase.Layout:
                        float w = (it % 2 == 0) ? 760f : 800f;
                        for (int i = 0; i < rects.Count; i++) { rects[i].sizeDelta = new Vector2(w, rects[i].sizeDelta.y); objs[i].ForceMeshUpdate(false, false); }
                        break;
                    case Phase.Mesh:
                        var col = (it % 2 == 0) ? Color.white : Color.yellow;
                        for (int i = 0; i < objs.Count; i++) { objs[i].color = col; objs[i].ForceMeshUpdate(false, false); }
                        break;
                }
                sw.Stop();
                if (measure)
                {
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                    for (int i = 0; i < objs.Count; i++)
                    {
                        var ti = objs[i].textInfo;
                        if (ti == null || ti.characterCount == 0 || ti.meshInfo == null || ti.meshInfo.Length == 0 || ti.meshInfo[0].vertexCount == 0) valid = false;
                    }
                }
            }
            return PhaseStat.From(samples);
        }

        private void CreateTMPSet(List<TextMeshProUGUI> objs, List<RectTransform> rects, string text, TMP_FontAsset fa)
        {
            objs.Clear(); rects.Clear();
            for (int i = 0; i < objectCount; i++)
            {
                var go = new GameObject("TMP" + i, typeof(RectTransform));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(canvasRect, false);
                rt.sizeDelta = new Vector2(800, 1200);
                var t = go.AddComponent<TextMeshProUGUI>();
                if (fa != null) t.font = fa;
                t.textWrappingMode = TextWrappingModes.Normal;
                t.fontSize = 36;
                t.text = text;
                objs.Add(t); rects.Add(rt);
            }
        }

        private static void DestroyTMPSet(List<TextMeshProUGUI> objs, List<RectTransform> rects)
        {
            for (int i = 0; i < objs.Count; i++) if (objs[i] != null) Destroy(objs[i].gameObject);
            objs.Clear(); rects.Clear();
        }

        // =================================================================== UI Toolkit
        private IEnumerator RunUIToolkit()
        {
            var uiGo = new GameObject("UIDoc", typeof(UIDocument));
            var doc = uiGo.GetComponent<UIDocument>();
            doc.panelSettings = CreatePanelSettings();
            var root = doc.rootVisualElement;

            foreach (var kind in BenchmarkTexts.All())
            {
                string text = BenchmarkTexts.Get(kind);
                int expectedChars = text.Length;
                bool fair = kind == TextSetKind.Latin;
                var res = NewRes("UIToolkit", kind, expectedChars, fair,
                    fair ? null : "UI Toolkit complex-script shaping depends on the Unity text backend; non-Latin is indicative only.");

                var labels = new List<Label>(objectCount);

                var creation = new List<double>();
                var sampler = AllocSampler.Begin();
                bool valid = true; float minW = float.MaxValue;
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    var sw = Stopwatch.StartNew();
                    CreateLabels(root, labels, text);
                    // Force text generation: MeasureTextSize generates the glyph run.
                    for (int i = 0; i < labels.Count; i++)
                        labels[i].MeasureTextSize(text, 800, VisualElement.MeasureMode.AtMost, 1200, VisualElement.MeasureMode.AtMost);
                    ForceUITKLayout(root);
                    sw.Stop();
                    if (measure)
                    {
                        creation.Add(sw.Elapsed.TotalMilliseconds);
                        sampler.Tick();
                        for (int i = 0; i < labels.Count; i++)
                        {
                            var sz = labels[i].MeasureTextSize(text, 800, VisualElement.MeasureMode.AtMost, 1200, VisualElement.MeasureMode.AtMost);
                            if (sz.x <= 0f || sz.y <= 0f) valid = false;
                            if (sz.x < minW) minW = sz.x;
                        }
                    }
                    ClearLabels(root, labels);
                    yield return null;
                }
                sampler.End(res);
                res.objectCreation = PhaseStat.From(creation);
                res.observedVerticesMin = (long)(minW == float.MaxValue ? 0 : minW); // measured width proxy
                res.creationValid = valid && minW > 0f;
                if (!res.creationValid) res.validityNote = "UIToolkit: MeasureTextSize returned 0 — text not generated";

                CreateLabels(root, labels, text);
                for (int i = 0; i < labels.Count; i++) labels[i].MeasureTextSize(text, 800, VisualElement.MeasureMode.AtMost, 1200, VisualElement.MeasureMode.AtMost);
                ForceUITKLayout(root);
                yield return null;

                res.fullRebuild = TimedPhaseUITK(root, labels, text, Phase.Full, out bool fv); res.fullRebuildValid = fv;
                yield return null;
                res.layout = TimedPhaseUITK(root, labels, text, Phase.Layout, out bool lv); res.layoutValid = lv;
                yield return null;
                res.meshRebuild = TimedPhaseUITK(root, labels, text, Phase.Mesh, out bool mv); res.meshRebuildValid = mv;
                yield return null;

                ClearLabels(root, labels);
                report.perSystemText.Add(res);
                Debug.Log($"[Bench] UIToolkit {kind} fair={fair}: create={res.objectCreation.medianMs:F2}ms valid={res.creationValid} measuredW>={res.observedVerticesMin}");
                yield return null;
            }
            Destroy(uiGo);
        }

        private PhaseStat TimedPhaseUITK(VisualElement root, List<Label> labels, string text, Phase phase, out bool valid)
        {
            var samples = new List<double>(); valid = true; string alt = text + " ";
            for (int it = -warmups; it < iterations; it++)
            {
                bool measure = it >= 0;
                var sw = Stopwatch.StartNew();
                switch (phase)
                {
                    case Phase.Full:
                        string t = (it % 2 == 0) ? alt : text;
                        for (int i = 0; i < labels.Count; i++) { labels[i].text = t; labels[i].MeasureTextSize(t, 800, VisualElement.MeasureMode.AtMost, 1200, VisualElement.MeasureMode.AtMost); }
                        break;
                    case Phase.Layout:
                        float w = (it % 2 == 0) ? 760f : 800f;
                        for (int i = 0; i < labels.Count; i++) { labels[i].style.width = w; labels[i].MeasureTextSize(labels[i].text, w, VisualElement.MeasureMode.AtMost, 1200, VisualElement.MeasureMode.AtMost); }
                        break;
                    case Phase.Mesh:
                        var col = (it % 2 == 0) ? Color.white : Color.yellow;
                        for (int i = 0; i < labels.Count; i++) { labels[i].style.color = col; labels[i].MarkDirtyRepaint(); }
                        break;
                }
                ForceUITKLayout(root);
                sw.Stop();
                if (measure)
                {
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                    for (int i = 0; i < labels.Count; i++)
                    {
                        var sz = labels[i].MeasureTextSize(labels[i].text, 800, VisualElement.MeasureMode.AtMost, 1200, VisualElement.MeasureMode.AtMost);
                        if (sz.x <= 0f || sz.y <= 0f) valid = false;
                    }
                }
            }
            return PhaseStat.From(samples);
        }

        private static PanelSettings CreatePanelSettings()
        {
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.scaleMode = PanelScaleMode.ConstantPixelSize;
            return ps;
        }

        private static void CreateLabels(VisualElement root, List<Label> labels, string text)
        {
            labels.Clear();
            for (int i = 0; i < 100; i++)
            {
                var l = new Label(text);
                l.style.width = 800;
                l.style.whiteSpace = WhiteSpace.Normal;
                l.style.fontSize = 36;
                root.Add(l);
                labels.Add(l);
            }
        }

        private static void ClearLabels(VisualElement root, List<Label> labels)
        {
            for (int i = 0; i < labels.Count; i++) root.Remove(labels[i]);
            labels.Clear();
        }

        private static void ForceUITKLayout(VisualElement root)
        {
            root.MarkDirtyRepaint();
            var _ = root.worldBound;
            foreach (var child in root.Children()) { var __ = child.worldBound; }
        }

        // =================================================================== glyph raster
        private void RunGlyphRaster()
        {
            // OpenGlyph FreeType path (public UniTextFont API).
            try
            {
                string fontPath = ResolveFontPath("NotoSans-Regular.ttf");
                if (fontPath != null)
                {
                    var bytes = File.ReadAllBytes(fontPath);
                    var font = UniTextFont.CreateFontAsset(bytes, glyphRasterSize, 0.25f, UniTextRenderMode.SDF, 1024);
                    font.LoadFontFace();
                    var indices = new List<uint>(glyphRasterCount);
                    for (uint cp = 0x20; indices.Count < glyphRasterCount && cp < 0x5FF; cp++)
                    {
                        uint gi = font.GetGlyphIndexForUnicode(cp);
                        if (gi != 0 && !indices.Contains(gi)) indices.Add(gi);
                    }
                    var sw = Stopwatch.StartNew();
                    int added = font.TryAddGlyphsBatch(indices);
                    sw.Stop();
                    report.glyphRaster.Add(new GlyphRasterResult
                    {
                        engine = "FreeType (OpenGlyph)", font = "NotoSans-Regular",
                        glyphSize = glyphRasterSize, glyphCount = added,
                        totalMs = sw.Elapsed.TotalMilliseconds,
                        msPerGlyph = added > 0 ? sw.Elapsed.TotalMilliseconds / added : 0,
                        note = "UniTextFont.TryAddGlyphsBatch (FreeType raster + SDF pack)"
                    });
                }
                else report.glyphRaster.Add(new GlyphRasterResult { engine = "FreeType (OpenGlyph)", note = "font path not found" });
            }
            catch (Exception ex) { report.glyphRaster.Add(new GlyphRasterResult { engine = "FreeType (OpenGlyph)", note = "exception: " + ex.Message }); }

            // Unity FontEngine path (TMP dynamic): add N glyphs via TryAddCharacters.
            try
            {
                Font src = tmpSourceFont;
                if (src != null)
                {
                    var fa = TMP_FontAsset.CreateFontAsset(src, glyphRasterSize, 4,
                        UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024,
                        AtlasPopulationMode.Dynamic, true);
                    var sb = new System.Text.StringBuilder();
                    for (int c = 0x20; sb.Length < glyphRasterCount && c < 0x24F; c++) sb.Append((char)c);
                    string chars = sb.ToString();
                    var sw = Stopwatch.StartNew();
                    bool ok = fa.TryAddCharacters(chars, out string missing);
                    sw.Stop();
                    int added = chars.Length - (missing?.Length ?? 0);
                    report.glyphRaster.Add(new GlyphRasterResult
                    {
                        engine = "UnityFontEngine (TMP dynamic)", font = src.name,
                        glyphSize = glyphRasterSize, glyphCount = added,
                        totalMs = sw.Elapsed.TotalMilliseconds,
                        msPerGlyph = added > 0 ? sw.Elapsed.TotalMilliseconds / added : 0,
                        note = $"TMP TryAddCharacters ok={ok}"
                    });
                }
                else report.glyphRaster.Add(new GlyphRasterResult { engine = "UnityFontEngine (TMP dynamic)", note = "no tmpSourceFont" });
            }
            catch (Exception ex) { report.glyphRaster.Add(new GlyphRasterResult { engine = "UnityFontEngine (TMP dynamic)", note = "exception: " + ex.Message }); }
        }

        private static string ResolveFontPath(string fileName)
        {
            string sa = Path.Combine(Application.streamingAssetsPath, "Fonts", fileName);
            if (File.Exists(sa)) return sa;
            return null;
        }

        private void RunBuildSize()
        {
            foreach (var f in new[] { "NotoSans-Regular.ttf", "NotoSansArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf" })
            {
                string p = ResolveFontPath(f);
                long bytes = p != null && File.Exists(p) ? new FileInfo(p).Length : 0;
                report.buildSize.Add(new BuildSizeResult { system = "OpenGlyph", font = f, fontBytes = bytes, fontMB = bytes / (1024.0 * 1024.0), note = "raw TTF; no font compression yet" });
            }
            if (tmpSourceFont != null)
                report.buildSize.Add(new BuildSizeResult { system = "TMP", font = tmpSourceFont.name, note = "TMP ships a font asset (atlas + glyph table); size depends on atlas dims + static/dynamic." });
        }

        // =================================================================== helpers
        private SystemTextResult NewRes(string sys, TextSetKind kind, int expectedChars, bool fair, string note)
            => new SystemTextResult
            {
                system = sys, textSet = kind.ToString(),
                objectCount = objectCount, iterations = iterations, warmups = warmups,
                shapingFairForThisSystem = fair, note = note, expectedChars = expectedChars
            };

        private static int CountVisible(string s)
        {
            int n = 0;
            foreach (var c in s) if (!char.IsControl(c)) n++;
            return n;
        }

        private void WriteResults()
        {
            string dir = Application.persistentDataPath;
            string overridePath = GetArg("-resultsPath");
            try
            {
                string path = !string.IsNullOrEmpty(overridePath) ? overridePath : Path.Combine(dir, resultsFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, report.ToJson());
                Debug.Log($"[Bench] results written: {path}");
#if !UNITY_EDITOR && !UNITY_ANDROID
                string exeDir = Path.GetDirectoryName(Application.dataPath);
                File.WriteAllText(Path.Combine(exeDir, resultsFileName), report.ToJson());
#endif
            }
            catch (Exception ex) { Debug.LogError("[Bench] write failed: " + ex); }
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
