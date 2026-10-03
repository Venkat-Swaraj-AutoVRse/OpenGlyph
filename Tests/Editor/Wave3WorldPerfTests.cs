using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 3, item 4 (EditMode part): N = 100 / 500 world labels against the same labels as UniText on
    /// one World Space Canvas each (the usual way to place many labels in a scene) and on one shared
    /// World Space Canvas. Measures create + first build and a full text rebuild, and asserts the
    /// structure that makes world labels cheap: one shared material, one renderer and one draw per label,
    /// no per-renderer material state. Frame draw counters are in the PlayMode benchmark.
    /// </summary>
    public class Wave3WorldPerfTests : Wave3TestBase
    {
        private struct Result
        {
            public double createMs, rebuildMs;
            public int renderers, materials, canvases;
        }

        private Result RunWorld(int n)
        {
            var sw = Stopwatch.StartNew();
            var list = new List<UniText>(n);
            for (var i = 0; i < n; i++)
            {
                var t = MakeWorld("Label " + i, true, width: 300, height: 60, fontSize: 32f, update: false);
                t.transform.position = new Vector3(i % 25, i / 25, 0);
                t.transform.localScale = Vector3.one * 0.01f;
                list.Add(t);
            }
            Canvas.ForceUpdateCanvases();
            var create = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            for (var i = 0; i < n; i++) list[i].Text = "Value " + (i * 7);
            Canvas.ForceUpdateCanvases();
            var rebuild = sw.Elapsed.TotalMilliseconds;
            var mats = list.SelectMany(t => t.GetComponent<MeshRenderer>().sharedMaterials).Distinct().Count();
            Assert.IsTrue(list.All(t => WorldMeshOf(t).vertexCount > 0), "every label built");
            Assert.IsTrue(list.All(t => t.GetComponent<MeshRenderer>().sharedMaterials.Length == 1), "one draw per label");
            Assert.IsTrue(list.All(t => !t.GetComponent<MeshRenderer>().HasPropertyBlock()), "no per-renderer state");
            var r = new Result { createMs = create, rebuildMs = rebuild, renderers = n, materials = mats, canvases = 0 };
            foreach (var t in list) Object.DestroyImmediate(t.gameObject);
            return r;
        }

        private Result RunCanvasPerLabel(int n, bool sharedCanvas)
        {
            var sw = Stopwatch.StartNew();
            var list = new List<UniText>(n);
            GameObject shared = null;
            if (sharedCanvas)
            {
                shared = new GameObject("shared", typeof(Canvas));
                _junk.Add(shared);
                var c = shared.GetComponent<Canvas>();
                c.renderMode = RenderMode.WorldSpace;
                c.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal;
            }
            for (var i = 0; i < n; i++)
            {
                UniText t;
                if (sharedCanvas)
                {
                    var go = new GameObject("l", typeof(RectTransform));
                    go.transform.SetParent(shared.transform, false);
                    ((RectTransform)go.transform).sizeDelta = new Vector2(300, 60);
                    go.transform.localPosition = new Vector3(i % 25 * 100, i / 25 * 100, 0);
                    t = go.AddComponent<UniText>();
                    t.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOn;
                    t.FontStack = _stack;
                    t.FontSize = 32f;
                    t.Text = "Label " + i;
                }
                else
                {
                    t = MakeCanvasText("Label " + i, true, width: 300, height: 60, fontSize: 32f, update: false);
                    var root = t.GetComponentInParent<Canvas>().transform;
                    root.position = new Vector3(i % 25, i / 25, 0);
                    root.localScale = Vector3.one * 0.01f;
                }
                list.Add(t);
            }
            Canvas.ForceUpdateCanvases();
            var create = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            for (var i = 0; i < n; i++) list[i].Text = "Value " + (i * 7);
            Canvas.ForceUpdateCanvases();
            var rebuild = sw.Elapsed.TotalMilliseconds;
            var r = new Result
            {
                createMs = create, rebuildMs = rebuild, renderers = list.Sum(t => t.ActiveSubMeshRendererCountForTests),
                materials = list.SelectMany(t => t.GetDrawnMeshMaterialsForTests().Select(x => x.mat)).Distinct().Count(),
                canvases = sharedCanvas ? 1 : n,
            };
            foreach (var t in list)
                Object.DestroyImmediate(sharedCanvas ? t.gameObject : t.GetComponentInParent<Canvas>().gameObject);
            if (shared != null) Object.DestroyImmediate(shared);
            return r;
        }

        [TestCase(100)]
        [TestCase(500)]
        public void WorldLabels_vs_CanvasLabels_CreateRebuild_AndSharing(int n)
        {
            // Warm the atlas / shaders once so the first measured scenario does not pay for it.
            RunWorld(10);
            RunCanvasPerLabel(10, false);

            var w = RunWorld(n);
            var c = RunCanvasPerLabel(n, false);
            var s = RunCanvasPerLabel(n, true);
            Log($"N={n} world labels:        create+build {w.createMs:0.0} ms, rebuild {w.rebuildMs:0.0} ms, renderers {w.renderers}, materials {w.materials}, canvases 0");
            Log($"N={n} canvas per label:    create+build {c.createMs:0.0} ms, rebuild {c.rebuildMs:0.0} ms, renderers {c.renderers}, materials {c.materials}, canvases {c.canvases}");
            Log($"N={n} one shared canvas:   create+build {s.createMs:0.0} ms, rebuild {s.rebuildMs:0.0} ms, renderers {s.renderers}, materials {s.materials}, canvases {s.canvases}");

            Assert.AreEqual(1, w.materials, "N world labels -> 1 shared material");
            Assert.AreEqual(n, w.renderers, "one MeshRenderer per label, no child renderer objects");
        }
    }
}
