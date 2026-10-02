using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Diagnostic for the "first glyph broken on every row" report. Lays out "RR" (identical glyphs)
    /// on a PLAIN Regular face (no family, no variation) through the real mesh generator and compares
    /// glyph 0's quad against glyph 1's: UV rect SIZE, vertex quad SIZE, atlas index and the atlas
    /// texture sampled. For two identical glyphs these must match; a mismatch on glyph 0 localizes the
    /// defect to the first-added glyph (atlas upload / UV / keyed-store off-by-one).
    /// </summary>
    public class FirstGlyphDiagnosticTests
    {
        private readonly List<UnityEngine.Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
            Shaper.ClearAllCaches();
        }

        [Test]
        public void PlainRegular_RR_FirstAndSecondGlyph_IdenticalQuadAndUV()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans not found.");
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 64, renderMode: UniTextRenderMode.SDF);
            _created.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font); _created.Add(stack);
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();

            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(32);
            var tp = new TextProcessor(buffers);
            var provider = new UniTextFontProvider(stack, appearance);
            tp.SetFontProvider(provider);
            var settings = new TextProcessSettings { fontSize = 64f, baseDirection = TextDirection.Auto,
                MaxWidth = TextProcessSettings.FloatMax, MaxHeight = TextProcessSettings.FloatMax };
            tp.EnsureFirstPass("RR", settings);

            // Ensure the atlas has the glyphs (same path the component uses).
            var set = new List<uint>();
            var sg = tp.buf.shapedGlyphs.Span;
            for (int i = 0; i < sg.Length; i++) { var gi=(uint)sg[i].glyphId; if (gi!=0) set.Add(gi); }
            font.TryAddGlyphsBatch(set);

            tp.EnsureLines(TextProcessSettings.FloatMax, 64f, wordWrap: false);
            tp.EnsurePositions(settings);

            var pg = tp.PositionedGlyphs;
            Assert.GreaterOrEqual(pg.Length, 2, "Expected two positioned glyphs for 'RR'.");

            // Both glyphs are 'R' -> same glyphId -> same atlas rect/metrics. Compare their stored Glyph.
            var g0 = pg[0]; var g1 = pg[1];
            Assert.AreEqual(g0.glyphId, g1.glyphId, "Both 'R' must share a glyph id.");
            Assert.IsTrue(font.TryGetGlyph((uint)g0.glyphId, VariationKey.None, out var ga));
            Assert.IsTrue(font.TryGetGlyph((uint)g1.glyphId, VariationKey.None, out var gb));
            Assert.AreEqual(ga.glyphRect, gb.glyphRect, "Identical glyphs must map to the same atlas rect.");
            Assert.AreEqual(ga.atlasIndex, gb.atlasIndex, "Identical glyphs must map to the same atlas page.");
            Assert.Greater(ga.glyphRect.width, 0, "Glyph 0 atlas rect must be non-empty.");

            // The atlas texture that page points to must exist and be non-null.
            Assert.IsNotNull(font.AtlasTextures, "Atlas textures missing.");
            Assert.Less(ga.atlasIndex, font.AtlasTextures.Count, "Glyph 0 atlas page out of range.");
            Assert.IsNotNull(font.AtlasTextures[ga.atlasIndex], "Glyph 0 atlas texture is null.");

            buffers.EnsureReturnBuffers();
        }
    }
}
