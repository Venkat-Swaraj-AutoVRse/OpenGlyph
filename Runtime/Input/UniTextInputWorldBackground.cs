using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// The panel behind a world-space <see cref="UniTextInputField"/> (a Canvas field uses an Image instead):
    /// a rounded rectangle with an optional border, drawn by a MeshRenderer with the shared unlit
    /// vertex-colour material and rebuilt when the field's rect changes. The colour follows the field's
    /// state (normal / focused / read-only). Created by <see cref="UniTextInputField.Create"/> for world fields.
    /// </summary>
    [AddComponentMenu("UniText/Input Field World Background")]
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    public sealed class UniTextInputWorldBackground : MonoBehaviour
    {
        [SerializeField] private Color m_Color = new(0.118f, 0.137f, 0.18f, 0.94f);
        [SerializeField] private Color m_FocusedColor = new(0.141f, 0.165f, 0.22f, 0.97f);
        [SerializeField] private Color m_BorderColor = new(0.30f, 0.34f, 0.42f, 1f);
        [SerializeField] private Color m_FocusedBorderColor = new(0.22f, 0.74f, 0.97f, 1f);
        [SerializeField, Min(0f), Tooltip("Border width in the field's local units (0 = none).")]
        private float m_BorderWidth = 2f;
        [SerializeField, Range(0f, 0.5f), Tooltip("Corner radius as a fraction of the field height.")]
        private float m_CornerRadius = 0.2f;
        [SerializeField, Tooltip("Local depth offset behind the text (positive = behind for a field facing the viewer).")]
        private float m_Depth = 0.5f;

        private UniTextKeyboardWorldSurface surface;
        private readonly KeyboardMesh mesh = new();
        private UniTextInputField field;
        private bool focused;

        public Color Color { get => m_Color; set { m_Color = value; Rebuild(); } }
        public Color FocusedColor { get => m_FocusedColor; set { m_FocusedColor = value; Rebuild(); } }
        public Color BorderColor { get => m_BorderColor; set { m_BorderColor = value; Rebuild(); } }
        public Color FocusedBorderColor { get => m_FocusedBorderColor; set { m_FocusedBorderColor = value; Rebuild(); } }
        public float BorderWidth { get => m_BorderWidth; set { m_BorderWidth = Mathf.Max(0f, value); Rebuild(); } }
        public float CornerRadius { get => m_CornerRadius; set { m_CornerRadius = Mathf.Clamp(value, 0f, 0.5f); Rebuild(); } }

        /// <summary>The MeshRenderer that draws the panel (for sorting).</summary>
        public MeshRenderer Renderer => EnsureSurface().Renderer;

        private UniTextKeyboardWorldSurface EnsureSurface()
        {
            if (surface != null) return surface;
            var t = transform.Find("Background");
            GameObject go;
            if (t != null) go = t.gameObject;
            else
            {
                go = new GameObject("Background", typeof(MeshFilter), typeof(MeshRenderer), typeof(UniTextKeyboardWorldSurface))
                {
                    hideFlags = HideFlags.DontSave | HideFlags.NotEditable
                };
                go.transform.SetParent(transform, false);
                go.transform.SetAsFirstSibling();
            }
            go.transform.localPosition = new Vector3(0f, 0f, m_Depth);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            surface = go.GetComponent<UniTextKeyboardWorldSurface>();
            if (surface == null) surface = go.AddComponent<UniTextKeyboardWorldSurface>();
            return surface;
        }

        private void OnEnable()
        {
            field = GetComponent<UniTextInputField>();
            Rebuild();
        }

        private void OnDisable()
        {
            if (surface != null) surface.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (surface != null) ObjectUtils.SafeDestroy(surface.gameObject);
            surface = null;
        }

        private void OnRectTransformDimensionsChange() => Rebuild();

        private void LateUpdate()
        {
            if (field == null) field = GetComponent<UniTextInputField>();
            var f = field != null && field.IsFocused;
            if (f != focused) { focused = f; Rebuild(); }
            else ApplySorting(); // follows the text's sorting if it is changed later
        }

        /// <summary>Rebuilds the panel mesh from the current rect and state.</summary>
        public void Rebuild()
        {
            if (!isActiveAndEnabled) return;
            var s = EnsureSurface();
            if (!s.gameObject.activeSelf) s.gameObject.SetActive(true);
            s.transform.localPosition = new Vector3(0f, 0f, m_Depth);
            var r = ((RectTransform)transform).rect;
            if (r.width <= 0f || r.height <= 0f) return;
            mesh.Clear();
            mesh.linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var radius = r.height * m_CornerRadius;
            if (m_BorderWidth > 0f)
            {
                mesh.RoundedRect(r, radius, focused ? m_FocusedBorderColor : m_BorderColor);
                var inner = new Rect(r.xMin + m_BorderWidth, r.yMin + m_BorderWidth,
                    Mathf.Max(0f, r.width - 2f * m_BorderWidth), Mathf.Max(0f, r.height - 2f * m_BorderWidth));
                // The inner fill sits slightly in front of the border so it wins the depth-less sort.
                var start = mesh.VertexCount;
                mesh.RoundedRect(inner, Mathf.Max(0f, radius - m_BorderWidth), focused ? m_FocusedColor : m_Color);
                for (var i = start; i < mesh.verts.Count; i++) mesh.verts[i] += new Vector3(0f, 0f, -0.01f);
            }
            else mesh.RoundedRect(r, radius, focused ? m_FocusedColor : m_Color);
            s.Apply(mesh);
            ApplySorting();
        }

        /// <summary>
        /// Draw below the text, placeholder and selection: transparent renderers are otherwise ordered by
        /// distance, and the panel's centre differs from the text's, so at some head angles the panel was
        /// drawn over the text (text "disappearing" on Quest).
        /// </summary>
        private void ApplySorting()
        {
            if (field == null) field = GetComponent<UniTextInputField>(); // added after this component by Create
            var text = field != null ? field.TextComponent : null;
            var tr = text != null ? text.WorldRenderer : null;
            var r = surface != null ? surface.Renderer : null;
            if (tr == null || r == null) return;
            var order = tr.sortingOrder;
            var ph = field.Placeholder as UniText;
            var pr = ph != null ? ph.WorldRenderer : null;
            if (pr != null) order = Mathf.Min(order, pr.sortingOrder);
            r.sortingLayerID = tr.sortingLayerID;
            r.sortingOrder = order - 2;
        }
    }
}
