using System;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LightSide
{
    // Editing: text changes, keys, IME, clipboard, undo/redo, the touch keyboard.
    public partial class UniTextInputField
    {
        private readonly StringBuilder insertBuffer = new();
        private char[] validateBuffer = new char[64];
        private readonly char[] charScratch = new char[2];

        // ---- text ----------------------------------------------------------------------------------

        private void SetText(string value, bool notify)
        {
            if (!OwnsText)
            {
                if (m_TextComponent != null) m_TextComponent.Text = value ?? string.Empty;
                return;
            }
            value = FilterText(InputFieldValidation.Sanitize(value ?? string.Empty, MultiLine));
            if (value == m_Text) { UpdateDisplay(); return; }
            m_Text = value;
            history.Clear();
            composition = string.Empty;
            srcSeg.Build(m_Text);
            anchor = ClampSnap(anchor);
            caret = ClampSnap(caret);
            UpdateDisplay();
            PushTouchText();
            if (notify) SendValueChanged();
            NotifySelection();
        }

        /// <summary>Validation and character limit applied to a whole text (Text setter, touch keyboard text).</summary>
        private string FilterText(string value)
        {
            if (m_CharacterValidation != InputFieldCharacterValidation.None || onValidateInput != null)
            {
                insertBuffer.Clear();
                for (var i = 0; i < value.Length; i++)
                {
                    var c = value[i];
                    var next = ValidateAt(insertBuffer, i, value, c);
                    if (next == '\0')
                    {
                        if (char.IsHighSurrogate(c) && i + 1 < value.Length) i++;
                        continue;
                    }
                    insertBuffer.Append(next);
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                        insertBuffer.Append(value[++i]);
                }
                value = insertBuffer.ToString();
            }
            if (m_CharacterLimit > 0 && value.Length > m_CharacterLimit)
            {
                scratchSeg.Build(value);
                value = value.Substring(0, scratchSeg.Floor(m_CharacterLimit));
            }
            return value;
        }

        private char ValidateAt(StringBuilder prefix, int index, string full, char c)
        {
            if (onValidateInput != null) return onValidateInput(full, index, c);
            if (m_CharacterValidation == InputFieldCharacterValidation.None || m_CharacterValidation == InputFieldCharacterValidation.CustomValidator) return c;
            EnsureValidateBuffer(prefix.Length);
            prefix.CopyTo(0, validateBuffer, 0, prefix.Length);
            return InputFieldValidation.Validate(m_CharacterValidation, new ReadOnlySpan<char>(validateBuffer, 0, prefix.Length), ReadOnlySpan<char>.Empty, c);
        }

        private void EnsureValidateBuffer(int n)
        {
            if (validateBuffer.Length < n) validateBuffer = new char[Mathf.NextPowerOfTwo(n + 1)];
        }

        private int ClampSnap(int i)
        {
            if (srcSeg.Text != m_Text) srcSeg.Build(m_Text);
            return srcSeg.Floor(Mathf.Clamp(i, 0, m_Text.Length));
        }

        private void SendValueChanged()
        {
            onValueChanged.Invoke(m_Text);
            ValueChanged?.Invoke(m_Text);
        }

        private void NotifySelection()
        {
            var s = focused ? SelectionStart : caret;
            var e = focused ? SelectionEnd : caret;
            if (s == lastSelStart && e == lastSelEnd) return;
            var had = lastSelEnd > lastSelStart;
            lastSelStart = s;
            lastSelEnd = e;
            if (e > s) onTextSelection.Invoke(m_Text, s, e);
            else if (had) onEndTextSelection.Invoke(m_Text, s, e);
            SelectionChanged?.Invoke(m_Text, s, e);
        }

        // ---- input seam ----------------------------------------------------------------------------

        /// <summary>
        /// Handles an editing key (arrows, Home/End, Backspace/Delete, Enter, Tab, Escape, Ctrl/Cmd
        /// shortcuts) as the keyboard would. Returns true when the key was handled. Keys are ignored while
        /// an IME composition is open (the IME owns them).
        /// </summary>
        public bool ProcessKey(InputFieldKey key, InputFieldModifiers modifiers = InputFieldModifiers.None)
        {
            if (!focused) return false;
            if (composition.Length > 0) return false;
            var shift = (modifiers & InputFieldModifiers.Shift) != 0;
            var ctrl = (modifiers & (InputFieldModifiers.Control | InputFieldModifiers.Command)) != 0;
            var alt = (modifiers & InputFieldModifiers.Alt) != 0;
            var word = ctrl || alt;
            switch (key)
            {
                case InputFieldKey.Left: MoveHorizontal(-1, shift, word); return true;
                case InputFieldKey.Right: MoveHorizontal(+1, shift, word); return true;
                case InputFieldKey.Up: MoveVertical(-1, shift); return true;
                case InputFieldKey.Down: MoveVertical(+1, shift); return true;
                case InputFieldKey.PageUp: MoveVertical(-VisibleLineCount(), shift); return true;
                case InputFieldKey.PageDown: MoveVertical(VisibleLineCount(), shift); return true;
                case InputFieldKey.Home:
                    if (ctrl) MoveTo(0, false, shift); else MoveLineEdge(false, shift);
                    return true;
                case InputFieldKey.End:
                    if (ctrl) MoveTo(m_Text.Length, true, shift); else MoveLineEdge(true, shift);
                    return true;
                case InputFieldKey.Backspace:
                    if (m_ReadOnly) return true;
                    if (HasSelection) DeleteSelection(TextEditHistory.EditKind.Delete);
                    else if (word) DeleteRange(srcSeg.PrevWordStart(caret), caret, TextEditHistory.EditKind.Other);
                    else DeleteRange(srcSeg.Prev(caret), caret, TextEditHistory.EditKind.Delete);
                    return true;
                case InputFieldKey.Delete:
                    if (shift && !ctrl) { Cut(); return true; }
                    if (m_ReadOnly) return true;
                    if (HasSelection) DeleteSelection(TextEditHistory.EditKind.Delete);
                    else if (word) DeleteRange(caret, srcSeg.NextWordStart(caret), TextEditHistory.EditKind.Other);
                    else DeleteRange(caret, srcSeg.Next(caret), TextEditHistory.EditKind.Delete);
                    return true;
                case InputFieldKey.Enter:
                case InputFieldKey.KeypadEnter:
                    if (m_LineType == InputFieldLineType.MultiLineNewline && !ctrl)
                    {
                        if (!m_ReadOnly) InsertText("\n", TextEditHistory.EditKind.Other);
                    }
                    else Submit();
                    return true;
                case InputFieldKey.Tab:
                    if (!m_TabNavigation) return false;
                    NavigateTab(shift);
                    return true;
                case InputFieldKey.Escape:
                    Cancel();
                    return true;
                case InputFieldKey.A:
                    if (!ctrl) return false;
                    SelectAll();
                    return true;
                case InputFieldKey.C:
                    if (!ctrl) return false;
                    Copy();
                    return true;
                case InputFieldKey.V:
                    if (!ctrl) return false;
                    Paste();
                    return true;
                case InputFieldKey.X:
                    if (!ctrl) return false;
                    Cut();
                    return true;
                case InputFieldKey.Z:
                    if (!ctrl) return false;
                    if (shift) Redo(); else Undo();
                    return true;
                case InputFieldKey.Y:
                    if (!ctrl) return false;
                    Redo();
                    return true;
                case InputFieldKey.Insert:
                    if (ctrl) { Copy(); return true; }
                    if (shift) { Paste(); return true; }
                    return false;
            }
            return false;
        }

        /// <summary>Types <paramref name="s"/> at the caret (replacing the selection), as keystrokes would: validated, limited, undoable.</summary>
        public void ProcessText(string s)
        {
            if (!focused || m_ReadOnly || string.IsNullOrEmpty(s)) return;
            InsertText(s, s.Length == 1 || s.Length == 2 && char.IsSurrogatePair(s[0], s[1]) ? TextEditHistory.EditKind.Insert : TextEditHistory.EditKind.Other);
        }

        /// <summary>Types one UTF-16 unit; a surrogate pair may arrive as two calls.</summary>
        public void ProcessChar(char c)
        {
            if (!focused || m_ReadOnly) return;
            if (char.IsHighSurrogate(c)) { pendingHighSurrogate = c; return; }
            int n;
            if (char.IsLowSurrogate(c))
            {
                if (pendingHighSurrogate == '\0') return;
                charScratch[0] = pendingHighSurrogate;
                charScratch[1] = c;
                n = 2;
            }
            else { charScratch[0] = c; n = 1; }
            pendingHighSurrogate = '\0';
            InsertText(new string(charScratch, 0, n), TextEditHistory.EditKind.Insert);
        }

        /// <summary>
        /// Shows <paramref name="compositionString"/> (the IME's uncommitted text) at the caret, underlined.
        /// An empty string ends the composition (cancelled, or about to be committed through
        /// <see cref="ProcessText"/>). Opening a composition replaces the selection.
        /// </summary>
        public void SetComposition(string compositionString)
        {
            if (!focused || m_ReadOnly) return;
            compositionString ??= string.Empty;
            compositionString = InputFieldValidation.Sanitize(compositionString, false);
            if (compositionString == composition) return;
            if (composition.Length == 0 && compositionString.Length > 0 && HasSelection)
                DeleteSelection(TextEditHistory.EditKind.Other);
            composition = compositionString;
            ResetBlink();
            UpdateDisplay();
        }

        // ---- edits ---------------------------------------------------------------------------------

        private void InsertText(string s, TextEditHistory.EditKind kind)
        {
            if (m_ReadOnly) return;
            s = InputFieldValidation.Sanitize(s, MultiLine, m_MaxPasteLength > 0 ? m_MaxPasteLength : int.MaxValue);
            if (s.Length == 0) return;
            var start = SelectionStart;
            var end = SelectionEnd;

            // Validate character by character against the text as it will be.
            insertBuffer.Clear();
            var validating = m_CharacterValidation != InputFieldCharacterValidation.None && m_CharacterValidation != InputFieldCharacterValidation.CustomValidator;
            if (validating || onValidateInput != null)
            {
                // validateBuffer holds the prefix: the text before the insertion plus what was accepted so far.
                EnsureValidateBuffer(start + s.Length + 1);
                m_Text.CopyTo(0, validateBuffer, 0, start);
                var plen = start;
                for (var i = 0; i < s.Length; i++)
                {
                    var c = s[i];
                    var r = onValidateInput != null
                        ? onValidateInput(m_Text, start + insertBuffer.Length, c)
                        : InputFieldValidation.Validate(m_CharacterValidation, new ReadOnlySpan<char>(validateBuffer, 0, plen), m_Text.AsSpan(end), c);
                    var pair = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                    if (r == '\0') { if (pair) i++; continue; }
                    insertBuffer.Append(r);
                    validateBuffer[plen++] = r;
                    if (pair) { insertBuffer.Append(s[++i]); validateBuffer[plen++] = s[i]; }
                }
            }
            else insertBuffer.Append(s);

            // Character limit: keep whole graphemes only.
            if (m_CharacterLimit > 0)
            {
                var room = m_CharacterLimit - (m_Text.Length - (end - start));
                if (room <= 0) insertBuffer.Clear();
                else if (insertBuffer.Length > room)
                {
                    var ins = insertBuffer.ToString();
                    scratchSeg.Build(ins);
                    insertBuffer.Length = scratchSeg.Floor(room);
                }
            }
            if (insertBuffer.Length == 0) return;
            var inserted = insertBuffer.ToString();
            var breaksWord = inserted.Length > 0 && char.IsWhiteSpace(inserted[0]) && start > 0 && !char.IsWhiteSpace(m_Text[start - 1]);
            // Typing over a selection starts a new undo group that the following keystrokes continue.
            Replace(start, end, inserted, kind, breaksWord);
        }

        private void DeleteSelection(TextEditHistory.EditKind kind)
        {
            if (!HasSelection) return;
            Replace(SelectionStart, SelectionEnd, string.Empty, TextEditHistory.EditKind.Other, false);
        }

        private void DeleteRange(int a, int b, TextEditHistory.EditKind kind)
        {
            if (a > b) (a, b) = (b, a);
            a = Mathf.Clamp(a, 0, m_Text.Length);
            b = Mathf.Clamp(b, 0, m_Text.Length);
            if (a == b) return;
            Replace(a, b, string.Empty, kind, false);
        }

        /// <summary>Replaces [start, end) with <paramref name="inserted"/>: one undo record, caret after the insertion.</summary>
        private void Replace(int start, int end, string inserted, TextEditHistory.EditKind kind, bool breaksWord)
        {
            var before = new TextEditHistory.State { text = m_Text, anchor = anchor, caret = caret };
            var caretAfter = start + inserted.Length;
            history.GroupTimeout = m_UndoGroupTimeout;
            // A backspace group continues at the new (left) caret: compare against where the caret was.
            history.Record(before, kind, caretAfter, Now, breaksWord);
            m_Text = string.Concat(m_Text.Substring(0, start), inserted, m_Text.Substring(end));
            srcSeg.Build(m_Text);
            anchor = caret = srcSeg.Floor(Mathf.Clamp(caretAfter, 0, m_Text.Length));
            caretUpstream = inserted.Length > 0;
            desiredX = float.NaN;
            ResetBlink();
            UpdateDisplay();
            PushTouchText();
            SendValueChanged();
            NotifySelection();
        }

        // ---- commands ------------------------------------------------------------------------------

        /// <summary>Selects all text.</summary>
        public void SelectAll()
        {
            anchor = 0;
            caret = m_Text.Length;
            caretUpstream = true;
            AfterCaretMove(false);
        }

        /// <summary>Copies the selection (plain text). Password fields never copy.</summary>
        public void Copy()
        {
            if (!HasSelection || m_InputType == InputFieldInputType.Password) return;
            Clipboard.Text = SelectedText;
        }

        /// <summary>Copies and deletes the selection.</summary>
        public void Cut()
        {
            if (!HasSelection || m_ReadOnly || m_InputType == InputFieldInputType.Password) return;
            Clipboard.Text = SelectedText;
            DeleteSelection(TextEditHistory.EditKind.Other);
        }

        /// <summary>Inserts the clipboard text: sanitised (control characters removed, newlines only in multi-line), capped by <see cref="MaxPasteLength"/> and the character limit, validated.</summary>
        public void Paste()
        {
            if (m_ReadOnly) return;
            var s = Clipboard.Text;
            if (string.IsNullOrEmpty(s)) return;
            history.Break();
            InsertText(s, TextEditHistory.EditKind.Other);
            history.Break();
        }

        /// <summary>Undoes the last edit group.</summary>
        public bool Undo()
        {
            if (m_ReadOnly) return false;
            var cur = new TextEditHistory.State { text = m_Text, anchor = anchor, caret = caret };
            if (!history.TryUndo(cur, out var r)) return false;
            ApplyHistoryState(r);
            return true;
        }

        /// <summary>Redoes the last undone edit group.</summary>
        public bool Redo()
        {
            if (m_ReadOnly) return false;
            var cur = new TextEditHistory.State { text = m_Text, anchor = anchor, caret = caret };
            if (!history.TryRedo(cur, out var r)) return false;
            ApplyHistoryState(r);
            return true;
        }

        private void ApplyHistoryState(TextEditHistory.State s)
        {
            m_Text = s.text ?? string.Empty;
            srcSeg.Build(m_Text);
            anchor = srcSeg.Floor(Mathf.Clamp(s.anchor, 0, m_Text.Length));
            caret = srcSeg.Floor(Mathf.Clamp(s.caret, 0, m_Text.Length));
            caretUpstream = false;
            desiredX = float.NaN;
            ResetBlink();
            UpdateDisplay();
            PushTouchText();
            SendValueChanged();
            NotifySelection();
        }

        private void Submit()
        {
            onSubmit.Invoke(m_Text);
            Submitted?.Invoke(m_Text);
            DeactivateInputField();
        }

        private void Cancel()
        {
            if (m_RestoreOriginalTextOnEscape && m_Text != originalText)
            {
                m_Text = originalText ?? string.Empty;
                srcSeg.Build(m_Text);
                anchor = caret = ClampSnap(caret);
                history.Clear();
                UpdateDisplay();
                SendValueChanged();
            }
            DeactivateInputField();
        }

        private void NavigateTab(bool backwards)
        {
            Selectable next = backwards ? FindSelectableOnUp() ?? FindSelectableOnLeft() : FindSelectableOnDown() ?? FindSelectableOnRight();
            if (next == null || next == this) return;
            DeactivateInputField();
            var es = EventSystem.current;
            if (es != null) es.SetSelectedGameObject(next.gameObject);
            else if (next is UniTextInputField f) f.ActivateInputField();
            else next.Select();
        }

        // ---- caret movement ------------------------------------------------------------------------

        /// <summary>Moves the caret to the start of the text (Ctrl+Home).</summary>
        public void MoveTextStart(bool shift) => MoveTo(0, false, shift);

        /// <summary>Moves the caret to the end of the text (Ctrl+End).</summary>
        public void MoveTextEnd(bool shift) => MoveTo(m_Text.Length, true, shift);

        /// <summary>Moves to the start of the visual line.</summary>
        public void MoveToStartOfLine(bool shift) => MoveLineEdge(false, shift);

        /// <summary>Moves to the end of the visual line.</summary>
        public void MoveToEndOfLine(bool shift) => MoveLineEdge(true, shift);

        private void MoveTo(int pos, bool upstream, bool shift)
        {
            caret = ClampSnap(pos);
            caretUpstream = upstream;
            if (!shift) anchor = caret;
            AfterCaretMove(false);
        }

        private void MoveHorizontal(int dir, bool shift, bool word)
        {
            if (!shift && HasSelection && !word)
            {
                // Collapse to the visually left / right end of the selection.
                EnsureMap();
                var a = SourceToLayout(anchor);
                var c = SourceToLayout(caret);
                map.CaretGeometry(a, false, out var ax, out _, out _, out var al);
                map.CaretGeometry(c, caretUpstream, out var cx, out _, out _, out var cl);
                bool pickAnchor;
                if (al != cl) pickAnchor = dir < 0 ? al < cl : al > cl;
                else pickAnchor = dir < 0 ? ax < cx : ax > cx;
                var p = pickAnchor ? anchor : caret;
                anchor = caret = p;
                AfterCaretMove(false);
                return;
            }
            if (word)
            {
                EnsureMap();
                var lay = SourceToLayout(caret);
                var li = map.LineOf(lay, caretUpstream);
                var rtl = map.LineCount > 0 && map.Lines[li].rtl;
                var forward = dir > 0 != rtl;
                caret = forward ? srcSeg.NextWordStart(caret) : srcSeg.PrevWordStart(caret);
                caretUpstream = !forward;
                if (!shift) anchor = caret;
                AfterCaretMove(false);
                return;
            }
            EnsureMap();
            var from = SourceToLayout(caret);
            var moved = map.MoveVisual(from, caretUpstream, dir);
            caret = LayoutToSource(moved.pos);
            caretUpstream = moved.upstream;
            if (!shift) anchor = caret;
            AfterCaretMove(false);
        }

        private void MoveVertical(int lines, bool shift)
        {
            if (!MultiLine)
            {
                if (lines < 0) MoveTo(0, false, shift); else MoveTo(m_Text.Length, true, shift);
                return;
            }
            EnsureMap();
            var lay = SourceToLayout(caret);
            map.CaretGeometry(lay, caretUpstream, out var x, out _, out _, out var line);
            if (float.IsNaN(desiredX)) desiredX = x;
            var target = line + lines;
            if (target < 0) { caret = 0; caretUpstream = false; }
            else if (target >= map.LineCount) { caret = m_Text.Length; caretUpstream = true; }
            else
            {
                var c = map.AtLineX(target, desiredX);
                caret = LayoutToSource(c.pos);
                caretUpstream = c.upstream;
            }
            if (!shift) anchor = caret;
            AfterCaretMove(true);
        }

        private void MoveLineEdge(bool end, bool shift)
        {
            EnsureMap();
            var c = map.LineEdge(SourceToLayout(caret), caretUpstream, end);
            caret = LayoutToSource(c.pos);
            caretUpstream = c.upstream;
            if (!shift) anchor = caret;
            AfterCaretMove(false);
        }

        private int VisibleLineCount()
        {
            EnsureMap();
            var h = m_TextViewport != null ? m_TextViewport.rect.height : m_TextComponent != null ? m_TextComponent.TextAreaRect.height : 0f;
            return Mathf.Max(1, Mathf.FloorToInt(h / Mathf.Max(1f, map.LineHeight)));
        }

        private void AfterCaretMove(bool keepDesiredX)
        {
            if (!keepDesiredX) desiredX = float.NaN;
            history.Break();
            ResetBlink();
            UpdateScroll();
            RefreshVisuals();
            PushTouchSelection();
            NotifySelection();
        }

        // ---- touch / system keyboard ----------------------------------------------------------------

        private void OpenTouchKeyboard()
        {
            if (m_HideSoftKeyboard || m_ReadOnly) return;
            var tk = TouchKeyboard;
            if (tk == null || !tk.IsSupported) return;
            tk.HideInput = m_HideMobileInput;
            var placeholderText = m_Placeholder is UniText pu ? pu.Text : string.Empty;
            tk.Open(m_Text, m_KeyboardType, m_InputType == InputFieldInputType.AutoCorrect, MultiLine,
                m_InputType == InputFieldInputType.Password, placeholderText, m_CharacterLimit);
            touchOpen = true;
            touchLastText = m_Text;
            PushTouchSelection();
        }

        private void CloseTouchKeyboard()
        {
            if (!touchOpen) return;
            touchOpen = false;
            touchKeyboard?.Close();
        }

        private void PushTouchText()
        {
            if (!touchOpen || touchKeyboard == null) return;
            if (touchKeyboard.Text != m_Text) touchKeyboard.Text = m_Text;
            touchLastText = m_Text;
            PushTouchSelection();
        }

        private void PushTouchSelection()
        {
            if (!touchOpen || touchKeyboard == null || !touchKeyboard.CanSetSelection) return;
            touchKeyboard.Selection = new RangeInt(SelectionStart, SelectionEnd - SelectionStart);
        }

        private void PollTouchKeyboard()
        {
            var tk = touchKeyboard;
            if (tk == null) { touchOpen = false; return; }
            var t = tk.Text ?? string.Empty;
            if (t != touchLastText) ApplyTouchText(t);
            if (tk.CanGetSelection)
            {
                var sel = tk.Selection;
                var a = ClampSnap(sel.start);
                var b = ClampSnap(sel.start + sel.length);
                if (a != SelectionStart || b != SelectionEnd)
                {
                    anchor = a;
                    caret = b;
                    caretUpstream = b > a;
                    desiredX = float.NaN;
                    history.Break();
                    ResetBlink();
                    UpdateScroll();
                    RefreshVisuals();
                    NotifySelection();
                }
            }
            if (!tk.Active || tk.Status != TouchScreenKeyboard.Status.Visible)
            {
                var status = tk.Status;
                touchOpen = false;
                if (status == TouchScreenKeyboard.Status.Done) Submit();
                else if (status == TouchScreenKeyboard.Status.Canceled) Cancel();
                else DeactivateInputField();
            }
        }

        private void ApplyTouchText(string t)
        {
            var v = FilterText(InputFieldValidation.Sanitize(t, MultiLine));
            touchLastText = t;
            if (v != m_Text)
            {
                var before = new TextEditHistory.State { text = m_Text, anchor = anchor, caret = caret };
                var kind = v.Length == m_Text.Length + 1 ? TextEditHistory.EditKind.Insert
                    : v.Length + 1 == m_Text.Length ? TextEditHistory.EditKind.Delete : TextEditHistory.EditKind.Other;
                history.Record(before, kind, v.Length, Now);
                m_Text = v;
                srcSeg.Build(m_Text);
                anchor = caret = m_Text.Length;
                caretUpstream = true;
                ResetBlink();
                UpdateDisplay();
                SendValueChanged();
                NotifySelection();
            }
            if (v != t)
            {
                touchKeyboard.Text = v;
                touchLastText = v;
            }
        }
    }
}
