using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Drives the real <see cref="LineBreaker"/> over a Thai paragraph using the actual
    /// segmentation break opportunities, with a synthetic uniform-width glyph per
    /// codepoint (one codepoint == one unit). This exercises wrapping end-to-end without
    /// requiring a Thai-capable font, and asserts that every produced line boundary is a
    /// dictionary word boundary and never falls inside a grapheme cluster.
    /// </summary>
    [TestFixture]
    public class SegmentationWrapTests
    {
        [OneTimeSetUp]
        public void Setup() { SegHelper.EnsureUnicode(); SegHelper.AssignDictionaries(); }

        [Test]
        public void NarrowWrap_BreaksAtWordBoundaries_NeverMidCluster()
        {
            // A long Thai paragraph: concatenate several fixture sentences.
            var text = SegmentationFixtures.Thai[0].Text
                     + SegmentationFixtures.Thai[1].Text
                     + SegmentationFixtures.Thai[2].Text
                     + SegmentationFixtures.Thai[3].Text;
            var cps = SegHelper.ToCodepoints(text);
            int n = cps.Length;

            // Break opportunities from UAX#14 + segmentation.
            var breaks = new LineBreakType[n + 1];
            SharedPipelineComponents.LineBreakAlgorithm.GetBreakOpportunitiesWithSegmentation(cps, breaks);

            // Grapheme boundaries (for the mid-cluster assertion).
            var grapheme = new bool[n + 1];
            new GraphemeBreaker(UnicodeData.Provider).GetBreakOpportunities(cps, grapheme);

            // Synthetic shaping: one glyph per codepoint, advance = 1, single LTR run.
            const float adv = 1f;
            var glyphs = new ShapedGlyph[n];
            for (int i = 0; i < n; i++)
                glyphs[i] = new ShapedGlyph { glyphId = i + 1, cluster = i, advanceX = adv };

            var runs = new ShapedRun[]
            {
                new ShapedRun
                {
                    range = new TextRange(0, n),
                    glyphStart = 0, glyphCount = n, width = n * adv,
                    direction = TextDirection.LeftToRight, bidiLevel = 0, fontId = 0
                }
            };
            var cpWidths = new float[n];
            for (int i = 0; i < n; i++) cpWidths[i] = adv;

            var paragraphs = new BidiParagraph[] { new BidiParagraph(0, n - 1, 0) };
            var startMargins = new float[n];

            // Narrow width: ~8 codepoints per line forces many wraps.
            const float maxWidth = 8f;

            var lineBuf = new TextLine[64];
            var orderedRuns = new ShapedRun[256];
            int lineCount = 0, orderedRunCount = 0;

            var lb = new LineBreaker();
            lb.BreakLines(cps, runs, glyphs, cpWidths, breaks, maxWidth, paragraphs,
                ref lineBuf, ref lineCount, ref orderedRuns, ref orderedRunCount, startMargins);

            Assert.Greater(lineCount, 1, "Narrow width should have produced multiple lines.");

            // The start of every line after the first is a wrap boundary. It must be a
            // legal break opportunity AND a grapheme-cluster boundary.
            for (int i = 1; i < lineCount; i++)
            {
                int lineStart = lineBuf[i].range.start;
                Assert.That(lineStart, Is.GreaterThan(0).And.LessThan(n));
                Assert.AreNotEqual(LineBreakType.None, breaks[lineStart],
                    $"Line {i} starts at index {lineStart}, which is not a break opportunity.");
                Assert.IsTrue(grapheme[lineStart],
                    $"Line {i} starts at index {lineStart}, which is inside a grapheme cluster.");
            }

            // No line may exceed the width budget unless it is a single unbreakable token.
            for (int i = 0; i < lineCount; i++)
            {
                var line = lineBuf[i];
                bool hasInteriorBreak = false;
                for (int k = line.range.start + 1; k < line.range.End; k++)
                    if (breaks[k] != LineBreakType.None) { hasInteriorBreak = true; break; }
                if (hasInteriorBreak)
                    Assert.LessOrEqual(line.width, maxWidth + 1e-3f,
                        $"Line {i} (width {line.width}) exceeds maxWidth {maxWidth} despite an interior break.");
            }
        }
    }
}
