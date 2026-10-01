using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Verifies every MSDF shader variant is present, supported, and compiles without errors.
    /// Uses ShaderUtil (editor-only) so a broken variant fails the suite rather than silently
    /// falling back at runtime.
    /// </summary>
    [TestFixture]
    public class MsdfShaderTests
    {
        // Every MSDF shader that must exist, by its declared "Shader \"...\"" name.
        private static readonly string[] MsdfShaderNames =
        {
            "UniText/MSDF",
            "UniText/MSDF-Base",
            "UniText/MSDF-Face",
            "UniText/MSDF SSD",
            "UniText/MSDF Overlay",
            "UniText/MSDF (Surface)",
            "UniText/Mobile/MSDF",
            "UniText/Mobile/MSDF-Base",
            "UniText/Mobile/MSDF-Face",
            "UniText/Mobile/MSDF SSD",
            "UniText/Mobile/MSDF Overlay",
            "UniText/Mobile/MSDF - Masking",
            "UniText/Mobile/MSDF - 2 Pass",
            "UniText/Mobile/MSDF (Surface)",
        };

        [Test]
        public void EveryMsdfShader_IsFound_Supported_AndErrorFree()
        {
            var failures = new List<string>();

            foreach (var name in MsdfShaderNames)
            {
                var shader = Shader.Find(name);
                if (shader == null)
                {
                    failures.Add($"MISSING: '{name}' (Shader.Find returned null)");
                    continue;
                }
                if (ShaderUtil.ShaderHasError(shader))
                {
                    var msgs = ShaderUtil.GetShaderMessages(shader);
                    var sb = new System.Text.StringBuilder();
                    foreach (var m in msgs)
                        if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                            sb.Append($"\n    [{m.platform}] {m.message} ({m.file}:{m.line})");
                    failures.Add($"COMPILE ERROR: '{name}'{sb}");
                    continue;
                }
                if (!shader.isSupported)
                    failures.Add($"UNSUPPORTED: '{name}' (shader.isSupported == false)");
            }

            Assert.IsEmpty(failures,
                "MSDF shader validation failed:\n" + string.Join("\n", failures));
        }

        [Test]
        public void EverySdfShader_StillCompiles_NoRegression()
        {
            // The MSDF work touched shared cginc files (macro + median). Confirm the original SDF
            // shaders still compile and are supported.
            string[] sdf =
            {
                "UniText/SDF", "UniText/SDF-Base", "UniText/SDF-Face", "UniText/SDF SSD",
                "UniText/SDF Overlay", "UniText/SDF (Surface)",
                "UniText/Mobile/SDF", "UniText/Mobile/SDF-Base", "UniText/Mobile/SDF-Face",
                "UniText/Mobile/SDF SSD", "UniText/Mobile/SDF Overlay",
                "UniText/Mobile/SDF - Masking", "UniText/Mobile/SDF - 2 Pass", "UniText/Mobile/SDF (Surface)",
            };
            var failures = new List<string>();
            foreach (var name in sdf)
            {
                var shader = Shader.Find(name);
                if (shader == null) { failures.Add($"MISSING SDF: '{name}'"); continue; }
                if (ShaderUtil.ShaderHasError(shader)) failures.Add($"SDF COMPILE ERROR: '{name}'");
            }
            Assert.IsEmpty(failures, "SDF regression:\n" + string.Join("\n", failures));
        }
    }
}
