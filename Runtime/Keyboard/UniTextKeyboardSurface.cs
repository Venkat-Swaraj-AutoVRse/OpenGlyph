using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LightSide
{
    /// <summary>
    /// The geometry of a keyboard's backgrounds: the panel, one rounded rectangle per key and the vector
    /// icons (Shift, Backspace, Enter, arrows, Hide, layout switch), as one vertex-coloured mesh. Key
    /// states (hover, press) only rewrite that key's vertex colours.
    /// </summary>
    internal sealed class KeyboardMesh
    {
        public readonly List<Vector3> verts = new();
        public readonly List<Color32> colors = new();
        public readonly List<int> tris = new();
        /// <summary>Convert colours to linear (a MeshRenderer in a linear-space project; a Canvas converts itself).</summary>
        public bool linear;

        private const int CornerSegments = 3;

        public void Clear()
        {
            verts.Clear();
            colors.Clear();
            tris.Clear();
        }

        public int VertexCount => verts.Count;

        public Color32 Col(Color c)
        {
            if (!linear) return c;
            var l = c.linear;
            l.a = c.a;
            return l;
        }

        /// <summary>A rounded rectangle (centre fan). Returns the first vertex; the count is <see cref="RoundedRectVertexCount"/>.</summary>
        public int RoundedRect(Rect r, float radius, Color c)
        {
            var start = verts.Count;
            var col = Col(c);
            radius = Mathf.Clamp(radius, 0f, Mathf.Min(r.width, r.height) * 0.5f);
            verts.Add(new Vector3(r.center.x, r.center.y));
            colors.Add(col);
            // Corners counter-clockwise from the top right.
            AddCorner(new Vector2(r.xMax - radius, r.yMax - radius), radius, 0f, col);
            AddCorner(new Vector2(r.xMin + radius, r.yMax - radius), radius, 90f, col);
            AddCorner(new Vector2(r.xMin + radius, r.yMin + radius), radius, 180f, col);
            AddCorner(new Vector2(r.xMax - radius, r.yMin + radius), radius, 270f, col);
            var n = verts.Count - start - 1;
            for (var i = 0; i < n; i++)
            {
                tris.Add(start);
                tris.Add(start + 1 + (i + 1) % n);
                tris.Add(start + 1 + i);
            }
            return start;
        }

        public static int RoundedRectVertexCount => 1 + 4 * (CornerSegments + 1);

        private void AddCorner(Vector2 c, float radius, float startDeg, Color32 col)
        {
            for (var i = 0; i <= CornerSegments; i++)
            {
                var a = (startDeg + 90f * i / CornerSegments) * Mathf.Deg2Rad;
                verts.Add(new Vector3(c.x + Mathf.Cos(a) * radius, c.y + Mathf.Sin(a) * radius));
                colors.Add(col);
            }
        }

        public void Recolor(int start, int count, Color c)
        {
            var col = Col(c);
            var end = Mathf.Min(colors.Count, start + count);
            for (var i = Mathf.Max(0, start); i < end; i++) colors[i] = col;
        }

        public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color32 col)
        {
            var v = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            colors.Add(col); colors.Add(col); colors.Add(col);
            tris.Add(v); tris.Add(v + 1); tris.Add(v + 2);
        }

        /// <summary>A bar of <paramref name="thickness"/> from <paramref name="a"/> to <paramref name="b"/>.</summary>
        public void Bar(Vector2 a, Vector2 b, float thickness, Color32 col)
        {
            var d = b - a;
            if (d.sqrMagnitude < 1e-8f) return;
            var n = new Vector2(-d.y, d.x).normalized * (thickness * 0.5f);
            var v = verts.Count;
            verts.Add(a - n); verts.Add(a + n); verts.Add(b + n); verts.Add(b - n);
            colors.Add(col); colors.Add(col); colors.Add(col); colors.Add(col);
            tris.Add(v); tris.Add(v + 1); tris.Add(v + 2);
            tris.Add(v + 2); tris.Add(v + 3); tris.Add(v);
        }

        /// <summary>An ellipse outline (rx, ry) of <paramref name="thickness"/>.</summary>
        public void Ring(Vector2 c, float rx, float ry, float thickness, int segments, Color32 col)
        {
            var v0 = verts.Count;
            for (var i = 0; i < segments; i++)
            {
                var a = i * Mathf.PI * 2f / segments;
                var cos = Mathf.Cos(a);
                var sin = Mathf.Sin(a);
                verts.Add(new Vector3(c.x + cos * (rx + thickness * 0.5f), c.y + sin * (ry + thickness * 0.5f)));
                verts.Add(new Vector3(c.x + cos * Mathf.Max(0f, rx - thickness * 0.5f), c.y + sin * Mathf.Max(0f, ry - thickness * 0.5f)));
                colors.Add(col); colors.Add(col);
            }
            for (var i = 0; i < segments; i++)
            {
                var o0 = v0 + 2 * i;
                var o1 = v0 + 2 * ((i + 1) % segments);
                tris.Add(o0); tris.Add(o0 + 1); tris.Add(o1 + 1);
                tris.Add(o1 + 1); tris.Add(o1); tris.Add(o0);
            }
        }

        /// <summary>
        /// Draws the icon of <paramref name="action"/> centred in <paramref name="r"/>. Returns the first
        /// vertex; <paramref name="extraStart"/>/<paramref name="extraCount"/> is the caps-lock bar of Shift
        /// (drawn transparent unless caps lock is on), otherwise empty.
        /// </summary>
        public int Icon(KeyboardKeyAction action, Rect r, Color c, out int extraStart, out int extraCount)
        {
            var start = verts.Count;
            extraStart = extraCount = 0;
            var s = Mathf.Min(r.width, r.height) * 0.42f;
            var o = r.center;
            var col = Col(c);
            var t = Mathf.Max(1f, s * 0.12f);
            Vector2 P(float x, float y) => new(o.x + x * s, o.y + y * s);
            switch (action)
            {
                case KeyboardKeyAction.Shift:
                    Triangle(P(0f, 0.55f), P(-0.5f, 0.02f), P(0.5f, 0.02f), col);
                    Bar(P(0f, 0.05f), P(0f, -0.4f), s * 0.42f, col);
                    extraStart = verts.Count;
                    Bar(P(-0.32f, -0.62f), P(0.32f, -0.62f), t, Col(new Color(c.r, c.g, c.b, 0f)));
                    extraCount = verts.Count - extraStart;
                    break;
                case KeyboardKeyAction.Backspace:
                {
                    // Outline of a left-pointing key cap with an x inside.
                    Vector2 a = P(-0.75f, 0f), b = P(-0.3f, 0.42f), cc = P(0.7f, 0.42f), d = P(0.7f, -0.42f), e = P(-0.3f, -0.42f);
                    Bar(a, b, t, col); Bar(b, cc, t, col); Bar(cc, d, t, col); Bar(d, e, t, col); Bar(e, a, t, col);
                    Bar(P(0.02f, 0.2f), P(0.42f, -0.2f), t, col);
                    Bar(P(0.02f, -0.2f), P(0.42f, 0.2f), t, col);
                    break;
                }
                case KeyboardKeyAction.Enter:
                    Bar(P(0.55f, 0.45f), P(0.55f, -0.06f), t * 1.2f, col);
                    Bar(P(0.61f, 0f), P(-0.3f, 0f), t * 1.2f, col);
                    Triangle(P(-0.62f, 0f), P(-0.25f, 0.3f), P(-0.25f, -0.3f), col);
                    break;
                case KeyboardKeyAction.Left:
                    Triangle(P(-0.55f, 0f), P(-0.1f, 0.38f), P(-0.1f, -0.38f), col);
                    Bar(P(-0.15f, 0f), P(0.55f, 0f), s * 0.2f, col);
                    break;
                case KeyboardKeyAction.Right:
                    Triangle(P(0.55f, 0f), P(0.1f, -0.38f), P(0.1f, 0.38f), col);
                    Bar(P(0.15f, 0f), P(-0.55f, 0f), s * 0.2f, col);
                    break;
                case KeyboardKeyAction.Hide:
                    Bar(P(-0.5f, 0.42f), P(0.5f, 0.42f), t, col);
                    Bar(P(-0.45f, 0.08f), P(0.02f, -0.4f), t * 1.2f, col);
                    Bar(P(-0.02f, -0.4f), P(0.45f, 0.08f), t * 1.2f, col);
                    break;
                case KeyboardKeyAction.NextLayout:
                    Ring(o, s * 0.6f, s * 0.6f, t, 24, col);
                    Ring(o, s * 0.25f, s * 0.6f, t, 18, col);
                    Bar(P(-0.6f, 0f), P(0.6f, 0f), t, col);
                    Bar(P(-0.5f, 0.3f), P(0.5f, 0.3f), t * 0.8f, col);
                    Bar(P(-0.5f, -0.3f), P(0.5f, -0.3f), t * 0.8f, col);
                    break;
                case KeyboardKeyAction.Tab:
                    Bar(P(-0.55f, 0f), P(0.35f, 0f), t, col);
                    Triangle(P(0.55f, 0f), P(0.25f, 0.25f), P(0.25f, -0.25f), col);
                    Bar(P(0.6f, 0.3f), P(0.6f, -0.3f), t, col);
                    break;
            }
            return start;
        }

        /// <summary>Whether <paramref name="action"/> is drawn as an icon (no text label).</summary>
        public static bool HasIcon(KeyboardKeyAction action) => action is KeyboardKeyAction.Shift or KeyboardKeyAction.Backspace
            or KeyboardKeyAction.Enter or KeyboardKeyAction.Left or KeyboardKeyAction.Right or KeyboardKeyAction.Hide
            or KeyboardKeyAction.NextLayout or KeyboardKeyAction.Tab;
    }

    /// <summary>
    /// Draws a <see cref="UniTextKeyboard"/>'s backgrounds and icons in world space: one MeshRenderer with
    /// the shared unlit vertex-colour material (the input field overlay material). Created by the keyboard,
    /// not saved.
    /// </summary>
    [AddComponentMenu("")]
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class UniTextKeyboardWorldSurface : MonoBehaviour
    {
        private Mesh mesh;
        private MeshRenderer meshRenderer;

        public MeshRenderer Renderer => meshRenderer != null ? meshRenderer : meshRenderer = GetComponent<MeshRenderer>();
        public Mesh Mesh => mesh;

        private void EnsureMesh()
        {
            if (mesh != null) return;
            mesh = new Mesh { name = "UniText Keyboard", hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = Renderer;
            r.sharedMaterial = UniTextInputWorldOverlay.SharedMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        internal void Apply(KeyboardMesh m)
        {
            EnsureMesh();
            mesh.Clear();
            mesh.SetVertices(m.verts);
            mesh.SetColors(m.colors);
            mesh.SetTriangles(m.tris, 0);
            mesh.RecalculateBounds();
        }

        /// <summary>Uploads only the vertex colours (key hover / press).</summary>
        internal void ApplyColors(KeyboardMesh m)
        {
            if (mesh == null || mesh.vertexCount != m.colors.Count) { Apply(m); return; }
            mesh.SetColors(m.colors);
        }

        private void OnDestroy()
        {
            if (mesh != null) ObjectUtils.SafeDestroy(mesh);
            mesh = null;
        }
    }

    /// <summary>Canvas counterpart of <see cref="UniTextKeyboardWorldSurface"/>: one CanvasRenderer.</summary>
    [AddComponentMenu("")]
    [ExecuteAlways]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class UniTextKeyboardCanvasSurface : MaskableGraphic
    {
        private KeyboardMesh source;

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        internal void Apply(KeyboardMesh m)
        {
            source = m;
            SetVerticesDirty();
        }

        internal void ApplyColors(KeyboardMesh m) => Apply(m);

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var m = source;
            if (m == null) return;
            var n = m.verts.Count;
            for (var i = 0; i < n; i++) vh.AddVert(m.verts[i], m.colors[i], Vector4.zero);
            var tris = m.tris;
            for (var i = 0; i + 2 < tris.Count; i += 3) vh.AddTriangle(tris[i], tris[i + 1], tris[i + 2]);
        }

        public override bool Raycast(Vector2 sp, Camera eventCamera) => false;
    }

    /// <summary>
    /// One key of a <see cref="UniTextKeyboard"/>: its label (a <see cref="UniTextWorld"/> with a collider in
    /// world space, a <see cref="UniText"/> on a Canvas) receives the EventSystem pointer events (mouse,
    /// touch, XR Interaction Toolkit rays through <c>TrackedDevicePhysicsRaycaster</c> /
    /// <c>TrackedDeviceGraphicRaycaster</c>) and forwards them to the keyboard. Created by the keyboard.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class UniTextKeyboardKey : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
    {
        internal UniTextKeyboard keyboard;
        internal KeyboardKey def;
        internal UniText label;
        internal Rect localRect;
        internal int bgStart, bgCount, iconStart, iconCount, capsStart, capsCount;
        internal int pressCount;
        internal bool hovered;
        internal bool hiddenByLayout;

        /// <summary>The keyboard that owns this key.</summary>
        public UniTextKeyboard Keyboard => keyboard;
        /// <summary>The key definition (action, text, shift text, width) from the layout.</summary>
        public KeyboardKey Definition => def;
        /// <summary>The label component (UniTextWorld in world space, UniText on a Canvas).</summary>
        public UniText Label => label;
        /// <summary>The key's rectangle in the keyboard's local space.</summary>
        public Rect LocalRect => localRect;
        public bool IsPressed => pressCount > 0;
        public bool IsHovered => hovered;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData != null && eventData.button != PointerEventData.InputButton.Left) return;
            if (keyboard != null) keyboard.KeyDown(this, eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData != null && eventData.button != PointerEventData.InputButton.Left) return;
            if (keyboard != null) keyboard.KeyUp(this);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (keyboard != null) keyboard.KeyHover(this, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (keyboard != null) keyboard.KeyHover(this, false);
        }
    }
}
