// OpenGlyph GlyphMeshPro — editor: a TextMeshPro-style sectioned inspector for
// GlyphMeshProUGUI, plus the GameObject/UI/OpenGlyph/GlyphMeshPro - Text menu
// factory. Clean-room: the inspector LAYOUT echoes TMP's section grouping; no
// TMP editor code is copied. See Documentation/GlyphMeshPro-Parity.md.

using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using OpenGlyph;

namespace OpenGlyph.EditorTools
{
    /// <summary>
    /// Custom inspector for <see cref="GlyphMeshProUGUI"/> arranged in TextMeshPro-like
    /// foldout sections (Font &amp; Material, Face, Text Layout, Spacing, Extra Settings),
    /// so the component feels familiar to a TMP user. Properties are drawn from the
    /// serialized object; the component applies them to the engine via its setters.
    /// </summary>
    [CustomEditor(typeof(GlyphMeshProUGUI))]
    [CanEditMultipleObjects]
    public class GlyphMeshProUGUIEditor : Editor
    {
        private static bool s_fontFoldout = true;
        private static bool s_faceFoldout = true;
        private static bool s_layoutFoldout = true;
        private static bool s_spacingFoldout;
        private static bool s_extraFoldout;

        private SerializedProperty _fontStyle, _alignment, _overflowMode, _textWrappingMode;
        private SerializedProperty _enableVertexGradient, _colorGradient;
        private SerializedProperty _characterSpacing, _wordSpacing, _lineSpacing, _paragraphSpacing, _margin;
        private SerializedProperty _maxVisibleCharacters, _maxVisibleWords, _maxVisibleLines, _richText;

        protected virtual void OnEnable()
        {
            _fontStyle = serializedObject.FindProperty("m_fontStyle");
            _alignment = serializedObject.FindProperty("m_alignment");
            _overflowMode = serializedObject.FindProperty("m_overflowMode");
            _textWrappingMode = serializedObject.FindProperty("m_textWrappingMode");
            _enableVertexGradient = serializedObject.FindProperty("m_enableVertexGradient");
            _colorGradient = serializedObject.FindProperty("m_colorGradient");
            _characterSpacing = serializedObject.FindProperty("m_characterSpacing");
            _wordSpacing = serializedObject.FindProperty("m_wordSpacing");
            _lineSpacing = serializedObject.FindProperty("m_lineSpacing");
            _paragraphSpacing = serializedObject.FindProperty("m_paragraphSpacing");
            _margin = serializedObject.FindProperty("m_margin");
            _maxVisibleCharacters = serializedObject.FindProperty("m_maxVisibleCharacters");
            _maxVisibleWords = serializedObject.FindProperty("m_maxVisibleWords");
            _maxVisibleLines = serializedObject.FindProperty("m_maxVisibleLines");
            _richText = serializedObject.FindProperty("m_richText");
        }

        public override void OnInspectorGUI()
        {
            var comp = (GlyphMeshProUGUI)target;
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "GlyphMeshProUGUI — TextMeshPro-parity API over the OpenGlyph engine. " +
                "Justified/Flush alignment and some overflow modes are Round 1 gaps " +
                "(see Documentation/GlyphMeshPro-Parity.md).", MessageType.None);

            // ---- Text content ----
            EditorGUI.BeginChangeCheck();
            string newText = EditorGUILayout.TextArea(comp.text ?? string.Empty,
                GUILayout.MinHeight(48));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(comp, "Edit GlyphMeshPro Text");
                comp.text = newText;
                EditorUtility.SetDirty(comp);
            }

            // ---- Font & Material ----
            s_fontFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(s_fontFoldout, "Font & Material");
            if (s_fontFoldout)
            {
                EditorGUI.BeginChangeCheck();
                var newFont = (LightSide.UniTextFont)EditorGUILayout.ObjectField(
                    "Font Asset", comp.font, typeof(LightSide.UniTextFont), false);
                float newSize = EditorGUILayout.FloatField("Font Size", comp.fontSize);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(comp, "Edit GlyphMeshPro Font");
                    if (newFont != comp.font) comp.font = newFont;
                    comp.fontSize = newSize;
                    EditorUtility.SetDirty(comp);
                }
                EditorGUILayout.PropertyField(_fontStyle, new GUIContent("Font Style"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ---- Face (color / gradient) ----
            s_faceFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(s_faceFoldout, "Face");
            if (s_faceFoldout)
            {
                EditorGUI.BeginChangeCheck();
                Color newColor = EditorGUILayout.ColorField("Vertex Color", comp.color);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(comp, "Edit GlyphMeshPro Color");
                    comp.color = newColor;
                    EditorUtility.SetDirty(comp);
                }
                EditorGUILayout.PropertyField(_enableVertexGradient, new GUIContent("Color Gradient"));
                if (_enableVertexGradient.boolValue)
                    EditorGUILayout.PropertyField(_colorGradient, new GUIContent("Gradient"), true);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ---- Text Layout ----
            s_layoutFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(s_layoutFoldout, "Text Layout");
            if (s_layoutFoldout)
            {
                EditorGUILayout.PropertyField(_alignment, new GUIContent("Alignment"));
                EditorGUILayout.PropertyField(_textWrappingMode, new GUIContent("Wrapping"));
                EditorGUILayout.PropertyField(_overflowMode, new GUIContent("Overflow"));
                EditorGUILayout.PropertyField(_margin, new GUIContent("Margins"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ---- Spacing ----
            s_spacingFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(s_spacingFoldout, "Spacing");
            if (s_spacingFoldout)
            {
                EditorGUILayout.PropertyField(_characterSpacing, new GUIContent("Character"));
                EditorGUILayout.PropertyField(_wordSpacing, new GUIContent("Word"));
                EditorGUILayout.PropertyField(_lineSpacing, new GUIContent("Line"));
                EditorGUILayout.PropertyField(_paragraphSpacing, new GUIContent("Paragraph"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ---- Extra Settings ----
            s_extraFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(s_extraFoldout, "Extra Settings");
            if (s_extraFoldout)
            {
                EditorGUILayout.PropertyField(_richText, new GUIContent("Rich Text"));
                EditorGUILayout.PropertyField(_maxVisibleCharacters, new GUIContent("Max Visible Characters"));
                EditorGUILayout.PropertyField(_maxVisibleWords, new GUIContent("Max Visible Words"));
                EditorGUILayout.PropertyField(_maxVisibleLines, new GUIContent("Max Visible Lines"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            if (serializedObject.ApplyModifiedProperties())
            {
                // Re-apply adapter state onto the engine for the serialized changes. (Re-assigning a
                // property to itself was a no-op: its setter skips unchanged values.)
                foreach (var t in targets)
                {
                    if (t is GlyphMeshProUGUI g)
                    {
                        g.RefreshFromSerializedState();
                        EditorUtility.SetDirty(g);
                    }
                }
            }
        }
    }

    /// <summary>GameObject menu factory: creates a GlyphMeshProUGUI under a Canvas,
    /// mirroring Unity's own UI element creation (ensures a Canvas + EventSystem).</summary>
    internal static class GlyphMeshProMenu
    {
        [MenuItem("GameObject/UI/OpenGlyph/GlyphMeshPro - Text", false, 2000)]
        private static void CreateGlyphMeshProUGUI(MenuCommand menuCommand)
        {
            // Find or create a Canvas to parent under.
            var parent = menuCommand.context as GameObject;
            Canvas canvas = parent != null ? parent.GetComponentInParent<Canvas>() : null;
            if (canvas == null) canvas = Object.FindFirstObjectByType<Canvas>();
            GameObject canvasGo;
            if (canvas == null)
            {
                canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");
            }
            else
            {
                canvasGo = canvas.gameObject;
            }

            EnsureEventSystem();

            var go = new GameObject("GlyphMeshPro Text (UGUI)", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            GameObjectUtility.SetParentAndAlign(go, parent != null && parent.GetComponentInParent<Canvas>() != null
                ? parent : canvasGo);
            rt.sizeDelta = new Vector2(200f, 50f);

            var comp = go.AddComponent<GlyphMeshProUGUI>();
            comp.text = "New Text";
            comp.fontSize = 36f;
            comp.color = Color.black;

            Undo.RegisterCreatedObjectUndo(go, "Create GlyphMeshPro Text");
            Selection.activeGameObject = go;
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;
            var es = new GameObject("EventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.EventSystems.StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
        }
    }
}
