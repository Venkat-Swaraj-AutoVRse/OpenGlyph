using System.IO;
using System.Linq;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression (QA B1/B2): UniText_Uber (the unified renderer) and the three Bitmap shaders had no
    /// stereo macros, so on Quest (single-pass instanced / multiview) their text would draw in one eye.
    /// Every vertex/fragment UI shader in the package must declare the instance id on its input, the
    /// stereo output on its v2f, and initialise both in the vertex shader. Surface shaders are skipped
    /// (Unity generates their stereo plumbing). A shader whose vertex program comes from a shared
    /// cginc is checked through that include.
    /// </summary>
    public class ShaderStereoLintTests
    {
        private static readonly string[] Required =
        {
            "UNITY_VERTEX_INPUT_INSTANCE_ID",
            "UNITY_VERTEX_OUTPUT_STEREO",
            "UNITY_SETUP_INSTANCE_ID",
            "UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO",
        };

        private static string ShadersDir()
        {
            var noto = MsdfTestUtil.FindNotoSansPath();
            // <pkg>/Defaults/NotoSans-Regular.ttf or <pkg>/Tests/Editor/Fixtures/... — walk up to the package root.
            var dir = new DirectoryInfo(Path.GetDirectoryName(noto ?? "."));
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Shaders"))) dir = dir.Parent;
            return dir == null ? null : Path.Combine(dir.FullName, "Shaders");
        }

        [Test]
        public void EveryVertexFragmentShader_DeclaresStereoMacros()
        {
            var shadersDir = ShadersDir();
            if (shadersDir == null) Assert.Ignore("package Shaders folder not found.");

            var missing = new System.Collections.Generic.List<string>();
            foreach (var path in Directory.GetFiles(shadersDir, "*.shader"))
            {
                var src = File.ReadAllText(path);
                if (src.Contains("#pragma surface")) continue;          // Unity generates stereo for these
                if (!src.Contains("#pragma vertex")) continue;          // UsePass / composite shaders

                // Include the package cgincs the shader pulls in (shared vertex programs live there).
                var full = src;
                foreach (var inc in Directory.GetFiles(shadersDir, "*.cginc"))
                    if (src.Contains(Path.GetFileName(inc))) full += File.ReadAllText(inc);

                var absent = Required.Where(m => !full.Contains(m)).ToArray();
                if (absent.Length > 0) missing.Add($"{Path.GetFileName(path)}: {string.Join(", ", absent)}");
            }

            Assert.IsEmpty(missing, "shaders missing stereo macros (text renders in one eye on Quest):\n" +
                                    string.Join("\n", missing));
        }
    }
}
