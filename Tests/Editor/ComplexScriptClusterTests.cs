using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Evidence for two FeatureComparison rows that previously had no committed test:
    /// <list type="bullet">
    /// <item>Devanagari: HarfBuzz conjunct shaping through the real <see cref="Shaper"/> pipeline
    /// produces substituted (conjunct / half-form) glyphs, so the glyph count differs from the
    /// per-codepoint count and the virama never survives as a standalone glyph.</item>
    /// <item>ZWJ emoji: a ZWJ family sequence is a single UAX #29 grapheme cluster.</item>
    /// </list>
    /// The Devanagari font is the OFL <c>NotoSansDevanagari-Regular.ttf</c> test fixture
    /// (see <c>Tests/Editor/Fixtures/SOURCES.md</c>).
    /// </summary>
    [TestFixture]
    public class ComplexScriptClusterTests
    {
        private const int Virama = 0x094D;

        // ---- Devanagari conjunct shaping ----

        [Test]
        public void Devanagari_KshaConjunct_ShapesToConjunctGlyph()
        {
            // क्ष = KA + VIRAMA + SSA -> one conjunct (ligature) glyph in Noto Sans Devanagari.
            AssertConjunct(new[] { 0x0915, Virama, 0x0937 }, "क्ष");
        }

        [Test]
        public void Devanagari_SteConjunct_ShapesToHalfFormAndMatra()
        {
            // स्ते = SA + VIRAMA + TA + VOWEL SIGN E -> half-form SA + TA + E matra.
            AssertConjunct(new[] { 0x0938, Virama, 0x0924, 0x0947 }, "स्ते");
        }

        private static void AssertConjunct(int[] codepoints, string label)
        {
            string path = FindFixtureFont("NotoSansDevanagari-Regular.ttf");
            if (path == null)
                Assert.Ignore("NotoSansDevanagari-Regular.ttf fixture not found under Tests/Editor/Fixtures.");

            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), 64, 0.25f, UniTextRenderMode.SDF, 512);
            Assert.IsNotNull(font, "Devanagari font asset creation failed.");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            try
            {
                stack.fonts.Add(font);
                var provider = new UniTextFontProvider(stack, null, 64f);

                // Glyphs a naive per-codepoint (cmap-only) renderer would draw.
                var cmapGlyphs = new HashSet<int>();
                foreach (int cp in codepoints)
                {
                    uint gi = Shaper.GetGlyphIndex(font, (uint)cp);
                    Assert.AreNotEqual(0u, gi, $"{label}: U+{cp:X4} not covered by the font.");
                    cmapGlyphs.Add((int)gi);
                }
                int viramaGlyph = (int)Shaper.GetGlyphIndex(font, Virama);

                var result = Shaper.Instance.Shape(codepoints, 0, codepoints.Length,
                    provider, provider.MainFontId, UnicodeScript.Devanagari, TextDirection.LeftToRight);
                var glyphs = result.Glyphs;

                Assert.Greater(glyphs.Length, 0, $"{label}: shaping produced no glyphs.");
                Assert.Less(glyphs.Length, codepoints.Length,
                    $"{label}: expected conjunct shaping to produce fewer glyphs than the {codepoints.Length} codepoints, got {glyphs.Length}.");

                bool hasSubstituted = false;
                foreach (var g in glyphs)
                {
                    Assert.AreNotEqual(0, g.glyphId, $"{label}: shaping produced .notdef.");
                    Assert.AreNotEqual(viramaGlyph, g.glyphId,
                        $"{label}: the virama survived as a standalone glyph (conjunct not formed).");
                    if (!cmapGlyphs.Contains(g.glyphId)) hasSubstituted = true;
                }
                Assert.IsTrue(hasSubstituted,
                    $"{label}: every shaped glyph is a plain cmap glyph; expected a GSUB conjunct/half-form glyph.");
            }
            finally
            {
                Object.DestroyImmediate(font);
                Object.DestroyImmediate(stack);
            }
        }

        // ---- ZWJ emoji grapheme clusters ----

        // 👨‍👩‍👧‍👦 MAN ZWJ WOMAN ZWJ GIRL ZWJ BOY
        private static readonly int[] Family = { 0x1F468, 0x200D, 0x1F469, 0x200D, 0x1F467, 0x200D, 0x1F466 };

        [Test]
        public void ZwjFamilyEmoji_IsSingleGraphemeCluster()
        {
            var breaks = GraphemeBreaks(Family);
            Assert.AreEqual(Family.Length + 1, breaks.Length);
            Assert.IsTrue(breaks[0], "Boundary expected before the sequence.");
            Assert.IsTrue(breaks[Family.Length], "Boundary expected after the sequence.");
            for (int i = 1; i < Family.Length; i++)
                Assert.IsFalse(breaks[i], $"Unexpected grapheme boundary inside the ZWJ sequence at index {i}.");
            Assert.AreEqual(1, CountClusters(breaks));
        }

        [Test]
        public void ZwjFamilyEmoji_BetweenLetters_IsThreeClusters()
        {
            var cps = new List<int> { 'a' };
            cps.AddRange(Family);
            cps.Add('b');
            Assert.AreEqual(3, CountClusters(GraphemeBreaks(cps.ToArray())),
                "Expected 'a' + one ZWJ family cluster + 'b'.");
        }

        private static bool[] GraphemeBreaks(int[] cps)
        {
            UnicodeData.EnsureInitialized();
            if (!UnicodeData.IsInitialized)
                Assert.Ignore("UnicodeData could not be initialized (Resources/UnicodeData.bytes missing).");
            return new GraphemeBreaker(UnicodeData.Provider).GetBreakOpportunities(cps);
        }

        private static int CountClusters(bool[] breaks)
        {
            int n = 0;
            for (int i = 1; i < breaks.Length; i++) if (breaks[i]) n++;
            return n;
        }

        private static string FindFixtureFont(string fileName)
        {
            string[] candidates =
            {
                "Packages/com.openglyph.text/Tests/Editor/Fixtures/" + fileName,
                Path.Combine(Application.dataPath ?? "", "..", "Packages", "com.openglyph.text",
                    "Tests", "Editor", "Fixtures", fileName),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }
    }
}
