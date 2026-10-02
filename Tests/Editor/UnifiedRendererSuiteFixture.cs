using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2: an opt-in assembly-wide fixture that forces the project default
    /// <see cref="UniTextSettings.UseUnifiedRenderer"/> ON for the ENTIRE EditMode suite when the
    /// environment variable <c>UNITEXT_FORCE_UNIFIED=1</c> is set. With the variable unset (normal
    /// CI / local runs) it does nothing, so the default suite exercises the legacy path. Running the
    /// suite with the variable set verifies the unified path against every existing text test at once
    /// (and surfaces any test that asserts legacy per-segment-renderer structure by design).
    /// </summary>
    [SetUpFixture]
    public class UnifiedRendererSuiteFixture
    {
        private bool _applied;

        [OneTimeSetUp]
        public void ForceUnifiedIfRequested()
        {
            var v = System.Environment.GetEnvironmentVariable("UNITEXT_FORCE_UNIFIED");
            if (v == "1" || string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase))
            {
                UniTextSettings.SetUseUnifiedRendererForTests(true);
                _applied = UniTextSettings.UseUnifiedRenderer;
                Debug.Log($"[UnifiedRendererSuiteFixture] forced UseUnifiedRenderer ON (effective={_applied}).");
            }
        }

        [OneTimeTearDown]
        public void Restore()
        {
            if (_applied) UniTextSettings.SetUseUnifiedRendererForTests(false);
        }
    }
}
