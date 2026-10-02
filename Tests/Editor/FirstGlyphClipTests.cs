using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression guard for the "first glyph of a multi-glyph run is clipped on its right side"
    /// report (the right half of the leading 's'/'a' vanished behind a hard vertical edge whose
    /// position moved with spreadStrength/padding). The guard asserts the mesh-level geometry
    /// invariant that must hold for EVERY glyph quad, first or not: the quad's UV rect and X extent
    /// span the full glyph cell (ink + 2*padding). If the first quad were narrowed/clipped, its UV
    /// width would be smaller than ink+2*padding and smaller than the same glyph rendered later in
    /// the run.
    /// </summary>
    [TestFixture]
    public class FirstGlyphClipTests
    {
        private const float FontSize = 400f;

        private static string NotoPath()
        {
            string p = MsdfTestUtil.FindNotoSansPath();
            return p;
        }

        [TestCase("sac", 0.25f)]
        [TestCase("sac", 0.10f)]
        [TestCase("asc", 0.25f)]
        public void FirstGlyph_QuadSpansFullCell_NotClipped(string text, float spread)
        {
            string fontPath = NotoPath();
            if (fontPath == null) Assert.Ignore("NotoSans fixture missing.");

            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 90, spread, UniTextRenderMode.SDF, 1024);
            if (font == null) Assert.Ignore("Font backend unavailable.");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font);
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();

            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(256);
            var tp = new TextProcessor(buffers);
            var fontProvider = new UniTextFontProvider(stack, appearance);
            tp.SetFontProvider(fontProvider);

            var settings = new TextProcessSettings
            {
                fontSize = FontSize,
                baseDirection = TextDirection.Auto,
                MaxWidth = TextProcessSettings.FloatMax,
                MaxHeight = TextProcessSettings.FloatMax,
                enableWordWrap = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            tp.EnsureFirstPass(text, settings);
            tp.EnsureLines(settings.MaxWidth, FontSize, wordWrap: false);
            tp.EnsurePositions(settings);

            // Rasterise the glyphs.
            var glyphSet = new List<uint>();
            var seen = new HashSet<uint>();
            var sg = tp.buf.shapedGlyphs.Span;
            for (int i = 0; i < sg.Length; i++) { var gi = (uint)sg[i].glyphId; if (gi != 0 && seen.Add(gi)) glyphSet.Add(gi); }
            if (glyphSet.Count > 0) font.TryAddGlyphsBatch(glyphSet);

            var gen = new UniTextMeshGenerator(fontProvider, buffers);
            gen.FontSize = FontSize;
            gen.defaultColor = new Color32(255, 255, 255, 255);
            gen.SetCanvasParametersCached(1f, false);
            gen.SetRectOffset(new Rect(0, 0, 4096, 2048));
            gen.SetHorizontalAlignment(HorizontalAlignment.Left);
            gen.GenerateMeshDataOnly(tp.PositionedGlyphs);
            Assert.IsTrue(gen.HasGeneratedData, "no mesh generated");

            int atlasSize = font.AtlasSize;
            int padding = font.AtlasPadding;

            // Collect per-quad UV-rect width (atlas px) and X width from the generated buffers.
            var verts = gen.Vertices;
            var uvs = gen.Uvs0;
            int vcount = 0;
            // vertex count: find how many quads were written (4 verts each) by scanning for the
            // segment via ApplyMeshesToUnity bounds — simpler: use the known glyph count.
            int quadCount = 0;
            // The generator wrote 4 verts per non-zero-rect glyph in order. Count quads by stepping
            // until a degenerate quad (all zero) — but safer to use text length as the max and read
            // the first N quads we actually have. Vertices array length is a pool buffer, so rely on
            // the ApplyMeshes segment counts instead.
            var rd = gen.ApplyMeshesToUnity();
            // Reconstruct per-quad data straight from the vertex/uv buffers in emission order.
            // Each quad: v0=BL, v1=TL, v2=TR, v3=BR ; uv0.x/uvBLx .. uv2.x/uvTRx.
            // Determine quad count from the first segment's vertex count sum.
            int totalVerts = 0;
            foreach (var seg in gen.GeneratedSegments) totalVerts += seg.vertexCount;
            quadCount = totalVerts / 4;
            Assert.GreaterOrEqual(quadCount, text.Length, $"expected >= {text.Length} quads, got {quadCount}");

            TestContext.WriteLine($"text='{text}' spread={spread} padding={padding} atlasSize={atlasSize} quads={quadCount}");

            // Build the expected UV-rect px width per glyph index in the run, from each glyph's
            // stored atlas rect (ink width) + 2*padding. This is the ground-truth cell width the
            // quad MUST sample. A clipped first quad would read narrower than this.
            var sgSpan = tp.buf.shapedGlyphs.Span;
            float scale = FontSize / font.FaceInfo.pointSize;

            float firstUvW = -1f, firstXW = -1f;
            for (int q = 0; q < quadCount && q < text.Length; q++)
            {
                int b = q * 4;
                float uvBLx = uvs[b].x;
                float uvTRx = uvs[b + 2].x;
                float uvRectPxW = (uvTRx - uvBLx) * atlasSize;
                float xW = verts[b + 2].x - verts[b].x; // trX - tlX

                // Ground-truth expected cell width = stored atlas rect width + 2*padding.
                uint gi = (uint)sgSpan[q].glyphId;
                font.TryGetGlyph(gi, VariationKey.None, out var g);
                float expectedCellPxW = g.glyphRect.width + 2 * padding;

                TestContext.WriteLine($"  quad[{q}] gi={gi} uvRectPxW={uvRectPxW:F1} xW={xW:F1} expectedCellPxW={expectedCellPxW:F1}");

                // ABSOLUTE invariant: the quad's UV rect width must equal ink+2*padding (the full
                // cell), for EVERY glyph including the first. If the first glyph were clipped, this
                // fails on q==0.
                Assert.That(uvRectPxW, Is.EqualTo(expectedCellPxW).Within(1.0f),
                    $"quad[{q}] gi={gi}: UV rect width {uvRectPxW:F1}px != glyph ink+2*padding {expectedCellPxW:F1}px " +
                    (q == 0 ? "(FIRST glyph is clipped in the mesh)" : "(glyph clipped in the mesh)"));
                // UV width must also be consistent with the X extent (UV px == xW/scale).
                Assert.That(uvRectPxW, Is.EqualTo(xW / scale).Within(1.0f),
                    $"quad[{q}] gi={gi}: UV width {uvRectPxW:F1} inconsistent with X extent {xW:F1}/scale.");

                if (q == 0) { firstUvW = uvRectPxW; firstXW = xW; }
            }

            TestContext.WriteLine($"first quad: uvRectPxW={firstUvW:F2} xW={firstXW:F2}");

            gen.ReturnInstanceBuffers();
            buffers.EnsureReturnBuffers();
            UnityEngine.Object.DestroyImmediate(stack);
            UnityEngine.Object.DestroyImmediate(font);
        }
    }
}
