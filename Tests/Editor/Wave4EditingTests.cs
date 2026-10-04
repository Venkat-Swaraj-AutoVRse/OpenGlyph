using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Editing in UniTextInputField: insert/delete, undo/redo coalescing, clipboard sanitising, character
    /// limit, content types, password masking, placeholder, IME composition, events.
    /// </summary>
    public class Wave4EditingTests : Wave4TestBase
    {
        [Test]
        public void Typing_Backspace_Delete_SelectionReplace()
        {
            var f = MakeField("");
            f.Chars("hello");
            Assert.AreEqual("hello", f.Text);
            Assert.AreEqual(5, f.Caret);
            Assert.AreEqual("hello", f.TextComponent.CleanText, "the text component shows the text");
            f.Key("Left"); f.Key("Left");
            f.Chars("X");
            Assert.AreEqual("helXlo", f.Text);
            f.Key("Backspace");
            f.Key("Delete");
            Assert.AreEqual("helo", f.Text);
            f.Key("A", "Control");
            Assert.AreEqual((0, 4), (f.SelStart, f.SelEnd), "Ctrl+A");
            f.Type("new");
            Assert.AreEqual("new", f.Text, "typing replaces the selection");
            // Surrogate pair typed as two UTF-16 units (Input System onTextInput delivers them separately).
            f.Chars("\U0001F44D");
            Assert.AreEqual("new\U0001F44D", f.Text);
            Assert.AreEqual(5, f.Caret);
            // Newlines are never inserted in a single-line field; Enter submits.
            var submitted = new List<string>();
            ((UnityEngine.Events.UnityEvent<string>)f.Get("onSubmit")).AddListener(submitted.Add);
            f.Type("a\nb");
            Assert.AreEqual("new\U0001F44Dab", f.Text);
            f.Key("Enter");
            CollectionAssert.AreEqual(new[] { "new\U0001F44Dab" }, submitted);
            Assert.IsFalse(f.Focused, "submit ends editing");
        }

        [Test]
        public void MultiLineNewline_EnterInsertsNewline_MultiLineSubmit_EnterSubmits()
        {
            var f = MakeField("ab", 600, 200, "MultiLineNewline");
            f.Caret = 1;
            f.Key("Enter");
            Assert.AreEqual("a\nb", f.Text);
            Assert.IsTrue(f.Focused);
            var g = MakeField("ab", 600, 200, "MultiLineSubmit");
            g.Key("Enter");
            Assert.AreEqual("ab", g.Text);
            Assert.IsFalse(g.Focused);
        }

        [Test]
        public void Undo_Redo_CoalescesTypingIntoWordGroups()
        {
            var f = MakeField("");
            f.Chars("hello");
            Assert.AreEqual(1, (int)f.Get("UndoCount"), "five keystrokes are one undo step");
            f.Chars(" world");
            Assert.AreEqual(2, (int)f.Get("UndoCount"), "a space after a word starts a new step");
            f.Key("Z", "Control");
            Assert.AreEqual("hello", f.Text);
            Assert.AreEqual(5, f.Caret);
            f.Key("Z", "Control");
            Assert.AreEqual("", f.Text);
            f.Key("Y", "Control");
            Assert.AreEqual("hello", f.Text);
            f.Key("Z", "Control, Shift");
            Assert.AreEqual("hello world", f.Text, "Ctrl+Shift+Z redoes too");

            // Backspaces coalesce; a caret move ends the group.
            f.Keys("Backspace", 3);
            Assert.AreEqual("hello wo", f.Text);
            var before = (int)f.Get("UndoCount");
            f.Key("Left");
            f.Key("Backspace");
            Assert.AreEqual(before + 1, (int)f.Get("UndoCount"), "moving the caret starts a new step");
            f.Key("Z", "Control");
            f.Key("Z", "Control");
            Assert.AreEqual("hello world", f.Text, "the three backspaces undo together");

            // A pause longer than the group timeout splits typing.
            var g = MakeField("");
            g.Chars("ab");
            _clock += 5f;
            g.Chars("cd");
            Assert.AreEqual(2, (int)g.Get("UndoCount"));
            g.Key("Z", "Control");
            Assert.AreEqual("ab", g.Text);

            // New edits clear redo.
            g.Chars("x");
            Assert.AreEqual(0, (int)g.Get("RedoCount"));

            // Typing over a selection is one step with the keystrokes that follow; undo restores the selection.
            var r = MakeField("Check valve pressure");
            r.Caret = 20;
            r.Key("Left", "Control, Shift");
            r.Chars("temp");
            Assert.AreEqual("Check valve temp", r.Text);
            Assert.AreEqual(1, (int)r.Get("UndoCount"));
            r.Key("Z", "Control");
            Assert.AreEqual("Check valve pressure", r.Text);
            Assert.AreEqual((12, 20), (r.SelStart, r.SelEnd), "the replaced selection comes back");
        }

        [Test]
        public void Paste_IsSanitised_CappedByLimitAndMaxLength_CopyCutRoundTrip()
        {
            var f = MakeField("");
            var clip = f.Get("Clipboard");
            W4.Set(clip, "Text", "line1\r\nline2\tTab\u0007bell\u0000\u0085end");
            f.Key("V", "Control");
            Assert.AreEqual("line1line2Tabbellend", f.Text, "single line: CR/LF, tab, BEL, NUL, NEL removed");

            var m = MakeField("", 600, 200, "MultiLineNewline");
            m.Set("Clipboard", clip);
            m.Key("V", "Control");
            Assert.AreEqual("line1\nline2Tabbellend", m.Text, "multi-line keeps the (normalised) newline only");

            // Character limit never splits a grapheme: room for 2 units, the next grapheme (emoji) needs 2 after 'e'.
            var g = MakeField("abcd");
            g.Set("Clipboard", clip);
            g.Set("CharacterLimit", 6);
            g.Key("End");
            W4.Set(clip, "Text", "e\U0001F44Df");
            g.Key("V", "Control");
            Assert.AreEqual("abcde", g.Text, "the emoji does not fit whole, so it is dropped");
            g.Key("V", "Control");
            Assert.AreEqual("abcdee", g.Text);
            g.Key("V", "Control");
            Assert.AreEqual("abcdee", g.Text, "full");

            // Max paste length.
            var h = MakeField("");
            h.Set("Clipboard", clip);
            W4.Set(clip, "Text", new string('x', 100000));
            h.Key("V", "Control");
            Assert.AreEqual(16384, h.Text.Length, "pasted text capped at MaxPasteLength");

            // Copy / cut put plain text on the clipboard.
            var c = MakeField("copy me");
            c.Set("Clipboard", clip);
            c.Call("SetSelection", 0, 4);
            c.Key("C", "Control");
            Assert.AreEqual("copy", W4.Get(clip, "Text"));
            c.Key("X", "Control");
            Assert.AreEqual(" me", c.Text);
            c.Key("End");
            c.Key("V", "Control");
            Assert.AreEqual(" mecopy", c.Text);
        }

        [Test]
        public void SetText_AppliesLimitAndSingleLine()
        {
            var f = MakeField("", activate: false);
            f.Set("CharacterLimit", 5);
            f.Text = "ab\U0001F44D\U0001F44Dcd";
            Assert.AreEqual("ab\U0001F44D", f.Text, "limit 5 cuts before the second emoji (no half pair)");
            f.Set("CharacterLimit", 0);
            f.Text = "a\nb";
            Assert.AreEqual("ab", f.Text, "single-line text has no newlines");
        }

        private static string TypeInto(Field f, string contentType, string typed, bool asOne = false)
        {
            f.SetEnum("ContentType", "LightSide.InputFieldContentType", contentType);
            f.Text = "";
            f.Call("ActivateInputField");
            if (asOne) f.Type(typed); else f.Chars(typed);
            return f.Text;
        }

        [Test]
        public void ContentTypes_ValidateEachCharacter()
        {
            var f = MakeField("", activate: false);
            Assert.AreEqual("-1234", TypeInto(f, "IntegerNumber", "-12a3-4"));
            f.Caret = 0;
            f.Chars("5");
            Assert.AreEqual("-1234", f.Text, "nothing goes in front of the sign");
            f.Call("DeactivateInputField", false);

            Assert.AreEqual("-1.234", TypeInto(f, "DecimalNumber", "-1.2.3,4"), "one decimal separator");
            f.Call("DeactivateInputField", false);
            Assert.AreEqual("12,5", TypeInto(f, "DecimalNumber", "12,5.0x").Substring(0, 4));
            f.Call("DeactivateInputField", false);
            Assert.AreEqual("ab12", TypeInto(f, "Alphanumeric", "ab-12_\u00E9!"));
            f.Call("DeactivateInputField", false);
            Assert.AreEqual("John O'neil Smith", TypeInto(f, "Name", "john o'neil  smith"));
            f.Call("DeactivateInputField", false);
            Assert.AreEqual("a@bc.d!x", TypeInto(f, "EmailAddress", "a@b@c..d!x"));
            f.Call("DeactivateInputField", false);
            Assert.AreEqual("123", TypeInto(f, "Pin", "12a3"));
            Assert.AreEqual("***", f.Displayed, "PIN is masked");
            f.Call("DeactivateInputField", false);
            Assert.AreEqual("p\u00E4ss w\u00F6rd!", TypeInto(f, "Password", "p\u00E4ss w\u00F6rd!"));
            f.Call("DeactivateInputField", false);
            Assert.AreEqual("any <b>thing</b> \u05E9", TypeInto(f, "Standard", "any <b>thing</b> \u05E9", true));

            // Content type sets the dependent options (TMP semantics).
            f.SetEnum("ContentType", "LightSide.InputFieldContentType", "Pin");
            Assert.AreEqual("Password", f.Get("InputType").ToString());
            Assert.AreEqual("Digit", f.Get("CharacterValidation").ToString());
            Assert.AreEqual(TouchScreenKeyboardType.NumberPad, f.Get("KeyboardType"));
            Assert.AreEqual("SingleLine", f.Get("LineType").ToString());
            f.SetEnum("ContentType", "LightSide.InputFieldContentType", "EmailAddress");
            Assert.AreEqual(TouchScreenKeyboardType.EmailAddress, f.Get("KeyboardType"));
            f.SetEnum("LineType", "LightSide.InputFieldLineType", "MultiLineNewline");
            Assert.AreEqual("Custom", f.Get("ContentType").ToString(), "changing a dependent option makes it Custom");

            // Paste goes through the same validation.
            var g = MakeField("", activate: false);
            g.SetEnum("ContentType", "LightSide.InputFieldContentType", "IntegerNumber");
            g.Call("ActivateInputField");
            W4.Set(g.Get("Clipboard"), "Text", "12-34 5x6");
            g.Key("V", "Control");
            Assert.AreEqual("123456", g.Text);

            // onValidateInput overrides.
            var h = MakeField("");
            var validatorType = W4.Type("LightSide.InputFieldValidateInput");
            Func<string, int, char, char> upper = (s, i, ch) => ch == 'x' ? '\0' : char.ToUpperInvariant(ch);
            h.Set("onValidateInput", Delegate.CreateDelegate(validatorType, upper.Target, upper.Method));
            h.Chars("axbyc");
            Assert.AreEqual("ABYC", h.Text);
        }

        [Test]
        public void Password_RendersMaskGlyphs_TextIsReal_NotCopyable()
        {
            var f = MakeField("", activate: false);
            f.SetEnum("ContentType", "LightSide.InputFieldContentType", "Password");
            f.Call("ActivateInputField");
            f.Chars("s3cr\u00E9t\U0001F44D");
            Update();
            Assert.AreEqual("s3cr\u00E9t\U0001F44D", f.Text, "Text is the real password");
            Assert.AreEqual(new string('*', 7), f.Displayed, "one mask character per grapheme (the emoji is one)");
            var t = f.TextComponent;
            Assert.AreEqual(new string('*', 7), t.CleanText, "the text component is given only mask characters");
            var glyphs = t.ResultGlyphs.ToArray();
            Assert.AreEqual(7, glyphs.Length, "7 rendered glyphs");
            Assert.AreEqual(1, glyphs.Select(g => g.glyphId).Distinct().Count(), "all rendered glyphs are the mask glyph");
            f.Set("AsteriskChar", '\u2022');
            Update();
            Assert.AreEqual(new string('\u2022', 7), t.CleanText);

            // Caret moves by real graphemes: one Left from the end skips the whole emoji.
            f.Key("Left");
            Assert.AreEqual(6, f.Caret);
            f.Key("A", "Control");
            var clip = f.Get("Clipboard");
            W4.Set(clip, "Text", "untouched");
            f.Key("C", "Control");
            Assert.AreEqual("untouched", W4.Get(clip, "Text"), "passwords are never copied");
        }

        [Test]
        public void Placeholder_VisibleOnlyWhenEmpty()
        {
            var f = MakeField("");
            var p = f.Placeholder;
            Assert.IsNotNull(p);
            Assert.IsTrue(p.enabled, "empty: placeholder shown");
            f.Chars("a");
            Assert.IsFalse(p.enabled, "text: placeholder hidden");
            f.Key("Backspace");
            Assert.IsTrue(p.enabled);
            f.Compose("\u306B");
            Assert.IsFalse(p.enabled, "an open IME composition hides the placeholder");
            f.Compose("");
            Assert.IsTrue(p.enabled);
            f.Call("DeactivateInputField", false);
            f.Text = "x";
            Assert.IsFalse(p.enabled, "set from code while not focused");
        }

        [Test]
        public void Ime_Composition_ShownInline_Underlined_ThenCommitted_OrCancelled()
        {
            var f = MakeField("ab");
            f.Caret = 1;
            f.Compose("\u306B");
            Assert.AreEqual("ab", f.Text, "composition is not part of the text");
            Assert.AreEqual("a\u306Bb", f.Displayed, "composition shown inline at the caret");
            f.Compose("\u306B\u307B\u3093");
            Update();
            Assert.AreEqual("a\u306B\u307B\u3093b", f.TextComponent.CleanText);
            var underline = f.SelectionRects();
            Assert.GreaterOrEqual(underline.Count, 1, "composition underline drawn");
            var t = f.TextComponent;
            Assert.AreEqual(GlyphLocal(t, 1, 0f).x, underline[0].xMin, 0.6f, "underline starts at the composition");
            Assert.AreEqual(GlyphLocal(t, 3, 1f).x, underline[0].xMax, 0.6f, "and ends after it");
            Assert.Less(underline[0].height, 6f, "a thin line, not a selection box");
            Assert.AreEqual(GlyphLocal(t, 3, 1f).x, f.CaretRect.center.x, 1.5f, "caret after the composition");
            Assert.IsFalse(f.Key("Left"), "keys go to the IME while composing");

            // Commit: the IME clears the composition and delivers the result as text.
            f.Compose("");
            f.Type("\u65E5\u672C");
            Assert.AreEqual("a\u65E5\u672Cb", f.Text);
            Assert.AreEqual(3, f.Caret);
            Assert.AreEqual(f.Text, f.Displayed);
            Update(); // visuals follow the next layout
            Assert.AreEqual(0, f.SelectionRects().Count, "no underline after commit");

            // Cancel: composition removed, text unchanged.
            f.Compose("x");
            f.Compose("");
            Assert.AreEqual("a\u65E5\u672Cb", f.Text);
            Assert.AreEqual(f.Text, f.Displayed);

            // Composition replaces the selection.
            f.Call("SetSelection", 1, 3);
            f.Compose("k");
            Assert.AreEqual("ab", f.Text);
            Assert.AreEqual("akb", f.Displayed);

            // Password fields mask the composition too.
            var p = MakeField("", activate: false);
            p.SetEnum("ContentType", "LightSide.InputFieldContentType", "Password");
            p.Call("ActivateInputField");
            p.Compose("ab");
            Assert.AreEqual("**", p.Displayed);
        }

        [Test]
        public void Events_ValueChanged_EndEdit_Selection_Escape()
        {
            var f = MakeField("start");
            var changes = new List<string>();
            var ends = new List<string>();
            var sel = new List<(int, int)>();
            ((UnityEngine.Events.UnityEvent<string>)f.Get("onValueChanged")).AddListener(changes.Add);
            ((UnityEngine.Events.UnityEvent<string>)f.Get("onEndEdit")).AddListener(ends.Add);
            ((UnityEngine.Events.UnityEvent<string, int, int>)f.Get("onTextSelection")).AddListener((s, a, b) => sel.Add((a, b)));
            f.Caret = 5;
            f.Chars("!");
            CollectionAssert.AreEqual(new[] { "start!" }, changes);
            f.Key("Left", "Shift");
            Assert.AreEqual((5, 6), sel.Last());
            f.Call("SetTextWithoutNotify", "quiet");
            Assert.AreEqual(1, changes.Count, "SetTextWithoutNotify does not raise onValueChanged");
            f.Chars("x");
            f.Key("Escape");
            Assert.AreEqual("start", f.Text, "Escape restores the text from when editing started");
            Assert.IsFalse(f.Focused);
            Assert.AreEqual("start", ends.Last());

            // Read-only: navigation and copy only.
            var r = MakeField("read only");
            r.Set("ReadOnly", true);
            r.Caret = 4;
            r.Chars("x");
            r.Key("Backspace");
            r.Key("V", "Control");
            Assert.AreEqual("read only", r.Text);
            Assert.IsFalse(r.HasCaretRect, "no caret in a read-only field");
            r.Key("Right", "Shift, Control");
            Assert.AreEqual((4, 5), (r.SelStart, r.SelEnd));
            r.Key("C", "Control");
            Assert.AreEqual(" ", W4.Get(r.Get("Clipboard"), "Text"));
        }

        [Test]
        public void RichTextOff_ShowsMarkupLiterally_EvenWithParseRules()
        {
            var f = MakeField("");
            var t = f.TextComponent;
            t.RegisterModifier(new ModRegister { Modifier = new BoldModifier(), Rule = new BoldParseRule() });
            f.Type("a<b>c</b>");
            Update();
            Assert.AreEqual("a<b>c</b>", f.Text);
            Assert.AreEqual("a<b>c</b>", t.CleanText, "markup is shown as typed (not parsed) by default");
            f.Key("Left");
            Assert.AreEqual(8, f.Caret, "caret walks the literal tag characters");

            f.Set("RichText", true);
            Update();
            Assert.AreEqual("ac", t.CleanText, "RichText on: tags are parsed");
            f.Key("End");
            f.Key("Left");
            Assert.AreEqual(4, f.Caret, "caret skips the markup: before 'c' in the source");
        }
    }
}
