// SPDX-License-Identifier: MIT
// Editor automation for the OpenGlyph benchmark project. Invoked via:
//   Unity -batchmode -projectPath <proj> -executeMethod OpenGlyph.Benchmarks.Editor.BenchBuild.<Method> -quit
// Methods:
//   BuildScene   : create font stack + benchmark scene (idempotent).
//   EditorSmoke  : build scene, enter play mode headless, run benchmark, write JSON (NOT representative).
//   BuildWindows : build scene, then StandaloneWindows64 IL2CPP Release player.
//   BuildAndroid : build scene, then Android IL2CPP Release APK.
using System.IO;
using LightSide;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpenGlyph.Benchmarks.Editor
{
    public static class BenchBuild
    {
        private const string SceneDir = "Assets/Benchmarks/Scenes";
        private const string ScenePath = SceneDir + "/Benchmark.unity";
        private const string GenDir = "Assets/Benchmarks/Generated";
        private const string FontStackPath = GenDir + "/BenchFontStack.asset";
        private const string FontDir = GenDir + "/Fonts";
        private const string AppearancePkg = "Packages/com.openglyph.text/Defaults/UniTextAppearance_Default.asset";
        private const string FontStackPkg = "Packages/com.openglyph.text/Defaults/UniTextFonts_Default.asset";

        [MenuItem("OpenGlyph/Build Benchmark Scene")]
        public static void BuildScene()
        {
            Directory.CreateDirectory(SceneDir);
            Directory.CreateDirectory(GenDir);
            Directory.CreateDirectory(FontDir);

            var fontStack = BuildFontStack();
            var appearance = AssetDatabase.LoadAssetAtPath<UniTextAppearance>(AppearancePkg);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var runnerGo = new GameObject("BenchmarkRunner");
            var runner = runnerGo.AddComponent<BenchmarkRunner>();
            runner.openGlyphFonts = fontStack;
            runner.openGlyphAppearance = appearance;
            runner.tmpSourceFont = ResolveTmpFont();
            runner.quitWhenDone = true;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[BenchBuild] scene built: " + ScenePath);
        }

        private static UniTextFontStack BuildFontStack()
        {
            var pkgStack = AssetDatabase.LoadAssetAtPath<UniTextFontStack>(FontStackPkg);
            if (pkgStack != null && pkgStack.fonts != null && pkgStack.fonts.Count > 0)
            {
                Debug.Log("[BenchBuild] using package default font stack");
                return pkgStack;
            }

            Debug.Log("[BenchBuild] constructing font stack from TTF bytes");
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            foreach (var f in new[] { "NotoSans-Regular.ttf", "NotoSansArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf" })
            {
                string ttf = Path.Combine(Application.streamingAssetsPath, "Fonts", f);
                if (!File.Exists(ttf)) { Debug.LogWarning("[BenchBuild] missing " + ttf); continue; }
                var bytes = File.ReadAllBytes(ttf);
                var font = UniTextFont.CreateFontAsset(bytes, 90, 0.25f, UniTextRenderMode.SDF, 1024);
                if (font == null) { Debug.LogWarning("[BenchBuild] CreateFontAsset failed for " + f); continue; }
                string assetPath = FontDir + "/" + Path.GetFileNameWithoutExtension(f) + ".asset";
                AssetDatabase.CreateAsset(font, assetPath);
                stack.fonts.Add(font);
            }
            AssetDatabase.CreateAsset(stack, FontStackPath);
            AssetDatabase.SaveAssets();
            return stack;
        }

        private static Font ResolveTmpFont()
        {
            string path = "Assets/StreamingAssets/Fonts/NotoSans-Regular.ttf";
            var font = AssetDatabase.LoadAssetAtPath<Font>(path);
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font;
        }

        public static void EditorSmoke()
        {
            BuildScene();
            EditorSceneManager.OpenScene(ScenePath);
            Debug.Log("[BenchBuild] EditorSmoke: entering play mode (results => persistentDataPath)");
            EditorApplication.EnterPlaymode();
        }

        public static void BuildWindows()
        {
            BuildScene();
            ConfigureCommon();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Standalone, Il2CppCompilerConfiguration.Release);
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);

            string outDir = AbsOut("Windows");
            Directory.CreateDirectory(outDir);
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(outDir, "OpenGlyphBench.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };
            LogReport("Windows", BuildPipeline.BuildPlayer(opts));
        }

        public static void BuildAndroid()
        {
            BuildScene();
            ConfigureCommon();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            string outDir = AbsOut("Android");
            Directory.CreateDirectory(outDir);
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(outDir, "OpenGlyphBench.apk"),
                target = BuildTarget.Android,
                options = BuildOptions.None
            };
            LogReport("Android", BuildPipeline.BuildPlayer(opts));
        }

        private static void ConfigureCommon()
        {
            PlayerSettings.productName = "OpenGlyphBench";
            PlayerSettings.companyName = "OpenGlyph";
            PlayerSettings.SplashScreen.showUnityLogo = false;
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultScreenWidth = 640;
            PlayerSettings.defaultScreenHeight = 480;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.openglyph.bench");
        }

        private static string AbsOut(string platform)
        {
            string proj = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(proj, "Build", platform);
        }

        private static void LogReport(string name, BuildReport report)
        {
            var s = report.summary;
            Debug.Log($"[BenchBuild] {name} build result={s.result} size={s.totalSize} bytes out={s.outputPath} errors={s.totalErrors}");
            if (s.result != BuildResult.Succeeded)
            {
                foreach (var step in report.steps)
                    foreach (var msg in step.messages)
                        if (msg.type == LogType.Error || msg.type == LogType.Exception)
                            Debug.LogError($"[BenchBuild] {name}: {msg.content}");
                EditorApplication.Exit(2);
            }
        }
    }
}
