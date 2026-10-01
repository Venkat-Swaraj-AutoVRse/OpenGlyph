using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LightSide.Tests
{
    /// <summary>
    /// End-to-end layout tests that drive OpenGlyph's REAL text pipeline — HarfBuzz
    /// shaping, UAX #14 line breaking + dictionary segmentation, and line layout — over a
    /// Thai paragraph using a real Thai-capable font (<c>NotoSansThai-Regular.ttf</c>, OFL,
    /// see <c>Fixtures/SOURCES.md</c>), with the Thai dictionary assigned through
    /// <see cref="UniTextSettings"/> exactly as a shipping project would.
    ///
    /// <para>These are distinct from <c>SegmentationWrapTests</c>, which runs the line
    /// breaker in isolation over synthetic unit-advance glyphs: here the glyph advances come
    /// from the real font via the real shaper, so line boundaries are produced by the actual
    /// engine, and we assert each wrap boundary is a dictionary word boundary that is also a
    /// grapheme-cluster boundary (never mid-word, never mid-cluster), plus that with NO
    /// dictionary assigned the same text falls back to the old single-token behaviour and
    /// emits the documented one-time warning.</para>
    /// </summary>
    [TestFixture]
    public class RealLayoutTests
    {
        private byte[] _thaiFontBytes;
        private UniTextFont _thaiFont;
        private UniTextFontStack _thaiStack;
        private UniTextAppearance _appearance;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            SegHelper.EnsureUnicode();

            string path = RealLayoutFixtures.FindThaiFontPath();
            if (path == null)
                Assert.Ignore("NotoSansThai-Regular.ttf fixture not found under Tests/Editor/Fixtures; " +
                              "skipping real-layout tests (font is required to shape Thai).");

            _thaiFontBytes = File.ReadAllBytes(path);
            _thaiFont = UniTextFont.CreateFontAsset(_thaiFontBytes);
            if (_thaiFont == null)
                Assert.Ignore("Could not build a UniTextFont from the Thai fixture (native font backend unavailable).");
            _thaiFont.name = "NotoSansThai-Regular (fixture)";

            _thaiStack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _thaiStack.fonts.Add(_thaiFont);

            _appearance = RealLayoutFixtures.LoadDefaultAppearance();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_thaiStack != null) UnityEngine.Object.DestroyImmediate(_thaiStack);
            if (_thaiFont != null) UnityEngine.Object.DestroyImmediate(_thaiFont);
        }

        /// <summary>
        /// Real pipeline, Thai dictionary assigned, narrow width: assert the engine wraps
        /// the paragraph into several lines, that every wrap boundary is BOTH a dictionary /
        /// UAX#14 break opportunity (never mid-word) AND a grapheme-cluster boundary (never
        /// mid-cluster), and that lines carrying an interior break opportunity respect the
        /// width budget.
        /// </summary>
        [Test]
        public void Thai_NarrowWidth_WrapsAtDictionaryWordBoundaries()
        {
            SegHelper.AssignDictionaries();

            // A long Thai paragraph (several fixture sentences concatenated).
            string text = SegmentationFixtures.Thai[0].Text
                        + SegmentationFixtures.Thai[1].Text
                        + SegmentationFixtures.Thai[2].Text
                        + SegmentationFixtures.Thai[3].Text
                        + SegmentationFixtures.Thai[4].Text
                        + SegmentationFixtures.Thai[5].Text;

            // Instantiate a REAL UniText component and assign the font + text + wrap, to
            // prove the component wires the font stack through to the processor. We then run
            // the identical processor pipeline the component uses internally for the asserts
            // (driving a MaskableGraphic's canvas pass deterministically in batchmode EditMode
            // is unreliable; the processor path is the same engine code).
            var canvasGo = new GameObject("Canvas", typeof(Canvas));
            try
            {
                var go = new GameObject("UniTextThai");
                go.transform.SetParent(canvasGo.transform, false);
                var comp = go.AddComponent<UniText>();
                comp.FontStack = _thaiStack;
                comp.WordWrap = true;
                comp.FontSize = 36f;
                comp.Text = text;
                Assert.AreSame(_thaiFont, comp.MainFont, "Component did not resolve the assigned Thai main font.");

                // ---- Real pipeline ----
                var (tp, buffers) = RealLayoutFixtures.BuildProcessor(_thaiStack, _appearance);
                try
                {
                    const float fontSize = 36f;
                    var settings = new TextProcessSettings { fontSize = fontSize, baseDirection = TextDirection.Auto };

                    tp.EnsureFirstPass(text, settings);
                    Assert.IsTrue(tp.HasValidFirstPassData, "First pass (shape/analyze) did not produce valid data.");

                    int n = tp.buf.codepoints.count;
                    Assert.Greater(n, 0, "No codepoints after parse.");

                    // Codepoints actually laid out (processor copy), and the segmentation +
                    // grapheme boundaries computed over that SAME buffer.
                    var cps = new int[n];
                    for (int i = 0; i < n; i++) cps[i] = tp.buf.codepoints[i];

                    var breaks = new LineBreakType[n + 1];
                    SharedPipelineComponents.LineBreakAlgorithm.GetBreakOpportunitiesWithSegmentation(cps, breaks);

                    var grapheme = new bool[n + 1];
                    new GraphemeBreaker(UnicodeData.Provider).GetBreakOpportunities(cps, grapheme);

                    // Pick a width ABOVE the widest single word (the max advance between two
                    // consecutive break opportunities). Then no word overflows, so the layout never
                    // has to emergency-break mid-word — every wrap boundary must be a real word
                    // boundary. (A width below a word's width would legitimately force a mid-word
                    // overflow break, which is overflow handling, not a segmentation bug.)
                    var cpw = tp.buf.cpWidths;
                    float widestWord = 0f, acc = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        acc += (i < cpw.count ? cpw[i] : 0f);
                        if (breaks[i + 1] != LineBreakType.None) { if (acc > widestWord) widestWord = acc; acc = 0f; }
                    }
                    if (acc > widestWord) widestWord = acc;
                    float unwrapped = tp.GetUnwrappedWidth();
                    float maxWidth = Mathf.Max(widestWord * 1.25f + 1f, unwrapped / 8f);

                    tp.EnsureLines(maxWidth, fontSize, wordWrap: true);
                    settings.MaxWidth = maxWidth;
                    settings.MaxHeight = TextProcessSettings.FloatMax;
                    settings.enableWordWrap = true;
                    tp.EnsurePositions(settings);

                    int lineCount = tp.buf.lines.count;
                    Assert.Greater(lineCount, 1,
                        $"Width ({maxWidth:F1}px of {unwrapped:F1}px unwrapped, widest word {widestWord:F1}px) should wrap into multiple lines; got {lineCount}.");

                    // Every line after the first starts at a wrap boundary. It must be a legal
                    // break opportunity (word boundary, never mid-word) AND a grapheme boundary
                    // (never inside a cluster).
                    for (int i = 1; i < lineCount; i++)
                    {
                        int lineStart = tp.buf.lines[i].range.start;
                        Assert.That(lineStart, Is.GreaterThan(0).And.LessThanOrEqualTo(n),
                            $"Line {i} start index {lineStart} out of range.");
                        if (lineStart >= n) continue; // trailing empty line, if any
                        Assert.AreNotEqual(LineBreakType.None, breaks[lineStart],
                            $"Line {i} starts at codepoint {lineStart} (U+{cps[lineStart]:X4}) which is NOT a break opportunity (mid-word wrap).");
                        Assert.IsTrue(grapheme[lineStart],
                            $"Line {i} starts at codepoint {lineStart} (U+{cps[lineStart]:X4}) which is inside a grapheme cluster (mid-cluster wrap).");
                    }

                    // A line that contains an interior break opportunity must honour the width
                    // budget (a line may exceed it only when it is a single unbreakable token).
                    for (int i = 0; i < lineCount; i++)
                    {
                        var line = tp.buf.lines[i];
                        bool hasInteriorBreak = false;
                        for (int k = line.range.start + 1; k < line.range.End && k <= n; k++)
                            if (breaks[k] != LineBreakType.None) { hasInteriorBreak = true; break; }
                        if (hasInteriorBreak)
                            Assert.LessOrEqual(line.width, maxWidth + 1f,
                                $"Line {i} (width {line.width:F1}) exceeds maxWidth {maxWidth:F1} despite containing an interior break.");
                    }

                    Debug.Log($"[RealLayoutTests] Thai: {n} codepoints shaped by the real font, wrapped into {lineCount} lines at {maxWidth:F1}px; all wrap boundaries are dictionary word + grapheme boundaries.");
                }
                finally
                {
                    buffers.EnsureReturnBuffers();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasGo);
            }
        }

        /// <summary>
        /// With NO dictionary assigned, the same Thai run must fall back to the pre-segmentation
        /// behaviour — a single SA token with no interior break opportunities, so a single line
        /// at any width — and emit the documented one-time warning.
        /// </summary>
        [Test]
        public void Thai_NoDictionaryAssigned_FallsBackWithWarning()
        {
            // Settings instance carrying UnicodeData but NO segmentation dictionaries.
            var settingsAsset = ScriptableObject.CreateInstance<UniTextSettings>();
            UniTextSettings.SetInstance(settingsAsset);
            SegHelper.ResetDictionaryAssignment(); // force the segmenter to re-resolve (and re-warn)

            try
            {
                string text = SegmentationFixtures.Thai[2].Text; // one Thai sentence (pure SA run)
                var cps = SegHelper.ToCodepoints(text);
                int n = cps.Length;

                // The one-time per-script warning is emitted when the segmenter first tries to
                // resolve the (now unassigned) Thai dictionary during break computation.
                LogAssert.Expect(LogType.Warning,
                    new Regex(@"\[DictionarySegmenter\] No segmentation dictionary assigned for Thai"));

                var breaks = new LineBreakType[n + 1];
                SharedPipelineComponents.LineBreakAlgorithm.GetBreakOpportunitiesWithSegmentation(cps, breaks);

                var baseline = new LineBreakType[n + 1];
                SharedPipelineComponents.LineBreakAlgorithm.GetBreakOpportunities(cps, baseline);

                // Fallback == baseline UAX#14: segmentation must ADD NO break opportunities when
                // no dictionary is assigned (the run stays one unbreakable SA token, exactly the
                // pre-segmentation behaviour). We compare the opportunity sets, not line count:
                // layout may still EMERGENCY-break an over-wide unbreakable token into pieces, which
                // is legitimate overflow handling and unrelated to word segmentation.
                int interior = 0, added = 0;
                for (int i = 1; i < n; i++)
                {
                    if (breaks[i] != LineBreakType.None) interior++;
                    if (breaks[i] != LineBreakType.None && baseline[i] == LineBreakType.None) added++;
                }
                Assert.AreEqual(0, added,
                    "Without a dictionary, segmentation must add no interior break opportunities beyond baseline UAX#14 (old single-token behaviour).");
                Assert.AreEqual(0, interior,
                    "A pure Thai run has no baseline UAX#14 interior break opportunities, so with no dictionary there must be none at all.");

                // With a dictionary assigned the SAME run DOES gain interior word boundaries —
                // this is the behavioural difference the fallback removes.
                SegHelper.ResetDictionaryAssignment();
                SegHelper.AssignDictionaries();
                var withDict = new LineBreakType[n + 1];
                SharedPipelineComponents.LineBreakAlgorithm.GetBreakOpportunitiesWithSegmentation(cps, withDict);
                int withDictInterior = 0;
                for (int i = 1; i < n; i++) if (withDict[i] != LineBreakType.None) withDictInterior++;
                Assert.Greater(withDictInterior, 0,
                    "With the Thai dictionary assigned the same run must gain interior word boundaries.");
                Debug.Log($"[RealLayoutTests] Thai no-dictionary fallback: 0 interior breaks + one-time warning; with dictionary: {withDictInterior} interior word boundaries.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settingsAsset);
                // Restore dictionaries for any later test in the run.
                SegHelper.ResetDictionaryAssignment();
                SegHelper.AssignDictionaries();
            }
        }

        /// <summary>
        /// Khmer and Myanmar real-layout coverage, using the bundled OFL Noto Sans fixtures
        /// (see Fixtures/SOURCES.md). Each script must wrap only at dictionary word boundaries
        /// and never inside a grapheme cluster.
        /// </summary>
        [TestCase(SegmentationScript.Khmer)]
        [TestCase(SegmentationScript.Myanmar)]
        public void ComplexScript_NarrowWidth_WrapsAtWordBoundaries(SegmentationScript script)
        {
            var candidate = RealLayoutFixtures.FindComplexScriptFont(script,
                out SegmentationFixtures.Case[] cases);
            Assert.IsNotNull(candidate, $"{script} font fixture missing under Tests/Editor/Fixtures/.");

            SegHelper.AssignDictionaries();
            var fontBytes = File.ReadAllBytes(candidate);
            var font = UniTextFont.CreateFontAsset(fontBytes);
            Assert.IsNotNull(font, $"Could not build a font from {candidate}.");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font);
            var (tp, buffers) = RealLayoutFixtures.BuildProcessor(stack, _appearance);
            try
            {
                string text = cases[0].Text + cases[1].Text + cases[2].Text + cases[3].Text;
                var s = new TextProcessSettings { fontSize = 36f, baseDirection = TextDirection.Auto };
                tp.EnsureFirstPass(text, s);
                int n = tp.buf.codepoints.count;
                float maxWidth = Mathf.Max(1f, tp.GetUnwrappedWidth() / 8f);
                tp.EnsureLines(maxWidth, 36f, true);

                var cps = new int[n];
                for (int i = 0; i < n; i++) cps[i] = tp.buf.codepoints[i];
                var breaks = new LineBreakType[n + 1];
                SharedPipelineComponents.LineBreakAlgorithm.GetBreakOpportunitiesWithSegmentation(cps, breaks);
                var grapheme = new bool[n + 1];
                new GraphemeBreaker(UnicodeData.Provider).GetBreakOpportunities(cps, grapheme);

                Assert.Greater(tp.buf.lines.count, 1, $"{script}: narrow width should produce multiple lines.");
                for (int i = 1; i < tp.buf.lines.count; i++)
                {
                    int ls = tp.buf.lines[i].range.start;
                    if (ls <= 0 || ls >= n) continue;
                    Assert.AreNotEqual(LineBreakType.None, breaks[ls], $"{script}: line {i} wraps mid-word at {ls}.");
                    Assert.IsTrue(grapheme[ls], $"{script}: line {i} wraps mid-cluster at {ls}.");
                }
            }
            finally
            {
                buffers.EnsureReturnBuffers();
                UnityEngine.Object.DestroyImmediate(stack);
                UnityEngine.Object.DestroyImmediate(font);
            }
        }
    }
}
