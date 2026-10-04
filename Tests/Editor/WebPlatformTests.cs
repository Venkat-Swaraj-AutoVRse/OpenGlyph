using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace LightSide.Tests
{
    /// <summary>
    /// Web build target: WebGL 2 and WebGPU. The build processor must never rewrite the user's web
    /// graphics API choice (WebGPU, alone or with a WebGL 2 fallback), and every package shader must
    /// compile for both web graphics APIs.
    /// </summary>
    public class WebPlatformTests
    {
        private static void Validate()
        {
            var type = System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("LightSide.UniTextBuildProcessor"))
                .FirstOrDefault(t => t != null);
            Assert.IsNotNull(type, "UniTextBuildProcessor not found");
            var m = type.GetMethod("ValidateWebGLSettings", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(m, "UniTextBuildProcessor.ValidateWebGLSettings not found");
            m.Invoke(null, null);
        }

#if UNITY_2023_3_OR_NEWER
        private static IEnumerable<GraphicsDeviceType[]> ExplicitApiLists()
        {
            yield return new[] { GraphicsDeviceType.WebGPU };
            yield return new[] { GraphicsDeviceType.WebGPU, GraphicsDeviceType.OpenGLES3 };
            yield return new[] { GraphicsDeviceType.OpenGLES3, GraphicsDeviceType.WebGPU };
            yield return new[] { GraphicsDeviceType.OpenGLES3 };
        }

        [TestCaseSource(nameof(ExplicitApiLists))]
        public void BuildProcessor_KeepsExplicitWebGraphicsApis(GraphicsDeviceType[] apis)
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
                Assert.Ignore("Active target is Web: changing its graphics APIs would switch the editor device.");
            var savedAuto = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.WebGL);
            var savedApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL);
            try
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, apis);
                Validate();
                Assert.IsFalse(PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.WebGL));
                CollectionAssert.AreEqual(apis, PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL),
                    "the web graphics API list (and its fallback order) must be left as the user set it");
            }
            finally
            {
                PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, savedApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, savedAuto);
            }
        }
#endif

#if UNITY_2023_1_OR_NEWER
        /// <summary>
        /// WebGL 1 is gone since Unity 2023.1, so the processor has nothing to fix: it must not turn
        /// "Auto Graphics API" off behind the user's back (it used to on every editor load of a Linear
        /// web project, freezing the list and hiding WebGPU when Unity's defaults include it).
        /// </summary>
        [Test]
        public void BuildProcessor_LeavesAutoGraphicsApiOn()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
                Assert.Ignore("Active target is Web: changing its graphics APIs would switch the editor device.");
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
                Assert.Ignore("The legacy fix-up only ran for Linear colour space projects.");
            var savedAuto = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.WebGL);
            var savedApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL);
            try
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, true);
                Validate();
                Assert.IsTrue(PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.WebGL),
                    "Auto Graphics API for Web must stay on");
            }
            finally
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, savedApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, savedAuto);
            }
        }
#endif

        /// <summary>
        /// Compiles the player scripts for the Web target (UNITY_WEBGL, !UNITY_EDITOR), the code path no
        /// editor test otherwise sees. It once failed: FT.GetBitmapLeft/Top were compiled out on web while
        /// the coverage-bitmap renderers called them, and UniTextFont reused a local name in the web-only
        /// branch, so every Web (WebGL 2 and WebGPU) build stopped at script compilation.
        /// </summary>
        [Test]
        public void PlayerScripts_CompileForWeb()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                Assert.Ignore("Web build support is not installed in this editor.");
            var outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenGlyphWebScripts_" + System.Guid.NewGuid().ToString("N"));
            var errors = new StringBuilder();
            void OnLog(string msg, string stack, LogType type)
            {
                if ((type == LogType.Error || type == LogType.Exception) && msg.Contains("error CS")) errors.AppendLine(msg);
            }
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            Application.logMessageReceived += OnLog;
            try
            {
                var settings = new UnityEditor.Build.Player.ScriptCompilationSettings
                {
                    target = BuildTarget.WebGL,
                    group = BuildTargetGroup.WebGL,
                    options = UnityEditor.Build.Player.ScriptCompilationOptions.None,
                };
                var result = UnityEditor.Build.Player.PlayerBuildInterface.CompilePlayerScripts(settings, outDir);
                Assert.IsEmpty(errors.ToString(), "Web player script compile errors");
                Assert.IsTrue(result.assemblies.Any(a => a.StartsWith("LightSide.UniText")),
                    "LightSide.UniText was not compiled for Web: " + string.Join(", ", result.assemblies));
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                try { if (System.IO.Directory.Exists(outDir)) System.IO.Directory.Delete(outDir, true); } catch (System.IO.IOException) { }
            }
        }

        private static IEnumerable<ShaderCompilerPlatform> WebPlatforms()
        {
            yield return ShaderCompilerPlatform.GLES3x;
#if UNITY_2023_3_OR_NEWER
            yield return ShaderCompilerPlatform.WebGPU;
#endif
        }

        /// <summary>
        /// Compiles every pass of every package shader (no keywords, then each keyword on its own) for
        /// the web graphics APIs and fails on any compile error.
        /// </summary>
        [TestCaseSource(nameof(WebPlatforms))]
        public void EveryPackageShader_CompilesForWeb(ShaderCompilerPlatform platform)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                Assert.Ignore("Web build support is not installed in this editor.");
            var guids = AssetDatabase.FindAssets("t:Shader", new[] { "Packages/com.openglyph.text/Shaders" });
            Assert.IsNotEmpty(guids, "no package shaders found");
            var errors = new StringBuilder();
            int compiled = 0;
            foreach (var guid in guids)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                var data = ShaderUtil.GetShaderData(shader);
                var sets = new List<string[]> { new string[0] };
                sets.AddRange(shader.keywordSpace.keywordNames.Where(k => !k.StartsWith("STEREO_")).Select(k => new[] { k }));
                for (int s = 0; s < data.SerializedSubshaderCount; s++)
                {
                    var sub = data.GetSerializedSubshader(s);
                    for (int p = 0; p < sub.PassCount; p++)
                    {
                        var pass = sub.GetPass(p);
                        foreach (var keywords in sets)
                            foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                            {
                                var r = pass.CompileVariant(stage, keywords, platform, BuildTarget.WebGL);
                                compiled++;
                                var msgs = r.Messages.Where(m => m.severity == ShaderCompilerMessageSeverity.Error)
                                    .Select(m => m.message + " (line " + m.line + ")").ToArray();
                                if (!r.Success || msgs.Length > 0)
                                    errors.AppendLine($"{shader.name} subshader {s} pass {p} {stage} " +
                                                      $"[{string.Join(" ", keywords)}]: {string.Join("; ", msgs)}");
                            }
                    }
                }
            }
            Assert.IsEmpty(errors.ToString(), $"{platform}: shader compile errors ({compiled} variants)");
            Assert.Greater(compiled, 100, "expected the package shaders to produce many variants");
        }
    }
}
