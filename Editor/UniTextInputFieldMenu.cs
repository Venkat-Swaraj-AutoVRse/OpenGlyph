using OpenGlyph;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LightSide
{
    /// <summary>GameObject menu items for input fields (Canvas and world space).</summary>
    internal static class UniTextInputFieldMenu
    {
        [MenuItem("GameObject/UI/OpenGlyph/UniText - Input Field", false, 2001)]
        private static void CreateCanvasField(MenuCommand cmd) =>
            Place(UniTextInputField.Create(CanvasParent(cmd), false, new Vector2(320, 48)).gameObject, "Create UniText Input Field");

        [MenuItem("GameObject/UI/OpenGlyph/GlyphMeshPro - Input Field", false, 2002)]
        private static void CreateGmpField(MenuCommand cmd) =>
            Place(GlyphMeshProInputField.CreateGlyphMeshPro(CanvasParent(cmd), false, new Vector2(320, 48)).gameObject, "Create GlyphMeshPro Input Field");

        [MenuItem("GameObject/3D Object/OpenGlyph/UniText Input Field (World)", false, 2102)]
        private static void CreateWorldField(MenuCommand cmd)
        {
            // 400 x 60 local units at scale 0.001 = a 40 cm x 6 cm field (comfortable in VR at arm's length).
            var parent = (cmd.context as GameObject)?.transform;
            var f = UniTextInputField.Create(parent, true, new Vector2(400, 60));
            f.transform.localScale = Vector3.one * 0.001f;
            EnsureEventSystem();
            Place(f.gameObject, "Create UniText Input Field (World)");
        }

        private static void Place(GameObject go, string undo)
        {
            Undo.RegisterCreatedObjectUndo(go, undo);
            Selection.activeGameObject = go;
        }

        private static Transform CanvasParent(MenuCommand cmd)
        {
            var parent = cmd.context as GameObject;
            var canvas = parent != null ? parent.GetComponentInParent<Canvas>() : null;
            if (canvas == null) canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                var cgo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = cgo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                Undo.RegisterCreatedObjectUndo(cgo, "Create Canvas");
            }
            EnsureEventSystem();
            return parent != null && parent.GetComponentInParent<Canvas>() != null ? parent.transform : canvas.transform;
        }

        /// <summary>An EventSystem with the input module of the active input handling (Input System UI module when present).</summary>
        private static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem", typeof(EventSystem));
            var isModule = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
#if ENABLE_INPUT_SYSTEM
            if (isModule != null) es.AddComponent(isModule);
            else es.AddComponent<StandaloneInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
        }
    }
}
