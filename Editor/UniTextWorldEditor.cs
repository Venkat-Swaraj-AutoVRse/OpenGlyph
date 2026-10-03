using UnityEditor;
using UnityEngine;
using OpenGlyph;
using OpenGlyph.EditorTools;

namespace LightSide
{
    /// <summary>Shared drawing for the world render options of UniTextWorld and GlyphMeshPro.</summary>
    internal static class WorldTextInspector
    {
        public static void DrawWorldSection(SerializedObject so, string optionsField, Object[] targets)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("World Rendering", EditorStyles.boldLabel);
            var options = so.FindProperty(optionsField);
            if (options != null)
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(options.FindPropertyRelative("lighting"), new GUIContent("Lighting"));
                EditorGUILayout.PropertyField(options.FindPropertyRelative("depthWrite"), new GUIContent("Depth Write"));
                EditorGUILayout.PropertyField(options.FindPropertyRelative("doubleSided"), new GUIContent("Double Sided"));
                EditorGUILayout.PropertyField(options.FindPropertyRelative("collider"), new GUIContent("Pointer Collider"));
                if (EditorGUI.EndChangeCheck())
                {
                    so.ApplyModifiedProperties();
                    foreach (var t in targets)
                        if (t is UniText ut)
                        {
                            ut.SetDirty(UniText.DirtyFlags.Material);
                            ut.UpdateWorldCollider();
                        }
                }
            }

            // Sorting lives on the MeshRenderer (like TextMeshPro).
            var first = targets.Length > 0 ? targets[0] as Component : null;
            var mr = first != null ? first.GetComponent<MeshRenderer>() : null;
            if (mr != null)
            {
                EditorGUI.BeginChangeCheck();
                var layers = SortingLayer.layers;
                var names = new string[layers.Length];
                var current = 0;
                for (var i = 0; i < layers.Length; i++)
                {
                    names[i] = layers[i].name;
                    if (layers[i].id == mr.sortingLayerID) current = i;
                }
                var layer = EditorGUILayout.Popup("Sorting Layer", current, names);
                var order = EditorGUILayout.IntField("Order in Layer", mr.sortingOrder);
                if (EditorGUI.EndChangeCheck())
                {
                    foreach (var t in targets)
                    {
                        var r = (t as Component)?.GetComponent<MeshRenderer>();
                        if (r == null) continue;
                        Undo.RecordObject(r, "Edit Text Sorting");
                        if (layers.Length > 0) r.sortingLayerID = layers[layer].id;
                        r.sortingOrder = order;
                    }
                }
            }

            if (first is UniText text && text.WorldMesh != null)
            {
                var b = text.WorldMesh.bounds;
                var s = text.transform.lossyScale;
                var rect = text.rectTransform.rect;
                EditorGUILayout.HelpBox(
                    $"Rect {rect.width:0.##} x {rect.height:0.##} local units = " +
                    $"{rect.width * Mathf.Abs(s.x):0.###} x {rect.height * Mathf.Abs(s.y):0.###} world units. " +
                    $"Mesh: {text.WorldMesh.vertexCount} vertices, {text.WorldMesh.subMeshCount} draw(s), bounds {b.size.x:0.#} x {b.size.y:0.#}.",
                    MessageType.None);
            }
        }

        /// <summary>Creates a world text object in front of the scene view (or under the selected parent).</summary>
        public static GameObject Create<T>(MenuCommand menuCommand, string name, Vector2 size, float scale, float fontSize)
            where T : UniText
        {
            var go = new GameObject(name, typeof(RectTransform));
            var parent = menuCommand.context as GameObject;
            if (parent != null) GameObjectUtility.SetParentAndAlign(go, parent);
            else if (SceneView.lastActiveSceneView != null)
                go.transform.position = SceneView.lastActiveSceneView.pivot;
            go.transform.localScale = Vector3.one * scale;
            ((RectTransform)go.transform).sizeDelta = size;
            var t = go.AddComponent<T>();
            t.FontSize = fontSize;
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Selection.activeGameObject = go;
            return go;
        }

        [MenuItem("GameObject/3D Object/OpenGlyph/UniText World", false, 2100)]
        private static void CreateUniTextWorld(MenuCommand menuCommand)
        {
            // 200 x 50 local units at scale 0.01 = a 2 m x 0.5 m label; font size 24 = 24 cm per em.
            var go = Create<UniTextWorld>(menuCommand, "UniText World", new Vector2(200, 50), 0.01f, 24f);
            go.GetComponent<UniTextWorld>().Text = "World Text";
        }

        [MenuItem("GameObject/3D Object/OpenGlyph/GlyphMeshPro - Text", false, 2101)]
        private static void CreateGlyphMeshPro(MenuCommand menuCommand)
        {
            // TextMeshPro's world default is a 20 x 5 rect with font size 36 at a 0.1 glyph scale:
            // the same look is a 200 x 50 rect at localScale 0.1.
            var go = Create<GlyphMeshPro>(menuCommand, "GlyphMeshPro Text", new Vector2(200, 50), 0.1f, 36f);
            go.GetComponent<GlyphMeshPro>().text = "Sample text";
        }
    }

    [CustomEditor(typeof(UniTextWorld))]
    [CanEditMultipleObjects]
    internal class UniTextWorldEditor : UniTextEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            serializedObject.Update();
            WorldTextInspector.DrawWorldSection(serializedObject, "worldOptions", targets);
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(GlyphMeshPro))]
    [CanEditMultipleObjects]
    internal class GlyphMeshProWorldEditor : GlyphMeshProUGUIEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            serializedObject.Update();
            WorldTextInspector.DrawWorldSection(serializedObject, "m_worldOptions", targets);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
