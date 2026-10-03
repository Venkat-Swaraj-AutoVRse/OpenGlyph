using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Assembly-wide fixture that pins the project default <see cref="UniTextSettings.UseUnifiedRenderer"/>
    /// for the ENTIRE EditMode suite so that BOTH renderer paths stay covered as two distinct runs.
    /// The shipped project default is now ON (unified), so:
    /// <list type="bullet">
    /// <item><c>UNITEXT_FORCE_UNIFIED=1</c> pins the default ON (unified run).</item>
    /// <item><c>UNITEXT_FORCE_LEGACY=1</c> pins the default OFF (legacy run).</item>
    /// <item>neither set: the shipped project default is used (unified).</item>
    /// </list>
    /// If both are set, LEGACY is rejected as ambiguous and UNIFIED wins (a warning is logged).
    /// Test host scripts: <c>-ForceUnified</c> sets UNITEXT_FORCE_UNIFIED, <c>-ForceLegacy</c> sets UNITEXT_FORCE_LEGACY.
    /// </summary>
    [SetUpFixture]
    public class UnifiedRendererSuiteFixture
    {
        private bool _applied;
        private bool _previous;

        private static bool IsSet(string name)
        {
            var v = System.Environment.GetEnvironmentVariable(name);
            return v == "1" || string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
        }

        [OneTimeSetUp]
        public void PinRendererModeIfRequested()
        {
            // The unified merged meshes are process-wide (shared like legacy SharedMeshes); tests read
            // a component's merged geometry back after other components have built, so keep a private
            // per-component copy for the whole suite. Allocation tests turn this off locally.
            UnifiedRenderBuilder.SnapshotMeshesForTests = true;

            bool unified = IsSet("UNITEXT_FORCE_UNIFIED");
            bool legacy = IsSet("UNITEXT_FORCE_LEGACY");
            if (unified && legacy)
            {
                Debug.LogWarning("[UnifiedRendererSuiteFixture] both UNITEXT_FORCE_UNIFIED and UNITEXT_FORCE_LEGACY set; using UNIFIED.");
                legacy = false;
            }
            if (!unified && !legacy) return;

            _previous = UniTextSettings.UseUnifiedRenderer;
            UniTextSettings.SetUseUnifiedRendererForTests(unified);
            _applied = true;
            Debug.Log($"[UnifiedRendererSuiteFixture] pinned UseUnifiedRenderer={UniTextSettings.UseUnifiedRenderer} ({(unified ? "unified" : "legacy")} run).");
        }

        [OneTimeTearDown]
        public void Restore()
        {
            UnifiedRenderBuilder.SnapshotMeshesForTests = false;
            if (_applied) UniTextSettings.SetUseUnifiedRendererForTests(_previous);
        }
    }
}
