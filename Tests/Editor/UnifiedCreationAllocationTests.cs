using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Pins the creation cost of the unified renderer. Each component used to own a
    /// <see cref="UnifiedRenderBuilder"/> with its own merge arrays (grown by doubling from 256
    /// vertices) and its own merged <see cref="Mesh"/>, all of it garbage on destroy: ~1 MB of
    /// managed allocation per 2,405-char component, 37 GCs per 1,000 creates on Quest vs 1 for legacy.
    /// The merge buffers and meshes are now process-wide (CanvasRenderer.SetMesh copies the mesh),
    /// like the legacy SharedMeshes.
    ///
    /// <para>Counter tests, not timing tests: after one warm-up batch, creating and building another
    /// batch of same-sized components must neither create a merged mesh nor grow a merge buffer.
    /// Components are pinned ForceOn, so the tests behave the same in both suite modes.</para>
    /// </summary>
    internal sealed class UnifiedCreationAllocationTests
    {
        private const int BatchSize = 8;

        private UniTextFontStack _stack;
        private UniTextFont _font;
        private GameObject _canvasGo;
        private bool _prevSnapshot;

        [SetUp]
        public void SetUp()
        {
            _prevSnapshot = UnifiedRenderBuilder.SnapshotMeshesForTests;
            string fontPath = MsdfTestUtil.FindNotoSansPath();
            if (fontPath == null) Assert.Ignore("NotoSans not found.");
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 90, 0.10f, UniTextRenderMode.SDF, 1024);
            if (_font == null) Assert.Ignore("Font backend unavailable.");
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);
            _canvasGo = new GameObject("CreationAllocCanvas", typeof(Canvas));
        }

        [TearDown]
        public void TearDown()
        {
            UnifiedRenderBuilder.SnapshotMeshesForTests = _prevSnapshot;
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
            if (_stack != null) Object.DestroyImmediate(_stack);
            if (_font != null) Object.DestroyImmediate(_font);
        }

        private static string LongText(int seed)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 12; i++)
                sb.Append("The quick brown fox jumps over the lazy dog ").Append(seed).Append(' ');
            return sb.ToString();
        }

        private UniText CreateComponent(string text, int index)
        {
            var ut = new GameObject("UT" + index, typeof(RectTransform)).AddComponent<UniText>();
            ut.transform.SetParent(_canvasGo.transform, false);
            ((RectTransform)ut.transform).sizeDelta = new Vector2(800, 600);
            ut.FontStack = _stack;
            ut.Appearance = RealLayoutFixtures.LoadDefaultAppearance();
            ut.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOn;
            ut.Text = text;
            return ut;
        }

        private void CreateBuildDestroyBatch()
        {
            var batch = new List<UniText>();
            for (int i = 0; i < BatchSize; i++) batch.Add(CreateComponent(LongText(i % 3), i));
            Canvas.ForceUpdateCanvases();
            foreach (var ut in batch)
                Assert.AreEqual(1, ut.UnifiedGroupCountForTests, $"{ut.name}: plain SDF text merges into one draw group.");
            foreach (var ut in batch) Object.DestroyImmediate(ut.gameObject);
        }

        [Test]
        public void CreatingUnifiedComponents_ReusesSharedMergeMeshAndBuffers()
        {
            // Model the player: no per-component test copies of the merged mesh.
            UnifiedRenderBuilder.SnapshotMeshesForTests = false;

            CreateBuildDestroyBatch(); // warm-up: the shared buffers/mesh reach their working size
            var probe = CreateComponent("probe", 99);
            Canvas.ForceUpdateCanvases();
            if (probe.ResultGlyphs.Length == 0) Assert.Ignore("Font backend produced no glyphs on this host.");
            Object.DestroyImmediate(probe.gameObject);

            long meshesBefore = UnifiedRenderBuilder.MeshesCreated;
            long growthsBefore = UnifiedRenderBuilder.BufferGrowths;

            CreateBuildDestroyBatch();
            CreateBuildDestroyBatch();

            Assert.AreEqual(0L, UnifiedRenderBuilder.MeshesCreated - meshesBefore,
                $"creating {2 * BatchSize} unified components after warm-up must not create merged meshes " +
                "(the merged mesh is shared; CanvasRenderer.SetMesh copies it).");
            Assert.AreEqual(0L, UnifiedRenderBuilder.BufferGrowths - growthsBefore,
                $"creating {2 * BatchSize} same-sized unified components after warm-up must not reallocate " +
                "merge buffers (they are shared, not per component).");
        }

        [Test]
        public void SharedMergeMesh_EachComponentSubmitsItsOwnGeometry()
        {
            // Per-component copies taken right after each component's SetMesh show what it submitted.
            UnifiedRenderBuilder.SnapshotMeshesForTests = true;

            var shortUt = CreateComponent("Short", 0);
            var longUt = CreateComponent(LongText(1), 1);
            Canvas.ForceUpdateCanvases();
            if (shortUt.ResultGlyphs.Length == 0) Assert.Ignore("Font backend produced no glyphs on this host.");

            var shortVerts = shortUt.GetGeneratedVerticesForEditorTests();
            var longVerts = longUt.GetGeneratedVerticesForEditorTests();
            Assert.AreEqual(4 * CountVisible(shortUt), shortVerts.Count, "short component: 4 verts per visible glyph.");
            Assert.AreEqual(4 * CountVisible(longUt), longVerts.Count, "long component: 4 verts per visible glyph.");
            Assert.Greater(longVerts.Count, shortVerts.Count, "the two components must not share submitted geometry.");

            // Rebuilding only the short one must not disturb the long one (its renderer holds a copy).
            shortUt.Text = "Changed";
            Canvas.ForceUpdateCanvases();
            CollectionAssert.AreEqual(longVerts, longUt.GetGeneratedVerticesForEditorTests());
        }

        private static int CountVisible(UniText ut)
        {
            // Count glyphs that produced a quad: the legacy path's segment vertex total / 4 is the
            // reference, so render the same text once through the legacy path.
            var go = new GameObject("Ref", typeof(RectTransform));
            go.transform.SetParent(ut.transform.parent, false);
            var r = go.AddComponent<UniText>();
            ((RectTransform)r.transform).sizeDelta = ((RectTransform)ut.transform).sizeDelta;
            r.FontStack = ut.FontStack;
            r.Appearance = ut.Appearance;
            r.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOff;
            r.Text = ut.Text;
            Canvas.ForceUpdateCanvases();
            var n = r.GetGeneratedVerticesForEditorTests().Count / 4;
            Object.DestroyImmediate(go);
            return n;
        }
    }
}
