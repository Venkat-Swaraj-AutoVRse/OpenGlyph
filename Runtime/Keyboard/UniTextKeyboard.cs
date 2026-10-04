using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LightSide
{
    /// <summary>How a <see cref="UniTextKeyboard"/> draws its keys.</summary>
    public enum KeyboardSurfaceMode
    {
        /// <summary>Canvas when the keyboard is under a Canvas, otherwise world space.</summary>
        Auto = 0,
        /// <summary>World space without a Canvas: <see cref="UniTextWorld"/> labels with per-key colliders.</summary>
        World = 1,
        /// <summary>On a Canvas: <see cref="UniText"/> labels (Screen Space or World Space canvas).</summary>
        Canvas = 2,
    }

    /// <summary>Where a <see cref="UniTextKeyboard"/> goes when it is shown for a field.</summary>
    public enum KeyboardPlacement
    {
        /// <summary>Below the focused field, tilted towards the viewer (default).</summary>
        BelowField = 0,
        /// <summary>At <see cref="UniTextKeyboard.Anchor"/> (plus <see cref="UniTextKeyboard.AnchorOffset"/>).</summary>
        Transform = 1,
        /// <summary>In front of and below the head (camera); lazily follows when the head turns away.</summary>
        FollowHead = 2,
        /// <summary>Never moved by the keyboard.</summary>
        Manual = 3,
    }

    /// <summary>Shift state of a <see cref="UniTextKeyboard"/>.</summary>
    public enum KeyboardShiftState
    {
        Off = 0,
        /// <summary>The next typed key is shifted, then Shift turns off.</summary>
        Once = 1,
        /// <summary>Caps lock (double-tap Shift).</summary>
        Locked = 2,
    }

    /// <summary>Event with a key id: the typed text, or the action name ("Backspace", "Enter", "Shift", ...).</summary>
    [Serializable] public class KeyboardKeyEvent : UnityEvent<string> { }

    /// <summary>
    /// A built-in on-screen keyboard drawn with OpenGlyph text, for VR (Meta Quest under OpenXR has no usable
    /// system keyboard) and anywhere else a soft keyboard is wanted. It types into the focused
    /// <see cref="UniTextInputField"/> through the field's input seam (<see cref="UniTextInputField.ProcessText"/>,
    /// <see cref="UniTextInputField.ProcessKey"/>), so every editing rule of the field applies (validation,
    /// limit, undo, multi-line, password masking).
    /// </summary>
    /// <remarks>
    /// <para><b>Showing.</b> A field whose <see cref="UniTextInputField.SoftKeyboard"/> resolves to the built-in
    /// keyboard opens it on focus (through <see cref="IInputFieldTouchKeyboard"/>) and closes it when editing
    /// ends (submit, deselect, Hide key). <see cref="Show(UniTextInputField)"/> / <see cref="Hide"/> do it from code.
    /// The layout follows the field: numeric pad for Integer / Decimal / PIN, e-mail row for e-mail fields,
    /// otherwise the current entry of <see cref="Layouts"/>.</para>
    /// <para><b>Input.</b> Keys react to standard EventSystem pointer events (press, release, hover): mouse,
    /// touch, and the XR Interaction Toolkit's ray interactors (<c>TrackedDevicePhysicsRaycaster</c> on world
    /// keys, which keep a BoxCollider each; <c>TrackedDeviceGraphicRaycaster</c>, including poke, on a World
    /// Space Canvas keyboard). Clicking a key does not take focus from the field (<see cref="IInputFieldFocusKeeper"/>).
    /// <see cref="PressKey"/> / <see cref="ReleaseKey"/> / <see cref="GetKeyAt"/> drive it from custom interaction code.</para>
    /// <para><b>Cost.</b> One label per key (all world labels share one material), one mesh for every key
    /// background and icon (shared vertex-colour material), and a key-preview popup. Hover and press rewrite
    /// vertex colours only. Hiding disables renderers and colliders without rebuilding. Nothing allocates
    /// per frame while the keyboard is idle.</para>
    /// </remarks>
    [AddComponentMenu("OpenGlyph/UniText Keyboard")]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    public partial class UniTextKeyboard : MonoBehaviour, IInputFieldFocusKeeper
    {
        // ---- serialized configuration ------------------------------------------------------------

        [SerializeField, Tooltip("World space (UniTextWorld labels, per-key colliders) or Canvas (UniText labels). Auto: Canvas when under a Canvas.")]
        private KeyboardSurfaceMode m_Mode = KeyboardSurfaceMode.Auto;

        [SerializeField, Tooltip("Fonts of the key labels (empty: the project default). Must cover every layout's script.")]
        private UniTextFontStack m_FontStack;

        [SerializeField, Tooltip("Text layouts; the layout key cycles through them. Empty: English QWERTY.")]
        private List<UniTextKeyboardLayout> m_Layouts = new();

        [SerializeField, Tooltip("Layout for Integer / Decimal / PIN fields. Empty: the built-in numeric pad.")]
        private UniTextKeyboardLayout m_NumericLayout;

        [SerializeField, Tooltip("Layout for e-mail fields. Empty: the built-in e-mail layout.")]
        private UniTextKeyboardLayout m_EmailLayout;

        [Header("Size (local units)")]
        [SerializeField, Min(1f), Tooltip("Size of a 1-unit key, in the keyboard's local units.")]
        private float m_KeySize = 100f;

        [SerializeField, Min(0f)] private float m_KeyGap = 8f;
        [SerializeField, Min(0f)] private float m_Padding = 16f;
        [SerializeField, Min(0f)] private float m_CornerRadius = 14f;
        [SerializeField, Range(0.1f, 0.9f), Tooltip("Letter label size as a fraction of the key size.")]
        private float m_LabelScale = 0.44f;

        [Header("Colours")]
        [SerializeField] private Color m_PanelColor = new(0.059f, 0.067f, 0.09f, 0.96f);
        [SerializeField] private Color m_KeyColor = new(0.17f, 0.19f, 0.235f, 1f);
        [SerializeField] private Color m_SpecialKeyColor = new(0.115f, 0.13f, 0.165f, 1f);
        [SerializeField] private Color m_AccentKeyColor = new(0.149f, 0.388f, 0.922f, 1f);
        [SerializeField] private Color m_HoverColor = new(0.25f, 0.28f, 0.34f, 1f);
        [SerializeField] private Color m_PressedColor = new(0.22f, 0.74f, 0.97f, 1f);
        [SerializeField] private Color m_LabelColor = new(0.95f, 0.96f, 0.98f, 1f);
        [SerializeField] private Color m_SpecialLabelColor = new(0.72f, 0.76f, 0.84f, 1f);

        [Header("Placement")]
        [SerializeField] private KeyboardPlacement m_Placement = KeyboardPlacement.BelowField;
        [SerializeField, Tooltip("Placement = Transform: the keyboard takes this transform's pose.")]
        private Transform m_Anchor;
        [SerializeField, Tooltip("Placement = Transform: offset in the anchor's local space.")]
        private Vector3 m_AnchorOffset;
        [SerializeField, Tooltip("Placement = FollowHead: the head (empty: Camera.main).")]
        private Transform m_Head;
        [SerializeField, Tooltip("FollowHead: metres in front of the head.")]
        private float m_FollowDistance = 0.45f;
        [SerializeField, Tooltip("FollowHead: metres above (+) / below (-) the head.")]
        private float m_FollowHeight = -0.28f;
        [SerializeField, Range(0f, 90f), Tooltip("FollowHead: start following when the keyboard is this many degrees off the view direction.")]
        private float m_FollowAngle = 30f;
        [SerializeField, Min(0.1f), Tooltip("FollowHead: follow speed (1/s).")]
        private float m_FollowSpeed = 5f;
        [SerializeField, Tooltip("BelowField: metres between the field's bottom edge and the keyboard.")]
        private float m_FieldGap = 0.02f;
        [SerializeField, Tooltip("BelowField / FollowHead: metres the keyboard is pulled towards the viewer.")]
        private float m_TowardViewer = 0.04f;
        [SerializeField, Range(-60f, 60f), Tooltip("Tilt in degrees (bottom edge towards the viewer).")]
        private float m_Tilt = 20f;

        [Header("Behaviour")]
        [SerializeField, Tooltip("Show a magnified popup of a pressed key (never for password fields).")]
        private bool m_ShowKeyPreview = true;
        [SerializeField, Tooltip("Visible when entering Play Mode (otherwise shown by a focused field or Show()).")]
        private bool m_StartVisible;

        // ---- events --------------------------------------------------------------------------------

        /// <summary>A key was pressed (also each Backspace repeat): the typed text, or the action name. For click sounds and haptics.</summary>
        public KeyboardKeyEvent onKeyPressed = new();
        public UnityEvent onShown = new();
        public UnityEvent onHidden = new();

        /// <summary>A key was pressed: the key and the pointer event (null for repeats and <see cref="PressKey"/>).</summary>
        public event Action<UniTextKeyboardKey, PointerEventData> KeyPressed;
        public event Action Shown;
        public event Action Hidden;

        // ---- runtime state -------------------------------------------------------------------------

        private static readonly List<UniTextKeyboard> s_instances = new();
        private static int s_creating;

        private bool built;
        private bool isWorld;
        private RectTransform keysRoot;
        private UniTextKeyboardWorldSurface worldSurface, worldPreviewSurface;
        private UniTextKeyboardCanvasSurface canvasSurface, canvasPreviewSurface;
        private UniText previewLabel;
        private RectTransform previewRoot;
        private CanvasGroup canvasGroup;
        private readonly List<UniTextKeyboardKey> keys = new();
        private int keyCount;
        private readonly KeyboardMesh mesh = new();
        private readonly KeyboardMesh previewMesh = new();
        private readonly List<CanvasRenderer> canvasRendererScratch = new();

        private UniTextKeyboardLayout layout;
        private int page;
        private int textLayoutIndex;
        private KeyboardShiftState shift;
        private float lastShiftTap = -10f;
        private bool visible;
        private bool visibilityApplied;
        private bool secure;
        private bool multiline;
        private UniTextInputField target;
        private UniTextKeyboardKey repeatKey;
        private float repeatNext;
        private UniTextKeyboardKey previewKey;
        private bool following;
        private float lastUpdate = float.NaN;
        private readonly Vector3[] cornerScratch = new Vector3[4];

        /// <summary>Double-tap window for caps lock, seconds.</summary>
        public static float CapsLockDoubleTap = 0.35f;

        /// <summary>When true (default) a field that needs the built-in keyboard and finds none creates one (<see cref="ForField"/>).</summary>
        public static bool AutoCreate = true;

        /// <summary>The enabled keyboards.</summary>
        public static IReadOnlyList<UniTextKeyboard> Instances => s_instances;

        // ---- public API ----------------------------------------------------------------------------

        /// <summary>Whether the keyboard is shown.</summary>
        public bool IsVisible => visible;

        /// <summary>Whether the keys are world-space <see cref="UniTextWorld"/> labels (false: Canvas <see cref="UniText"/>).</summary>
        public bool IsWorld { get { EnsureBuilt(); return isWorld; } }

        /// <summary>The field the keyboard types into (the field it was opened for).</summary>
        public UniTextInputField Target => target;

        /// <summary>The layout shown.</summary>
        public UniTextKeyboardLayout CurrentLayout { get { EnsureBuilt(); return layout; } }

        /// <summary>The page of <see cref="CurrentLayout"/> shown (0 = letters).</summary>
        public int Page
        {
            get => page;
            set { EnsureBuilt(); ApplyLayout(layout, value); }
        }

        public KeyboardShiftState Shift
        {
            get => shift;
            set { if (shift == value) return; shift = value; RefreshShift(); }
        }

        /// <summary>The keys of the current page (in layout order).</summary>
        public IReadOnlyList<UniTextKeyboardKey> Keys
        {
            get { EnsureBuilt(); return keyCount == keys.Count ? keys : keys.GetRange(0, keyCount); }
        }

        /// <summary>Number of keys on the current page.</summary>
        public int KeyCount { get { EnsureBuilt(); return keyCount; } }

        /// <summary>Key <paramref name="index"/> of the current page.</summary>
        public UniTextKeyboardKey KeyAt(int index) { EnsureBuilt(); return index >= 0 && index < keyCount ? keys[index] : null; }

        /// <summary>The text layouts the layout key cycles through (empty: English QWERTY). Call <see cref="Rebuild"/> after editing the list.</summary>
        public List<UniTextKeyboardLayout> Layouts => m_Layouts;

        public UniTextKeyboardLayout NumericLayout
        {
            get => m_NumericLayout != null ? m_NumericLayout : UniTextKeyboardLayout.Numeric;
            set => m_NumericLayout = value;
        }

        public UniTextKeyboardLayout EmailLayout
        {
            get => m_EmailLayout != null ? m_EmailLayout : UniTextKeyboardLayout.Email;
            set => m_EmailLayout = value;
        }

        public KeyboardSurfaceMode Mode
        {
            get => m_Mode;
            set { if (m_Mode == value) return; m_Mode = value; if (built) Rebuild(); }
        }

        /// <summary>Fonts of the key labels (null: the project default).</summary>
        public UniTextFontStack FontStack
        {
            get => m_FontStack;
            set
            {
                m_FontStack = value;
                for (var i = 0; i < keys.Count; i++) if (keys[i] != null && keys[i].label != null) ApplyFont(keys[i].label);
                if (previewLabel != null) ApplyFont(previewLabel);
            }
        }

        public float KeySize { get => m_KeySize; set { m_KeySize = Mathf.Max(1f, value); Relayout(); } }
        public float KeyGap { get => m_KeyGap; set { m_KeyGap = Mathf.Max(0f, value); Relayout(); } }
        public float Padding { get => m_Padding; set { m_Padding = Mathf.Max(0f, value); Relayout(); } }
        public float CornerRadius { get => m_CornerRadius; set { m_CornerRadius = Mathf.Max(0f, value); Relayout(); } }
        public float LabelScale { get => m_LabelScale; set { m_LabelScale = Mathf.Clamp(value, 0.1f, 0.9f); Relayout(); } }

        public Color PanelColor { get => m_PanelColor; set { m_PanelColor = value; Relayout(); } }
        public Color KeyColor { get => m_KeyColor; set { m_KeyColor = value; Relayout(); } }
        public Color SpecialKeyColor { get => m_SpecialKeyColor; set { m_SpecialKeyColor = value; Relayout(); } }
        public Color AccentKeyColor { get => m_AccentKeyColor; set { m_AccentKeyColor = value; Relayout(); } }
        public Color HoverColor { get => m_HoverColor; set => m_HoverColor = value; }
        public Color PressedColor { get => m_PressedColor; set => m_PressedColor = value; }
        public Color LabelColor { get => m_LabelColor; set { m_LabelColor = value; Relayout(); } }
        public Color SpecialLabelColor { get => m_SpecialLabelColor; set { m_SpecialLabelColor = value; Relayout(); } }

        public KeyboardPlacement Placement { get => m_Placement; set { m_Placement = value; following = false; } }
        public Transform Anchor { get => m_Anchor; set => m_Anchor = value; }
        public Vector3 AnchorOffset { get => m_AnchorOffset; set => m_AnchorOffset = value; }
        public Transform Head { get => m_Head; set => m_Head = value; }
        public float FollowDistance { get => m_FollowDistance; set => m_FollowDistance = value; }
        public float FollowHeight { get => m_FollowHeight; set => m_FollowHeight = value; }
        public float FollowAngle { get => m_FollowAngle; set => m_FollowAngle = Mathf.Clamp(value, 0f, 90f); }
        public float FollowSpeed { get => m_FollowSpeed; set => m_FollowSpeed = Mathf.Max(0.1f, value); }
        public float FieldGap { get => m_FieldGap; set => m_FieldGap = value; }
        public float TowardViewer { get => m_TowardViewer; set => m_TowardViewer = value; }
        public float Tilt { get => m_Tilt; set => m_Tilt = Mathf.Clamp(value, -60f, 60f); }

        /// <summary>Magnified popup of the pressed key (never shown while typing into a password field).</summary>
        public bool ShowKeyPreview { get => m_ShowKeyPreview; set { m_ShowKeyPreview = value; if (!value) HidePreview(); } }

        /// <summary>Whether the field being typed into is secure (password / PIN): no key previews.</summary>
        public bool IsSecure => secure;

        /// <summary>The key preview popup is showing.</summary>
        public bool IsPreviewVisible => previewKey != null;

        /// <summary>The preview label (null until built).</summary>
        public UniText PreviewLabel => previewLabel;

        /// <summary>The world-space background mesh renderer (null on a Canvas).</summary>
        public MeshRenderer SurfaceRenderer => worldSurface != null ? worldSurface.Renderer : null;

        /// <summary>The Canvas background graphic (null in world space).</summary>
        public Graphic SurfaceGraphic => canvasSurface;

        /// <summary>The panel rectangle in local units (the RectTransform rect).</summary>
        public Rect PanelRect => ((RectTransform)transform).rect;

        /// <summary>Shows the keyboard (typing goes to the focused field).</summary>
        public void Show()
        {
            EnsureBuilt();
            SetVisible(true);
        }

        /// <summary>Focuses <paramref name="field"/> (if needed) and shows the keyboard for it, placed per <see cref="Placement"/>.</summary>
        public void Show(UniTextInputField field)
        {
            if (field == null) { Show(); return; }
            EnsureBuilt();
            if (!field.IsFocused) field.ActivateInputField();
            if (!(visible && target == field))
                Open(field, field.KeyboardType, field.MultiLine, field.InputType == InputFieldInputType.Password);
        }

        /// <summary>Hides the keyboard and ends editing of its field.</summary>
        public void Hide()
        {
            var f = target;
            SetVisible(false);
            target = null;
            if (f != null && f.IsFocused) f.DeactivateInputField();
        }

        /// <summary>Shows <paramref name="newLayout"/> (page 0). Also selects it for future text fields if it is in <see cref="Layouts"/>.</summary>
        public void SetLayout(UniTextKeyboardLayout newLayout)
        {
            EnsureBuilt();
            if (newLayout == null) return;
            var i = m_Layouts.IndexOf(newLayout);
            if (i >= 0) textLayoutIndex = i;
            shift = KeyboardShiftState.Off;
            ApplyLayout(newLayout, 0);
        }

        /// <summary>Rebuilds every key (after changing <see cref="Layouts"/>, mode or sizes).</summary>
        public void Rebuild()
        {
            built = false;
            EnsureBuilt();
        }

        /// <summary>The first key of the current page whose label or typed text is <paramref name="labelOrText"/> (with the current shift state), or whose action name matches.</summary>
        public UniTextKeyboardKey FindKey(string labelOrText)
        {
            EnsureBuilt();
            if (string.IsNullOrEmpty(labelOrText)) return null;
            var shifted = shift != KeyboardShiftState.Off;
            for (var i = 0; i < keyCount; i++)
            {
                var k = keys[i];
                if (k.hiddenByLayout) continue;
                if (k.def.action == KeyboardKeyAction.Text && (k.def.Output(shifted) == labelOrText || k.def.Label(shifted) == labelOrText)) return k;
            }
            for (var i = 0; i < keyCount; i++)
            {
                var k = keys[i];
                if (k.hiddenByLayout) continue;
                if (k.def.action != KeyboardKeyAction.Text && (k.def.label == labelOrText || k.def.action.ToString() == labelOrText)) return k;
            }
            return null;
        }

        /// <summary>The key under <paramref name="worldPoint"/> (projected onto the keyboard plane), or null.</summary>
        public UniTextKeyboardKey GetKeyAt(Vector3 worldPoint)
        {
            EnsureBuilt();
            var local = (Vector2)transform.InverseTransformPoint(worldPoint);
            for (var i = 0; i < keyCount; i++)
                if (!keys[i].hiddenByLayout && keys[i].localRect.Contains(local)) return keys[i];
            return null;
        }

        /// <summary>Presses <paramref name="key"/> as a pointer would (for poke / custom interaction code). Pair with <see cref="ReleaseKey"/>.</summary>
        public void PressKey(UniTextKeyboardKey key) { if (key != null && key.keyboard == this) KeyDown(key, null); }

        /// <summary>Releases a key pressed with <see cref="PressKey"/>.</summary>
        public void ReleaseKey(UniTextKeyboardKey key) { if (key != null && key.keyboard == this) KeyUp(key); }

        /// <summary>Presses and releases <paramref name="key"/>.</summary>
        public void TapKey(UniTextKeyboardKey key) { PressKey(key); ReleaseKey(key); }

        /// <summary>Whether a press on this keyboard keeps <paramref name="field"/> focused (it is the field being typed into).</summary>
        public bool KeepsFocus(UniTextInputField field) => visible && (target == null || target == field);

        // ---- factory -------------------------------------------------------------------------------

        /// <summary>
        /// Creates a keyboard (hidden). <paramref name="world"/>: <see cref="UniTextWorld"/> keys (the object is
        /// scaled so a key is <paramref name="physicalKeySize"/> metres when it has no scaled parent); otherwise
        /// <see cref="UniText"/> keys, under <paramref name="parent"/>'s Canvas.
        /// </summary>
        public static UniTextKeyboard Create(Transform parent, bool world, UniTextFontStack fonts = null, float physicalKeySize = 0.045f)
        {
            s_creating++;
            try
            {
                var go = new GameObject("OpenGlyph Keyboard", typeof(RectTransform));
                var rt = (RectTransform)go.transform;
                if (parent != null) rt.SetParent(parent, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                var k = go.AddComponent<UniTextKeyboard>();
                k.m_Mode = world ? KeyboardSurfaceMode.World : KeyboardSurfaceMode.Canvas;
                k.m_FontStack = fonts;
                if (world) k.SetPhysicalKeySize(physicalKeySize);
                k.built = false;
                k.EnsureBuilt();
                k.visible = false;
                k.ApplyVisibility();
                return k;
            }
            finally { s_creating--; }
        }

        /// <summary>Scales the keyboard so a 1-unit key is <paramref name="metres"/> wide in the world.</summary>
        public void SetPhysicalKeySize(float metres)
        {
            var parentScale = transform.parent != null ? transform.parent.lossyScale.x : 1f;
            if (Mathf.Abs(parentScale) < 1e-8f) parentScale = 1f;
            transform.localScale = Vector3.one * (metres / m_KeySize / parentScale);
        }

        /// <summary>
        /// The keyboard <paramref name="field"/> uses: its <see cref="UniTextInputField.BuiltInKeyboard"/>, else an
        /// enabled keyboard that suits it (same Canvas, or world space for world fields), else (when
        /// <see cref="AutoCreate"/>) a new one: world space for world fields, on the field's Canvas otherwise
        /// (docked at the bottom of a Screen Space canvas, below the field on a World Space canvas).
        /// </summary>
        public static UniTextKeyboard ForField(UniTextInputField field)
        {
            if (field == null) return null;
            if (field.BuiltInKeyboard != null) return field.BuiltInKeyboard;
            var canvas = field.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            var worldField = root == null || (field.TextComponent != null && field.TextComponent.IsWorldText);
            UniTextKeyboard any = null;
            for (var i = 0; i < s_instances.Count; i++)
            {
                var k = s_instances[i];
                if (k == null || !k.isActiveAndEnabled) continue;
                k.EnsureBuilt();
                if (worldField ? k.isWorld : !k.isWorld && k.RootCanvas == root) return k;
                if (any == null) any = k;
            }
            if (!AutoCreate) return any;
            return CreateFor(field, worldField ? null : root);
        }

        private Canvas RootCanvas
        {
            get
            {
                var c = GetComponentInParent<Canvas>();
                return c != null ? c.rootCanvas : null;
            }
        }

        private static UniTextKeyboard CreateFor(UniTextInputField field, Canvas canvas)
        {
            var fonts = field.TextComponent != null ? field.TextComponent.FontStack : null;
            UniTextKeyboard k;
            if (canvas == null)
            {
                k = Create(null, true, fonts);
                k.m_Placement = KeyboardPlacement.BelowField;
            }
            else if (canvas.renderMode == RenderMode.WorldSpace)
            {
                k = Create(canvas.transform, false, fonts);
                k.SetPhysicalKeySize(0.045f);
                k.m_Placement = KeyboardPlacement.BelowField;
            }
            else
            {
                k = Create(canvas.transform, false, fonts);
                var rt = (RectTransform)k.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, 12f);
                var crt = (RectTransform)canvas.transform;
                var w = rt.rect.width;
                var avail = crt.rect.width * 0.96f;
                if (w > avail && avail > 1f) rt.localScale = Vector3.one * (avail / w);
                k.m_Placement = KeyboardPlacement.Manual;
            }
            k.name = "OpenGlyph Keyboard (auto)";
            return k;
        }

        // ---- lifecycle -----------------------------------------------------------------------------

        private void OnEnable()
        {
            if (!s_instances.Contains(this)) s_instances.Add(this);
            if (s_creating > 0) return;
            built = false;
            EnsureBuilt();
            visible = Application.isPlaying ? m_StartVisible : true;
            ApplyVisibility();
        }

        private void OnDisable()
        {
            s_instances.Remove(this);
            ReleaseAll();
            if (target != null)
            {
                var f = target;
                target = null;
                visible = false;
                if (f.IsFocused) f.DeactivateInputField();
            }
        }

        private void OnDestroy()
        {
            s_instances.Remove(this);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!built || !isActiveAndEnabled) return;
            // OnValidate cannot create objects; rebuild on the next editor tick.
            UnityEditor.EditorApplication.delayCall += () => { if (this != null && isActiveAndEnabled) Rebuild(); };
        }
#endif

        private void Update()
        {
            if (!visible) return;
            var now = UniTextInputField.Now;
            var dt = float.IsNaN(lastUpdate) ? 0f : Mathf.Clamp(now - lastUpdate, 0f, 0.1f);
            lastUpdate = now;
            if (repeatKey != null)
            {
                if (!repeatKey.IsPressed) repeatKey = null;
                else if (now >= repeatNext)
                {
                    repeatNext = now + 1f / Mathf.Max(1f, InputFieldKeyboardSources.RepeatRate);
                    Trigger(repeatKey, null);
                }
            }
            if (m_Placement == KeyboardPlacement.FollowHead) UpdateFollow(dt);
        }

        // ---- opening (from the field's touch keyboard adapter) -------------------------------------

        /// <summary>Opens for <paramref name="field"/>: picks the layout for the keyboard type, places and shows the keyboard.</summary>
        internal void Open(UniTextInputField field, TouchScreenKeyboardType type, bool isMultiline, bool isSecure)
        {
            EnsureBuilt();
            ReleaseAll();
            target = field;
            secure = isSecure;
            multiline = isMultiline;
            shift = KeyboardShiftState.Off;
            ApplyLayout(LayoutFor(type), 0);
            following = false;
            Place(field);
            SetVisible(true);
        }

        /// <summary>Closes when still bound to <paramref name="field"/>.</summary>
        internal void Close(UniTextInputField field)
        {
            if (target != field) return;
            target = null;
            secure = false;
            SetVisible(false);
        }

        /// <summary>The layout used for a field with the given keyboard type.</summary>
        public UniTextKeyboardLayout LayoutFor(TouchScreenKeyboardType type)
        {
            switch (type)
            {
                case TouchScreenKeyboardType.NumberPad:
                case TouchScreenKeyboardType.DecimalPad:
                case TouchScreenKeyboardType.PhonePad:
                    return NumericLayout;
                case TouchScreenKeyboardType.EmailAddress:
                    return EmailLayout;
                default:
                    return TextLayout;
            }
        }

        /// <summary>The current text layout (the entry of <see cref="Layouts"/> the layout key selected).</summary>
        public UniTextKeyboardLayout TextLayout
        {
            get
            {
                var n = CycleCount;
                if (n == 0) return UniTextKeyboardLayout.Qwerty;
                textLayoutIndex = Mathf.Clamp(textLayoutIndex, 0, n - 1);
                var l = m_Layouts[textLayoutIndex];
                return l != null ? l : UniTextKeyboardLayout.Qwerty;
            }
        }

        private int CycleCount
        {
            get
            {
                if (m_Layouts == null) return 0;
                // Trailing nulls (an unassigned Inspector slot) are ignored.
                var n = m_Layouts.Count;
                while (n > 0 && m_Layouts[n - 1] == null) n--;
                return n;
            }
        }

        private void SetVisible(bool v)
        {
            if (visible == v && visibilityApplied) return;
            visible = v;
            if (!v) { ReleaseAll(); following = false; }
            lastUpdate = float.NaN;
            ApplyVisibility();
            if (v) { Shown?.Invoke(); onShown.Invoke(); }
            else { Hidden?.Invoke(); onHidden.Invoke(); }
        }

        /// <summary>Shows or hides without rebuilding: renderers and colliders (world), culling and raycasts (Canvas).</summary>
        private void ApplyVisibility()
        {
            visibilityApplied = true;
            if (!built) return;
            if (isWorld)
            {
                if (worldSurface != null) worldSurface.Renderer.enabled = visible;
                for (var i = 0; i < keys.Count; i++)
                {
                    var k = keys[i];
                    if (k == null || k.label == null) continue;
                    var on = visible && i < keyCount && !k.hiddenByLayout;
                    var r = k.label.WorldRenderer;
                    if (r != null) r.enabled = on;
                    if (k.label is UniTextWorld w) w.Collider = on ? WorldTextCollider.Always : WorldTextCollider.None;
                }
            }
            else
            {
                if (canvasGroup != null)
                {
                    canvasGroup.alpha = visible ? 1f : 0f;
                    canvasGroup.blocksRaycasts = visible;
                    canvasGroup.interactable = visible;
                }
                GetComponentsInChildren(true, canvasRendererScratch);
                for (var i = 0; i < canvasRendererScratch.Count; i++) canvasRendererScratch[i].cull = !visible;
                canvasRendererScratch.Clear();
            }
            if (!visible) HidePreview();
        }

        // ---- key events ----------------------------------------------------------------------------

        internal void KeyDown(UniTextKeyboardKey k, PointerEventData e)
        {
            if (!visible || k.hiddenByLayout) return;
            k.pressCount++;
            ApplyKeyState(k);
            if (k.def.action == KeyboardKeyAction.Text && m_ShowKeyPreview && !secure) ShowPreview(k);
            if (k.def.action == KeyboardKeyAction.Backspace)
            {
                repeatKey = k;
                repeatNext = UniTextInputField.Now + InputFieldKeyboardSources.RepeatDelay;
            }
            Trigger(k, e);
        }

        internal void KeyUp(UniTextKeyboardKey k)
        {
            if (k.pressCount == 0) return;
            k.pressCount--;
            if (k.pressCount == 0)
            {
                if (repeatKey == k) repeatKey = null;
                if (previewKey == k) HidePreview();
            }
            ApplyKeyState(k);
        }

        internal void KeyHover(UniTextKeyboardKey k, bool on)
        {
            if (k.hovered == on) return;
            k.hovered = on;
            ApplyKeyState(k);
        }

        private void ReleaseAll()
        {
            repeatKey = null;
            HidePreview();
            var changed = false;
            for (var i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                if (k == null || (k.pressCount == 0 && !k.hovered)) continue;
                k.pressCount = 0;
                k.hovered = false;
                SetKeyLook(k);
                changed = true;
            }
            if (changed) UploadColors();
        }

        /// <summary>The field keystrokes go to: the target while focused, else the focused field selected in the EventSystem.</summary>
        private UniTextInputField TypingTarget()
        {
            if (target != null && target.IsFocused) return target;
            var es = EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            if (sel != null && sel.TryGetComponent<UniTextInputField>(out var f) && f.IsFocused) return f;
            return target;
        }

        private void Trigger(UniTextKeyboardKey k, PointerEventData e)
        {
            var def = k.def;
            var field = TypingTarget();
            string id;
            switch (def.action)
            {
                case KeyboardKeyAction.Text:
                {
                    var s = def.Output(shift != KeyboardShiftState.Off);
                    id = s;
                    if (field != null) field.ProcessText(s);
                    if (shift == KeyboardShiftState.Once) { shift = KeyboardShiftState.Off; RefreshShift(); }
                    break;
                }
                case KeyboardKeyAction.Space:
                    id = " ";
                    if (field != null) field.ProcessText(" ");
                    if (shift == KeyboardShiftState.Once) { shift = KeyboardShiftState.Off; RefreshShift(); }
                    break;
                case KeyboardKeyAction.Backspace:
                    id = "Backspace";
                    if (field != null) field.ProcessKey(InputFieldKey.Backspace);
                    break;
                case KeyboardKeyAction.Enter:
                    id = "Enter";
                    if (field != null) field.ProcessKey(InputFieldKey.Enter);
                    break;
                case KeyboardKeyAction.Left:
                    id = "Left";
                    if (field != null) field.ProcessKey(InputFieldKey.Left);
                    break;
                case KeyboardKeyAction.Right:
                    id = "Right";
                    if (field != null) field.ProcessKey(InputFieldKey.Right);
                    break;
                case KeyboardKeyAction.Tab:
                    id = "Tab";
                    if (field != null) field.ProcessKey(InputFieldKey.Tab);
                    break;
                case KeyboardKeyAction.Shift:
                {
                    id = "Shift";
                    var now = UniTextInputField.Now;
                    if (shift == KeyboardShiftState.Once && now - lastShiftTap <= CapsLockDoubleTap) shift = KeyboardShiftState.Locked;
                    else shift = shift == KeyboardShiftState.Off ? KeyboardShiftState.Once : KeyboardShiftState.Off;
                    lastShiftTap = now;
                    RefreshShift();
                    break;
                }
                case KeyboardKeyAction.Page:
                    id = "Page";
                    shift = KeyboardShiftState.Off;
                    ApplyLayout(layout, def.page);
                    break;
                case KeyboardKeyAction.NextLayout:
                {
                    id = "NextLayout";
                    var n = CycleCount;
                    if (n > 1)
                    {
                        textLayoutIndex = (textLayoutIndex + 1) % n;
                        shift = KeyboardShiftState.Off;
                        ApplyLayout(TextLayout, 0);
                    }
                    break;
                }
                case KeyboardKeyAction.Hide:
                    id = "Hide";
                    RaiseKey(k, e, id);
                    Hide();
                    return;
                default:
                    id = def.action.ToString();
                    break;
            }
            RaiseKey(k, e, id);
        }

        private void RaiseKey(UniTextKeyboardKey k, PointerEventData e, string id)
        {
            KeyPressed?.Invoke(k, e);
            onKeyPressed.Invoke(id);
        }
    }
}
