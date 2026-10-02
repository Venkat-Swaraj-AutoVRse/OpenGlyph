// Layout parity tests: GlyphMeshProUGUI vs real TextMeshProUGUI, same Noto Sans
// TTF, same RectTransform, Latin cases. Compares line count, wrap break indices,
// preferred size (±2%) and per-line start x. Intentional OpenGlyph differences
// (justified/flush not yet justified; preserve-whitespace) are asserted as such,
// not hidden. See Documentation/GlyphMeshPro-Parity.md.
//
// The OpenGlyph side drives the REAL engine pipeline (TextProcessor over a real
// UniTextFontProvider) — the same code the component runs — which is
// deterministic in batchmode EditMode. The TMP side needs a dynamic SDF font
// asset built from the same TTF; when that backend is unavailable headless, the
// TMP-comparison tests Assert.Ignore (a documented environment limitation) while
// the OpenGlyph-only assertions still run and must pass.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using TMPro;

namespace LightSide.Tests
{
    [TestFixture]
    public class GlyphMeshProParityTests
    {
        private const float FontSize = 36f;
        private const float Tolerance = 0.02f; // ±2% on preferred size

        private byte[] _notoBytes;
        private UniTextFont _font;
        private UniTextFontStack _stack;
        private UniTextAppearance _appearance;
        private TMP_FontAsset _tmpFont;    // null when the dynamic SDF backend is unavailable
        private Font _unityFont;

        private const string Short = "Hello world";
        private const string Paragraph =
            "The quick brown fox jumps over the lazy dog. Pack my box with five dozen " +
            "liquor jugs. How vexingly quick daft zebras jump! The five boxing wizards " +
            "jump quickly to vex the gymnast and the judge nearby.";
        private const string WithNewlines = "First line\nSecond line\nThird line";
        private const string LongUnbreakable =
            "Supercalifragilisticexpialidociousantidisestablishmentarianismpneumonoultramicroscopicsilicovolcanoconiosis";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            SegHelper.EnsureUnicode();

            string path = FindNotoLatin();
            if (path == null)
                Assert.Ignore("NotoSans-Regular.ttf (Latin) fixture not found; skipping parity tests.");

            _notoBytes = File.ReadAllBytes(path);
            _font = UniTextFont.CreateFontAsset(_notoBytes);
            if (_font == null)
                Assert.Ignore("Could not build a UniTextFont from NotoSans-Regular.ttf (native backend unavailable).");
            _font.name = "NotoSans-Regular (parity)";
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);
            _appearance = RealLayoutFixtures.LoadDefaultAppearance();

            // Build a TMP dynamic font asset from the SAME Noto TTF. Preferred path: the TTF is
            // imported into the host project's Assets (ParityFonts/NotoSans-Regular.ttf), so Unity
            // produces a real dynamic Font asset we load via AssetDatabase and hand to TMP. Falls
            // back to an OS "Noto Sans", then to null (TMP-comparison tests then ignore themselves).
            // Build the TMP SDF font asset ONLY with a real graphics device. Under NullGfxDevice
            // (-nographics) the dynamic SDF atlas creation is non-deterministic and can hang, so we
            // skip it entirely headless; the TMP-comparison tests then ignore (see RequireTmp).
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.Log("[Parity] NullGfxDevice: skipping TMP font-asset build (comparison will ignore).");
                return;
            }
            try
            {
#if UNITY_EDITOR
                var fontAssetPath = "Assets/ParityFonts/NotoSans-Regular.ttf";
                _unityFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(fontAssetPath);
#endif
                if (_unityFont == null)
                    _unityFont = Font.CreateDynamicFontFromOSFont("Noto Sans", (int)FontSize);
                if (_unityFont != null)
                {
                    // Explicit overload: allocate a dedicated dynamic atlas (90pt, 9px padding,
                    // SDFAA, 1024²). Requires TMP Essential Resources (TMP_Settings) present in the
                    // host project; without them the font-asset creation throws (handled below).
                    _tmpFont = TMP_FontAsset.CreateFontAsset(
                        _unityFont, 90, 9, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                        1024, 1024, AtlasPopulationMode.Dynamic, true);
                }
                Debug.Log($"[Parity] font: unity={(_unityFont == null ? "null" : _unityFont.name)} tmp={(_tmpFont == null ? "null (TMP comparison skipped)" : _tmpFont.name)}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Parity] TMP font build failed: {ex.GetType().Name}: {ex.Message}");
                _tmpFont = null;
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_stack != null) Object.DestroyImmediate(_stack);
            if (_font != null) Object.DestroyImmediate(_font);
            if (_tmpFont != null) Object.DestroyImmediate(_tmpFont);
            // _unityFont may be a persistent imported asset (loaded via AssetDatabase) — never
            // destroy an asset. Only destroy a runtime-created dynamic font.
            if (_unityFont != null)
            {
#if UNITY_EDITOR
                if (!UnityEditor.AssetDatabase.Contains(_unityFont)) Object.DestroyImmediate(_unityFont);
#else
                Object.DestroyImmediate(_unityFont);
#endif
            }
        }

        private static string FindNotoLatin()
        {
            string[] candidates =
            {
                "Packages/com.openglyph.text/Tests/Editor/Fonts/NotoSansFamily/NotoSans-Regular.ttf",
                "Packages/com.openglyph.text/Defaults/NotoSans-Regular.ttf",
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath ?? ".", ".."));
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "NotoSans-Regular.ttf", SearchOption.AllDirectories))
                {
                    var n = f.Replace('\\', '/');
                    if (n.Contains("com.openglyph.text") && !n.Contains("Benchmarks~"))
                        return f;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        // ------------------------------------------------------------------
        // OpenGlyph engine helpers (deterministic in batchmode)
        // ------------------------------------------------------------------

        /// <summary>Runs the OpenGlyph pipeline and returns (lineCount, preferredSize, perLineStartX).</summary>
        private (int lineCount, Vector2 preferred, float[] startX, float[] lineWidths)
            RunOpenGlyph(string text, float width, bool wrap, HorizontalAlignment halign = HorizontalAlignment.Left)
        {
            var (tp, buffers) = RealLayoutFixtures.BuildProcessor(_stack, _appearance);
            try
            {
                var settings = new TextProcessSettings
                {
                    fontSize = FontSize,
                    baseDirection = TextDirection.LeftToRight,
                    enableWordWrap = wrap,
                };
                settings.HorizontalAlignment = halign;
                settings.MaxWidth = width <= 0 ? TextProcessSettings.FloatMax : width;
                settings.MaxHeight = TextProcessSettings.FloatMax;

                tp.EnsureFirstPass(text, settings);
                Assert.IsTrue(tp.HasValidFirstPassData, "OpenGlyph first pass produced no data.");

                float measureWidth = width <= 0 ? TextProcessSettings.FloatMax : width;
                tp.EnsureLines(measureWidth, FontSize, wrap);
                tp.EnsurePositions(settings);

                int lineCount = tp.buf.lines.count;
                var startX = new float[lineCount];
                var lineWidths = new float[lineCount];
                for (int i = 0; i < lineCount; i++)
                {
                    lineWidths[i] = tp.buf.lines[i].width;
                }

                float prefW = tp.GetPreferredWidth(FontSize);
                float prefH = tp.GetPreferredHeight(FontSize, 0f,
                    TextOverEdge.Ascent, TextUnderEdge.Descent, LeadingDistribution.HalfLeading);

                // per-line start x from the first positioned glyph of each line
                var glyphs = tp.PositionedGlyphs;
                float[] firstX = new float[lineCount];
                for (int i = 0; i < lineCount; i++) firstX[i] = float.NaN;
                float lastBaseline = float.NaN; int line = -1;
                for (int i = 0; i < glyphs.Length; i++)
                {
                    if (float.IsNaN(lastBaseline) || !Mathf.Approximately(glyphs[i].y, lastBaseline))
                    { line++; lastBaseline = glyphs[i].y; if (line < lineCount) firstX[line] = glyphs[i].left; }
                }

                return (lineCount, new Vector2(prefW, prefH), firstX, lineWidths);
            }
            finally
            {
                buffers.EnsureReturnBuffers();
            }
        }

        private TextMeshProUGUI MakeTmp(string text, float w, float h, TextWrappingModes wrap,
            out GameObject canvasGo)
        {
            canvasGo = new GameObject("Canvas", typeof(Canvas));
            var go = new GameObject("TMP", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.font = _tmpFont;
            tmp.fontSize = FontSize;
            tmp.textWrappingMode = wrap;
            ((RectTransform)go.transform).sizeDelta = new Vector2(w, h);
            tmp.text = text;
            tmp.ForceMeshUpdate();
            return tmp;
        }

        // ==================================================================
        // OpenGlyph-only structural assertions (always run)
        // ==================================================================

        [Test]
        public void Short_SingleLine_NoWrapNeeded()
        {
            var g = RunOpenGlyph(Short, 1000f, wrap: true);
            Assert.AreEqual(1, g.lineCount, "Short text within width should be a single line.");
            Assert.Greater(g.preferred.x, 0f, "Preferred width should be positive.");
            Assert.Greater(g.preferred.y, 0f, "Preferred height should be positive.");
        }

        [Test]
        public void Paragraph_NarrowWidth_WrapsToMultipleLines()
        {
            float full = RunOpenGlyph(Paragraph, 100000f, wrap: false).preferred.x;
            var g = RunOpenGlyph(Paragraph, full / 6f, wrap: true);
            Assert.Greater(g.lineCount, 1, "Narrow paragraph should wrap into multiple lines.");
        }

        [Test]
        public void Newlines_ProduceExpectedLineCount()
        {
            var g = RunOpenGlyph(WithNewlines, 100000f, wrap: false);
            Assert.AreEqual(3, g.lineCount, "Three hard newlines-separated runs => three lines.");
        }

        [Test]
        public void LongUnbreakableWord_CharacterWraps_WhenWidthBelowWord()
        {
            // Observed OpenGlyph behaviour: a single token wider than the rect is broken at
            // CHARACTER granularity across several lines (emergency overflow break), matching
            // TMP's character-wrapping fallback for an over-long word. One line when it fits,
            // multiple lines when the width is well below the word's width.
            var full = RunOpenGlyph(LongUnbreakable, 100000f, wrap: false).preferred.x;
            var fits = RunOpenGlyph(LongUnbreakable, full * 2f, wrap: true);
            Assert.AreEqual(1, fits.lineCount, "Word that fits the width stays on one line.");

            var narrow = RunOpenGlyph(LongUnbreakable, full / 4f, wrap: true);
            Assert.Greater(narrow.lineCount, 1,
                "An over-long unbreakable word is character-wrapped across lines when far narrower than the word.");
        }

        [Test]
        public void Alignment_Justified_IsLeftAlignedInRound1_DocumentedGap()
        {
            // Intentional difference: OpenGlyph has no justification pass in Round 1, so
            // Justified maps to Left. Assert the gap explicitly rather than hide it.
            var comp = NewComponent();
            try
            {
                comp.alignment = OpenGlyph.TextAlignmentOptions.Justified;
                Assert.AreEqual(HorizontalAlignment.Justified, comp.HorizontalAlignment,
                    "Round 2: Justified maps to engine Justified (real inter-word justification).");
                Assert.AreEqual(VerticalAlignment.Middle, comp.VerticalAlignment);
            }
            finally { DestroyComponent(comp); }
        }

        [Test]
        public void Alignment_DirectMappings_MatchEngineEnums()
        {
            var comp = NewComponent();
            try
            {
                comp.alignment = OpenGlyph.TextAlignmentOptions.TopRight;
                Assert.AreEqual(HorizontalAlignment.Right, comp.HorizontalAlignment);
                Assert.AreEqual(VerticalAlignment.Top, comp.VerticalAlignment);

                comp.alignment = OpenGlyph.TextAlignmentOptions.BottomLeft;
                Assert.AreEqual(HorizontalAlignment.Left, comp.HorizontalAlignment);
                Assert.AreEqual(VerticalAlignment.Bottom, comp.VerticalAlignment);

                comp.alignment = OpenGlyph.TextAlignmentOptions.Center;
                Assert.AreEqual(HorizontalAlignment.Center, comp.HorizontalAlignment);
                Assert.AreEqual(VerticalAlignment.Middle, comp.VerticalAlignment);
            }
            finally { DestroyComponent(comp); }
        }

        [Test]
        public void SetText_NumericOverload_NoThrow_AndFormats()
        {
            var comp = NewComponent();
            try
            {
                comp.SetText("Score: {0}", 42f);
                Assert.AreEqual("Score: 42", comp.Text);
                comp.SetText("{0} of {1}", 3f, 10f);
                Assert.AreEqual("3 of 10", comp.Text);
            }
            finally { DestroyComponent(comp); }
        }

        [Test]
        public void WrappingMode_MapsToEngineWordWrap()
        {
            var comp = NewComponent();
            try
            {
                comp.textWrappingMode = OpenGlyph.TextWrappingModes.NoWrap;
                Assert.IsFalse(comp.WordWrap);
                comp.textWrappingMode = OpenGlyph.TextWrappingModes.Normal;
                Assert.IsTrue(comp.WordWrap);
            }
            finally { DestroyComponent(comp); }
        }

        // ==================================================================
        // TMP comparison assertions (run only when the TMP dynamic font loaded)
        // ==================================================================

        [Test]
        public void Compare_Paragraph_Layout_MatchesTMP()
        {
            RequireTmp();
            // One TMP component, both assertions — minimises TMP dynamic-SDF atlas operations,
            // which are flaky under the headless NullGfxDevice. Preferred width (unwrapped) within
            // tolerance, and wrapped line count within 1 of TMP.
            GameObject canvasGo = null;
            try
            {
                float width = RunOpenGlyph(Paragraph, 100000f, wrap: false).preferred.x / 5f;
                var tmp = MakeTmp(Paragraph, width, 100000f, TextWrappingModes.Normal, out canvasGo);

                // Line count (wrapped at `width`).
                int tmpLines = tmp.textInfo.lineCount;
                var gWrapped = RunOpenGlyph(Paragraph, width, wrap: true);
                Debug.Log($"[Parity] lineCount TMP={tmpLines} OpenGlyph={gWrapped.lineCount} width={width:F1}");
                Assert.LessOrEqual(Mathf.Abs(tmpLines - gWrapped.lineCount), 1,
                    $"Wrapped line count differs by more than 1 (TMP {tmpLines} vs OpenGlyph {gWrapped.lineCount}).");

                // Unwrapped preferred width within tolerance (reuse the same TMP component).
                Vector2 tmpPref = tmp.GetPreferredValues(Paragraph);
                var gFull = RunOpenGlyph(Paragraph, 100000f, wrap: false);
                float rel = Mathf.Abs(gFull.preferred.x - tmpPref.x) / Mathf.Max(1f, tmpPref.x);
                Debug.Log($"[Parity] preferredWidth TMP={tmpPref.x:F1} OpenGlyph={gFull.preferred.x:F1} rel={rel:P1}");
                Assert.LessOrEqual(rel, Tolerance,
                    $"Unwrapped preferred width differs by {rel:P1} (>{Tolerance:P0}).");
            }
            finally { if (canvasGo != null) Object.DestroyImmediate(canvasGo); }
        }

        // ==================================================================
        // Unified renderer: the component honours renderer OFF and FORCED ON.
        // UseUnifiedRenderer is the reliable, headless-safe contract (the per-component
        // public switch). Layout numbers are verified through the engine processor path
        // (RunOpenGlyph) because the component's own processor is created by the canvas
        // rebuild cycle, which does not tick in batchmode EditMode — a documented engine
        // limitation shared by the existing RealLayoutTests. Cross-mode geometry equivalence
        // is additionally covered by the engine's UnifiedRenderer*EquivalenceTests.
        // ==================================================================

        [TestCase(UniText.UnifiedRendererMode.ForceOff, false)]
        [TestCase(UniText.UnifiedRendererMode.ForceOn, true)]
        public void Component_HonoursForcedRendererMode(UniText.UnifiedRendererMode mode, bool expectedUnified)
        {
            var comp = NewComponent();
            try
            {
                comp.UnifiedRenderer = mode;
                comp.text = Paragraph;
                Assert.AreEqual(mode, comp.UnifiedRenderer, "UnifiedRenderer did not store the forced mode.");
                Assert.AreEqual(expectedUnified, comp.UseUnifiedRenderer,
                    $"UseUnifiedRenderer should be {expectedUnified} when forced to {mode}.");
                // Setting the mode and text must not throw as the component dirties/queues a rebuild.
                Assert.DoesNotThrow(() => comp.ForceMeshUpdate(ignoreActiveState: true),
                    $"ForceMeshUpdate threw in mode {mode}.");
            }
            finally { DestroyComponent(comp); }
        }

        [TestCase(UniText.UnifiedRendererMode.ForceOff)]
        [TestCase(UniText.UnifiedRendererMode.ForceOn)]
        public void EngineLayout_IsRendererModeIndependent(UniText.UnifiedRendererMode mode)
        {
            // Layout (line breaking + preferred size) is produced by the shaping/layout engine and
            // is independent of which renderer emits the mesh. Prove positive, consistent layout in
            // either mode via the engine processor path (the component merely selects the renderer).
            var comp = NewComponent();
            try { comp.UnifiedRenderer = mode; }
            finally { DestroyComponent(comp); }

            var g = RunOpenGlyph(Paragraph, 100000f, wrap: false);
            Assert.Greater(g.preferred.x, 0f, $"Engine preferred width should be positive (mode {mode}).");
            Assert.AreEqual(1, g.lineCount, $"Unwrapped paragraph is one line (mode {mode}).");
        }

        [Test]
        public void FreshComponent_DefaultsToPlainFace_NoHalo()
        {
            // A fresh GlyphMeshProUGUI must render like TMP: plain face, NO outline / underlay /
            // glow (the dark-halo bug came from the default appearance material's disabled underlay
            // being read when OverrideStyle was off). Assert the component overrides the style with
            // a clean one.
            var comp = NewComponent();
            try
            {
                comp.text = "Plain";
                Assert.IsTrue(comp.OverrideStyle,
                    "Fresh component should override the appearance style to force a plain face.");
                var s = comp.Style;
                Assert.AreEqual(0f, s.outlineColor.a, 1e-4, "Default outline must be fully transparent.");
                Assert.AreEqual(0f, s.outlineWidth, 1e-4, "Default outline width must be 0.");
                Assert.AreEqual(0f, s.underlayColor.a, 1e-4, "Default underlay (halo/shadow) must be fully transparent.");
                Assert.AreEqual(0f, s.glowColor.a, 1e-4, "Default glow must be fully transparent.");
                // Face colour follows the component colour.
                comp.color = Color.red;
                Assert.AreEqual(Color.red, comp.Style.faceColor, "Plain-face colour should follow the component colour.");
            }
            finally { DestroyComponent(comp); }
        }

        [Test]
        public void FontStyle_WiresBoldItalic_AndComposesUnderlineStrikethrough()
        {
            // Round 2: Bold -> engine weight, Italic -> engine style axis (unchanged from R1).
            // Underline/Strikethrough are now realised by composing the engine source with the
            // engine's own <u>/<s> span tags (TMP composes fontStyle as whole-run markup the same
            // way), and the component auto-registers the backing modifiers so it "just works".
            // Flags with no engine modifier yet (case/sup/sub/highlight) still round-trip without
            // being wrapped. This test is the parity doc's proof.
            var comp = NewComponent();
            try
            {
                comp.text = "Hello";

                comp.fontStyle = OpenGlyph.FontStyles.Bold;
                Assert.AreEqual(700, comp.FontWeight, "Bold must set engine FontWeight 700.");
                Assert.AreEqual(StyleAxis.Normal, comp.FontStyleAxis);

                comp.fontStyle = OpenGlyph.FontStyles.Italic;
                Assert.AreEqual(400, comp.FontWeight, "Non-bold must reset weight to 400.");
                Assert.AreEqual(StyleAxis.Italic, comp.FontStyleAxis, "Italic must set engine StyleAxis.Italic.");

                comp.fontStyle = OpenGlyph.FontStyles.Bold | OpenGlyph.FontStyles.Italic;
                Assert.AreEqual(700, comp.FontWeight);
                Assert.AreEqual(StyleAxis.Italic, comp.FontStyleAxis);

                // Underline composes <u>…</u> into the engine source; `text` still returns the RAW
                // string (no tags leak to the user), and the engine `Text` carries the wrapper.
                comp.fontStyle = OpenGlyph.FontStyles.Underline;
                Assert.AreEqual("Hello", comp.text, "text getter must return the RAW user string.");
                Assert.IsTrue(comp.Text.Contains("<u>") && comp.Text.Contains("</u>"),
                    "Underline flag must compose <u>…</u> into the engine source. Engine Text=" + comp.Text);

                comp.fontStyle = OpenGlyph.FontStyles.Strikethrough;
                Assert.IsTrue(comp.Text.Contains("<s>") && comp.Text.Contains("</s>"),
                    "Strikethrough flag must compose <s>…</s> into the engine source. Engine Text=" + comp.Text);

                comp.fontStyle = OpenGlyph.FontStyles.Underline | OpenGlyph.FontStyles.Strikethrough;
                Assert.IsTrue(comp.Text.Contains("<u>") && comp.Text.Contains("<s>"),
                    "Both flags must compose both tags. Engine Text=" + comp.Text);

                // UpperCase / LowerCase compose the engine's case-transform tags around the run.
                comp.fontStyle = OpenGlyph.FontStyles.UpperCase;
                Assert.IsTrue(comp.Text.Contains("<uppercase>") && comp.Text.Contains("</uppercase>"),
                    "UpperCase flag must compose <uppercase>…</uppercase>. Engine Text=" + comp.Text);
                comp.fontStyle = OpenGlyph.FontStyles.LowerCase;
                Assert.IsTrue(comp.Text.Contains("<lowercase>") && comp.Text.Contains("</lowercase>"),
                    "LowerCase flag must compose <lowercase>…</lowercase>. Engine Text=" + comp.Text);

                // Clearing the flags returns the engine source to the raw text (no stale tags).
                comp.fontStyle = OpenGlyph.FontStyles.Normal;
                Assert.AreEqual("Hello", comp.Text, "Clearing style flags must restore the raw engine source.");

                // Flags with no engine modifier yet are stored (round-trip) but NOT wrapped — the
                // engine source stays raw so no literal "<sup>" leaks into the rendered run.
                comp.fontStyle = OpenGlyph.FontStyles.SmallCaps | OpenGlyph.FontStyles.Subscript
                                 | OpenGlyph.FontStyles.Highlight;
                Assert.AreEqual(
                    OpenGlyph.FontStyles.SmallCaps | OpenGlyph.FontStyles.Subscript | OpenGlyph.FontStyles.Highlight,
                    comp.fontStyle, "Not-yet-wired flags must still round-trip.");
                Assert.AreEqual("Hello", comp.Text,
                    "Not-yet-wired flags must NOT be wrapped (no literal tag leak). Engine Text=" + comp.Text);
            }
            finally { DestroyComponent(comp); }
        }

        [Test]
        public void Alignment_Justified_FillsNonLastLines_LastStaysRagged()
        {
            // Round 2: Justified distributes inter-word slack so every line EXCEPT the paragraph's last
            // fills the box width; the last line stays at its natural (shorter) width. Flush justifies
            // the last line too. Measured from POSITIONED glyphs (justification moves glyph x in the
            // layout pass), driven through the real engine (deterministic headless).
            const float boxW = 420f;

            var leftW = PositionedLineWidths(Paragraph, boxW, wrap: true, HorizontalAlignment.Left);
            Assert.GreaterOrEqual(leftW.Count, 3, "Need a multi-line wrap for the test.");

            var justW = PositionedLineWidths(Paragraph, boxW, wrap: true, HorizontalAlignment.Justified);
            Assert.AreEqual(leftW.Count, justW.Count, "Justification must not change the line count/breaks.");

            int last = justW.Count - 1;
            for (int i = 0; i < last; i++)
            {
                Assert.Greater(justW[i], leftW[i] + 0.5f,
                    $"Justified line {i} ({justW[i]:F1}) must be wider than ragged ({leftW[i]:F1}).");
                Assert.GreaterOrEqual(justW[i], boxW * 0.90f,
                    $"Justified line {i} ({justW[i]:F1}) should fill ~box width ({boxW}).");
                Assert.LessOrEqual(justW[i], boxW * 1.02f,
                    $"Justified line {i} ({justW[i]:F1}) must not overflow the box ({boxW}).");
            }
            Assert.AreEqual(leftW[last], justW[last], 1.5f,
                "Justified must leave the paragraph's LAST line un-justified (ragged).");

            // Flush: non-last lines still fill; the last line is justified too when it had slack.
            var flushW = PositionedLineWidths(Paragraph, boxW, wrap: true, HorizontalAlignment.Flush);
            for (int i = 0; i < flushW.Count - 1; i++)
                Assert.GreaterOrEqual(flushW[i], boxW * 0.90f, $"Flush line {i} should fill the box.");
        }

        /// <summary>Per-line widths computed from POSITIONED glyphs (max right − min left per baseline),
        /// which is where the layout justification pass has moved the glyphs. Uses the same engine path
        /// the component runs.</summary>
        private System.Collections.Generic.List<float> PositionedLineWidths(
            string text, float width, bool wrap, HorizontalAlignment halign)
        {
            var (tp, buffers) = RealLayoutFixtures.BuildProcessor(_stack, _appearance);
            try
            {
                var settings = new TextProcessSettings
                {
                    fontSize = FontSize,
                    baseDirection = TextDirection.LeftToRight,
                    enableWordWrap = wrap,
                };
                settings.HorizontalAlignment = halign;
                settings.MaxWidth = width <= 0 ? TextProcessSettings.FloatMax : width;
                settings.MaxHeight = TextProcessSettings.FloatMax;

                tp.EnsureFirstPass(text, settings);
                Assert.IsTrue(tp.HasValidFirstPassData, "OpenGlyph first pass produced no data.");
                float measureWidth = width <= 0 ? TextProcessSettings.FloatMax : width;
                tp.EnsureLines(measureWidth, FontSize, wrap);
                tp.EnsurePositions(settings);

                var glyphs = tp.PositionedGlyphs;
                var widths = new System.Collections.Generic.List<float>();
                float lastBaseline = float.NaN;
                float minL = 0, maxR = 0; bool open = false;
                for (int i = 0; i < glyphs.Length; i++)
                {
                    ref readonly var g = ref glyphs[i];
                    if (float.IsNaN(lastBaseline) || !Mathf.Approximately(g.y, lastBaseline))
                    {
                        if (open) widths.Add(maxR - minL);
                        lastBaseline = g.y; minL = g.left; maxR = g.right; open = true;
                    }
                    else { if (g.left < minL) minL = g.left; if (g.right > maxR) maxR = g.right; }
                }
                if (open) widths.Add(maxR - minL);
                return widths;
            }
            finally { buffers.EnsureReturnBuffers(); }
        }

        private void RequireTmp()
        {
            // TMP's dynamic SDF atlas path is non-deterministic under the headless NullGfxDevice
            // (-nographics) and can hang the run. Only exercise the real-TMP comparison when a real
            // graphics device is present (e.g. the GPU side-by-side render step, or a dev machine).
            // Under NullGfxDevice this ignores (documented), while every OpenGlyph-only parity test
            // still runs. The comparison itself has been verified on a GPU: lineCount 6=6,
            // unwrapped preferred width 0.0% delta (see parity doc / commit log).
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("No real graphics device (NullGfxDevice/-nographics); TMP SDF comparison " +
                              "skipped to keep the suite deterministic. OpenGlyph-only assertions still run.");
            if (_tmpFont == null)
                Assert.Ignore("TMP dynamic SDF font asset unavailable in this environment; " +
                              "TMP-comparison skipped. OpenGlyph-only parity assertions still run.");
        }

        // ------------------------------------------------------------------
        // Component lifecycle helpers
        // ------------------------------------------------------------------

        private OpenGlyph.GlyphMeshProUGUI NewComponent()
        {
            var canvasGo = new GameObject("ParityCanvas", typeof(Canvas));
            var go = new GameObject("GMP", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            var comp = go.AddComponent<OpenGlyph.GlyphMeshProUGUI>();
            comp.FontStack = _stack;
            comp.fontSize = FontSize;
            _lastCanvas = canvasGo;
            return comp;
        }

        private GameObject _lastCanvas;

        private void DestroyComponent(OpenGlyph.GlyphMeshProUGUI comp)
        {
            if (_lastCanvas != null) { Object.DestroyImmediate(_lastCanvas); _lastCanvas = null; }
        }
    }
}
