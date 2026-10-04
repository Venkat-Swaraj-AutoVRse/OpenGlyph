using UnityEngine;
using UnityEngine.UI;

namespace LightSide
{
    // Factory: the default field hierarchy for Canvas and world space.
    public partial class UniTextInputField
    {
        /// <summary>
        /// Builds a field: the field object (with a background Image on a Canvas; in world space a
        /// <see cref="UniTextInputWorldBackground"/> panel with light text, and a BoxCollider
        /// for PhysicsRaycaster / XRI ray pointers), a "Text Area" viewport (RectMask2D on a Canvas) and
        /// "Placeholder" + "Text" children (<see cref="UniText"/>, or <see cref="UniTextWorld"/> when
        /// <paramref name="world"/>). Sizes are in the parent's local units; for world space scale the
        /// returned object (for example 0.001 for 1 mm per unit).
        /// </summary>
        public static UniTextInputField Create(Transform parent, bool world = false, Vector2? size = null,
            string placeholderText = "Enter text...", string name = "Input Field") =>
            CreateInternal(parent, world, size, placeholderText, name, typeof(UniTextInputField),
                world ? typeof(UniTextWorld) : typeof(UniText));

        internal static UniTextInputField CreateInternal(Transform parent, bool world, Vector2? size, string placeholderText,
            string name, System.Type fieldType, System.Type textType)
        {
            var sz = size ?? new Vector2(320f, 48f);
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            if (parent != null) rt.SetParent(parent, false);
            rt.sizeDelta = sz;

            Image background = null;
            if (!world)
            {
                background = go.AddComponent<Image>();
                background.color = Color.white;
            }

            var area = new GameObject("Text Area", typeof(RectTransform));
            var art = (RectTransform)area.transform;
            art.SetParent(rt, false);
            Stretch(art, new Vector4(10f, 6f, 10f, 6f));
            if (!world) area.AddComponent<RectMask2D>();

            var fontSize = Mathf.Clamp(sz.y * 0.5f, 8f, 64f);
            var placeholder = MakeText(art, "Placeholder", textType, fontSize);
            placeholder.Text = placeholderText ?? string.Empty;
            var text = MakeText(art, "Text", textType, fontSize);
            if (world)
            {
                // World fields have no Canvas Image: a dark rounded panel with light text reads well in a
                // headset against any scene (dark text on nothing was unreadable on device).
                go.AddComponent<UniTextInputWorldBackground>();
                placeholder.color = new Color(0.545f, 0.576f, 0.655f, 0.85f);
                text.color = new Color(0.91f, 0.925f, 0.957f, 1f);
            }
            else
            {
                placeholder.color = new Color(0.196f, 0.196f, 0.196f, 0.5f);
                text.color = new Color(0.196f, 0.196f, 0.196f, 1f);
            }

            var field = (UniTextInputField)go.AddComponent(fieldType);
            field.targetGraphic = background;
            field.m_TextViewport = art;
            field.m_Placeholder = placeholder;
            field.TextComponent = text;

#if OPENGLYPH_PHYSICS
            if (world)
            {
                var box = go.AddComponent<BoxCollider>();
                var r = rt.rect;
                box.center = new Vector3(r.center.x, r.center.y, 0f);
                box.size = new Vector3(r.width, r.height, Mathf.Max(0.01f, r.height * 0.05f));
            }
#endif
            field.ConfigureTextComponent();
            field.UpdateDisplay();
            return field;
        }

        private static UniText MakeText(RectTransform parent, string name, System.Type textType, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            Stretch(rt, Vector4.zero);
            var t = (UniText)go.AddComponent(textType);
            t.FontSize = fontSize;
            t.VerticalAlignment = VerticalAlignment.Middle;
            t.raycastTarget = false;
            t.Highlighter = null;
            return t;
        }

        private static void Stretch(RectTransform rt, Vector4 inset)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset.x, inset.w);
            rt.offsetMax = new Vector2(-inset.z, -inset.y);
        }

        /// <summary>Resizes the world-space BoxCollider to the field's rect (call after changing the size).</summary>
        public void FitCollider()
        {
#if OPENGLYPH_PHYSICS
            var box = GetComponent<BoxCollider>();
            if (box == null) return;
            var r = ((RectTransform)transform).rect;
            box.center = new Vector3(r.center.x, r.center.y, 0f);
            box.size = new Vector3(r.width, r.height, Mathf.Max(0.01f, r.height * 0.05f));
#endif
        }
    }
}
