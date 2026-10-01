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
            // Set product/company up front so persistentDataPath is stable
            // (<LocalLow>/OpenGlyph/OpenGlyphBench) in editor AND player.
            PlayerSettings.productName = "OpenGlyphBench";
            PlayerSettings.companyName = "OpenGlyph";
            EnsureTmpEssentials();

            Directory.CreateDirectory(SceneDir);
            Directory.CreateDirectory(GenDir);
            Directory.CreateDirectory(FontDir);

            var fontStack = BuildFontStack();
            var appearance = AssetDatabase.LoadAssetAtPath<UniTextAppearance>(AppearancePkg);
            EnsureFontResources();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var runnerGo = new GameObject("BenchmarkRunner");
            var runner = runnerGo.AddComponent<BenchmarkRunner>();
            runner.openGlyphFonts = fontStack;
            runner.openGlyphAppearance = appearance;
            var tmpFont = ResolveTmpFont();
            runner.tmpSourceFont = tmpFont;
            runner.tmpFontAsset = BuildTmpFontAsset(tmpFont);
            runner.quitWhenDone = true;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[BenchBuild] scene built: " + ScenePath);
        }

        // Build (once) a dynamic TMP font asset from the Noto Sans TTF so TMP has a
        // real, build-included font asset at runtime (fixes player TMP_Settings/font NRE).
        private static TMPro.TMP_FontAsset BuildTmpFontAsset(Font font)
        {
            const string path = GenDir + "/BenchTMP_NotoSans.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(path);
            if (existing != null) return existing;
            if (font == null) { Debug.LogWarning("[BenchBuild] no Font for TMP asset"); return null; }

            // TMP_FontAsset.CreateFontAsset dereferences TMP_Settings.instance; make sure
            // it is loaded (the essentials import does not populate the singleton in a
            // batchmode session). If it still cannot load, skip asset creation here and
            // let the runner build one at runtime (CreateFontAsset(Font) resolves settings then).
            var settings = Resources.Load<TMPro.TMP_Settings>("TMP Settings");
            if (settings == null)
            {
                Debug.LogWarning("[BenchBuild] TMP_Settings not loadable at build time — skipping prebuilt TMP font asset; runner will build one at runtime.");
                return null;
            }
            TMPro.TMP_Settings.LoadDefaultSettings();

            TMPro.TMP_FontAsset fa;
            try
            {
                fa = TMPro.TMP_FontAsset.CreateFontAsset(font, 90, 9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024,
                    TMPro.AtlasPopulationMode.Dynamic, true);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[BenchBuild] CreateFontAsset threw (" + ex.Message + ") — runner builds TMP font at runtime.");
                return null;
            }
            if (fa == null) { Debug.LogWarning("[BenchBuild] CreateFontAsset returned null"); return null; }
            fa.name = "BenchTMP_NotoSans";
            AssetDatabase.CreateAsset(fa, path);
            if (fa.atlasTexture != null) { fa.atlasTexture.name = "BenchTMP_Atlas"; AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa); }
            if (fa.material != null) { fa.material.name = "BenchTMP_Mat"; AssetDatabase.AddObjectToAsset(fa.material, fa); }
            AssetDatabase.SaveAssets();
            Debug.Log("[BenchBuild] built TMP font asset: " + path);
            return fa;
        }

        // Copy the Noto TTFs into Assets/Benchmarks/Resources/Fonts/*.bytes so the
        // runner can Resources.Load<TextAsset> them on any platform (Android StreamingAssets
        // lives inside the APK and is not File-readable).
        private static void EnsureFontResources()
        {
            const string resFontDir = "Assets/Benchmarks/Resources/Fonts";
            Directory.CreateDirectory(resFontDir);
            foreach (var f in new[] { "NotoSans-Regular.ttf", "NotoSansArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf" })
            {
                string src = Path.Combine(Application.streamingAssetsPath, "Fonts", f);
                string dst = resFontDir + "/" + Path.GetFileNameWithoutExtension(f) + ".bytes";
                if (File.Exists(src) && !File.Exists(dst))
                {
                    File.Copy(src, dst, true);
                    AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
                }
            }
            AssetDatabase.SaveAssets();
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
            // StreamingAssets TTFs are copied verbatim (NOT imported as Font assets),
            // so copy the Noto Sans TTF into Assets so Unity imports it as a Font.
            const string dst = GenDir + "/Fonts/NotoSans-Regular.ttf";
            var font = AssetDatabase.LoadAssetAtPath<Font>(dst);
            if (font == null)
            {
                string src = Path.Combine(Application.streamingAssetsPath, "Fonts", "NotoSans-Regular.ttf");
                if (File.Exists(src))
                {
                    Directory.CreateDirectory(FontDir + "/Fonts");
                    File.Copy(src, dst, true);
                    AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
                    font = AssetDatabase.LoadAssetAtPath<Font>(dst);
                }
            }
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
            // Prefer IL2CPP Release; fall back to Mono if the IL2CPP module isn't
            // installed in this editor (Windows build-support IL2CPP variation absent).
            bool il2cpp = Il2CppAvailable(BuildTarget.StandaloneWindows64);
            if (il2cpp)
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Standalone, Il2CppCompilerConfiguration.Release);
                Debug.Log("[BenchBuild] Windows backend: IL2CPP (Release)");
            }
            else
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
                Debug.LogWarning("[BenchBuild] Windows IL2CPP module NOT installed — falling back to Mono (representative player, non-IL2CPP). Install 'Windows Build Support (IL2CPP)' via Unity Hub for an IL2CPP run.");
            }
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

        // True only if the IL2CPP player variation for this target is actually on disk.
        private static bool Il2CppAvailable(BuildTarget target)
        {
            try
            {
                string data = EditorApplication.applicationContentsPath;
                if (target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneWindows)
                {
                    string varDir = Path.Combine(data, "PlaybackEngines/windowsstandalonesupport/Variations");
                    if (!Directory.Exists(varDir)) return false;
                    foreach (var d in Directory.GetDirectories(varDir))
                        if (Path.GetFileName(d).ToLowerInvariant().Contains("il2cpp")) return true;
                    return false;
                }
                if (target == BuildTarget.Android)
                {
                    string il2 = Path.Combine(data, "PlaybackEngines/AndroidPlayer/Variations/il2cpp");
                    return Directory.Exists(il2);
                }
            }
            catch { }
            return false;
        }

        public static void BuildAndroid()
        {
            BuildScene();
            ConfigureCommon();
            bool il2cpp = Il2CppAvailable(BuildTarget.Android);
            if (il2cpp)
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);
                Debug.Log("[BenchBuild] Android backend: IL2CPP (Release)");
            }
            else
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.Mono2x);
                Debug.LogWarning("[BenchBuild] Android IL2CPP module NOT installed — falling back to Mono.");
            }
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

        // Import TMP Essential Resources non-interactively so no modal window opens
        // in batchmode. Idempotent: skips if the essentials settings asset exists.
        private static void EnsureTmpEssentials()
        {
            try
            {
                if (File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
                    return;
                string pkg = Path.Combine(
                    EditorApplication.applicationContentsPath,
                    "Resources/PackageManager/BuiltInPackages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage");
                if (File.Exists(pkg))
                {
                    AssetDatabase.ImportPackage(pkg, false); // interactive=false
                    AssetDatabase.Refresh();
                    Debug.Log("[BenchBuild] imported TMP Essential Resources (non-interactive)");
                }
                else Debug.LogWarning("[BenchBuild] TMP essentials unitypackage not found: " + pkg);
            }
            catch (System.Exception ex) { Debug.LogWarning("[BenchBuild] TMP essentials import skipped: " + ex.Message); }
        }

        // ---------------- guarded batchmode entry points (always Exit) ----------------
        public static void BuildWindowsCI() => Guard(BuildWindows);
        public static void BuildAndroidCI() => Guard(BuildAndroid);
        public static void BuildSceneCI()   => Guard(BuildScene);

        private static void Guard(System.Action body)
        {
            try { body(); EditorApplication.Exit(0); }
            catch (System.Exception ex)
            {
                Debug.LogError("[BenchBuild] FAILED: " + ex);
                EditorApplication.Exit(3);
            }
        }
    }
}
