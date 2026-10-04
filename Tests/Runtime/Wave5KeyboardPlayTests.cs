using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 5: draw cost of the full built-in world keyboard (QWERTY letters page) in Play Mode with real
    /// frames: draw calls / batches / SetPass calls of a camera that sees only the keyboard, against an empty
    /// frame. The keyboard type is reached by reflection so the file compiles without it.
    /// </summary>
    public class Wave5KeyboardPlayTests
    {
        private readonly List<Object> _junk = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        private static string FindFont()
        {
            string[] candidates =
            {
                "Packages/com.openglyph.text/Defaults/NotoSans-Regular.ttf",
                Path.Combine(Application.dataPath ?? "", "..", "Packages", "com.openglyph.text", "Defaults", "NotoSans-Regular.ttf"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        private static long EditorStat(string name)
        {
            var t = System.Type.GetType("UnityEditor.UnityStats, UnityEditor");
            var p = t?.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return p == null ? 0 : System.Convert.ToInt64(p.GetValue(null));
        }

        private static IEnumerator Measure(Camera cam, long[] result)
        {
            using var draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            using var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            using var setpass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            for (var i = 0; i < 4; i++) { cam.Render(); yield return null; }
            long d = draws.Valid ? draws.LastValue : 0, b = batches.Valid ? batches.LastValue : 0, s = setpass.Valid ? setpass.LastValue : 0;
            if (d == 0 && b == 0) { d = EditorStat("drawCalls"); b = EditorStat("batches"); s = EditorStat("setPassCalls"); }
            result[0] = d; result[1] = b; result[2] = s;
        }

        [UnityTest]
        public IEnumerator WorldKeyboard_DrawCalls_FullQwertyPage()
        {
            var kbType = typeof(UniText).Assembly.GetType("LightSide.UniTextKeyboard");
            Assert.IsNotNull(kbType, "LightSide.UniTextKeyboard missing");
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) { Assert.Ignore("FreeType native unavailable."); yield break; }
            var path = FindFont();
            if (path == null) { Assert.Ignore("NotoSans-Regular.ttf not found."); yield break; }
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), 48);
            _junk.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font);
            _junk.Add(stack);

            var camGo = new GameObject("Cam", typeof(Camera));
            _junk.Add(camGo);
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.targetTexture = new RenderTexture(1280, 720, 24);
            _junk.Add(cam.targetTexture);
            cam.transform.position = new Vector3(0f, 0f, -0.6f);

            var empty = new long[3];
            yield return Measure(cam, empty);

            var create = kbType.GetMethod("Create");
            var kb = (Component)create.Invoke(null, new object[] { null, true, stack, 0.045f });
            _junk.Add(kb.gameObject);
            kbType.GetMethod("Show", System.Type.EmptyTypes).Invoke(kb, null);
            kb.transform.position = Vector3.zero;
            Canvas.ForceUpdateCanvases();
            yield return null;
            Canvas.ForceUpdateCanvases();

            var shown = new long[3];
            yield return Measure(cam, shown);
            var keys = (int)kbType.GetProperty("KeyCount").GetValue(kb);
            var renderers = kb.GetComponentsInChildren<MeshRenderer>(false)
                .Where(r => r.enabled && r.GetComponent<MeshFilter>().sharedMesh != null && r.GetComponent<MeshFilter>().sharedMesh.vertexCount > 0).ToList();
            var materials = renderers.SelectMany(r => r.sharedMaterials).Distinct().Count();
            var line = $"world keyboard: {keys} keys, {renderers.Count} renderers, {materials} materials; " +
                       $"frame draws {shown[0]} (empty {empty[0]}), batches {shown[1]} (empty {empty[1]}), setpass {shown[2]} (empty {empty[2]}); " +
                       $"SRP batcher n/a (built-in pipeline), dynamic batching {(QualitySettings.renderPipeline == null ? "per player settings" : "n/a")}";
            Debug.Log("[W5] " + line);
            Assert.AreEqual(37, keys);
            Assert.LessOrEqual(materials, 2, "labels share one material, backgrounds one");
            if (shown[0] > 0) Assert.LessOrEqual(shown[0] - empty[0], renderers.Count, "at most one draw per renderer");
        }
    }
}
