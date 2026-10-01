using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 3: the editor migration tool core (no UI). Converts a
    /// <see cref="UniText"/> component's legacy appearance/material settings into a component
    /// <see cref="UniTextStyle"/> (via the <see cref="AppearanceStyleShim"/> mapping) and switches the
    /// component to the unified path. Prefabs are walked with <see cref="AssetDatabase"/>; open/selected
    /// scenes are walked in place. Supports a dry-run report, Undo + prefab modification recording, and
    /// is idempotent (a component already migrated is skipped).
    /// </summary>
    public static class AppearanceMigration
    {
        /// <summary>Options controlling what the migration does.</summary>
        public struct Options
        {
            /// <summary>Force the component onto the unified renderer (ForceOn). Default true.</summary>
            public bool switchToUnified;
            /// <summary>Clear the legacy appearance reference after converting. Default FALSE during the
            /// deprecation window so old assets still load/render through the shim fallback until removal.</summary>
            public bool clearAppearance;

            public static Options Default => new() { switchToUnified = true, clearAppearance = false };
        }

        /// <summary>One component's migration outcome, for the dry-run report.</summary>
        public struct Entry
        {
            public string objectPath;     // scene path or prefab asset path + hierarchy
            public bool wouldMigrate;      // true = convertible and not already migrated
            public bool alreadyMigrated;   // idempotence: already on a component style
            public string note;            // e.g. "no appearance" / "default style"
        }

        /// <summary>Aggregate dry-run / apply result.</summary>
        public sealed class Report
        {
            public int scanned;
            public int migrated;           // (apply) or wouldMigrate count (dry run)
            public int alreadyMigrated;
            public int skippedNoAppearance;
            public readonly List<Entry> entries = new();

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"UniText appearance→style migration: scanned {scanned}, " +
                              $"to migrate {migrated}, already migrated {alreadyMigrated}, " +
                              $"no appearance {skippedNoAppearance}.");
                foreach (var e in entries)
                {
                    string tag = e.alreadyMigrated ? "SKIP(done)" : e.wouldMigrate ? "MIGRATE" : "SKIP";
                    sb.AppendLine($"  [{tag}] {e.objectPath}{(string.IsNullOrEmpty(e.note) ? "" : "  — " + e.note)}");
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Converts ONE component in memory. Idempotent: returns false (reason="already migrated") if
        /// the component already uses its own style. Pure data change; the CALLER records Undo / prefab
        /// modifications. Returns true and sets the component's <c>Style</c>/<c>OverrideStyle</c> on success.
        /// </summary>
        public static bool TryMigrateComponent(UniText t, Options opt, out string reason)
        {
            reason = null;
            if (t == null) { reason = "null"; return false; }
            if (t.OverrideStyle) { reason = "already migrated"; return false; }

            var appearance = t.Appearance;
            var mainFont = t.FontStack != null ? t.FontStack.MainFont : null;
            // Faithful mapping: the same shim that keeps old assets rendering produces the style.
            // With a font we use its (possibly per-font) materials; without one we fall back to the
            // appearance's DEFAULT materials so a component that has not resolved a font still migrates.
            GlyphStyle glyphStyle;
            if (appearance == null)
                glyphStyle = GlyphStyle.Default;
            else if (mainFont != null)
                glyphStyle = AppearanceStyleShim.StyleFor(appearance, mainFont);
            else
                glyphStyle = AppearanceStyleShim.StyleFromMaterials(appearance.GetDefaultMaterials());

            t.Style = UniTextStyle.FromGlyphStyle(glyphStyle);
            t.OverrideStyle = true;
            if (opt.switchToUnified) t.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOn;
            if (opt.clearAppearance) t.Appearance = null;
            return true;
        }

        /// <summary>Reads a component's would-migrate entry for the dry-run report (no mutation).</summary>
        private static Entry Inspect(UniText t, string objectPath)
        {
            var e = new Entry { objectPath = objectPath };
            if (t.OverrideStyle) { e.alreadyMigrated = true; e.note = "component already uses a style"; return e; }
            e.wouldMigrate = true;
            if (t.Appearance == null) e.note = "no appearance → default style";
            return e;
        }

        /// <summary>
        /// Scans project prefabs (and optionally the open scenes) WITHOUT changing anything, returning a
        /// report of what would migrate. This is the dry-run the UI shows before applying.
        /// </summary>
        public static Report DryRun(bool includeOpenScenes)
        {
            var report = new Report();
            // Prefabs.
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<UniText>(true))
                {
                    report.scanned++;
                    var e = Inspect(t, $"{path} :: {GetHierarchyPath(t.transform)}");
                    Tally(report, e);
                }
            }
            // Open scenes.
            if (includeOpenScenes)
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var go in scene.GetRootGameObjects())
                        foreach (var t in go.GetComponentsInChildren<UniText>(true))
                        {
                            report.scanned++;
                            var e = Inspect(t, $"{scene.name} :: {GetHierarchyPath(t.transform)}");
                            Tally(report, e);
                        }
                }
            }
            return report;
        }

        private static void Tally(Report r, Entry e)
        {
            r.entries.Add(e);
            if (e.alreadyMigrated) r.alreadyMigrated++;
            else if (e.wouldMigrate) { r.migrated++; if (e.note != null && e.note.StartsWith("no appearance")) r.skippedNoAppearance++; }
        }

        /// <summary>
        /// Applies migration to all project prefabs (and optionally open scenes). Records Undo for scene
        /// objects and prefab-asset modifications so the operation is reversible, and is idempotent.
        /// Returns the apply report.
        /// </summary>
        public static Report Migrate(Options opt, bool includeOpenScenes)
        {
            var report = new Report();

            // Prefabs — load contents, migrate, SaveAsPrefabAsset (records the asset change).
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                if (root == null) continue;
                bool changed = false;
                foreach (var t in root.GetComponentsInChildren<UniText>(true))
                {
                    report.scanned++;
                    if (TryMigrateComponent(t, opt, out var reason))
                    {
                        EditorUtility.SetDirty(t);
                        report.migrated++;
                        changed = true;
                        report.entries.Add(new Entry { objectPath = $"{path} :: {GetHierarchyPath(t.transform)}", wouldMigrate = true });
                    }
                    else if (reason == "already migrated")
                    {
                        report.alreadyMigrated++;
                        report.entries.Add(new Entry { objectPath = $"{path} :: {GetHierarchyPath(t.transform)}", alreadyMigrated = true });
                    }
                }
                if (changed) PrefabUtility.SaveAsPrefabAsset(root, path);
                PrefabUtility.UnloadPrefabContents(root);
            }

            // Open scenes — Undo-recorded in-place.
            if (includeOpenScenes)
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    bool dirty = false;
                    foreach (var go in scene.GetRootGameObjects())
                        foreach (var t in go.GetComponentsInChildren<UniText>(true))
                        {
                            report.scanned++;
                            if (!t.OverrideStyle)
                            {
                                Undo.RecordObject(t, "Migrate UniText Appearance to Style");
                                if (TryMigrateComponent(t, opt, out _))
                                {
                                    EditorUtility.SetDirty(t);
                                    PrefabUtility.RecordPrefabInstancePropertyModifications(t);
                                    report.migrated++;
                                    dirty = true;
                                    report.entries.Add(new Entry { objectPath = $"{scene.name} :: {GetHierarchyPath(t.transform)}", wouldMigrate = true });
                                }
                            }
                            else
                            {
                                report.alreadyMigrated++;
                                report.entries.Add(new Entry { objectPath = $"{scene.name} :: {GetHierarchyPath(t.transform)}", alreadyMigrated = true });
                            }
                        }
                    if (dirty) EditorSceneManager.MarkSceneDirty(scene);
                }
            }

            AssetDatabase.SaveAssets();
            return report;
        }

        private static string GetHierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
