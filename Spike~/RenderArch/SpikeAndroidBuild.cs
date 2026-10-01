// THROWAWAY SPIKE — Android/GLES3 build check. NOT production.
// Builds a minimal APK with the uber-shader + a runtime bootstrap, forcing the
// shader to compile for the Android GLES3/Vulkan targets. Proves the
// Texture2DArray + StructuredBuffer path BUILDS for Android. Always exits.
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class SpikeAndroidBuild
{
    public static void Build()
    {
        int code = 0;
        try
        {
            var log = Path.Combine(Directory.GetCurrentDirectory(), "android_build_result.txt");
            var sb = new System.Text.StringBuilder();
            void L(string s){ sb.Append(s).Append('\n'); Debug.Log("[APK] " + s); }

            L("=== OpenGlyph spike Android build ===");
            // Force GLES3 (primary mobile target in the design) + Vulkan as a secondary.
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[]{ UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3,
                       UnityEngine.Rendering.GraphicsDeviceType.Vulkan });
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.openglyph.spike");

            L("GLES3 set; scripting=IL2CPP arm64");

            // Minimal scene with a bootstrap MonoBehaviour that builds the mesh at runtime.
            var scenePath = "Assets/SpikeScene.unity";
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            var go = new GameObject("Bootstrap");
            go.AddComponent<SpikeBootstrap>();
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);

            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
            Directory.CreateDirectory(outDir);
            var apk = Path.Combine(outDir, "ogspike.apk");

            var opts = new BuildPlayerOptions
            {
                scenes = new[]{ scenePath },
                locationPathName = apk,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(opts);
            var sum = report.summary;
            L("Build result: " + sum.result + "  size=" + sum.totalSize + " bytes  errors=" + sum.totalErrors);
            L("APK exists: " + File.Exists(apk) + "  path=" + apk);
            File.WriteAllText(log, sb.ToString());
            code = (sum.result == BuildResult.Succeeded && File.Exists(apk)) ? 0 : 4;
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(),"android_build_result.txt"),
                "EXCEPTION: " + e); } catch {}
            code = 5;
        }
        EditorApplication.Exit(code);
    }
}
