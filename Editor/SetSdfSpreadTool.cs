using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Editor tool: set the SDF <c>spreadStrength</c> on every <see cref="UniTextFont"/> asset in the
    /// project and regenerate each atlas through the font's own regenerate path (clear the dynamic
    /// glyph/atlas data so it rebuilds at the new padding on next use — exactly what the font
    /// inspector's Apply button does).
    /// </summary>
    /// <remarks>
    /// <para>
    /// OpenGlyph atlases are generated at runtime from <c>fontData</c> + <c>spreadStrength</c>; the
    /// <c>.asset</c> serializes no glyph table or atlas texture (they are <c>[NonSerialized]</c>). So
    /// "regenerate" here means: write the new spread, then <see cref="UniTextFont.ClearDynamicData"/>
    /// to discard any stale in-memory glyphs/atlases so padding is re-derived from the new spread.
    /// </para>
    /// <para>
    /// The tool shows a DRY-RUN list first (font, path, current spread, new spread, render mode, and
    /// whether it will change), applies only on confirmation, records an <see cref="Undo"/> step per
    /// asset, and is idempotent — a second run over fonts already at the target reports "no change"
    /// and writes nothing. Spread is clamped to the serialized range [0.1, 1.0]. Distance-field
    /// (SDF/MSDF) fonts are affected; coverage/bitmap (Smooth/Mono) and color (emoji) fonts ignore
    /// spread, so they are listed as "n/a (mode)" and skipped.
    /// </para>
    /// </remarks>
    internal static class SetSdfSpreadTool
    {
        private const float DefaultTargetSpread = 0.1f;
        private const float Epsilon = 0.0001f;

        private struct FontRow
        {
            public UniTextFont font;
            public string path;
            public float currentSpread;
            public float newSpread;
            public UniTextRenderMode mode;
            public bool isDistanceField;
            public bool willChange;
        }

        [MenuItem("Tools/OpenGlyph/Set SDF Spread on All Fonts...", false, 120)]
        private static void SetSdfSpreadOnAllFonts()
        {
            // Target spread: default 0.10, but let the user pick before scanning so the dry-run shows
            // the real delta. EditorInputDialog is not built in, so use a small confirm chain:
            float target = PromptForSpread(DefaultTargetSpread);
            if (float.IsNaN(target))
                return; // cancelled

            target = Mathf.Clamp(target, 0.1f, 1f);

            var rows = ScanProject(target);
            if (rows.Count == 0)
            {
                EditorUtility.DisplayDialog("Set SDF Spread",
                    "No UniTextFont assets found in the project.", "OK");
                return;
            }

            // --- DRY RUN -------------------------------------------------------------------------
            var sb = new StringBuilder();
            int changing = 0, unchanged = 0, nonDf = 0;
            sb.AppendLine($"Target spreadStrength = {target:0.###}");
            sb.AppendLine($"Found {rows.Count} UniTextFont asset(s):");
            sb.AppendLine();
            foreach (var r in rows)
            {
                string state;
                if (!r.isDistanceField) { state = $"n/a ({r.mode}) — skipped"; nonDf++; }
                else if (r.willChange) { state = $"{r.currentSpread:0.###} -> {r.newSpread:0.###}"; changing++; }
                else { state = $"{r.currentSpread:0.###} (no change)"; unchanged++; }
                sb.AppendLine($"  {r.font.name,-32} [{state}]  {r.path}");
            }
            sb.AppendLine();
            sb.AppendLine($"Will change: {changing}   Already at target: {unchanged}   Non-SDF (skipped): {nonDf}");

            // Log the full dry-run (dialogs truncate long bodies).
            Debug.Log("[Set SDF Spread] DRY RUN\n" + sb);

            if (changing == 0)
            {
                EditorUtility.DisplayDialog("Set SDF Spread",
                    $"Nothing to do — all {rows.Count} SDF/MSDF font(s) are already at {target:0.###}.\n\n" +
                    "(Full list logged to the Console.)", "OK");
                return;
            }

            string summary =
                $"Target spreadStrength: {target:0.###}\n\n" +
                $"Will change: {changing} font(s)\n" +
                $"Already at target: {unchanged}\n" +
                $"Non-SDF (skipped): {nonDf}\n\n" +
                "The full per-font list is in the Console.\n\n" +
                "Apply now? Each change is undoable and the atlas regenerates at runtime.";

            if (!EditorUtility.DisplayDialog("Set SDF Spread — Confirm", summary, "Apply", "Cancel"))
            {
                Debug.Log("[Set SDF Spread] Cancelled by user (dry run only).");
                return;
            }

            // --- APPLY ---------------------------------------------------------------------------
            Apply(rows);
        }

        private static float PromptForSpread(float suggested)
        {
            // Built-in Unity has no numeric input dialog; offer the recommended default (0.10) or the
            // crisp alternatives from the investigation. "Custom..." falls back to the field default.
            int choice = EditorUtility.DisplayDialogComplex(
                "Set SDF Spread on All Fonts",
                "Choose the SDF spread strength to apply to every UniTextFont asset.\n\n" +
                "0.10 matches the display shader's reference ratio (and TMP) for the crispest edges.\n" +
                "0.125 keeps slightly more encoded distance for wide outline/underlay effects.\n\n" +
                "Spread is clamped to [0.1, 1.0]. A dry-run list is shown before anything is written.",
                "Use 0.10 (recommended)",   // 0
                "Cancel",                    // 1
                "Use 0.125");                // 2
            switch (choice)
            {
                case 0: return 0.10f;
                case 2: return 0.125f;
                default: return float.NaN; // cancel
            }
        }

        private static List<FontRow> ScanProject(float target)
        {
            var rows = new List<FontRow>();
            var guids = AssetDatabase.FindAssets("t:UniTextFont");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var font = AssetDatabase.LoadAssetAtPath<UniTextFont>(path);
                if (font == null) continue;

                // ConfiguredRenderMode is the SERIALIZED mode (no native probe needed in a scan).
                var mode = font.ConfiguredRenderMode;
                bool isDf = mode == UniTextRenderMode.SDF || mode == UniTextRenderMode.Msdf;

                float cur = font.SpreadStrength;
                float nw = Mathf.Clamp(target, 0.1f, 1f);
                rows.Add(new FontRow
                {
                    font = font,
                    path = path,
                    currentSpread = cur,
                    newSpread = nw,
                    mode = mode,
                    isDistanceField = isDf,
                    willChange = isDf && Mathf.Abs(cur - nw) > Epsilon,
                });
            }
            rows.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            return rows;
        }

        private static void Apply(List<FontRow> rows)
        {
            int applied = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (!r.willChange) continue;

                    EditorUtility.DisplayProgressBar("Set SDF Spread",
                        $"{r.font.name} ({r.currentSpread:0.###} -> {r.newSpread:0.###})",
                        (float)i / rows.Count);

                    // Write via SerializedObject so it works regardless of field access level, and so
                    // the change is recorded for Undo exactly like the inspector's Apply button.
                    Undo.RecordObject(r.font, "Set SDF Spread");
                    var so = new SerializedObject(r.font);
                    var prop = so.FindProperty("spreadStrength");
                    if (prop == null)
                    {
                        Debug.LogWarning($"[Set SDF Spread] {r.path}: no 'spreadStrength' property; skipped.");
                        continue;
                    }
                    prop.floatValue = r.newSpread;
                    so.ApplyModifiedProperties();

                    // Regenerate: discard stale in-memory glyphs/atlas so padding re-derives from the
                    // new spread on next use. This is the font's own regenerate path (same call the
                    // inspector's ApplyAtlasSettings makes). No serialized atlas/glyph data exists to
                    // clear in the .asset — the tables are [NonSerialized].
                    r.font.ClearDynamicData();

                    EditorUtility.SetDirty(r.font);
                    applied++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            Debug.Log($"[Set SDF Spread] Applied to {applied} font asset(s); atlases will regenerate at runtime. " +
                      "Undo (Ctrl+Z) reverts each asset.");
            EditorUtility.DisplayDialog("Set SDF Spread",
                $"Done — updated {applied} font asset(s). Atlases regenerate at runtime.\n\n" +
                "Each change is undoable.", "OK");
        }
    }
}
