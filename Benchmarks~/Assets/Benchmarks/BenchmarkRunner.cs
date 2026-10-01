// SPDX-License-Identifier: MIT
// OpenGlyph benchmark runner. Measures, per system x text set:
//   Object Creation, Full Rebuild, Layout, Mesh Rebuild (median + p95 of N iterations
//   after W warmups, 100 objects), GC collections + MB allocated during creation,
//   KB allocated per full-rebuild op; plus glyph rasterization (FreeType vs Unity
//   FontEngine) and font build-size. Writes JSON next to the player / to a path.
//
// Methodology mirrors UniText's published marketing methodology (100 objects,
// 10 iterations, 3 warmups, ~2300 chars/object, Latin+Arabic+Hebrew+Mixed,
// parallel OFF) and ADDS a parallel-ON OpenGlyph pass for completeness.
//
// Forced synchronous updates per system:
//   OpenGlyph : set Text / SetDirty, then Canvas.ForceUpdateCanvases() which fires
//               the static Canvas.willRenderCanvases batch (where UseParallel applies).
//   TMP       : set .text, then ForceMeshUpdate().
//   UIToolkit : set Label.text / style, then flush the panel layout via
//               UIElementsUtility-free path: Panel.UpdateAnimations is unavailable,
//               so we read resolvedStyle after MarkDirtyRepaint + a forced visual-tree
//               layout by querying worldBound (triggers layout pass synchronously).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LightSide;
using TMPro;
using UnityEngine;
using UnityEngine.Profiling;
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

        [Header("Fonts (assigned in scene / loaded from StreamingAssets)")]
        public Font tmpSourceFont;              // used to build a TMP dynamic font asset
        public UniTextFontStack openGlyphFonts; // OpenGlyph font stack (Noto Sans/Arabic/Hebrew)
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

        // Hard safety net: force-exit if the run wedges (e.g. a stuck forced update).
        private IEnumerator Watchdog()
        {
            float budget = 600f; // 10 minutes
            float t = 0f;
            while (t < budget) { t += Time.unscaledDeltaTime; yield return null; }
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
            report.objectCount = objectCount;
            report.iterations = iterations;
            report.warmups = warmups;
            FillEnvironment(report.environment);

            Debug.Log($"[Bench] START {report.environment.buildType} unity={report.environment.unityVersion}");

            SetupCanvas();
            yield return null;

            // ---- Per-system x text-set perf ----
            // OpenGlyph parallel OFF (matches published methodology), then ON.
            yield return RunOpenGlyph(parallel: false);
            yield return RunOpenGlyph(parallel: true);
            // TMP (Latin fair; Arabic/Hebrew/Mixed unshaped -> flagged not fair)
            yield return RunTMP();
            // UI Toolkit Label
            yield return RunUIToolkit();

            // ---- Glyph rasterization: FreeType vs Unity FontEngine ----
            RunGlyphRaster();

            // ---- Build size ----
            RunBuildSize();

            WriteResults();

            Debug.Log("[Bench] DONE");
            if (quitWhenDone)
                HardExit(0);
        }

        // ---------------------------------------------------------------- env
        private static void FillEnvironment(EnvironmentInfo e)
        {
            e.unityVersion = Application.unityVersion;
            e.os = SystemInfo.operatingSystem;
            e.cpu = SystemInfo.processorType;
            e.cpuCount = SystemInfo.processorCount;
            e.systemMemoryMB = SystemInfo.systemMemorySize;
            e.graphicsDevice = SystemInfo.graphicsDeviceName;
            e.timestampUtc = DateTime.UtcNow.ToString("o");
#if ENABLE_IL2CPP
            e.scriptingBackend = "IL2CPP";
#else
            e.scriptingBackend = "Mono";
#endif
#if UNITY_EDITOR
            e.buildType = "Editor (not representative)";
            e.representative = false;
#elif UNITY_ANDROID
            e.buildType = "Android-" + e.scriptingBackend + "-Release";
            e.representative = true;
#else
            e.buildType = "StandaloneWindows64-" + e.scriptingBackend + "-Release";
            e.representative = true;
#endif
        }

        private void SetupCanvas()
        {
            var go = new GameObject("BenchCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasRect = canvas.GetComponent<RectTransform>();
        }

        // ---------------------------------------------------------------- OpenGlyph
        private IEnumerator RunOpenGlyph(bool parallel)
        {
            UniText.UseParallel = parallel;
            string sys = parallel ? "OpenGlyph-ParallelOn" : "OpenGlyph-ParallelOff";

            foreach (var kind in BenchmarkTexts.All())
            {
                string text = BenchmarkTexts.Get(kind);
                var res = new SystemTextResult
                {
                    system = sys, textSet = kind.ToString(),
                    objectCount = objectCount, iterations = iterations, warmups = warmups,
                    shapingFairForThisSystem = true
                };

                var objs = new List<UniText>(objectCount);
                var rects = new List<RectTransform>(objectCount);

                // ---- Object Creation (median/p95 over iterations; each iteration
                // creates+first-mesh for the full set, then destroys) ----
                var creation = new List<double>();
                int gcBefore = 0; long bytesBefore = 0;
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    if (measure) { GC.Collect(); gcBefore = GC.CollectionCount(0); bytesBefore = Profiler.GetMonoUsedSizeLong(); }
                    var sw = Stopwatch.StartNew();
                    CreateUniTextSet(objs, rects, text);
                    Canvas.ForceUpdateCanvases(); // first mesh ready for all
                    sw.Stop();
                    if (measure)
                    {
                        creation.Add(sw.Elapsed.TotalMilliseconds);
                        if (it == 0)
                        {
                            res.gcCollectionsDuringCreation = GC.CollectionCount(0) - gcBefore;
                            res.allocatedMBDuringCreation = Math.Max(0, Profiler.GetMonoUsedSizeLong() - bytesBefore) / (1024.0 * 1024.0);
                        }
                    }
                    DestroySet(objs, rects);
                    yield return null;
                }
                res.objectCreation = PhaseStat.From(creation);

                // Build a persistent set for the mutate phases.
                CreateUniTextSet(objs, rects, text);
                Canvas.ForceUpdateCanvases();
                yield return null;

                // ---- Full Rebuild (change text on all + force complete regenerate) ----
                var full = new List<double>();
                long fullBytes = 0; bool fullBytesTaken = false;
                string altText = text + " "; // trivially different content -> Text dirty
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    string t = (it % 2 == 0) ? altText : text;
                    if (measure && !fullBytesTaken) { GC.Collect(); fullBytes = Profiler.GetMonoUsedSizeLong(); }
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < objs.Count; i++) objs[i].Text = t;
                    Canvas.ForceUpdateCanvases();
                    sw.Stop();
                    if (measure)
                    {
                        full.Add(sw.Elapsed.TotalMilliseconds);
                        if (!fullBytesTaken)
                        {
                            double kb = Math.Max(0, Profiler.GetMonoUsedSizeLong() - fullBytes) / 1024.0 / objs.Count;
                            res.kbPerFullRebuildOp = kb; fullBytesTaken = true;
                        }
                    }
                    yield return null;
                }
                res.fullRebuild = PhaseStat.From(full);

                // ---- Layout (re-layout only: width change, no text change) ----
                var layout = new List<double>();
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    float w = (it % 2 == 0) ? 760f : 800f;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < rects.Count; i++)
                    {
                        rects[i].sizeDelta = new Vector2(w, rects[i].sizeDelta.y);
                        objs[i].SetDirty(UniText.DirtyFlags.Layout);
                    }
                    Canvas.ForceUpdateCanvases();
                    sw.Stop();
                    if (measure) layout.Add(sw.Elapsed.TotalMilliseconds);
                    yield return null;
                }
                res.layout = PhaseStat.From(layout);

                // ---- Mesh Rebuild (color change only, mesh regen, no re-layout) ----
                var mesh = new List<double>();
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    var col = (it % 2 == 0) ? Color.white : Color.yellow;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < objs.Count; i++)
                    {
                        objs[i].color = col;
                        objs[i].SetDirty(UniText.DirtyFlags.Color);
                    }
                    Canvas.ForceUpdateCanvases();
                    sw.Stop();
                    if (measure) mesh.Add(sw.Elapsed.TotalMilliseconds);
                    yield return null;
                }
                res.meshRebuild = PhaseStat.From(mesh);

                DestroySet(objs, rects);
                report.perSystemText.Add(res);
                Debug.Log($"[Bench] {sys} {kind}: create={res.objectCreation.medianMs:F2}ms full={res.fullRebuild.medianMs:F2}ms layout={res.layout.medianMs:F2}ms mesh={res.meshRebuild.medianMs:F2}ms gc={res.gcCollectionsDuringCreation} alloc={res.allocatedMBDuringCreation:F2}MB");
                yield return null;
            }
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
                rt.anchoredPosition = Vector2.zero;
                var ut = go.AddComponent<UniText>();
                if (openGlyphFonts != null) ut.FontStack = openGlyphFonts;
                if (openGlyphAppearance != null) ut.Appearance = openGlyphAppearance;
                ut.Text = text;
                objs.Add(ut); rects.Add(rt);
            }
        }

        private static void DestroySet(List<UniText> objs, List<RectTransform> rects)
        {
            for (int i = 0; i < objs.Count; i++)
                if (objs[i] != null) Destroy(objs[i].gameObject);
            objs.Clear(); rects.Clear();
        }

        // ---------------------------------------------------------------- TMP
        private IEnumerator RunTMP()
        {
            foreach (var kind in BenchmarkTexts.All())
            {
                string text = BenchmarkTexts.Get(kind);
                bool fair = kind == TextSetKind.Latin; // TMP can't shape Arabic/Hebrew without a plugin
                var res = new SystemTextResult
                {
                    system = "TMP", textSet = kind.ToString(),
                    objectCount = objectCount, iterations = iterations, warmups = warmups,
                    shapingFairForThisSystem = fair,
                    note = fair ? null : "TMP cannot shape Arabic/Hebrew without a third-party plugin; this measures unshaped fallback, NOT equivalent output."
                };

                var objs = new List<TextMeshProUGUI>(objectCount);
                var rects = new List<RectTransform>(objectCount);

                var creation = new List<double>();
                int gcBefore = 0; long bytesBefore = 0;
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    if (measure) { GC.Collect(); gcBefore = GC.CollectionCount(0); bytesBefore = Profiler.GetMonoUsedSizeLong(); }
                    var sw = Stopwatch.StartNew();
                    CreateTMPSet(objs, rects, text);
                    Canvas.ForceUpdateCanvases();
                    for (int i = 0; i < objs.Count; i++) objs[i].ForceMeshUpdate();
                    sw.Stop();
                    if (measure)
                    {
                        creation.Add(sw.Elapsed.TotalMilliseconds);
                        if (it == 0)
                        {
                            res.gcCollectionsDuringCreation = GC.CollectionCount(0) - gcBefore;
                            res.allocatedMBDuringCreation = Math.Max(0, Profiler.GetMonoUsedSizeLong() - bytesBefore) / (1024.0 * 1024.0);
                        }
                    }
                    DestroyTMPSet(objs, rects);
                    yield return null;
                }
                res.objectCreation = PhaseStat.From(creation);

                CreateTMPSet(objs, rects, text);
                for (int i = 0; i < objs.Count; i++) objs[i].ForceMeshUpdate();
                yield return null;

                var full = new List<double>();
                long fullBytes = 0; bool taken = false;
                string altText = text + " ";
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    string t = (it % 2 == 0) ? altText : text;
                    if (measure && !taken) { GC.Collect(); fullBytes = Profiler.GetMonoUsedSizeLong(); }
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < objs.Count; i++) { objs[i].text = t; objs[i].ForceMeshUpdate(); }
                    sw.Stop();
                    if (measure)
                    {
                        full.Add(sw.Elapsed.TotalMilliseconds);
                        if (!taken) { res.kbPerFullRebuildOp = Math.Max(0, Profiler.GetMonoUsedSizeLong() - fullBytes) / 1024.0 / objs.Count; taken = true; }
                    }
                    yield return null;
                }
                res.fullRebuild = PhaseStat.From(full);

                var layout = new List<double>();
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    float w = (it % 2 == 0) ? 760f : 800f;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < rects.Count; i++)
                    {
                        rects[i].sizeDelta = new Vector2(w, rects[i].sizeDelta.y);
                        objs[i].ForceMeshUpdate(); // re-layout within new width
                    }
                    sw.Stop();
                    if (measure) layout.Add(sw.Elapsed.TotalMilliseconds);
                    yield return null;
                }
                res.layout = PhaseStat.From(layout);

                var mesh = new List<double>();
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    var col = (it % 2 == 0) ? Color.white : Color.yellow;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < objs.Count; i++) { objs[i].color = col; objs[i].ForceMeshUpdate(); }
                    sw.Stop();
                    if (measure) mesh.Add(sw.Elapsed.TotalMilliseconds);
                    yield return null;
                }
                res.meshRebuild = PhaseStat.From(mesh);

                DestroyTMPSet(objs, rects);
                report.perSystemText.Add(res);
                Debug.Log($"[Bench] TMP {kind} (fair={fair}): create={res.objectCreation.medianMs:F2}ms full={res.fullRebuild.medianMs:F2}ms");
                yield return null;
            }
        }

        private void CreateTMPSet(List<TextMeshProUGUI> objs, List<RectTransform> rects, string text)
        {
            objs.Clear(); rects.Clear();
            for (int i = 0; i < objectCount; i++)
            {
                var go = new GameObject("TMP" + i, typeof(RectTransform));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(canvasRect, false);
                rt.sizeDelta = new Vector2(800, 1200);
                var t = go.AddComponent<TextMeshProUGUI>();
                t.textWrappingMode = TextWrappingModes.Normal;
                t.fontSize = 36;
                t.text = text;
                objs.Add(t); rects.Add(rt);
            }
        }

        private static void DestroyTMPSet(List<TextMeshProUGUI> objs, List<RectTransform> rects)
        {
            for (int i = 0; i < objs.Count; i++)
                if (objs[i] != null) Destroy(objs[i].gameObject);
            objs.Clear(); rects.Clear();
        }

        // ---------------------------------------------------------------- UI Toolkit
        private IEnumerator RunUIToolkit()
        {
            var uiGo = new GameObject("UIDoc", typeof(UIDocument));
            var doc = uiGo.GetComponent<UIDocument>();
            doc.panelSettings = CreatePanelSettings();
            var root = doc.rootVisualElement;

            foreach (var kind in BenchmarkTexts.All())
            {
                string text = BenchmarkTexts.Get(kind);
                bool fair = kind == TextSetKind.Latin; // UITK text shaping for complex scripts varies by Unity version
                var res = new SystemTextResult
                {
                    system = "UIToolkit", textSet = kind.ToString(),
                    objectCount = objectCount, iterations = iterations, warmups = warmups,
                    shapingFairForThisSystem = fair,
                    note = fair ? null : "UI Toolkit complex-script shaping depends on the Unity text backend; treat non-Latin as indicative only."
                };

                var labels = new List<Label>(objectCount);

                var creation = new List<double>();
                int gcBefore = 0; long bytesBefore = 0;
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    if (measure) { GC.Collect(); gcBefore = GC.CollectionCount(0); bytesBefore = Profiler.GetMonoUsedSizeLong(); }
                    var sw = Stopwatch.StartNew();
                    CreateLabels(root, labels, text);
                    ForceUITKLayout(root);
                    sw.Stop();
                    if (measure)
                    {
                        creation.Add(sw.Elapsed.TotalMilliseconds);
                        if (it == 0)
                        {
                            res.gcCollectionsDuringCreation = GC.CollectionCount(0) - gcBefore;
                            res.allocatedMBDuringCreation = Math.Max(0, Profiler.GetMonoUsedSizeLong() - bytesBefore) / (1024.0 * 1024.0);
                        }
                    }
                    ClearLabels(root, labels);
                    yield return null;
                }
                res.objectCreation = PhaseStat.From(creation);

                CreateLabels(root, labels, text);
                ForceUITKLayout(root);
                yield return null;

                var full = new List<double>();
                long fullBytes = 0; bool taken = false;
                string altText = text + " ";
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    string t = (it % 2 == 0) ? altText : text;
                    if (measure && !taken) { GC.Collect(); fullBytes = Profiler.GetMonoUsedSizeLong(); }
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < labels.Count; i++) labels[i].text = t;
                    ForceUITKLayout(root);
                    sw.Stop();
                    if (measure)
                    {
                        full.Add(sw.Elapsed.TotalMilliseconds);
                        if (!taken) { res.kbPerFullRebuildOp = Math.Max(0, Profiler.GetMonoUsedSizeLong() - fullBytes) / 1024.0 / labels.Count; taken = true; }
                    }
                    yield return null;
                }
                res.fullRebuild = PhaseStat.From(full);

                var layout = new List<double>();
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    float w = (it % 2 == 0) ? 760f : 800f;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < labels.Count; i++) labels[i].style.width = w;
                    ForceUITKLayout(root);
                    sw.Stop();
                    if (measure) layout.Add(sw.Elapsed.TotalMilliseconds);
                    yield return null;
                }
                res.layout = PhaseStat.From(layout);

                var mesh = new List<double>();
                for (int it = -warmups; it < iterations; it++)
                {
                    bool measure = it >= 0;
                    var col = (it % 2 == 0) ? Color.white : Color.yellow;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < labels.Count; i++) { labels[i].style.color = col; labels[i].MarkDirtyRepaint(); }
                    ForceUITKLayout(root);
                    sw.Stop();
                    if (measure) mesh.Add(sw.Elapsed.TotalMilliseconds);
                    yield return null;
                }
                res.meshRebuild = PhaseStat.From(mesh);

                ClearLabels(root, labels);
                report.perSystemText.Add(res);
                Debug.Log($"[Bench] UIToolkit {kind} (fair={fair}): create={res.objectCreation.medianMs:F2}ms full={res.fullRebuild.medianMs:F2}ms");
                yield return null;
            }

            Destroy(uiGo);
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

        // Force a synchronous UITK layout pass by reading worldBound of each element,
        // which resolves style + layout immediately rather than deferring to next frame.
        private static void ForceUITKLayout(VisualElement root)
        {
            root.MarkDirtyRepaint();
            var _ = root.worldBound; // touch to force layout resolution
            foreach (var child in root.Children())
            {
                var __ = child.worldBound;
            }
        }

        // ---------------------------------------------------------------- Glyph raster
        private void RunGlyphRaster()
        {
            // OpenGlyph FreeType-backed path: build a font from Noto Sans bytes and
            // time adding N distinct glyphs to the atlas (FreeType raster + SDF pack).
            // Uses the public UniTextFont API; FreeType itself is internal to the package.
            try
            {
                string fontPath = ResolveFontPath("NotoSans-Regular.ttf");
                if (fontPath != null)
                {
                    var bytes = File.ReadAllBytes(fontPath);
                    var font = LightSide.UniTextFont.CreateFontAsset(bytes, glyphRasterSize, 0.25f,
                        LightSide.UniTextRenderMode.SDF, 1024);
                    font.LoadFontFace();
                    // Collect N distinct glyph indices from Latin + a few Arabic/Hebrew.
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
                        note = "UniTextFont.TryAddGlyphsBatch (FreeType raster + SDF pack into atlas)"
                    });
                    Debug.Log($"[Bench] OpenGlyph raster {added} glyphs in {sw.Elapsed.TotalMilliseconds:F2}ms");
                }
                else
                {
                    report.glyphRaster.Add(new GlyphRasterResult { engine = "FreeType (OpenGlyph)", note = "font path not found" });
                }
            }
            catch (Exception ex)
            {
                report.glyphRaster.Add(new GlyphRasterResult { engine = "FreeType (OpenGlyph)", note = "exception: " + ex.Message });
            }

            // Unity FontEngine path (TMP dynamic): add N glyphs to a dynamic font asset.
            try
            {
                if (tmpSourceFont != null)
                {
                    var fa = TMP_FontAsset.CreateFontAsset(tmpSourceFont, glyphRasterSize, 4,
                        UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024,
                        TMPro.AtlasPopulationMode.Dynamic, true);
                    // Request N distinct characters so the FontEngine rasterizes them.
                    var sb = new System.Text.StringBuilder();
                    for (int c = 0x20; sb.Length < glyphRasterCount && c < 0x24F; c++) sb.Append((char)c);
                    string chars = sb.ToString();
                    var sw = Stopwatch.StartNew();
                    fa.TryAddCharacters(chars, out string missing);
                    sw.Stop();
                    int added = chars.Length - (missing?.Length ?? 0);
                    report.glyphRaster.Add(new GlyphRasterResult
                    {
                        engine = "UnityFontEngine (TMP dynamic)", font = tmpSourceFont.name,
                        glyphSize = glyphRasterSize, glyphCount = added,
                        totalMs = sw.Elapsed.TotalMilliseconds,
                        msPerGlyph = added > 0 ? sw.Elapsed.TotalMilliseconds / added : 0,
                        note = "TMP TryAddCharacters (FontEngine raster + SDF bake)"
                    });
                    Debug.Log($"[Bench] FontEngine raster {added} glyphs in {sw.Elapsed.TotalMilliseconds:F2}ms");
                }
                else
                {
                    report.glyphRaster.Add(new GlyphRasterResult { engine = "UnityFontEngine (TMP dynamic)", note = "no tmpSourceFont assigned" });
                }
            }
            catch (Exception ex)
            {
                report.glyphRaster.Add(new GlyphRasterResult { engine = "UnityFontEngine (TMP dynamic)", note = "exception: " + ex.Message });
            }
        }

        private static string ResolveFontPath(string fileName)
        {
            string sa = Path.Combine(Application.streamingAssetsPath, "Fonts", fileName);
            if (File.Exists(sa)) return sa;
            string pd = Path.Combine(Application.persistentDataPath, fileName);
            if (File.Exists(pd)) return pd;
            return null;
        }

        // ---------------------------------------------------------------- Build size
        private void RunBuildSize()
        {
            // Font bytes shipped per system. OpenGlyph ships raw TTF (no compression yet).
            foreach (var f in new[] { "NotoSans-Regular.ttf", "NotoSansArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf" })
            {
                string p = ResolveFontPath(f);
                long bytes = p != null && File.Exists(p) ? new FileInfo(p).Length : 0;
                report.buildSize.Add(new BuildSizeResult
                {
                    system = "OpenGlyph", font = f, fontBytes = bytes, fontMB = bytes / (1024.0 * 1024.0),
                    note = "raw TTF; OpenGlyph has NO font compression yet"
                });
            }
            if (tmpSourceFont != null)
            {
                report.buildSize.Add(new BuildSizeResult
                {
                    system = "TMP", font = tmpSourceFont.name,
                    note = "TMP ships a font asset (atlas texture + glyph table); size depends on static/dynamic + atlas dimensions. See RESULTS.md for the built-player measurement."
                });
            }
        }

        // ---------------------------------------------------------------- output
        private void WriteResults()
        {
            string dir = Application.persistentDataPath;
            string overridePath = GetArg("-resultsPath");
            try
            {
                string path = !string.IsNullOrEmpty(overridePath)
                    ? overridePath
                    : Path.Combine(dir, resultsFileName);
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
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
