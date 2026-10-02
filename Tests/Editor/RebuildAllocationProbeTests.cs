using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Feature tests for the fullRebuild geometry-identity re-upload skip
    /// (UniTextMeshGenerator.GeometryFingerprint + UniText.DoApplyMesh gate, default on via
    /// UniTextSettings.SkipUnchangedGeometryUpload). The Quest benchmark alternates text between
    /// `t` and `t + " "`; a trailing space is non-rendering, so both produce identical geometry and
    /// the skip eliminates the redundant Unity mesh re-upload (the fullRebuild totalAlloc driver).
    /// These assert the skip is (1) correct — identical geometry fingerprints and output-preserving,
    /// and (2) does not wrongly fire on a real text change. The allocation decomposition that proved
    /// the pipeline is already zero-alloc at steady state is recorded in
    /// Documentation/Design/MemoryBudgets.md §6a (measured during this work).
    /// </summary>
    internal sealed class GeometrySkipTests
    {
        private const float FontSize = 36f;

        private UniTextSettings _savedSettings;

        [SetUp]
        public void SetUp() => _savedSettings = UniTextSettings.IsNull ? null : UniTextSettings.Instance;

        [TearDown]
        public void TearDown()
        {
            if (_savedSettings != null) UniTextSettings.SetInstance(_savedSettings);
            SegHelper.ResetDictionaryAssignment();
        }

        private static string MixedText()
        {
            const string unit =
                "The quick brown fox jumps over the lazy dog while typesetting mixed scripts here. " +
                "\u0627\u0644\u0646\u0635 \u0627\u0644\u0639\u0631\u0628\u064a \u064a\u062e\u062a\u0628\u0631 \u0627\u0644\u062a\u0634\u0643\u064a\u0644. " +
                "\u05d8\u05e7\u05e1\u05d8 \u05e2\u05d1\u05e8\u05d9 \u05dc\u05d1\u05d3\u05d9\u05e7\u05ea. ";
            var sb = new StringBuilder(2600);
            while (sb.Length < 2405) sb.Append(unit);
            return sb.ToString(0, 2405);
        }

        private static readonly TextProcessSettings Settings = new TextProcessSettings
        {
            fontSize = FontSize,
            baseDirection = TextDirection.Auto,
            MaxWidth = 1200f,
            MaxHeight = TextProcessSettings.FloatMax,
            enableWordWrap = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        private static ulong FingerprintFor(TextProcessor tp, UniTextMeshGenerator gen, string text)
        {
            tp.InvalidateFirstPassData();
            tp.EnsureFirstPass(text, Settings);
            tp.EnsureLines(1200f, FontSize, wordWrap: true);
            tp.EnsurePositions(Settings);
            gen.GenerateMeshDataOnly(tp.PositionedGlyphs);
            ulong fp = gen.GeometryFingerprint();
            gen.ReturnInstanceBuffers();
            return fp;
        }

        [Test]
        public void TrailingSpaceVariant_HasIdenticalGeometryFingerprint()
        {
            string fontPath = MsdfTestUtil.FindNotoSansPath();
            if (fontPath == null) { Assert.Ignore("NotoSans fixture missing."); return; }
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 90, 0.10f, UniTextRenderMode.SDF, 1024);
            if (font == null) { Assert.Ignore("Font backend unavailable."); return; }
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font);
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();

            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(256);
            var tp = new TextProcessor(buffers);
            var fontProvider = new UniTextFontProvider(stack, appearance);
            tp.SetFontProvider(fontProvider);
            var gen = new UniTextMeshGenerator(fontProvider, buffers);
            gen.FontSize = FontSize; gen.defaultColor = new Color32(255, 255, 255, 255);
            gen.SetCanvasParametersCached(1f, false);
            gen.SetRectOffset(new Rect(0, 0, 1200, 100000));
            gen.SetHorizontalAlignment(HorizontalAlignment.Left);

            string text = MixedText();
            try
            {
                ulong fpText = FingerprintFor(tp, gen, text);
                ulong fpAlt = FingerprintFor(tp, gen, text + " ");
                Assert.AreNotEqual(0UL, fpText, "fingerprint is non-zero for real geometry");
                Assert.AreEqual(fpText, fpAlt,
                    "a trailing space produces identical rendered geometry, so the skip fires on the " +
                    "benchmark's alternating fullRebuild — eliminating the redundant mesh re-upload.");
                // A genuinely different string must change the fingerprint.
                ulong fpDiff = FingerprintFor(tp, gen, text.Substring(0, text.Length - 1) + "X");
                Assert.AreNotEqual(fpText, fpDiff, "a real content change changes the geometry fingerprint");
            }
            finally
            {
                gen.Dispose(); buffers.EnsureReturnBuffers();
                UnityEngine.Object.DestroyImmediate(stack);
                UnityEngine.Object.DestroyImmediate(font);
            }
        }

        [Test]
        public void GeometrySkip_PreservesRenderedOutput_AndStillUpdatesOnRealChange()
        {
            string fontPath = MsdfTestUtil.FindNotoSansPath();
            if (fontPath == null) { Assert.Ignore("NotoSans fixture missing."); return; }
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 90, 0.10f, UniTextRenderMode.SDF, 1024);
            if (font == null) { Assert.Ignore("Font backend unavailable."); return; }
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font);
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();

            var canvasGo = new GameObject("SkipCanvas", typeof(Canvas));
            var ut = new GameObject("UT", typeof(RectTransform)).AddComponent<UniText>();
            ut.transform.SetParent(canvasGo.transform, false);
            ((RectTransform)ut.transform).sizeDelta = new Vector2(1200, 2000);
            ut.FontStack = stack;
            ut.Appearance = appearance;

            try
            {
                ut.Text = "Hello World";
                Canvas.ForceUpdateCanvases();
                int glyphsA = ut.ResultGlyphs.Length;
                var sizeA = ut.ResultSize;
                Assert.Greater(glyphsA, 0, "initial build produced glyphs");

                ut.Text = "Hello World!";   // genuinely different -> must update (skip must not fire)
                Canvas.ForceUpdateCanvases();
                Assert.AreNotEqual(glyphsA, ut.ResultGlyphs.Length, "a real text change still updates geometry");

                ut.Text = "Hello World";     // back to original -> identical output restored
                Canvas.ForceUpdateCanvases();
                Assert.AreEqual(glyphsA, ut.ResultGlyphs.Length, "returning to the original text restores identical glyph count");
                Assert.That(ut.ResultSize.x, Is.EqualTo(sizeA.x).Within(0.01f), "restored width matches");
                Assert.That(ut.ResultSize.y, Is.EqualTo(sizeA.y).Within(0.01f), "restored height matches");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasGo);
                UnityEngine.Object.DestroyImmediate(stack);
                UnityEngine.Object.DestroyImmediate(font);
            }
        }
    }
}
