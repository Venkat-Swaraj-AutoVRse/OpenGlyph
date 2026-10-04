using UnityEngine;
using UnityEngine.UI;

namespace LightSide
{
    /// <summary>
    /// Makes any <see cref="UniText"/> selectable and copyable without editing it: click/drag/double-click/
    /// triple-click and Shift/Ctrl+arrow selection, Ctrl+C (and the clipboard API), with the same
    /// grapheme- and BiDi-aware caret logic as <see cref="UniTextInputField"/>. Selection runs over the
    /// visible text (<see cref="UniText.CleanText"/>), so markup in the label is never copied or changed.
    /// </summary>
    /// <remarks>Add it to the GameObject of the UniText (it uses that component). No caret is drawn and the
    /// label's wrapping, overflow and source text are left as they are.</remarks>
    [AddComponentMenu("OpenGlyph/UniText Selectable Text")]
    [RequireComponent(typeof(UniText))]
    [DisallowMultipleComponent]
    public class UniTextSelectableText : UniTextInputField
    {
        /// <inheritdoc/>
        protected override bool OwnsText => false;

        protected override void Awake()
        {
            Configure();
            base.Awake();
        }

        protected override void OnEnable()
        {
            Configure();
            base.OnEnable();
        }

#if UNITY_EDITOR
        protected override void Reset()
        {
            base.Reset();
            Configure();
        }
#endif

        private void Configure()
        {
            if (m_TextComponent == null) m_TextComponent = GetComponent<UniText>();
            m_ReadOnly = true;
            m_OnFocusSelectAll = false;
            m_RestoreOriginalTextOnEscape = false;
            m_HideSoftKeyboard = true;
            m_TabNavigation = false;
            m_ContentType = InputFieldContentType.Custom;
            m_LineType = InputFieldLineType.MultiLineSubmit;
            // The label itself must not be tinted by selection state transitions.
            transition = Transition.None;
        }
    }
}
