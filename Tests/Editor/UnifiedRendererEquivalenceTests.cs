using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 3 acceptance: a component with the DEFAULT appearance
    /// renders equivalently through the legacy per-segment path and the unified single-renderer path
    /// (<c>UniText/Uber</c> + shared Texture2DArray + style table). Two separate components (one
    /// ForceOff, one ForceOn) render the same text; we compare the geometry each actually submits and
    /// assert the unified one collapses to AT MOST TWO uber draw groups.
    /// </summary>
    /// <remarks>
    /// The unified builder reuses the legacy segment meshes' geometry verbatim (pages-as-slices), so
    /// the submitted quad POSITIONS must match exactly — a deterministic equivalence that holds in
    /// batchmode without a GPU readback. Draw-group materials are asserted source-side (the materials
    /// the component bound) because a batchmode CanvasRenderer does not reliably report GetMaterial()
    /// back without a camera. A pixel-level camera comparison is the PlayMode test.
    /// </remarks>
    public class UnifiedRendererEquivalenceTests
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

        private UniText MakeText(UniText.UnifiedRendererMode mode, UniTextFontStack stack, UniTextAppearance app)
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("UniText", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(canvasGo.transform, false);
            var t = go.AddComponent<UniText>();
            t.SetUnifiedRendererModeForTests(mode);
            t.FontStack = stack;
            t.Appearance = app;
            t.FontSize = 40f;
            t.Text = "Reading 123";
            Canvas.ForceUpdateCanvases();
            return t;
        }

        private static List<Vector3> Sorted(List<Vector3> v)
        {
            v.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : (a.y != b.y ? a.y.CompareTo(b.y) : a.z.CompareTo(b.z)));
            return v;
        }

        [Test]
        public void UnifiedPath_SameGeometry_AndAtMostTwoUberGroups()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();

            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (font == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(font);
            var app = ScriptableObject.CreateInstance<UniTextAppearance>(); _junk.Add(app);

            var legacy = MakeText(UniText.UnifiedRendererMode.ForceOff, stack, app);
            var legacyVerts = legacy.GetDrawnVerticesForTests();
            if (legacyVerts.Count == 0) Assert.Ignore("Pipeline produced no geometry in this environment.");

            var unified = MakeText(UniText.UnifiedRendererMode.ForceOn, stack, app);

            // Structural win: unified produced <=2 merged groups, all on UniText/Uber.
            int groups = unified.UnifiedGroupCountForTests;
            var groupShaders = unified.GetUnifiedGroupShaderNamesForTests();
            Assert.GreaterOrEqual(groups, 1, "unified path must produce >=1 group for non-empty text");
            Assert.LessOrEqual(groups, 2, $"unified path must produce <=2 draw groups (got {groups})");
            CollectionAssert.AreEqual(Enumerable.Repeat("UniText/Uber", groupShaders.Count).ToList(), groupShaders,
                "every unified draw group must use UniText/Uber; got: " + string.Join(", ", groupShaders));

            // Geometry equivalence: same vertex count, same positions (set-equal within tolerance).
            var unifiedVerts = unified.GetDrawnVerticesForTests();
            Assert.AreEqual(legacyVerts.Count, unifiedVerts.Count, "unified path emits the same vertex count as legacy");
            var a = Sorted(legacyVerts); var b = Sorted(unifiedVerts);
            const float tol = 1e-3f; // positions are copied verbatim by the merge; any delta is ULP-scale.
            int mism = 0;
            for (int i = 0; i < a.Count; i++)
                if (Vector3.Distance(a[i], b[i]) > tol) mism++;
            Assert.AreEqual(0, mism, $"{mism}/{a.Count} vertices differ beyond {tol} between legacy and unified geometry");
        }
    }
}
