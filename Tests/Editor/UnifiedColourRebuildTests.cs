using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Pins the colour-only rebuild cost of the unified renderer. Since the unified renderer became
    /// the default, every rebuild uploaded the per-segment meshes, then read them back
    /// (<c>Mesh.GetVertices</c>/<c>GetColors</c>/<c>GetUVs</c>/<c>GetTriangles</c>) to merge them,
    /// roughly doubling a colour-only rebuild (Windows: ~51 ms -> ~90 ms per 100 x 2,405 chars).
    /// The merge now reads the generator buffers directly and skips the per-segment upload.
    ///
    /// <para>Counter tests, not timing tests. Each component is pinned with
    /// <see cref="UniText.UnifiedRendererMode.ForceOn"/>/<c>ForceOff</c>, so they behave the same in
    /// the -ForceLegacy and -ForceUnified suite runs.</para>
    /// </summary>
    internal sealed class UnifiedColourRebuildTests
    {
        private const string Text = "Colour only — the quick brown fox jumps over the lazy dog 0123456789";

        private sealed class Rig : System.IDisposable
        {
            public GameObject canvasGo;
            public UniText ut;
            private UniTextFontStack _stack;
            private UniTextFont _font;

            public static Rig Create(UniText.UnifiedRendererMode mode)
            {
                string fontPath = MsdfTestUtil.FindNotoSansPath();
                if (fontPath == null) return null;
                var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 90, 0.10f, UniTextRenderMode.SDF, 1024);
                if (font == null) return null;
                var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
                stack.fonts.Add(font);

                var canvasGo = new GameObject("ColourRebuildCanvas", typeof(Canvas));
                var ut = new GameObject("UT", typeof(RectTransform)).AddComponent<UniText>();
                ut.transform.SetParent(canvasGo.transform, false);
                ((RectTransform)ut.transform).sizeDelta = new Vector2(600, 400);
                ut.FontStack = stack;
                ut.Appearance = RealLayoutFixtures.LoadDefaultAppearance();
                ut.UnifiedRenderer = mode;
                ut.Text = Text;
                Canvas.ForceUpdateCanvases();
                return new Rig { canvasGo = canvasGo, ut = ut, _stack = stack, _font = font };
            }

            public void Dispose()
            {
                if (canvasGo != null) Object.DestroyImmediate(canvasGo);
                if (_stack != null) Object.DestroyImmediate(_stack);
                if (_font != null) Object.DestroyImmediate(_font);
            }
        }

        private static Mesh MergedMesh(UniText ut)
        {
            var data = ut.UnifiedRenderDataForEditorTests;
            Assert.IsNotNull(data, "unified render data must exist after a unified rebuild.");
            Assert.AreEqual(1, data.Count, "plain SDF text merges into exactly one draw group.");
            return data[0].mesh;
        }

        [Test]
        public void UnifiedColourChange_MergesFromGeneratorBuffers_NoMeshReadback()
        {
            using var rig = Rig.Create(UniText.UnifiedRendererMode.ForceOn);
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }
            if (rig.ut.ResultGlyphs.Length == 0) { Assert.Ignore("Font backend produced no glyphs on this host."); return; }

            var positionsBefore = MergedMesh(rig.ut).vertices;
            UnifiedRenderBuilder.MeshReadbackSegments = 0;
            UniText.ResetMeshUploadCount();

            var colours = new[] { Color.red, Color.green, new Color(0.2f, 0.4f, 0.9f, 0.5f) };
            foreach (var c in colours)
            {
                rig.ut.color = c;
                Canvas.ForceUpdateCanvases();

                var mesh = MergedMesh(rig.ut);
                var cols = mesh.colors32;
                Assert.AreEqual(positionsBefore.Length, cols.Length, "one colour per merged vertex.");
                Color32 want = c;
                for (var i = 0; i < cols.Length; i++)
                    Assert.AreEqual(want, cols[i], $"vertex {i}: the colour change must reach the merged mesh.");
                CollectionAssert.AreEqual(positionsBefore, mesh.vertices, "a colour-only change must not move any vertex.");
            }

            Assert.AreEqual(colours.Length, UniText.MeshUploadCount, "sanity: each colour change rebuilt the mesh once.");
            Assert.AreEqual(0L, UnifiedRenderBuilder.MeshReadbackSegments,
                "the unified merge must read the generator buffers, not read the just-uploaded segment " +
                "meshes back (that readback was the colour-only rebuild regression).");
        }

        [Test]
        public void UnifiedMergedGeometry_MatchesLegacySegments()
        {
            List<Vector3> legacy;
            using (var rig = Rig.Create(UniText.UnifiedRendererMode.ForceOff))
            {
                if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }
                if (rig.ut.ResultGlyphs.Length == 0) { Assert.Ignore("Font backend produced no glyphs on this host."); return; }
                legacy = rig.ut.GetGeneratedVerticesForEditorTests();
            }

            using (var rig = Rig.Create(UniText.UnifiedRendererMode.ForceOn))
            {
                var mesh = MergedMesh(rig.ut);
                CollectionAssert.AreEqual(legacy, mesh.vertices, "merged positions must equal the legacy segment positions.");

                var uv1 = new List<Vector4>();
                mesh.GetUVs(1, uv1);
                Assert.AreEqual(legacy.Count, uv1.Count);
                for (var i = 0; i < uv1.Count; i++)
                    Assert.Greater(uv1[i].x, 0f, $"vertex {i}: UV1.x must carry the real spreadRatio.");

                // Generator-merged text carries only the UGUI forward normal, so the channel is left
                // off like the legacy segment meshes (the canvas supplies the default normal).
                var normals = mesh.normals;
                Assert.IsTrue(normals.Length == 0 || (normals.Length == legacy.Count && normals[0] == new Vector3(0, 0, -1)),
                    "no normal channel (legacy layout), or the UGUI forward normal.");
                Assert.AreEqual(legacy.Count / 4 * 6, mesh.triangles.Length, "two triangles per glyph quad.");
            }
        }
    }
}
