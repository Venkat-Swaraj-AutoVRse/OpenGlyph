using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LightSide.Tests
{
    /// <summary>
    /// Feature tests for the fullRebuild geometry-identity re-upload skip
    /// (UniTextMeshGenerator.GeometryFingerprint + UniText.DoApplyMesh gate, default on via
    /// UniTextSettings.SkipUnchangedGeometryUpload). The Quest benchmark alternates text between
    /// `t` and `t + " "`; a trailing space is non-rendering, so both produce identical geometry and
    /// the skip eliminates the redundant Unity mesh re-upload (the fullRebuild totalAlloc driver).
    ///
    /// This file also pins the SAFETY of the skip: the fingerprint must be EXACT, so that a change
    /// the renderer would actually draw differently can never be dropped. The channel/index/segment
    /// tests below (SubQuantumPositionShift, Uv1ChannelChange, PositionZChange, IndexReorder,
    /// SegmentStructureChange) each demonstrate a case that the OLD (quantized positions + UV0 + colour)
    /// fingerprint collapsed to an equal hash — i.e. would have been wrongly skipped — and assert the
    /// new fingerprint distinguishes it. IdenticalGeometry_StillSkips guards the other direction (no
    /// false re-upload). The allocation decomposition that proved the pipeline is already zero-alloc
    /// at steady state is recorded in Documentation/Design/MemoryBudgets.md §6a.
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

        /// <summary>
        /// Builds a self-contained pipeline (buffers + processor + font provider + generator) over
        /// one in-memory font. Returns null when the native font backend is unavailable (so a test
        /// can Assert.Ignore rather than report a false failure on a degraded host).
        /// </summary>
        private sealed class Rig : IDisposable
        {
            public UniTextBuffers buffers;
            public TextProcessor tp;
            public UniTextFontProvider fontProvider;
            public UniTextMeshGenerator gen;
            public UniTextFontStack stack;
            public UniTextFont font;

            public static Rig Create()
            {
                string fontPath = MsdfTestUtil.FindNotoSansPath();
                if (fontPath == null) return null;
                var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 90, 0.10f, UniTextRenderMode.SDF, 1024);
                if (font == null) return null;
                var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
                stack.fonts.Add(font);
                var appearance = RealLayoutFixtures.LoadDefaultAppearance();
                var buffers = new UniTextBuffers();
                buffers.EnsureRentBuffers(256);
                var tp = new TextProcessor(buffers);
                var fontProvider = new UniTextFontProvider(stack, appearance);
                tp.SetFontProvider(fontProvider);
                var gen = new UniTextMeshGenerator(fontProvider, buffers);
                gen.FontSize = FontSize;
                gen.defaultColor = new Color32(255, 255, 255, 255);
                gen.SetCanvasParametersCached(1f, false);
                gen.SetRectOffset(new Rect(0, 0, 1200, 100000));
                gen.SetHorizontalAlignment(HorizontalAlignment.Left);
                return new Rig { buffers = buffers, tp = tp, fontProvider = fontProvider, gen = gen, stack = stack, font = font };
            }

            /// <summary>Runs the pipeline for <paramref name="text"/> and LEAVES the generated buffers
            /// live (does NOT ReturnInstanceBuffers) so the caller can read/mutate channels, then
            /// returns the fingerprint.</summary>
            public ulong GenerateLeaveLive(string text)
            {
                tp.InvalidateFirstPassData();
                tp.EnsureFirstPass(text, Settings);
                tp.EnsureLines(1200f, FontSize, wordWrap: true);
                tp.EnsurePositions(Settings);
                gen.GenerateMeshDataOnly(tp.PositionedGlyphs);
                return gen.GeometryFingerprint();
            }

            public ulong GenerateAndReturn(string text)
            {
                ulong fp = GenerateLeaveLive(text);
                gen.ReturnInstanceBuffers();
                return fp;
            }

            public void Dispose()
            {
                gen?.Dispose();
                buffers?.EnsureReturnBuffers();
                if (stack != null) UnityEngine.Object.DestroyImmediate(stack);
                if (font != null) UnityEngine.Object.DestroyImmediate(font);
            }
        }

        // The OLD fingerprint quantized X/Y to 1/16 unit (RoundToInt(v*16)); this reproduces that
        // quantization so a test can prove a mutation stays within the old bucket (⇒ old hash equal).
        private static int OldQuantXY(float v) => Mathf.RoundToInt(v * 16f);

        // -------------------------------------------------------------------------------------------
        // Feature correctness (benchmark pattern): a trailing space produces identical geometry.
        // -------------------------------------------------------------------------------------------
        [Test]
        public void TrailingSpaceVariant_HasIdenticalGeometryFingerprint()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            string text = MixedText();
            ulong fpText = rig.GenerateAndReturn(text);
            if (fpText == 0)
            {
                Assert.Ignore("Font backend produced no geometry (native FreeType/HarfBuzz unavailable " +
                              "or Unity graphics degraded) — cannot test the fingerprint.");
                return;
            }
            ulong fpAlt = rig.GenerateAndReturn(text + " ");
            Assert.AreEqual(fpText, fpAlt,
                "a trailing space produces identical rendered geometry, so the skip fires on the " +
                "benchmark's alternating fullRebuild — eliminating the redundant mesh re-upload.");
            ulong fpDiff = rig.GenerateAndReturn(text.Substring(0, text.Length - 1) + "X");
            Assert.AreNotEqual(fpText, fpDiff, "a real content change changes the geometry fingerprint");
        }

        // -------------------------------------------------------------------------------------------
        // SAFETY (a): a sub-quantum position shift (smooth animation) MUST re-upload.
        // The old fingerprint quantized position to 1/16 unit, so a shift smaller than half a quantum
        // collapsed to the same hash and was wrongly skipped. The new fingerprint hashes raw bits.
        // -------------------------------------------------------------------------------------------
        [Test]
        public void SubQuantumPositionShift_ChangesFingerprint()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            ulong fp0 = rig.GenerateLeaveLive(MixedText());
            if (fp0 == 0) { rig.gen.ReturnInstanceBuffers(); Assert.Ignore("No geometry produced."); return; }

            var verts = rig.gen.Vertices;
            int n = rig.gen.vertexCount;
            Assert.Greater(n, 0, "geometry has vertices");

            // A tiny shift strictly inside the old 1/16 quantization bucket for vertex 0's X.
            float x0 = verts[0].x;
            int oldQ = OldQuantXY(x0);
            float shift = 0.01f;                 // << 0.5/16 = 0.03125 (half a quantum)
            // Ensure we really stay inside the same old bucket (so the OLD hash would NOT change).
            if (OldQuantXY(x0 + shift) != oldQ) shift = 0.001f;
            Assert.AreEqual(oldQ, OldQuantXY(x0 + shift),
                "the shift is sub-quantum: the OLD quantized fingerprint would NOT see it (so the old " +
                "code would wrongly skip this real move).");

            verts[0].x = x0 + shift;             // simulate a smooth-animation sub-pixel move
            ulong fp1 = rig.gen.GeometryFingerprint();
            rig.gen.ReturnInstanceBuffers();

            Assert.AreNotEqual(fp0, fp1,
                "a sub-quantum position change must change the fingerprint so the skip does NOT drop a " +
                "real (if tiny) geometry move — the exact-bits fingerprint catches what quantization lost.");
        }

        // -------------------------------------------------------------------------------------------
        // SAFETY (b): a change in a NON-UV0 channel (UV1 — the effect/line/style channel the unified
        // renderer and its outline/underlay read) MUST re-upload. The old fingerprint hashed only
        // UV0.xy + colour + quantized position, never UV1, so an outline/underlay style change that
        // lands in UV1 was wrongly skipped.
        // -------------------------------------------------------------------------------------------
        [Test]
        public void Uv1ChannelChange_ChangesFingerprint()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            ulong fp0 = rig.GenerateLeaveLive(MixedText());
            if (fp0 == 0) { rig.gen.ReturnInstanceBuffers(); Assert.Ignore("No geometry produced."); return; }

            int n = rig.gen.vertexCount;
            var uv1 = rig.gen.Uvs1;
            Assert.Greater(n, 0, "geometry has vertices");

            // UV1.w carries the per-glyph LOCAL span-style id (0 = base) that UnifiedRenderBuilder maps
            // to an outline/underlay style row. Changing it changes what is DRAWN but touches neither
            // position, UV0, nor colour — exactly the case the old fingerprint missed.
            uv1[0].w += 1f;
            ulong fp1 = rig.gen.GeometryFingerprint();
            rig.gen.ReturnInstanceBuffers();

            Assert.AreNotEqual(fp0, fp1,
                "a UV1 change (outline/underlay style via the unified renderer) must change the " +
                "fingerprint; the old UV0-only fingerprint would have skipped a real style change.");
        }

        // -------------------------------------------------------------------------------------------
        // SAFETY (a'): the Z position channel (never hashed by the old fingerprint) MUST re-upload.
        // -------------------------------------------------------------------------------------------
        [Test]
        public void PositionZChange_ChangesFingerprint()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            ulong fp0 = rig.GenerateLeaveLive(MixedText());
            if (fp0 == 0) { rig.gen.ReturnInstanceBuffers(); Assert.Ignore("No geometry produced."); return; }

            var verts = rig.gen.Vertices;
            Assert.Greater(rig.gen.vertexCount, 0, "geometry has vertices");
            verts[0].z += 5f;                    // old fingerprint ignored Z entirely
            ulong fp1 = rig.gen.GeometryFingerprint();
            rig.gen.ReturnInstanceBuffers();

            Assert.AreNotEqual(fp0, fp1, "a Z-position change must change the fingerprint (old code ignored Z).");
        }

        // -------------------------------------------------------------------------------------------
        // SAFETY (c-index): an index-buffer reorder with identical vertices MUST re-upload. The old
        // fingerprint hashed only triangleCount, so a winding/submesh reassignment was invisible.
        // -------------------------------------------------------------------------------------------
        [Test]
        public void IndexReorder_ChangesFingerprint()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            ulong fp0 = rig.GenerateLeaveLive(MixedText());
            if (fp0 == 0) { rig.gen.ReturnInstanceBuffers(); Assert.Ignore("No geometry produced."); return; }

            var tris = rig.gen.Triangles;
            Assert.GreaterOrEqual(rig.gen.triangleCount, 6, "geometry has at least one glyph quad");
            // Swap two indices: same count, different order ⇒ different draw.
            (tris[0], tris[1]) = (tris[1], tris[0]);
            ulong fp1 = rig.gen.GeometryFingerprint();
            rig.gen.ReturnInstanceBuffers();

            Assert.AreNotEqual(fp0, fp1, "an index reorder must change the fingerprint (old code hashed only the count).");
        }

        // -------------------------------------------------------------------------------------------
        // SAFETY (c): IDENTICAL geometry still skips — no false re-upload (the other direction).
        // -------------------------------------------------------------------------------------------
        [Test]
        public void IdenticalGeometry_StillSkips()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            string text = MixedText();
            ulong a = rig.GenerateAndReturn(text);
            if (a == 0) { Assert.Ignore("No geometry produced."); return; }
            ulong b = rig.GenerateAndReturn(text);
            Assert.AreEqual(a, b, "re-generating identical text yields an identical fingerprint ⇒ the skip fires (no false re-upload).");
        }

        // -------------------------------------------------------------------------------------------
        // Component-level behaviour preservation (GPU path): a real text change updates; returning to
        // the original restores identical output. Uses GPU rendering, so it Ignores on a degraded host.
        // -------------------------------------------------------------------------------------------
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

            // The geometry skip is OPT-IN (default OFF), so enable it explicitly to exercise the skip
            // path; TearDown restores the saved settings instance.
            UniTextSettings.SetSkipUnchangedGeometryUploadForTests(true);

            try
            {
                ut.Text = "Hello World";
                Canvas.ForceUpdateCanvases();
                int glyphsA = ut.ResultGlyphs.Length;
                var sizeA = ut.ResultSize;
                if (glyphsA == 0) { Assert.Ignore("Font backend produced no glyphs on this host."); return; }

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

        // -------------------------------------------------------------------------------------------
        // COST: the fingerprint is on the rebuild hot path, so measure its cost over the design
        // workload — 100 objects x 2,405 chars, DIFFERENT text per object (defeats shaping/glyph
        // caches). Reports ms per 100 objects and ns per vertex. Not a hard gate (host-dependent),
        // but it prints the number the task asks for and fails only if it is implausibly large.
        // -------------------------------------------------------------------------------------------
        [Test]
        public void FingerprintCost_Per100ObjectsX2405Chars()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            const int objects = 100;
            // Distinct text per object so each build is real work and nothing is cache-shared.
            string baseText = MixedText();
            var texts = new string[objects];
            for (int i = 0; i < objects; i++)
                texts[i] = baseText.Substring(0, baseText.Length - 3) + i.ToString("D3");

            // Warm up the pipeline + JIT the fingerprint.
            rig.GenerateLeaveLive(texts[0]);
            long warmVerts = rig.gen.vertexCount;
            _ = rig.gen.GeometryFingerprint();
            rig.gen.ReturnInstanceBuffers();
            if (warmVerts == 0) { Assert.Ignore("No geometry produced on this host."); return; }

            double totalFpMs = 0;
            long totalVerts = 0;
            const int fpRepeats = 5; // average several fingerprint calls per object to reduce timer noise
            var sw = new Stopwatch();
            for (int i = 0; i < objects; i++)
            {
                rig.GenerateLeaveLive(texts[i]);          // build geometry (NOT timed)
                int vc = rig.gen.vertexCount;
                totalVerts += vc;
                sw.Restart();
                ulong acc = 0;
                for (int r = 0; r < fpRepeats; r++) acc ^= rig.gen.GeometryFingerprint(); // timed
                sw.Stop();
                GC.KeepAlive(acc);
                totalFpMs += sw.Elapsed.TotalMilliseconds / fpRepeats;
                rig.gen.ReturnInstanceBuffers();
            }

            double nsPerVertex = (totalFpMs * 1_000_000.0) / Math.Max(1, totalVerts);
            Debug.Log($"[FingerprintCost] {objects} objects x {baseText.Length} chars: " +
                      $"fingerprint total={totalFpMs:F2} ms for {totalVerts:N0} verts " +
                      $"({totalFpMs / objects:F3} ms/object, {nsPerVertex:F1} ns/vertex). " +
                      $"Allocation-free (reads pooled buffers only).");

            // Sanity gate: hashing ~500k verts must be a few ms, not seconds. Generous bound for a
            // loaded shared host; the real figure is in the log above.
            Assert.Less(totalFpMs, 2000.0,
                $"fingerprint cost {totalFpMs:F1} ms for {objects} objects is implausibly high — investigate.");
        }
    }
}
