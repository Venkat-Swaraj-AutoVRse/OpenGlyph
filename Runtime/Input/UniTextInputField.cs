using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LightSide
{
    /// <summary>
    /// Editable text field over a <see cref="UniText"/> (Canvas) or <see cref="UniTextWorld"/> (world space,
    /// no Canvas). Full Unicode editing on the engine's own layout: grapheme-cluster caret and deletion,
    /// BiDi visual caret, word and line navigation, mouse/touch/XR-ray selection, undo/redo, clipboard,
    /// IME composition, content types (number, e-mail, name, password, PIN, ...), character limit,
    /// horizontal/vertical scrolling, and an on-screen keyboard: the platform touch keyboard
    /// (<see cref="TouchScreenKeyboard"/>) on phones and tablets, the built-in <see cref="UniTextKeyboard"/> in
    /// XR (see <see cref="SoftKeyboard"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Hierarchy</b> (built by <see cref="Create"/> or the GameObject menu): the field (this
    /// component, a background Image on a Canvas) &gt; Text Area (the viewport; RectMask2D on a Canvas) &gt;
    /// Placeholder and Text (UniText / UniTextWorld components stretched over the Text Area). The caret and
    /// selection are drawn by two helper objects created next to the Text at runtime (not saved).</para>
    /// <para><b>Text and markup.</b> <see cref="Text"/> is the plain string that is edited. The text
    /// component shows it literally (markup is not parsed) unless <see cref="RichText"/> is on; then tags
    /// are rendered and the caret moves over the visible characters only.</para>
    /// <para><b>Input.</b> Keys, characters and IME composition arrive through <see cref="ProcessKey"/>,
    /// <see cref="ProcessText"/> and <see cref="SetComposition"/>, fed each frame by
    /// <see cref="KeyboardSource"/> (Input System or legacy Input Manager, chosen at compile time; see
    /// <see cref="InputFieldKeyboardSources"/>). Call them directly to drive the field from a virtual
    /// keyboard. Pointer events come from the EventSystem (also XRI ray interactors on world fields).</para>
    /// <para>Positions (<see cref="CaretPosition"/>, selection) are UTF-16 string indices that are always
    /// on grapheme-cluster boundaries.</para>
    /// </remarks>
    [AddComponentMenu("OpenGlyph/UniText Input Field")]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public partial class UniTextInputField : Selectable, IBeginDragHandler, IDragHandler, IEndDragHandler, ISubmitHandler
    {
        // ---- serialized configuration ------------------------------------------------------------

        [SerializeField, Tooltip("The text component that shows the field's text (UniText, UniTextWorld or a GlyphMeshPro component).")]
        protected UniText m_TextComponent;

        [SerializeField, Tooltip("The visible area the text scrolls in (the text component's parent). A RectMask2D on it clips Canvas fields.")]
        protected RectTransform m_TextViewport;

        [SerializeField, Tooltip("Shown while the text is empty (any Graphic; usually a UniText).")]
        protected Graphic m_Placeholder;

        [SerializeField, TextArea(1, 10)]
        protected string m_Text = string.Empty;

        [SerializeField] protected InputFieldContentType m_ContentType = InputFieldContentType.Standard;
        [SerializeField] protected InputFieldLineType m_LineType = InputFieldLineType.SingleLine;
        [SerializeField] protected InputFieldInputType m_InputType = InputFieldInputType.Standard;
        [SerializeField] protected TouchScreenKeyboardType m_KeyboardType = TouchScreenKeyboardType.Default;
        [SerializeField] protected InputFieldCharacterValidation m_CharacterValidation = InputFieldCharacterValidation.None;

        [SerializeField, Tooltip("Maximum length in UTF-16 units (0 = no limit). Never splits a grapheme.")]
        protected int m_CharacterLimit;

        [SerializeField, Tooltip("Mask character for Password/Pin (one per grapheme).")]
        protected char m_AsteriskChar = '*';

        [SerializeField] protected bool m_ReadOnly;

        [SerializeField, Tooltip("Render markup in the text (off: the text is shown literally; tags typed by the user are plain text).")]
        protected bool m_RichText;

        [SerializeField, Range(0f, 4f), Tooltip("Caret blinks per second (0 = steady caret).")]
        protected float m_CaretBlinkRate = 0.85f;

        [SerializeField, Tooltip("Caret width in the text's local units.")]
        protected float m_CaretWidth = 2f;

        [SerializeField] protected InputFieldCaretShape m_CaretShape = InputFieldCaretShape.Bar;
        [SerializeField] protected bool m_CustomCaretColor;
        [SerializeField] protected Color m_CaretColor = new(0.196f, 0.196f, 0.196f, 1f);
        [SerializeField] protected Color m_SelectionColor = new(0.659f, 0.808f, 1f, 0.753f);

        [SerializeField, Tooltip("Optional object moved to the caret instead of the built-in caret (a sprite, a 3D cursor, ...).")]
        protected Transform m_CustomCaret;

        [SerializeField, Tooltip("Select all text when the field is focused by keyboard/navigation (a click places the caret).")]
        protected bool m_OnFocusSelectAll = true;

        [SerializeField, Tooltip("Collapse the selection when the field loses focus.")]
        protected bool m_ResetOnDeActivation = true;

        [SerializeField, Tooltip("Escape restores the text the field had when it was focused.")]
        protected bool m_RestoreOriginalTextOnEscape = true;

        [SerializeField, Tooltip("Tab / Shift+Tab move focus to the next / previous selectable.")]
        protected bool m_TabNavigation = true;

        [SerializeField, Tooltip("Hide the platform's input line above the touch keyboard (iOS/Android).")]
        protected bool m_HideMobileInput;

        [SerializeField, Tooltip("Never open any on-screen keyboard (same as Soft Keyboard = None).")]
        protected bool m_HideSoftKeyboard;

        [SerializeField, Tooltip("On-screen keyboard opened on focus. Auto: the system keyboard where it works (phones), the built-in OpenGlyph keyboard when an XR device is active (Quest/OpenXR, PC VR), none on desktop.")]
        protected InputFieldSoftKeyboard m_SoftKeyboard = InputFieldSoftKeyboard.Auto;

        [SerializeField, Tooltip("The built-in keyboard to use (empty: an enabled keyboard in the scene, or one created on demand).")]
        protected UniTextKeyboard m_Keyboard;

        [SerializeField, Tooltip("Seconds between keystrokes that still belong to one undo step.")]
        protected float m_UndoGroupTimeout = 1f;

        [SerializeField, Tooltip("Pasted text is cut to this many UTF-16 units.")]
        protected int m_MaxPasteLength = 16384;

        // ---- events --------------------------------------------------------------------------------

        /// <summary>The text changed by editing or by <see cref="Text"/> (not <see cref="SetTextWithoutNotify"/>).</summary>
        public InputFieldTextEvent onValueChanged = new();
        /// <summary>Editing ended (focus lost, submit, cancel).</summary>
        public InputFieldTextEvent onEndEdit = new();
        /// <summary>Enter in a single-line / MultiLineSubmit field, or Done on the touch keyboard.</summary>
        public InputFieldTextEvent onSubmit = new();
        /// <summary>The field was selected by the EventSystem.</summary>
        public InputFieldTextEvent onSelect = new();
        /// <summary>The field was deselected by the EventSystem.</summary>
        public InputFieldTextEvent onDeselect = new();
        /// <summary>A non-empty selection was made or changed (text, start, end).</summary>
        public InputFieldSelectionEvent onTextSelection = new();
        /// <summary>The selection became empty.</summary>
        public InputFieldSelectionEvent onEndTextSelection = new();
        /// <summary>Optional character filter; see <see cref="InputFieldValidateInput"/>.</summary>
        public InputFieldValidateInput onValidateInput;

        /// <summary>C# counterpart of <see cref="onValueChanged"/>.</summary>
        public event Action<string> ValueChanged;
        /// <summary>C# counterpart of <see cref="onEndEdit"/>.</summary>
        public event Action<string> EndEdit;
        /// <summary>C# counterpart of <see cref="onSubmit"/>.</summary>
        public event Action<string> Submitted;
        /// <summary>C# counterpart of <see cref="onSelect"/>.</summary>
        public event Action<string> Selected;
        /// <summary>C# counterpart of <see cref="onDeselect"/>.</summary>
        public event Action<string> Deselected;
        /// <summary>The selection changed: (text, start, end); start == end when it was cleared.</summary>
        public event Action<string, int, int> SelectionChanged;

        // ---- runtime state -------------------------------------------------------------------------

        private bool focused;
        private string originalText = string.Empty;
        private int anchor, caret;
        private bool caretUpstream;
        private float desiredX = float.NaN;
        private string composition = string.Empty;
        private char pendingHighSurrogate;
        private readonly TextSegments srcSeg = new();
        private readonly TextSegments scratchSeg = new();
        private readonly TextCaretMap map = new();
        private readonly TextEditHistory history = new();
        private readonly List<Rect> rectScratch = new();
        private bool dragging;
        private int lastSelStart = -1, lastSelEnd = -1;
        private float blinkStart;
        private int caretVisibleState = -1;
        private IInputFieldKeyboardSource keyboardSource;
        private bool keyboardSourceAssigned;
        private bool keyboardBegun;
        private IInputFieldTouchKeyboard touchKeyboard;
        private IInputFieldTouchKeyboard openKeyboard;
        private UniTextKeyboardTouchAdapter builtInAdapter;
        private bool touchOpen;
        private string touchLastText;
        private IInputFieldClipboard clipboard;
        private UniText subscribedText;
        private bool keepFocusReselect;
        private bool reselecting;

        /// <summary>Clock used for caret blink, key repeat and undo grouping (default <see cref="Time.unscaledTime"/>). For replays and tests.</summary>
        public static Func<float> TimeSource;

        /// <summary>The current time of <see cref="TimeSource"/>.</summary>
        public static float Now => TimeSource != null ? TimeSource() : Time.unscaledTime;

        // ---- public API ----------------------------------------------------------------------------

        /// <summary>The field's text (plain string). Setting it validates, applies the character limit, moves the caret into range and raises <see cref="onValueChanged"/>.</summary>
        public string Text
        {
            get => m_Text;
            set => SetText(value, true);
        }

        /// <summary>Sets the text without raising <see cref="onValueChanged"/>.</summary>
        public void SetTextWithoutNotify(string value) => SetText(value, false);

        /// <summary>The text component that shows the text.</summary>
        public UniText TextComponent
        {
            get => m_TextComponent;
            set
            {
                if (m_TextComponent == value) return;
                Unsubscribe();
                m_TextComponent = value;
                if (isActiveAndEnabled) { Subscribe(); ConfigureTextComponent(); UpdateDisplay(); }
            }
        }

        /// <summary>The viewport the text scrolls in.</summary>
        public RectTransform TextViewport
        {
            get => m_TextViewport;
            set { m_TextViewport = value; RefreshVisuals(); }
        }

        /// <summary>Shown while the text is empty.</summary>
        public Graphic Placeholder
        {
            get => m_Placeholder;
            set { m_Placeholder = value; UpdatePlaceholder(); }
        }

        /// <summary>Content type: sets line type, input type, keyboard type and validation together.</summary>
        public InputFieldContentType ContentType
        {
            get => m_ContentType;
            set { m_ContentType = value; EnforceContentType(); UpdateDisplay(); }
        }

        public InputFieldLineType LineType
        {
            get => m_LineType;
            set
            {
                if (m_LineType == value) return;
                m_LineType = value;
                if (m_ContentType != InputFieldContentType.Standard && m_ContentType != InputFieldContentType.Autocorrected)
                    m_ContentType = InputFieldContentType.Custom;
                ConfigureTextComponent();
                if (value == InputFieldLineType.SingleLine && m_Text.IndexOf('\n') >= 0) SetText(m_Text, true);
                else UpdateDisplay();
            }
        }

        public InputFieldInputType InputType
        {
            get => m_InputType;
            set { if (m_InputType == value) return; m_InputType = value; SetToCustom(); UpdateDisplay(); }
        }

        public TouchScreenKeyboardType KeyboardType
        {
            get => m_KeyboardType;
            set { m_KeyboardType = value; SetToCustom(); }
        }

        public InputFieldCharacterValidation CharacterValidation
        {
            get => m_CharacterValidation;
            set { m_CharacterValidation = value; SetToCustom(); }
        }

        /// <summary>Maximum length in UTF-16 units (0 = none). Lowering it cuts the text at a grapheme boundary.</summary>
        public int CharacterLimit
        {
            get => m_CharacterLimit;
            set
            {
                m_CharacterLimit = Mathf.Max(0, value);
                if (m_CharacterLimit > 0 && m_Text.Length > m_CharacterLimit) SetText(m_Text, true);
            }
        }

        public char AsteriskChar
        {
            get => m_AsteriskChar;
            set { if (m_AsteriskChar == value) return; m_AsteriskChar = value; UpdateDisplay(); }
        }

        /// <summary>No editing; the caret is hidden but text can still be selected and copied.</summary>
        public bool ReadOnly
        {
            get => m_ReadOnly;
            set { m_ReadOnly = value; RefreshVisuals(); }
        }

        public bool RichText
        {
            get => m_RichText;
            set { if (m_RichText == value) return; m_RichText = value; lastPushed = null; UpdateDisplay(); }
        }

        public float CaretBlinkRate
        {
            get => m_CaretBlinkRate;
            set { m_CaretBlinkRate = Mathf.Max(0f, value); ResetBlink(); }
        }

        public float CaretWidth
        {
            get => m_CaretWidth;
            set { m_CaretWidth = Mathf.Max(0f, value); RefreshVisuals(); }
        }

        public InputFieldCaretShape CaretShape
        {
            get => m_CaretShape;
            set { m_CaretShape = value; RefreshVisuals(); }
        }

        /// <summary>Caret colour (the text colour unless <see cref="CustomCaretColor"/>).</summary>
        public Color CaretColor
        {
            get => m_CustomCaretColor || m_TextComponent == null ? m_CaretColor : m_TextComponent.color;
            set { m_CaretColor = value; m_CustomCaretColor = true; RefreshVisuals(); }
        }

        public bool CustomCaretColor
        {
            get => m_CustomCaretColor;
            set { m_CustomCaretColor = value; RefreshVisuals(); }
        }

        public Color SelectionColor
        {
            get => m_SelectionColor;
            set { m_SelectionColor = value; RefreshVisuals(); }
        }

        /// <summary>Object moved to the caret instead of the built-in caret.</summary>
        public Transform CustomCaret
        {
            get => m_CustomCaret;
            set { m_CustomCaret = value; RefreshVisuals(); }
        }

        public bool OnFocusSelectAll { get => m_OnFocusSelectAll; set => m_OnFocusSelectAll = value; }
        public bool ResetOnDeActivation { get => m_ResetOnDeActivation; set => m_ResetOnDeActivation = value; }
        public bool RestoreOriginalTextOnEscape { get => m_RestoreOriginalTextOnEscape; set => m_RestoreOriginalTextOnEscape = value; }
        public bool TabNavigation { get => m_TabNavigation; set => m_TabNavigation = value; }
        public bool HideMobileInput { get => m_HideMobileInput; set => m_HideMobileInput = value; }
        public bool HideSoftKeyboard { get => m_HideSoftKeyboard; set => m_HideSoftKeyboard = value; }

        public float UndoGroupTimeout
        {
            get => m_UndoGroupTimeout;
            set => m_UndoGroupTimeout = Mathf.Max(0f, value);
        }

        public int MaxPasteLength
        {
            get => m_MaxPasteLength;
            set => m_MaxPasteLength = Mathf.Max(0, value);
        }

        /// <summary>Whether the field is being edited (has keyboard focus).</summary>
        public bool IsFocused => focused;

        /// <summary>Whether the text is multi-line.</summary>
        public bool MultiLine => m_LineType != InputFieldLineType.SingleLine;

        /// <summary>The caret (selection focus) as a string index. Setting it collapses the selection.</summary>
        public int CaretPosition
        {
            get => caret;
            set { var p = ClampSnap(value); anchor = caret = p; caretUpstream = false; AfterCaretMove(false); }
        }

        /// <summary>The fixed end of the selection.</summary>
        public int SelectionAnchorPosition
        {
            get => anchor;
            set { anchor = ClampSnap(value); AfterCaretMove(false); }
        }

        /// <summary>The moving end of the selection (= <see cref="CaretPosition"/>).</summary>
        public int SelectionFocusPosition
        {
            get => caret;
            set { caret = ClampSnap(value); caretUpstream = false; AfterCaretMove(false); }
        }

        public int SelectionStart => Mathf.Min(anchor, caret);
        public int SelectionEnd => Mathf.Max(anchor, caret);
        public bool HasSelection => anchor != caret;
        public string SelectedText => HasSelection ? m_Text.Substring(SelectionStart, SelectionEnd - SelectionStart) : string.Empty;

        /// <summary>The open IME composition ("" when none).</summary>
        public string CompositionString => composition;

        /// <summary>Selects [start, end) (clamped to grapheme boundaries); the caret goes to <paramref name="end"/>.</summary>
        public void SetSelection(int start, int end)
        {
            anchor = ClampSnap(start);
            caret = ClampSnap(end);
            caretUpstream = end > start;
            AfterCaretMove(false);
        }

        /// <summary>
        /// Where keys, characters and IME composition come from while focused. Defaults to
        /// <see cref="InputFieldKeyboardSources.CreateDefault"/>; set null to accept input only through
        /// <see cref="ProcessKey"/>/<see cref="ProcessText"/> (a custom in-VR keyboard).
        /// </summary>
        public IInputFieldKeyboardSource KeyboardSource
        {
            get => keyboardSourceAssigned ? keyboardSource : keyboardSource ??= InputFieldKeyboardSources.CreateDefault();
            set
            {
                if (keyboardBegun) { keyboardSource?.End(this); keyboardBegun = false; }
                keyboardSource = value;
                keyboardSourceAssigned = true;
                if (focused) BeginKeyboard();
            }
        }

        /// <summary>The system / platform touch keyboard (default <see cref="SystemTouchKeyboard"/>), used when <see cref="SoftKeyboard"/> resolves to System.</summary>
        public IInputFieldTouchKeyboard TouchKeyboard
        {
            get => touchKeyboard ??= new SystemTouchKeyboard();
            set
            {
                if (touchOpen) CloseTouchKeyboard();
                touchKeyboard = value;
                if (focused) OpenTouchKeyboard();
            }
        }

        /// <summary>Which on-screen keyboard opens on focus (default <see cref="InputFieldSoftKeyboard.Auto"/>).</summary>
        public InputFieldSoftKeyboard SoftKeyboard
        {
            get => m_SoftKeyboard;
            set
            {
                if (m_SoftKeyboard == value) return;
                m_SoftKeyboard = value;
                if (touchOpen) CloseTouchKeyboard();
                if (focused) OpenTouchKeyboard();
            }
        }

        /// <summary>The built-in keyboard this field uses (null: any enabled one in the scene, or one created on demand; see <see cref="UniTextKeyboard.ForField"/>).</summary>
        public UniTextKeyboard BuiltInKeyboard
        {
            get => m_Keyboard;
            set
            {
                if (m_Keyboard == value) return;
                var reopen = touchOpen && openKeyboard == builtInAdapter;
                if (reopen) CloseTouchKeyboard();
                m_Keyboard = value;
                if (reopen && focused) OpenTouchKeyboard();
            }
        }

        /// <summary>What <see cref="SoftKeyboard"/> resolves to now: System, BuiltIn or None (never Auto).</summary>
        public InputFieldSoftKeyboard ResolvedSoftKeyboard
        {
            get
            {
                if (m_HideSoftKeyboard || m_ReadOnly) return InputFieldSoftKeyboard.None;
                switch (m_SoftKeyboard)
                {
                    case InputFieldSoftKeyboard.System:
                    case InputFieldSoftKeyboard.BuiltIn:
                    case InputFieldSoftKeyboard.None:
                        return m_SoftKeyboard;
                    default:
                        if (InputFieldPlatform.SystemKeyboardUsable(TouchKeyboard)) return InputFieldSoftKeyboard.System;
                        return InputFieldPlatform.XRActive ? InputFieldSoftKeyboard.BuiltIn : InputFieldSoftKeyboard.None;
                }
            }
        }

        /// <summary>The on-screen keyboard open right now (the system one or the built-in adapter), or null.</summary>
        public IInputFieldTouchKeyboard ActiveSoftKeyboard => touchOpen ? openKeyboard : null;

        /// <summary>The built-in keyboard that is open for this field, or null.</summary>
        public UniTextKeyboard OpenBuiltInKeyboard => touchOpen && openKeyboard == builtInAdapter ? builtInAdapter?.Keyboard : null;

        /// <summary>Clipboard for cut/copy/paste (default: the system clipboard).</summary>
        public IInputFieldClipboard Clipboard
        {
            get => clipboard ?? SystemInputFieldClipboard.Instance;
            set => clipboard = value;
        }

        /// <summary>Undo steps available.</summary>
        public int UndoCount => history.UndoCount;
        /// <summary>Redo steps available.</summary>
        public int RedoCount => history.RedoCount;

        // ---- lifecycle -----------------------------------------------------------------------------

        protected override void Awake()
        {
            base.Awake();
            m_Text ??= string.Empty;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            m_Text ??= string.Empty;
            history.GroupTimeout = m_UndoGroupTimeout;
            EnforceContentType();
            Subscribe();
            ConfigureTextComponent();
            CaptureTextBase();
            if (OwnsText) UpdateDisplay(); else SyncExternalText();
        }

        protected override void OnDisable()
        {
            DeactivateInputField();
            Unsubscribe();
            RestoreTextBase();
            HideOverlays();
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            DestroyOverlays();
            base.OnDestroy();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            m_Text ??= string.Empty;
            m_CharacterLimit = Mathf.Max(0, m_CharacterLimit);
            EnforceContentType();
            if (isActiveAndEnabled)
            {
                ConfigureTextComponent();
                UpdateDisplay();
            }
        }
#endif

        private void Subscribe()
        {
            if (subscribedText == m_TextComponent) return;
            Unsubscribe();
            if (m_TextComponent == null) return;
            m_TextComponent.LayoutApplied += OnTextLayoutApplied;
            subscribedText = m_TextComponent;
        }

        private void Unsubscribe()
        {
            if (subscribedText != null) subscribedText.LayoutApplied -= OnTextLayoutApplied;
            subscribedText = null;
        }

        /// <summary>
        /// Settings the field needs on its text component: no wrap on a single line (it scrolls), wrap on
        /// multiple lines, and Overflow (no truncation; the field scrolls and clips instead).
        /// </summary>
        protected virtual void ConfigureTextComponent()
        {
            var t = m_TextComponent;
            if (t == null || !OwnsText) return;
            var wrap = MultiLine;
            if (t.WordWrap != wrap) t.WordWrap = wrap;
            if (t.Overflow != TextOverflow.Overflow) t.Overflow = TextOverflow.Overflow;
        }

        /// <summary>
        /// False for <see cref="UniTextSelectableText"/>: the field does not own the text; it selects over
        /// the text component's visible text (<see cref="UniText.CleanText"/>) and never writes to it.
        /// </summary>
        protected virtual bool OwnsText => true;

        /// <summary>For fields that do not own their text: take the component's current visible text.</summary>
        private void SyncExternalText()
        {
            if (OwnsText || m_TextComponent == null) return;
            var clean = m_TextComponent.CleanText ?? string.Empty;
            m_Text = clean;
            srcSeg.Build(m_Text);
            anchor = ClampSnap(anchor);
            caret = ClampSnap(caret);
            UpdateDisplay();
        }

        // ---- content type --------------------------------------------------------------------------

        private void SetToCustom()
        {
            if (m_ContentType != InputFieldContentType.Custom) m_ContentType = InputFieldContentType.Custom;
        }

        private void EnforceContentType()
        {
            switch (m_ContentType)
            {
                case InputFieldContentType.Standard:
                    m_InputType = InputFieldInputType.Standard;
                    m_KeyboardType = TouchScreenKeyboardType.Default;
                    m_CharacterValidation = InputFieldCharacterValidation.None;
                    break;
                case InputFieldContentType.Autocorrected:
                    m_InputType = InputFieldInputType.AutoCorrect;
                    m_KeyboardType = TouchScreenKeyboardType.Default;
                    m_CharacterValidation = InputFieldCharacterValidation.None;
                    break;
                case InputFieldContentType.IntegerNumber:
                    Single(InputFieldInputType.Standard, TouchScreenKeyboardType.NumberPad, InputFieldCharacterValidation.Integer);
                    break;
                case InputFieldContentType.DecimalNumber:
                    Single(InputFieldInputType.Standard, TouchScreenKeyboardType.DecimalPad, InputFieldCharacterValidation.Decimal);
                    break;
                case InputFieldContentType.Alphanumeric:
                    Single(InputFieldInputType.Standard, TouchScreenKeyboardType.ASCIICapable, InputFieldCharacterValidation.Alphanumeric);
                    break;
                case InputFieldContentType.Name:
                    Single(InputFieldInputType.Standard, TouchScreenKeyboardType.Default, InputFieldCharacterValidation.Name);
                    break;
                case InputFieldContentType.EmailAddress:
                    Single(InputFieldInputType.Standard, TouchScreenKeyboardType.EmailAddress, InputFieldCharacterValidation.EmailAddress);
                    break;
                case InputFieldContentType.Password:
                    Single(InputFieldInputType.Password, TouchScreenKeyboardType.Default, InputFieldCharacterValidation.None);
                    break;
                case InputFieldContentType.Pin:
                    Single(InputFieldInputType.Password, TouchScreenKeyboardType.NumberPad, InputFieldCharacterValidation.Digit);
                    break;
            }

            void Single(InputFieldInputType it, TouchScreenKeyboardType kt, InputFieldCharacterValidation cv)
            {
                m_LineType = InputFieldLineType.SingleLine;
                m_InputType = it;
                m_KeyboardType = kt;
                m_CharacterValidation = cv;
            }
        }

        // ---- activation ----------------------------------------------------------------------------

        /// <summary>Gives the field keyboard focus (opens the touch keyboard where there is one).</summary>
        public void ActivateInputField() => Activate(m_OnFocusSelectAll);

        private void Activate(bool selectAll)
        {
            if (focused || !IsActive() || !IsInteractable()) return;
            focused = true;
            originalText = m_Text;
            composition = string.Empty;
            pendingHighSurrogate = '\0';
            history.GroupTimeout = m_UndoGroupTimeout;
            history.Break();
            if (selectAll) { anchor = 0; caret = m_Text.Length; caretUpstream = true; }
            else { anchor = ClampSnap(anchor); caret = ClampSnap(caret); }
            desiredX = float.NaN;
            BeginKeyboard();
            OpenTouchKeyboard();
            ResetBlink();
            UpdateScroll();
            RefreshVisuals();
            NotifySelection();
        }

        /// <summary>Ends editing: closes the touch keyboard, hides the caret and raises <see cref="onEndEdit"/>.</summary>
        public void DeactivateInputField(bool clearSelection = false)
        {
            if (!focused) return;
            if (composition.Length > 0) { composition = string.Empty; UpdateDisplay(); }
            focused = false;
            dragging = false;
            EndKeyboard();
            CloseTouchKeyboard();
            if (m_ResetOnDeActivation || clearSelection) anchor = caret;
            history.Break();
            RefreshVisuals();
            NotifySelection();
            onEndEdit.Invoke(m_Text);
            EndEdit?.Invoke(m_Text);
        }

        private void BeginKeyboard()
        {
            var src = KeyboardSource;
            if (src == null || keyboardBegun) return;
            src.Begin(this);
            keyboardBegun = true;
        }

        private void EndKeyboard()
        {
            if (!keyboardBegun) return;
            keyboardBegun = false;
            keyboardSource?.End(this);
        }

        public override void OnSelect(BaseEventData eventData)
        {
            base.OnSelect(eventData);
            // Re-selected after a press on an on-screen keyboard: still the same editing session.
            if (reselecting) return;
            onSelect.Invoke(m_Text);
            Selected?.Invoke(m_Text);
            Activate(m_OnFocusSelectAll && !(eventData is PointerEventData));
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            if (focused && PressKeepsFocus(eventData))
            {
                // A press on an on-screen keyboard (IInputFieldFocusKeeper): keep editing and select this
                // field again once the EventSystem has finished changing the selection (LateUpdate).
                keepFocusReselect = true;
                base.OnDeselect(eventData);
                return;
            }
            DeactivateInputField();
            base.OnDeselect(eventData);
            onDeselect.Invoke(m_Text);
            Deselected?.Invoke(m_Text);
        }

        private bool PressKeepsFocus(BaseEventData eventData)
        {
            if (!(eventData is PointerEventData pe)) return false;
            var go = pe.pointerCurrentRaycast.gameObject;
            if (go == null) go = pe.pointerPressRaycast.gameObject;
            if (go == null) go = pe.pointerEnter;
            if (go == null) return false;
            var keeper = go.GetComponentInParent<IInputFieldFocusKeeper>();
            return keeper != null && keeper.KeepsFocus(this);
        }

        /// <summary>Selects this field again in the EventSystem after a focus-keeping press (see <see cref="IInputFieldFocusKeeper"/>).</summary>
        private void ReselectAfterKeeper()
        {
            var es = EventSystem.current;
            if (es == null) { keepFocusReselect = false; return; }
            if (es.alreadySelecting) return;
            keepFocusReselect = false;
            if (!focused) return;
            var cur = es.currentSelectedGameObject;
            if (cur == null)
            {
                reselecting = true;
                try { es.SetSelectedGameObject(gameObject); }
                finally { reselecting = false; }
            }
            else if (cur != gameObject) DeactivateInputField();
        }

        /// <summary>Submit (Enter / gamepad) on the selected but not focused field activates it.</summary>
        public virtual void OnSubmit(BaseEventData eventData)
        {
            if (!IsActive() || !IsInteractable() || focused) return;
            Activate(m_OnFocusSelectAll);
        }

        /// <summary>Per-frame work while focused: keyboard polling, touch keyboard sync, caret blink. No allocation while idle.</summary>
        protected virtual void LateUpdate()
        {
            if (keepFocusReselect) ReselectAfterKeeper();
            if (!focused) return;
            if (keyboardBegun && keyboardSource != null) keyboardSource.Poll(this);
            if (!focused) return;
            if (touchOpen) PollTouchKeyboard();
            UpdateBlink();
        }

        // ---- pointer -------------------------------------------------------------------------------

        public override void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (!IsActive() || !IsInteractable()) return;
            base.OnPointerDown(eventData);
            if (!focused) Activate(false);
            if (!focused) return;
            if (!TryPointerToLayout(eventData, out var lx, out var ly)) return;
            var clicks = Mathf.Max(1, eventData.clickCount);
            var shift = InputFieldKeyboardSources.ShiftHeldForPointer();
            PlaceFromLayout(lx, ly, shift && clicks == 1, clicks);
            dragging = true;
        }

        public virtual void OnBeginDrag(PointerEventData eventData)
        {
            dragging = focused && eventData.button == PointerEventData.InputButton.Left;
        }

        public virtual void OnDrag(PointerEventData eventData)
        {
            if (!dragging || !focused || eventData.button != PointerEventData.InputButton.Left) return;
            if (!TryPointerToLayout(eventData, out var lx, out var ly)) return;
            PlaceFromLayout(lx, ly, true, 1);
        }

        public virtual void OnEndDrag(PointerEventData eventData)
        {
            dragging = false;
        }

        /// <summary>
        /// Places the caret at a world-space point on the text plane (for XR pointers that do not go
        /// through the EventSystem). Returns the caret index.
        /// </summary>
        public int SetCaretFromWorldPoint(Vector3 worldPoint, bool extendSelection = false, int clickCount = 1)
        {
            if (m_TextComponent == null) return caret;
            if (!focused) Activate(false);
            var local = m_TextComponent.rectTransform.InverseTransformPoint(worldPoint);
            LocalToLayout(local, out var lx, out var ly);
            PlaceFromLayout(lx, ly, extendSelection, clickCount);
            return caret;
        }

        /// <summary>Places the caret where <paramref name="ray"/> meets the text plane. False when the ray misses the plane.</summary>
        public bool SetCaretFromRay(Ray ray, bool extendSelection = false, int clickCount = 1)
        {
            if (m_TextComponent == null) return false;
            var rt = m_TextComponent.rectTransform;
            var plane = new Plane(rt.forward, rt.position);
            if (!plane.Raycast(ray, out var enter) || enter < 0f) return false;
            SetCaretFromWorldPoint(ray.GetPoint(enter), extendSelection, clickCount);
            return true;
        }

        private bool TryPointerToLayout(PointerEventData e, out float lx, out float ly)
        {
            lx = ly = 0f;
            var t = m_TextComponent;
            if (t == null) return false;
            var rt = t.rectTransform;
            Vector3 local;
            if (!t.IsWorldText && t.canvas != null)
            {
                var cam = e.pressEventCamera != null ? e.pressEventCamera : e.enterEventCamera;
                if (cam == null && t.canvas.renderMode != RenderMode.ScreenSpaceOverlay) cam = t.canvas.worldCamera;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, cam, out var l2)) return false;
                local = l2;
            }
            else
            {
                // The raycast hit point (PhysicsRaycaster, XRI TrackedDevicePhysicsRaycaster) when there is one;
                // isValid also requires a raycaster module, which a scripted pointer may not set.
                var rr = e.pointerCurrentRaycast;
                if (rr.gameObject == null) rr = e.pointerPressRaycast;
                if (rr.gameObject != null && (rr.worldPosition != Vector3.zero || rr.worldNormal != Vector3.zero))
                {
                    local = rt.InverseTransformPoint(rr.worldPosition);
                }
                else
                {
                    var cam = e.enterEventCamera != null ? e.enterEventCamera : e.pressEventCamera != null ? e.pressEventCamera : Camera.main;
                    if (cam == null) return false;
                    var ray = cam.ScreenPointToRay(e.position);
                    var plane = new Plane(rt.forward, rt.position);
                    if (!plane.Raycast(ray, out var enter)) return false;
                    local = rt.InverseTransformPoint(ray.GetPoint(enter));
                }
            }
            LocalToLayout(local, out lx, out ly);
            return true;
        }

        private void LocalToLayout(Vector3 local, out float lx, out float ly)
        {
            var area = m_TextComponent.TextAreaRect;
            lx = local.x - area.xMin;
            ly = area.yMax - local.y;
        }

        private void PlaceFromLayout(float lx, float ly, bool extend, int clicks)
        {
            EnsureMap();
            var c = map.HitTest(lx, ly);
            var src = LayoutToSource(c.pos);
            if (clicks >= 3)
            {
                var line = map.LineOf(c.pos, c.upstream);
                var l = map.Lines[line];
                anchor = LayoutToSource(l.start);
                caret = LayoutToSource(l.end);
                caretUpstream = true;
            }
            else if (clicks == 2)
            {
                srcSeg.SegmentAt(src, out var ws, out var we);
                anchor = ws;
                caret = we;
                caretUpstream = true;
            }
            else
            {
                caret = src;
                caretUpstream = c.upstream;
                if (!extend) anchor = src;
            }
            AfterCaretMove(false);
        }
    }
}
