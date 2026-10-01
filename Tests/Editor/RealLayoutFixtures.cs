using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Shared setup for the real-layout and engine-render tests: locating the bundled Thai
    /// font fixture, loading the package's default appearance (for materials), and building a
    /// real <see cref="TextProcessor"/> wired to a real <see cref="UniTextFontProvider"/> — the
    /// same pipeline objects the <see cref="UniText"/> component constructs internally.
    /// </summary>
    internal static class RealLayoutFixtures
    {
        /// <summary>
        /// Locates <c>NotoSansThai-Regular.ttf</c> under the package's test Fixtures folder,
        /// probing the Packages virtual path and then walking the project tree.
        /// </summary>
        public static string FindThaiFontPath() => FindFixtureFont("NotoSansThai-Regular.ttf");

        /// <summary>
        /// Finds the bundled font fixture for a complex script (Khmer: NotoSansKhmer-Regular.ttf,
        /// Myanmar: NotoSansMyanmar-Regular.ttf, both OFL). Returns the path and the script's
        /// reference sentences, or null if the fixture is missing.
        /// </summary>
        public static string FindComplexScriptFont(SegmentationScript script,
            out SegmentationFixtures.Case[] cases)
        {
            switch (script)
            {
                case SegmentationScript.Khmer:
                    cases = SegmentationFixtures.Khmer;
                    return FindFixtureFont("NotoSansKhmer-Regular.ttf");
                case SegmentationScript.Myanmar:
                    cases = SegmentationFixtures.Myanmar;
                    return FindFixtureFont("NotoSansMyanmar-Regular.ttf");
                default:
                    cases = null;
                    return null;
            }
        }

        private static string FindFixtureFont(string fileName)
        {
            string[] candidates =
            {
                "Packages/com.openglyph.text/Tests/Editor/Fixtures/" + fileName,
                Path.Combine(Application.dataPath ?? "", "..", "Packages", "com.openglyph.text",
                    "Tests", "Editor", "Fixtures", fileName),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            string root = Path.GetFullPath(Path.Combine(Application.dataPath ?? ".", ".."));
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                    if (f.Replace('\\', '/').Contains("/Fixtures/"))
                        return f;
            }
            catch { /* ignore */ }
            return null;
        }

        /// <summary>
        /// Loads the package's default <see cref="UniTextAppearance"/> (for mesh materials), or
        /// an in-memory instance if the asset cannot be located. Appearance affects only the
        /// mesh/material stage, not line breaking or layout.
        /// </summary>
        public static UniTextAppearance LoadDefaultAppearance()
        {
#if UNITY_EDITOR
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("UniTextAppearance_Default t:UniTextAppearance"))
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var a = UnityEditor.AssetDatabase.LoadAssetAtPath<UniTextAppearance>(path);
                if (a != null) return a;
            }
#endif
            return ScriptableObject.CreateInstance<UniTextAppearance>();
        }

        /// <summary>
        /// Builds a real <see cref="TextProcessor"/> + <see cref="UniTextFontProvider"/> over a
        /// freshly rented <see cref="UniTextBuffers"/>, exactly as <see cref="UniText"/> does in
        /// <c>ValidateAndInitialize</c>. The caller owns the returned buffers and must call
        /// <see cref="UniTextBuffers.EnsureReturnBuffers"/> when finished.
        /// </summary>
        public static (TextProcessor processor, UniTextBuffers buffers) BuildProcessor(
            UniTextFontStack stack, UniTextAppearance appearance)
        {
            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(256);
            var processor = new TextProcessor(buffers);
            var fontProvider = new UniTextFontProvider(stack, appearance);
            processor.SetFontProvider(fontProvider);
            return (processor, buffers);
        }

        /// <summary>Absolute path to the engine-render PNG output under the project root.</summary>
        public static string RenderOutputPath(string fileName)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath ?? ".", ".."));
            string dir = Path.Combine(root, "TestResults", "SegmentationRender");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
    }
}
