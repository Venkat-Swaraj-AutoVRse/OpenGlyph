using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Pins the FIX in <see cref="UniText.DoApplyMesh"/>: the full-geometry fingerprint
    /// (<see cref="UniTextMeshGenerator.GeometryFingerprint"/>) is a pure cost whenever the
    /// geometry-identity re-upload skip is OFF (the default) — the hash is never compared, so
    /// computing it only adds a full extra pass over every vertex channel. PR #16 hashed every mesh
    /// on every upload even with the skip off; the fix guards the hash behind the setting.
    ///
    /// <para>These are COUNTER tests, not timing tests: they read the two internal instrumentation
    /// counters (<see cref="UniTextMeshGenerator.GeometryFingerprintCalls"/> and
    /// <see cref="UniText.MeshUploadCount"/>) across real component rebuilds driven through
    /// <see cref="Canvas.ForceUpdateCanvases"/>, so the result is deterministic and host-independent
    /// (the number of fingerprint calls does not depend on CPU speed).</para>
    ///
    /// <para><b>Fails without the fix.</b> On un-patched main, <c>DoApplyMesh</c> computes the
    /// fingerprint unconditionally, so <see cref="RebuildsWithSkipOff_DoNotComputeFingerprint"/>
    /// would see a non-zero call count and fail. With the fix it is exactly 0.</para>
    ///
    /// <para>Each rebuild is given DIFFERENT text (defeats OpenGlyph's text-keyed shaping/glyph
    /// cache, per the project's benchmark lesson) so every rebuild is real work that reaches the
    /// apply-mesh path.</para>
    /// </summary>
    internal sealed class FingerprintCostGateTests
    {
        private UniTextSettings _savedSettings;
        private bool _savedWasNull;

        [SetUp]
        public void SetUp()
        {
            _savedWasNull = UniTextSettings.IsNull;
            _savedSettings = _savedWasNull ? null : UniTextSettings.Instance;
            // Default is OFF; make the starting state explicit and independent of other tests.
            UniTextSettings.SetSkipUnchangedGeometryUploadForTests(false);
        }

        [TearDown]
        public void TearDown()
        {
            // Restore whatever skip state the shared settings instance had.
            UniTextSettings.SetSkipUnchangedGeometryUploadForTests(_savedSettings != null
                && !_savedWasNull && UniTextSettings.SkipUnchangedGeometryUpload);
        }

        // -----------------------------------------------------------------------------------------
        // Harness: one live UniText on a Canvas, rebuilt synchronously via ForceUpdateCanvases.
        // Mirrors GeometrySkipTests.GeometrySkip_PreservesRenderedOutput. Returns null (⇒ Ignore)
        // when the native font backend is unavailable on this host.
        // -----------------------------------------------------------------------------------------
        private sealed class Rig : System.IDisposable
        {
            public GameObject canvasGo;
            public UniText ut;
            private UniTextFontStack _stack;
            private UniTextFont _font;

            public static Rig Create()
            {
                string fontPath = MsdfTestUtil.FindNotoSansPath();
                if (fontPath == null) return null;
                var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 90, 0.10f, UniTextRenderMode.SDF, 1024);
                if (font == null) return null;
                var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
                stack.fonts.Add(font);
                var appearance = RealLayoutFixtures.LoadDefaultAppearance();

                var canvasGo = new GameObject("FpGateCanvas", typeof(Canvas));
                var ut = new GameObject("UT", typeof(RectTransform)).AddComponent<UniText>();
                ut.transform.SetParent(canvasGo.transform, false);
                ((RectTransform)ut.transform).sizeDelta = new Vector2(1200, 2000);
                ut.FontStack = stack;
                ut.Appearance = appearance;
                return new Rig { canvasGo = canvasGo, ut = ut, _stack = stack, _font = font };
            }

            /// <summary>Assigns <paramref name="text"/> and forces a synchronous rebuild (which runs
            /// the batch → DoApplyMesh for this component).</summary>
            public void Rebuild(string text)
            {
                ut.Text = text;
                Canvas.ForceUpdateCanvases();
            }

            public int GlyphCount => ut.ResultGlyphs.Length;

            public void Dispose()
            {
                if (canvasGo != null) Object.DestroyImmediate(canvasGo);
                if (_stack != null) Object.DestroyImmediate(_stack);
                if (_font != null) Object.DestroyImmediate(_font);
            }
        }

        // Distinct, non-trivial strings so each rebuild is real work (defeats the shaping/glyph cache).
        private static readonly string[] DistinctTexts =
        {
            "Score 0 — the quick brown fox jumps over the lazy dog A",
            "Score 1 — the quick brown fox jumps over the lazy dog BB",
            "Score 2 — the quick brown fox jumps over the lazy dog CCC",
            "Score 3 — the quick brown fox jumps over the lazy dog DDDD",
            "Score 4 — the quick brown fox jumps over the lazy dog EEEEE",
        };

        // -----------------------------------------------------------------------------------------
        // THE regression gate: skip OFF ⇒ the fingerprint must NOT be computed on any rebuild.
        // (Fails on un-patched main, where DoApplyMesh hashes unconditionally.)
        // -----------------------------------------------------------------------------------------
        [Test]
        public void RebuildsWithSkipOff_DoNotComputeFingerprint()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            // Warm-up build; confirm the host actually produces geometry (else the apply path is a
            // no-op and the test would be vacuous).
            rig.Rebuild(DistinctTexts[0]);
            if (rig.GlyphCount == 0) { Assert.Ignore("Font backend produced no glyphs on this host."); return; }

            UniTextSettings.SetSkipUnchangedGeometryUploadForTests(false);

            UniTextMeshGenerator.ResetGeometryFingerprintCalls();
            UniText.ResetMeshUploadCount();

            for (int i = 0; i < DistinctTexts.Length; i++)
                rig.Rebuild(DistinctTexts[i]);

            Assert.AreEqual(0L, UniTextMeshGenerator.GeometryFingerprintCalls,
                "with the geometry-skip OFF (the default), DoApplyMesh must NOT compute the full-mesh " +
                "fingerprint — it is never compared, so hashing every vertex channel is pure cost. " +
                "A non-zero count means the PR #16 unconditional hash regressed back in.");
            Assert.Greater(UniText.MeshUploadCount, 0L,
                "sanity: with distinct text each rebuild really uploaded a mesh (so 0 fingerprint " +
                "calls is a real skip of the hash, not a skip of the whole apply path).");
        }

        // -----------------------------------------------------------------------------------------
        // Skip ON ⇒ the fingerprint IS computed (the comparison/record cost is paid when it can pay off).
        // -----------------------------------------------------------------------------------------
        [Test]
        public void RebuildsWithSkipOn_DoComputeFingerprint()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            rig.Rebuild(DistinctTexts[0]);
            if (rig.GlyphCount == 0) { Assert.Ignore("Font backend produced no glyphs on this host."); return; }

            UniTextSettings.SetSkipUnchangedGeometryUploadForTests(true);

            UniTextMeshGenerator.ResetGeometryFingerprintCalls();

            for (int i = 0; i < DistinctTexts.Length; i++)
                rig.Rebuild(DistinctTexts[i]);

            Assert.Greater(UniTextMeshGenerator.GeometryFingerprintCalls, 0L,
                "with the geometry-skip ON, DoApplyMesh records (and, when applicable, compares) the " +
                "fingerprint, so the hash must run.");
        }

        // -----------------------------------------------------------------------------------------
        // Switching the skip ON after building with it OFF: the first ON build cannot skip (it has
        // nothing recorded — the OFF build left hasAppliedGeometry=false), so it uploads and records.
        // A SECOND identical rebuild then skips (no re-upload). Mirrors the fix's own comment.
        // -----------------------------------------------------------------------------------------
        [Test]
        public void SkipOnAfterOff_FirstRebuildRecords_SecondIdenticalRebuildSkips()
        {
            using var rig = Rig.Create();
            if (rig == null) { Assert.Ignore("Font backend unavailable."); return; }

            const string text = "Steady state — identical geometry on the next rebuild should be skipped.";

            // Build with skip OFF: records nothing (lastAppliedGeometryFingerprint=0, hasAppliedGeometry=false).
            UniTextSettings.SetSkipUnchangedGeometryUploadForTests(false);
            rig.Rebuild(text);
            if (rig.GlyphCount == 0) { Assert.Ignore("Font backend produced no glyphs on this host."); return; }

            // Now turn the skip ON.
            UniTextSettings.SetSkipUnchangedGeometryUploadForTests(true);

            // First ON rebuild of the SAME text: cannot skip (nothing was recorded under OFF), so it
            // must upload once and record the fingerprint.
            UniText.ResetMeshUploadCount();
            rig.Rebuild(text);
            Assert.AreEqual(1L, UniText.MeshUploadCount,
                "the first rebuild after enabling the skip must upload once and record the fingerprint " +
                "(the OFF build left nothing recorded, so there is nothing to skip against yet).");

            // Second identical rebuild: geometry is byte-identical and now recorded ⇒ the skip fires,
            // so NO new mesh upload happens.
            UniText.ResetMeshUploadCount();
            rig.Rebuild(text);
            Assert.AreEqual(0L, UniText.MeshUploadCount,
                "a second identical rebuild with the skip ON must take the geometry-identity skip — no " +
                "mesh re-upload — because the recorded fingerprint matches.");
        }
    }
}
