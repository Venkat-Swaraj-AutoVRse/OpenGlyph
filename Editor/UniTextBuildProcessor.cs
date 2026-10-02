using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using LightSide;


namespace LightSide
{
    [InitializeOnLoad]
    internal class UniTextBuildProcessor : IPreprocessBuildWithReport, IActiveBuildTargetChanged
    {
        public int callbackOrder => -100;

        static UniTextBuildProcessor()
        {
            Cat.Meow($"[UniText] InitializeOnLoad, target: {EditorUserBuildSettings.activeBuildTarget}");

            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
            {
                ValidateWebGLSettings();
            }
        }

        public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget)
        {
            Cat.Meow($"[UniText] Build target changed: {previousTarget} → {newTarget}");
            if (newTarget == BuildTarget.WebGL)
            {
                ValidateWebGLSettings();
            }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            Cat.Meow($"[UniText] OnPreprocessBuild, platform: {report.summary.platformGroup}");

            EnsureUberShaderIncluded();

            if (report.summary.platformGroup == BuildTargetGroup.WebGL)
            {
                ValidateWebGLSettings();
            }
        }

        // The UniText/Uber shader is instantiated at runtime via Shader.Find (no material asset
        // references it), so Unity's build-time shader stripping would drop it from the player and
        // the unified renderer would silently fall back to UI/Default. Pin it into GraphicsSettings'
        // Always-Included Shaders so it ships in every build. Idempotent.
        private static void EnsureUberShaderIncluded()
        {
            var uber = Shader.Find("UniText/Uber");
            if (uber == null)
            {
                Cat.MeowWarnFormat("[UniText] UniText/Uber not found at build time; unified renderer will be unavailable in the player.");
                return;
            }

            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            if (arr == null) return;

            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == uber)
                {
                    Cat.Meow("[UniText] UniText/Uber already in Always-Included Shaders.");
                    return;
                }

            int idx = arr.arraySize;
            arr.InsertArrayElementAtIndex(idx);
            arr.GetArrayElementAtIndex(idx).objectReferenceValue = uber;
            so.ApplyModifiedProperties();
            Cat.Meow("[UniText] Added UniText/Uber to Always-Included Shaders for the build.");
        }

        private static void ValidateWebGLSettings()
        {
            var colorSpace = PlayerSettings.colorSpace;
            var isAutoAPI = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.WebGL);
            var graphicsAPIs = isAutoAPI ? System.Array.Empty<GraphicsDeviceType>() : PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL);

            Cat.Meow($"[UniText] ValidateWebGLSettings: colorSpace={colorSpace}, autoAPI={isAutoAPI}, APIs=[{string.Join(", ", graphicsAPIs)}]");

            if (colorSpace != ColorSpace.Linear)
                return;

            if (isAutoAPI)
            {
                Cat.Meow("[UniText] Disabling Auto Graphics API for WebGL");
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
                graphicsAPIs = PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL);
                Cat.Meow($"[UniText] After disabling auto: APIs=[{string.Join(", ", graphicsAPIs)}]");
            }

            if (!graphicsAPIs.Contains(GraphicsDeviceType.OpenGLES2))
            {
                Cat.Meow("[UniText] No WebGL 1.0 found, settings OK");
                return;
            }

            var newAPIs = graphicsAPIs
                .Where(api => api != GraphicsDeviceType.OpenGLES2)
                .DefaultIfEmpty(GraphicsDeviceType.OpenGLES3)
                .Distinct()
                .ToArray();

            Cat.Meow($"[UniText] Switching to WebGL 2.0: [{string.Join(", ", newAPIs)}]");
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, newAPIs);
        }
    }

}
