// OpenGlyph GlyphMeshProUGUI — a drop-in-shaped Canvas text component that
// mirrors TextMeshPro's public API and layout semantics while rendering through
// OpenGlyph's own shaping/layout/renderer engine (LightSide.UniText).
//
// Clean-room: TMP member *names* are mirrored so migration is a rename. No TMP
// source/shader/asset is copied — every member below forwards to, or is
// re-implemented over, the inherited UniText engine. See the parity doc:
// Documentation/GlyphMeshPro-Parity.md
//
// Architecture: GlyphMeshProUGUI : LightSide.UniText. Subclassing (not
// composition) is deliberate — it reuses the single engine instance, its one
// CanvasRenderer, the unified-renderer on/off path, and ILayoutElement, none of
// which can be driven as a detached field without introducing a second
// MaskableGraphic (i.e. a second engine), which the brief forbids.

using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using LightSide;

namespace OpenGlyph
{
    /// <summary>
    /// Canvas (uGUI) text component with a TextMeshPro-parity public API, backed
    /// by the OpenGlyph engine. Works with the engine's unified renderer on or off.
    /// </summary>
    [AddComponentMenu("")] // menu item is provided by the editor factory
    [DisallowMultipleComponent]
    public partial class GlyphMeshProUGUI : UniText, IGlyphMeshText
    {
        // ---- Adapter-only state (TMP concepts the engine stores differently) ----

        [SerializeField] private FontStyles m_fontStyle = FontStyles.Normal;
        [SerializeField] private TextAlignmentOptions m_alignment = TextAlignmentOptions.TopLeft;
        [SerializeField] private TextOverflowModes m_overflowMode = TextOverflowModes.Overflow;
        [SerializeField] private TextWrappingModes m_textWrappingMode = TextWrappingModes.Normal;

        [SerializeField] private bool m_enableVertexGradient;
        [SerializeField] private VertexGradient m_colorGradient = new VertexGradient(Color.white);

        [SerializeField] private float m_characterSpacing;
        [SerializeField] private float m_wordSpacing;
        [SerializeField] private float m_lineSpacing;
        [SerializeField] private float m_paragraphSpacing;
        [SerializeField] private Vector4 m_margin = Vector4.zero;

        [SerializeField] private int m_maxVisibleCharacters = int.MaxValue;
        [SerializeField] private int m_maxVisibleWords = int.MaxValue;
        [SerializeField] private int m_maxVisibleLines = int.MaxValue;
        [SerializeField] private bool m_richText = true;

        [Tooltip("Default a fresh component to a plain face (TMP's look) by overriding the engine " +
                 "style with a clean UniTextStyle. Clear to use the appearance/material styling instead.")]
        [SerializeField] private bool m_plainFaceStyle = true;

        // No-alloc numeric SetText scratch buffer (mirrors TMP's reusable buffer).
        private char[] m_numberBuffer = new char[32];
        private readonly StringBuilder m_formatBuffer = new StringBuilder(64);

        private GlyphTextInfo m_textInfo;

        // =====================================================================
        // Text content
        // =====================================================================

        /// <summary>The displayed text (the user's RAW source, before fontStyle-flag wrapping).
        /// Mirrors <c>TMP_Text.text</c>. The setter stores the raw value and feeds the engine the
        /// style-composed form (see <see cref="ComposeStyledSource"/>), so <c>fontStyle</c> flags
        /// such as Underline/Strikethrough render exactly as TMP's whole-run markup would.</summary>
        public virtual string text
        {
            get => m_rawText;
            set { m_rawText = value ?? string.Empty; ApplyStyledSource(); }
        }

        /// <summary>Mirrors <c>TMP_Text.SetText(string)</c>.</summary>
        public void SetText(string sourceText) => text = sourceText;

        /// <summary>Mirrors <c>TMP_Text.SetText(string, bool)</c>. The sync flag has no
        /// effect here (no bound input field in Round 1).</summary>
        public void SetText(string sourceText, bool syncTextInputBox) => text = sourceText;

        /// <summary>Mirrors <c>TMP_Text.SetText(StringBuilder)</c>. Copies into a reused
        /// char buffer to avoid a per-call string allocation.</summary>
        public void SetText(StringBuilder sourceText)
        {
            if (sourceText == null) { Text = string.Empty; return; }
            int len = sourceText.Length;
            EnsureNumberBuffer(len);
            sourceText.CopyTo(0, m_numberBuffer, 0, len);
            SetText(m_numberBuffer, 0, len);
        }

        // ---- No-alloc numeric overloads (mirror TMP's {0}..{7} placeholders) ----
        // These format a template containing {0}..{N} with the given numbers into a
        // reused char[] and feed the engine's char-span SetText — no string alloc.

        /// <summary>Mirrors <c>SetText(string, float)</c>.</summary>
        public void SetText(string sourceText, float arg0)
            => SetTextFormat(sourceText, arg0, 0, 0, 0, 0, 0, 0, 0, 1);

        /// <summary>Mirrors <c>SetText(string, float, float)</c>.</summary>
        public void SetText(string sourceText, float arg0, float arg1)
            => SetTextFormat(sourceText, arg0, arg1, 0, 0, 0, 0, 0, 0, 2);

        /// <summary>Mirrors <c>SetText(string, float, float, float)</c>.</summary>
        public void SetText(string sourceText, float arg0, float arg1, float arg2)
            => SetTextFormat(sourceText, arg0, arg1, arg2, 0, 0, 0, 0, 0, 3);

        /// <summary>Mirrors <c>SetText(string, float x4)</c>.</summary>
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3)
            => SetTextFormat(sourceText, arg0, arg1, arg2, arg3, 0, 0, 0, 0, 4);

        /// <summary>Mirrors <c>SetText(string, float x5)</c>.</summary>
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3, float arg4)
            => SetTextFormat(sourceText, arg0, arg1, arg2, arg3, arg4, 0, 0, 0, 5);

        /// <summary>Mirrors <c>SetText(string, float x6)</c>.</summary>
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3, float arg4, float arg5)
            => SetTextFormat(sourceText, arg0, arg1, arg2, arg3, arg4, arg5, 0, 0, 6);

        /// <summary>Mirrors <c>SetText(string, float x7)</c>.</summary>
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3, float arg4, float arg5, float arg6)
            => SetTextFormat(sourceText, arg0, arg1, arg2, arg3, arg4, arg5, arg6, 0, 7);

        /// <summary>Mirrors <c>SetText(string, float x8)</c>.</summary>
        public void SetText(string sourceText, float arg0, float arg1, float arg2, float arg3, float arg4, float arg5, float arg6, float arg7)
            => SetTextFormat(sourceText, arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, 8);

        private void SetTextFormat(string template, float a0, float a1, float a2, float a3,
            float a4, float a5, float a6, float a7, int argCount)
        {
            if (string.IsNullOrEmpty(template)) { Text = template ?? string.Empty; return; }

            // Build into the reused formatBuffer, substituting {i} and {i:fmt}. No string.Format
            // (which allocates) — we append chars directly, then copy to the char[] span.
            m_formatBuffer.Length = 0;
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{' && i + 1 < template.Length)
                {
                    int close = template.IndexOf('}', i + 1);
                    if (close > i)
                    {
                        string token = template.Substring(i + 1, close - i - 1);
                        int colon = token.IndexOf(':');
                        string indexPart = colon >= 0 ? token.Substring(0, colon) : token;
                        string fmtPart = colon >= 0 ? token.Substring(colon + 1) : null;
                        if (int.TryParse(indexPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                            && idx >= 0 && idx < argCount)
                        {
                            float value = idx switch
                            {
                                0 => a0, 1 => a1, 2 => a2, 3 => a3,
                                4 => a4, 5 => a5, 6 => a6, _ => a7
                            };
                            AppendNumber(value, fmtPart);
                            i = close + 1;
                            continue;
                        }
                    }
                }
                m_formatBuffer.Append(c);
                i++;
            }

            int len = m_formatBuffer.Length;
            EnsureNumberBuffer(len);
            m_formatBuffer.CopyTo(0, m_numberBuffer, 0, len);
            SetText(m_numberBuffer, 0, len);
        }

        private void AppendNumber(float value, string fmt)
        {
            // fmt mirrors TMP's precision spec; default is "0.##" style. We route through
            // the invariant culture. This path allocates only when the number's string
            // form is produced by the runtime; the SetText char copy itself is alloc-free.
            string s = string.IsNullOrEmpty(fmt)
                ? value.ToString(CultureInfo.InvariantCulture)
                : value.ToString(fmt, CultureInfo.InvariantCulture);
            m_formatBuffer.Append(s);
        }

        private void EnsureNumberBuffer(int n)
        {
            if (m_numberBuffer.Length < n)
                m_numberBuffer = new char[Mathf.NextPowerOfTwo(Mathf.Max(n, 32))];
        }

        // =====================================================================
        // Font / size / style
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.font</c> → engine main font (<c>UniTextFont</c>). The setter
        /// places <paramref name="value"/> in the main slot of a font stack. A stack is a
        /// <c>ScriptableObject</c>; a runtime-created one is used when none is assigned.</summary>
        public UniTextFont font
        {
            get => MainFont;
            set
            {
                var stack = FontStack;
                if (stack == null)
                {
                    stack = ScriptableObject.CreateInstance<UniTextFontStack>();
                    stack.hideFlags = HideFlags.DontSave;
                }
                if (stack.fonts == null)
                    stack.fonts = new StyledList<UniTextFont>();
                if (stack.fonts.Count == 0)
                    stack.fonts.Add(value);
                else
                    stack.fonts[0] = value;
                FontStack = stack;
                SetDirty(DirtyFlags.Font);
            }
        }

        /// <summary>Mirrors <c>TMP_Text.fontSize</c>.</summary>
        public float fontSize
        {
            get => FontSize;
            set => FontSize = value;
        }

        /// <summary>Mirrors <c>TMP_Text.enableAutoSizing</c>.</summary>
        public bool enableAutoSizing
        {
            get => AutoSize;
            set => AutoSize = value;
        }

        /// <summary>Mirrors <c>TMP_Text.fontSizeMin</c>.</summary>
        public float fontSizeMin
        {
            get => MinFontSize;
            set => MinFontSize = value;
        }

        /// <summary>Mirrors <c>TMP_Text.fontSizeMax</c>.</summary>
        public float fontSizeMax
        {
            get => MaxFontSize;
            set => MaxFontSize = value;
        }

        /// <summary>Mirrors <c>TMP_Text.fontStyle</c>. Bold/Italic map to the engine's
        /// FontWeight/StyleAxis (a real or synthetic face). The remaining flags
        /// (Underline, Strikethrough, Upper/LowerCase, SmallCaps, Super/Subscript, Highlight)
        /// are realised by wrapping the whole run in the engine's span tags — exactly how TMP
        /// composes <c>fontStyle</c> as whole-run markup. See <see cref="ComposeStyledSource"/>.</summary>
        public FontStyles fontStyle
        {
            get => m_fontStyle;
            set
            {
                if (m_fontStyle == value) return;
                m_fontStyle = value;
                FontWeight = (value & FontStyles.Bold) != 0 ? 700 : 400;
                FontStyleAxis = (value & FontStyles.Italic) != 0 ? StyleAxis.Italic : StyleAxis.Normal;
                // Re-compose the engine source so wrapping flags (u/s/case/sup/sub/mark) apply.
                ApplyStyledSource();
                SetDirty(DirtyFlags.Text); // style flags affect span processing → full rebuild
            }
        }

        /// <summary>Mirrors <c>TMP_Text.fontWeight</c> as a named enum over the engine's int weight.</summary>
        public FontWeight fontWeight
        {
            get => (FontWeight)Mathf.Clamp(FontWeight / 100 * 100, 100, 900);
            set => FontWeight = (int)value;
        }

        // =====================================================================
        // Color / gradient
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.enableVertexGradient</c>.</summary>
        public bool enableVertexGradient
        {
            get => m_enableVertexGradient;
            set { if (m_enableVertexGradient == value) return; m_enableVertexGradient = value; SetDirty(DirtyFlags.Color); }
        }

        /// <summary>Mirrors <c>TMP_Text.colorGradient</c>. The 4-corner gradient is applied as a
        /// per-vertex color modifier when <see cref="enableVertexGradient"/> is set.</summary>
        public VertexGradient colorGradient
        {
            get => m_colorGradient;
            set { m_colorGradient = value; if (m_enableVertexGradient) SetDirty(DirtyFlags.Color); }
        }

        // =====================================================================
        // Alignment
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.alignment</c>. Split into the engine's
        /// <c>HorizontalAlignment</c>/<c>VerticalAlignment</c>. Justified/Flush/Geometry
        /// have no engine equivalent in Round 1 and are documented as gaps.</summary>
        public TextAlignmentOptions alignment
        {
            get => m_alignment;
            set
            {
                m_alignment = value;
                ApplyAlignment(value);
            }
        }

        /// <summary>GlyphMeshPro mirrors TextMeshPro, so it opts into TMP-compatible justification
        /// (5% wrap overrun + word/character spacing split). Plain <c>UniText</c> keeps the inherited
        /// <c>false</c>, so this scoping change leaves every existing plain-UniText layout untouched.</summary>
        protected override bool UseTmpJustification => true;

        private void ApplyAlignment(TextAlignmentOptions value)
        {
            int bits = (int)value;

            // Horizontal
            if ((bits & (int)HorizontalAlignmentOptions.Right) != 0)
                HorizontalAlignment = HorizontalAlignment.Right;
            else if ((bits & (int)HorizontalAlignmentOptions.Center) != 0)
                HorizontalAlignment = HorizontalAlignment.Center;
            else if ((bits & (int)HorizontalAlignmentOptions.Flush) != 0)
                HorizontalAlignment = HorizontalAlignment.Flush;      // Round 2: real flush justification
            else if ((bits & (int)HorizontalAlignmentOptions.Justified) != 0)
                HorizontalAlignment = HorizontalAlignment.Justified;  // Round 2: real inter-word justification
            else // Left, Geometry → Left
                HorizontalAlignment = HorizontalAlignment.Left;

            // Vertical
            if ((bits & (int)VerticalAlignmentOptions.Bottom) != 0
                || (bits & (int)VerticalAlignmentOptions.Baseline) != 0)
                VerticalAlignment = VerticalAlignment.Bottom;
            else if ((bits & (int)VerticalAlignmentOptions.Middle) != 0
                || (bits & (int)VerticalAlignmentOptions.Geometry) != 0)
                VerticalAlignment = VerticalAlignment.Middle;
            else // Top, Capline
                VerticalAlignment = VerticalAlignment.Top;
        }

        // =====================================================================
        // Wrapping / overflow
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.textWrappingMode</c>.</summary>
        public TextWrappingModes textWrappingMode
        {
            get => m_textWrappingMode;
            set
            {
                m_textWrappingMode = value;
                WordWrap = value == TextWrappingModes.Normal || value == TextWrappingModes.PreserveWhitespace;
            }
        }

        /// <summary>Obsolete legacy TMP bool. Mirrors <c>enableWordWrapping</c>.</summary>
        public bool enableWordWrapping
        {
            get => WordWrap;
            set
            {
                WordWrap = value;
                m_textWrappingMode = value ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            }
        }

        /// <summary>Mirrors <c>TMP_Text.overflowMode</c>. Overflow/Ellipsis/Truncate map to the engine's
        /// <see cref="UniText.Overflow"/> modes and Masking maps to Clip; ScrollRect/Page/Linked fall back to
        /// Overflow with a one-time warning (parity doc).</summary>
        public TextOverflowModes overflowMode
        {
            get => m_overflowMode;
            set
            {
                if (m_overflowMode == value) return;
                m_overflowMode = value;
                ApplyOverflowMode(value);
                SetDirty(DirtyFlags.Layout);
            }
        }

        private static bool s_warnedUnsupportedOverflow;

        private void ApplyOverflowMode(TextOverflowModes mode)
        {
            TextOverflow mapped;
            switch (mode)
            {
                case TextOverflowModes.Ellipsis: mapped = TextOverflow.Ellipsis; break;
                case TextOverflowModes.Truncate: mapped = TextOverflow.Truncate; break;
                case TextOverflowModes.Masking: mapped = TextOverflow.Clip; break;
                case TextOverflowModes.Overflow: mapped = TextOverflow.Overflow; break;
                default:
                    mapped = TextOverflow.Overflow;
                    if (!s_warnedUnsupportedOverflow)
                    {
                        s_warnedUnsupportedOverflow = true;
                        Debug.LogWarning("[GlyphMeshProUGUI] overflowMode " + mode +
                                         " is not supported; falling back to Overflow.");
                    }
                    break;
            }
            Overflow = mapped;
        }

        // =====================================================================
        // Spacing / margin
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.characterSpacing</c>. Applied via the engine's
        /// character-spacing span during processing (adapter path).</summary>
        public float characterSpacing
        {
            get => m_characterSpacing;
            set { if (m_characterSpacing == value) return; m_characterSpacing = value; SetDirty(DirtyFlags.Layout); }
        }

        /// <summary>Mirrors <c>TMP_Text.wordSpacing</c>.</summary>
        public float wordSpacing
        {
            get => m_wordSpacing;
            set { if (m_wordSpacing == value) return; m_wordSpacing = value; SetDirty(DirtyFlags.Layout); }
        }

        /// <summary>Mirrors <c>TMP_Text.lineSpacing</c>. Stored as adapter state; the engine
        /// applies line spacing through its layout config rather than a public processor setter,
        /// so Round 1 records the value for API parity and migration (parity doc notes the wiring
        /// as the engine-config pass).</summary>
        public float lineSpacing
        {
            get => m_lineSpacing;
            set { if (m_lineSpacing == value) return; m_lineSpacing = value; SetDirty(DirtyFlags.Layout); }
        }

        /// <summary>Mirrors <c>TMP_Text.paragraphSpacing</c>.</summary>
        public float paragraphSpacing
        {
            get => m_paragraphSpacing;
            set { if (m_paragraphSpacing == value) return; m_paragraphSpacing = value; SetDirty(DirtyFlags.Layout); }
        }

        /// <summary>Mirrors <c>TMP_Text.margin</c> (x=left, y=top, z=right, w=bottom).</summary>
        public virtual Vector4 margin
        {
            get => m_margin;
            set { if (m_margin == value) return; m_margin = value; SetDirty(DirtyFlags.Layout); }
        }

        // =====================================================================
        // Rich text / direction / visibility caps
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.richText</c>. The OpenGlyph engine always parses rich-text
        /// span tags; there is no engine toggle to disable parsing in Round 1. The flag is stored
        /// for API parity and migration. When set to <c>false</c> the component tags the text as
        /// literal by escaping the opening angle bracket so markup is shown verbatim (adapter path;
        /// see parity doc — a native parse-off switch is a Round 2 gap).</summary>
        public bool richText
        {
            get => m_richText;
            set { if (m_richText == value) return; m_richText = value; SetDirty(DirtyFlags.Text); }
        }

        /// <summary>Mirrors <c>TMP_Text.isRightToLeftText</c>.</summary>
        public bool isRightToLeftText
        {
            get => BaseDirection == TextDirection.RightToLeft;
            set => BaseDirection = value ? TextDirection.RightToLeft : TextDirection.LeftToRight;
        }

        /// <summary>Mirrors <c>TMP_Text.maxVisibleCharacters</c>.</summary>
        public int maxVisibleCharacters
        {
            get => m_maxVisibleCharacters;
            set { if (m_maxVisibleCharacters == value) return; m_maxVisibleCharacters = value; SetDirty(DirtyFlags.Layout); }
        }

        /// <summary>Mirrors <c>TMP_Text.maxVisibleWords</c>.</summary>
        public int maxVisibleWords
        {
            get => m_maxVisibleWords;
            set { if (m_maxVisibleWords == value) return; m_maxVisibleWords = value; SetDirty(DirtyFlags.Layout); }
        }

        /// <summary>Mirrors <c>TMP_Text.maxVisibleLines</c>.</summary>
        public int maxVisibleLines
        {
            get => m_maxVisibleLines;
            set { if (m_maxVisibleLines == value) return; m_maxVisibleLines = value; SetDirty(DirtyFlags.Layout); }
        }

        // =====================================================================
        // Preferred / rendered values
        // =====================================================================
        // preferredWidth / preferredHeight are inherited from UniText (ILayoutElement).

        /// <summary>Mirrors <c>TMP_Text.GetPreferredValues()</c>.</summary>
        public Vector2 GetPreferredValues()
        {
            var rect = rectTransform.rect;
            return GetPreferredValues(rect.width, rect.height);
        }

        /// <summary>Mirrors <c>TMP_Text.GetPreferredValues(width, height)</c>. Drives the engine's
        /// first pass synchronously if it has not run yet (so the value is correct in EditMode /
        /// headless without waiting for a canvas update — matching TMP's force-generate behaviour).</summary>
        public Vector2 GetPreferredValues(float width, float height)
        {
            if (TextProcessor == null) return Vector2.zero;
            EnsureEngineFirstPass(width, height);
            if (!TextProcessor.HasValidFirstPassData) return Vector2.zero;

            float fs = AutoSize ? MaxFontSize : FontSize;
            float w = TextProcessor.GetPreferredWidth(fs);
            float measureWidth = width > 0 ? width : TextProcessSettings.FloatMax;
            TextProcessor.EnsureLines(measureWidth, fs, WordWrap, HorizontalAlignment, UseTmpJustification);
            float h = TextProcessor.GetPreferredHeight(fs, 0f, OverEdge, UnderEdge, LeadingDistribution);
            return new Vector2(w, h);
        }

        /// <summary>Runs the engine's first pass over the current text + settings if it is not already
        /// valid. Lets layout queries (preferredWidth/Height, textInfo) work before the canvas tick.</summary>
        private void EnsureEngineFirstPass(float width, float height)
        {
            if (TextProcessor == null) return;
            if (TextProcessor.HasValidFirstPassData) return;
            var src = Text;
            if (string.IsNullOrEmpty(src)) return;

            float fs = AutoSize ? MaxFontSize : FontSize;
            var settings = new TextProcessSettings
            {
                MaxWidth = width > 0 ? width : TextProcessSettings.FloatMax,
                MaxHeight = height > 0 ? height : TextProcessSettings.FloatMax,
                HorizontalAlignment = HorizontalAlignment,
                VerticalAlignment = VerticalAlignment,
                OverEdge = OverEdge,
                UnderEdge = UnderEdge,
                LeadingDistribution = LeadingDistribution,
                fontSize = fs,
                baseDirection = BaseDirection,
                enableWordWrap = WordWrap,
                TmpJustification = UseTmpJustification,
            };
            TextProcessor.EnsureFirstPass(src.AsSpan(), settings);
        }

        /// <summary>Mirrors <c>TMP_Text.GetPreferredValues(string)</c>.</summary>
        public Vector2 GetPreferredValues(string sourceText)
        {
            string prev = Text;
            Text = sourceText;
            SetDirty(DirtyFlags.Text);
            var v = GetPreferredValues();
            Text = prev;
            return v;
        }

        /// <summary>Mirrors <c>TMP_Text.GetPreferredValues(string, width, height)</c>.</summary>
        public Vector2 GetPreferredValues(string sourceText, float width, float height)
        {
            string prev = Text;
            Text = sourceText;
            SetDirty(DirtyFlags.Text);
            var v = GetPreferredValues(width, height);
            Text = prev;
            return v;
        }

        /// <summary>Mirrors <c>TMP_Text.GetRenderedValues()</c> → engine's laid-out result size.</summary>
        public Vector2 GetRenderedValues() => ResultSize;

        /// <summary>Mirrors <c>TMP_Text.GetRenderedValues(bool)</c>. In Round 1 the result size
        /// already reflects visible content, so the flag is advisory.</summary>
        public Vector2 GetRenderedValues(bool onlyVisibleCharacters) => ResultSize;

        // =====================================================================
        // Rebuild
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.ForceMeshUpdate</c>. Forces a full engine rebuild and a
        /// synchronous layout flush so queries after the call see fresh values.</summary>
        public virtual void ForceMeshUpdate(bool ignoreActiveState = false, bool forceTextReparsing = false)
        {
            SetDirty(DirtyFlags.Text); // Text flag = full rebuild per engine DirtyFlags
            if (TextProcessor == null) return;
            var rect = rectTransform.rect;
            float measureW = rect.width > 0 ? rect.width : TextProcessSettings.FloatMax;
            float measureH = rect.height > 0 ? rect.height : TextProcessSettings.FloatMax;
            // Drive the engine synchronously so queries right after this call see fresh values,
            // without waiting for the canvas update loop (which does not tick in batchmode/EditMode).
            EnsureEngineFirstPass(measureW, measureH);
            if (TextProcessor.HasValidFirstPassData)
            {
                float fs = AutoSize ? MaxFontSize : FontSize;
                TextProcessor.EnsureLines(measureW, fs, WordWrap, HorizontalAlignment, UseTmpJustification);
            }
        }

        // =====================================================================
        // TextInfo
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.textInfo</c>. Rebuilt from the engine's positioned
        /// glyphs. Line grouping is derived from distinct baselines (adapter approximation —
        /// parity doc).</summary>
        public GlyphTextInfo textInfo
        {
            get
            {
                m_textInfo ??= new GlyphTextInfo();
                PopulateTextInfo(m_textInfo);
                return m_textInfo;
            }
        }

        /// <summary>Mirrors <c>TMP_Text.GetTextInfo(string)</c>.</summary>
        public GlyphTextInfo GetTextInfo(string sourceText)
        {
            string prev = Text;
            Text = sourceText;
            ForceMeshUpdate(ignoreActiveState: true);
            var info = new GlyphTextInfo();
            PopulateTextInfo(info);
            Text = prev;
            return info;
        }

        private void PopulateTextInfo(GlyphTextInfo info)
        {
            info.Clear();
            if (TextProcessor == null) return;

            var glyphs = ResultGlyphs;
            if (glyphs.Length == 0) return;

            info.EnsureCharacterCapacity(glyphs.Length);

            string src = Text ?? string.Empty;
            float lastBaseline = float.NaN;
            int line = -1;
            int lineFirst = 0;
            float lineStartX = 0f;

            for (int i = 0; i < glyphs.Length; i++)
            {
                ref readonly var g = ref glyphs[i];

                if (float.IsNaN(lastBaseline) || !Mathf.Approximately(g.y, lastBaseline))
                {
                    // close previous line
                    if (line >= 0)
                        CloseLine(info, line, lineFirst, i - 1, lineStartX);
                    line++;
                    lineFirst = i;
                    lineStartX = g.left;
                    lastBaseline = g.y;
                    info.EnsureLineCapacity(line + 1);
                    info.lineInfo[line].baseline = g.y;
                }

                bool visible = line < m_maxVisibleLines && i < m_maxVisibleCharacters;
                info.characterInfo[i] = new GlyphCharacterInfo
                {
                    character = (g.cluster >= 0 && g.cluster < src.Length) ? src[g.cluster] : '\0',
                    lineNumber = line,
                    isVisible = visible,
                    origin = g.x,
                    xAdvance = g.right - g.left,
                    topLeft = new Vector2(g.left, g.top),
                    bottomRight = new Vector2(g.right, g.bottom),
                };
            }

            if (line >= 0)
                CloseLine(info, line, lineFirst, glyphs.Length - 1, lineStartX);

            info.characterCount = glyphs.Length;
            info.lineCount = line + 1;
        }

        private static void CloseLine(GlyphTextInfo info, int line, int first, int last, float startX)
        {
            ref var li = ref info.lineInfo[line];
            li.firstCharacterIndex = first;
            li.lastCharacterIndex = last;
            li.characterCount = last - first + 1;
            li.startX = startX;
            float minX = info.characterInfo[first].topLeft.x;
            float maxX = info.characterInfo[last].bottomRight.x;
            li.lineWidth = maxX - minX;
        }

        // =====================================================================
        // Unity lifecycle — keep adapter state and engine state in sync
        // =====================================================================

        protected override void OnEnable()
        {
            base.OnEnable();
            // m_rawText is the serialized source of truth. Legacy components (saved before this
            // field existed) have base.Text set but m_rawText empty — migrate once, treating the
            // persisted engine text as the raw source. Then always compose from m_rawText so the
            // engine sees the style-wrapped form.
            if (string.IsNullOrEmpty(m_rawText) && !string.IsNullOrEmpty(base.Text))
                m_rawText = base.Text;
            RefreshFromSerializedState();
            ApplyPlainFaceDefault();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            // Inspector edits, undo/redo and prefab reverts write the serialized fields directly.
            if (isActiveAndEnabled) RefreshFromSerializedState();
        }
#endif

        /// <summary>
        /// Re-applies every TMP-named serialized field onto the engine and requests a full rebuild.
        /// The Inspector writes the serialized fields directly, bypassing the property setters (whose
        /// same-value checks then make re-assigning a no-op), so before this a Font Style or other
        /// Inspector change only showed after the component was disabled and re-enabled.
        /// </summary>
        public void RefreshFromSerializedState()
        {
            ApplyAlignment(m_alignment);
            ApplyOverflowMode(m_overflowMode);
            FontWeight = (m_fontStyle & FontStyles.Bold) != 0 ? 700 : 400;
            FontStyleAxis = (m_fontStyle & FontStyles.Italic) != 0 ? StyleAxis.Italic : StyleAxis.Normal;
            WordWrap = m_textWrappingMode == TextWrappingModes.Normal
                       || m_textWrappingMode == TextWrappingModes.PreserveWhitespace;
            ApplyStyledSource();
            SetDirty(DirtyFlags.All);
        }

        /// <summary>When <see cref="m_plainFaceStyle"/> is set (the default), force a plain face —
        /// TMP's look — by overriding the engine style with a clean <see cref="UniTextStyle"/>
        /// (zero outline / underlay / glow). This prevents the ambient default <c>UniTextAppearance</c>
        /// material (whose disabled-underlay serialized default would otherwise be read by the
        /// appearance shim) from adding a dark halo/shadow around every glyph. The face stays white:
        /// the component <c>color</c> already reaches the glyphs as vertex colour, so copying it into
        /// the face too applied it twice (alpha 0.5 rendered as ~0.25). Clearing
        /// <see cref="m_plainFaceStyle"/> restores the engine's appearance-driven styling.</summary>
        private void ApplyPlainFaceDefault()
        {
            if (!m_plainFaceStyle) return;
            var plain = UniTextStyle.Default; // white face, no outline/underlay/glow
            Style = plain;
            OverrideStyle = true;             // use Style, not the appearance material
            // The component-authored style is honoured only on the UNIFIED renderer path; the legacy
            // path renders the appearance MATERIAL directly (whose disabled-underlay serialized default
            // produces the dark halo). A TMP-parity component wants the plain face, so default to the
            // unified renderer unless the user explicitly forced a mode. Only switch when a real
            // graphics device is present — the unified path needs a GPU, and forcing it under
            // NullGfxDevice (headless EditMode) hangs.
            if (UnifiedRenderer == UnifiedRendererMode.UseProjectSetting
                && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                UnifiedRenderer = UnifiedRendererMode.ForceOn;
        }
    }
}
