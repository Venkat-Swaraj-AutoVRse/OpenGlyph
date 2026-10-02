using UnityEditor;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 3: the <c>Tools/OpenGlyph/Migrate Appearance to Styles</c>
    /// menu + window. Shows a DRY-RUN report (counts, per-object list, anything it can't convert) and
    /// only mutates on an explicit "Migrate" click, so the user previews before committing. The core
    /// conversion, Undo, prefab recording and idempotence live in <see cref="AppearanceMigration"/>.
    /// </summary>
    public sealed class AppearanceMigrationWindow : EditorWindow
    {
        private AppearanceMigration.Report _report;
        private Vector2 _scroll;
        private bool _includeScenes = true;
        private bool _switchToUnified = true;
        private bool _clearAppearance = false;

        [MenuItem("Tools/OpenGlyph/Migrate Appearance to Styles")]
        public static void Open()
        {
            var w = GetWindow<AppearanceMigrationWindow>(true, "Migrate Appearance → Styles");
            w.minSize = new Vector2(520, 360);
            w._report = AppearanceMigration.DryRun(w._includeScenes);
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Converts legacy UniTextAppearance / material settings on UniText components to " +
                "component styles (UniTextStyle) and switches them to the unified renderer. Idempotent: " +
                "already-migrated components are skipped. Undo-able for scene objects; prefab assets are " +
                "modified in place. Old assets keep rendering through the deprecation shim until removal.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                _includeScenes = EditorGUILayout.ToggleLeft("Include open scenes", _includeScenes);
                if (GUILayout.Button("Re-scan (dry run)", GUILayout.Width(140)))
                    _report = AppearanceMigration.DryRun(_includeScenes);
            }
            _switchToUnified = EditorGUILayout.ToggleLeft("Switch migrated components to the unified renderer", _switchToUnified);
            _clearAppearance = EditorGUILayout.ToggleLeft("Clear the legacy appearance reference (NOT recommended during deprecation)", _clearAppearance);

            if (_report != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(
                    $"Scanned {_report.scanned} · to migrate {_report.migrated} · already migrated {_report.alreadyMigrated} · no appearance {_report.skippedNoAppearance}",
                    EditorStyles.boldLabel);

                _scroll = EditorGUILayout.BeginScrollView(_scroll, "box", GUILayout.MinHeight(180));
                foreach (var e in _report.entries)
                {
                    string tag = e.alreadyMigrated ? "SKIP(done)" : e.wouldMigrate ? "MIGRATE" : "SKIP";
                    EditorGUILayout.LabelField($"[{tag}] {e.objectPath}{(string.IsNullOrEmpty(e.note) ? "" : "  — " + e.note)}");
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_report == null || _report.migrated == 0))
            {
                if (GUILayout.Button($"Migrate {_report?.migrated ?? 0} component(s)", GUILayout.Height(30)))
                {
                    var opt = new AppearanceMigration.Options { switchToUnified = _switchToUnified, clearAppearance = _clearAppearance };
                    var applied = AppearanceMigration.Migrate(opt, _includeScenes);
                    Debug.Log("[UniText] " + applied);
                    _report = AppearanceMigration.DryRun(_includeScenes); // refresh (should now show all SKIP(done))
                }
            }
        }
    }

    /// <summary>
    /// Render-Architecture R2 sub-task 3: the OPT-IN auto-migrate-on-import hook. Off by default
    /// (<see cref="UniTextSettings.AutoMigrateAppearanceOnImport"/>); when the project setting is ON,
    /// imported prefabs/scenes are migrated. Guarded so it never runs unless explicitly enabled.
    /// </summary>
    public sealed class AppearanceMigrationImporter : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!UniTextSettings.AutoMigrateAppearanceOnImport) return;      // OFF by default

            bool anyRelevant = false;
            foreach (var p in imported)
                if (p.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase)) { anyRelevant = true; break; }
            if (!anyRelevant) return;

            // Migrate prefabs only here (scenes are migrated via the menu); scene imports can trigger
            // re-entrant saves, so we keep the auto path to the prefab asset flow.
            var opt = AppearanceMigration.Options.Default;
            var report = AppearanceMigration.Migrate(opt, includeOpenScenes: false);
            if (report.migrated > 0)
                Debug.Log($"[UniText] Auto-migrated {report.migrated} component(s) on import.");
        }
    }
}
