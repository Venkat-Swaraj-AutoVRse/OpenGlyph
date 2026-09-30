#if UNITY_EDITOR
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LightSide.EditorTools
{
    /// <summary>
    /// Editor convenience wrapper around the canonical offline dictionary generator
    /// (<c>Tools~/DictGen</c>, an ICU-license-clean .NET tool). It shells out to
    /// <c>dotnet run</c> so the trie-building logic lives in exactly one place.
    /// </summary>
    /// <remarks>
    /// The generator reads the pinned ICU break-iterator dictionaries (thaidict.txt,
    /// laodict.txt, khmerdict.txt, burmesedict.txt from unicode-org/icu, tag
    /// release-74-2, Unicode License V3) and writes the OGSD binaries into
    /// <c>Resources/Segmentation</c>. Set the ICU input directory in the dialog.
    /// </remarks>
    internal static class SegmentationDictionaryBuilder
    {
        private const string IcuDirKey = "OpenGlyph.Segmentation.IcuDir";

        [MenuItem("Tools/OpenGlyph/Regenerate Segmentation Dictionaries…")]
        private static void Regenerate()
        {
            var icuDir = EditorPrefs.GetString(IcuDirKey, "");
            icuDir = EditorUtility.OpenFolderPanel(
                "Select ICU dictionary folder (thaidict.txt, laodict.txt, khmerdict.txt, burmesedict.txt)",
                string.IsNullOrEmpty(icuDir) ? Application.dataPath : icuDir, "");
            if (string.IsNullOrEmpty(icuDir))
                return;
            EditorPrefs.SetString(IcuDirKey, icuDir);

            var toolProj = FindDictGenProject();
            if (toolProj == null)
            {
                Debug.LogError("[OpenGlyph] Could not find Tools~/DictGen/DictGen.csproj. " +
                               "Run the generator manually: dotnet run --project Tools~/DictGen -- <icuDir> Resources/Segmentation");
                return;
            }

            var outDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(toolProj))),
                "Resources", "Segmentation");

            var psi = new ProcessStartInfo("dotnet",
                $"run -c Release --project \"{toolProj}\" -- \"{icuDir}\" \"{outDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode == 0)
            {
                Debug.Log("[OpenGlyph] Segmentation dictionaries regenerated:\n" + stdout);
                AssetDatabase.Refresh();
            }
            else
            {
                Debug.LogError($"[OpenGlyph] Generator failed (exit {p.ExitCode}):\n{stdout}\n{stderr}");
            }
        }

        private static string FindDictGenProject()
        {
            foreach (var guid in AssetDatabase.FindAssets("DictionaryTrie t:MonoScript"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var full = Path.GetFullPath(path);
                // Walk up to the package root (contains package.json), then Tools~/DictGen.
                var dir = Path.GetDirectoryName(full);
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir, "package.json")))
                    {
                        var proj = Path.Combine(dir, "Tools~", "DictGen", "DictGen.csproj");
                        if (File.Exists(proj)) return proj;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return null;
        }
    }
}
#endif
