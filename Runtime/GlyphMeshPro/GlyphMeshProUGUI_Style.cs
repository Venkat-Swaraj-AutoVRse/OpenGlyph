// OpenGlyph GlyphMeshPro — fontStyle flag wiring (Round 2, completed in Round 3).
//
// TextMeshPro's `fontStyle` is, semantically, markup applied to the whole run:
// FontStyles.Underline == wrapping the text in <u>…</u>, Strikethrough == <s>…</s>,
// UpperCase == <uppercase>…</uppercase>, and so on. OpenGlyph's engine already
// parses rich-text span tags for the whole source, so the component realises the
// style flags the same way TMP does internally — by composing the active flags
// into a tag wrapper around the user's raw text and feeding that to the engine.
//
// Bold and Italic remain mapped to the engine's FontWeight / StyleAxis (a real or
// synthetic face), matching Round 1; they are NOT wrapped as tags.
//
// Flags realised as tags (each backed by an engine rule + modifier the component registers):
//   Underline -> <u>, Strikethrough -> <s>, UpperCase -> <uppercase>, LowerCase -> <lowercase>,
//   SmallCaps -> <smallcaps> (OpenType smcp or synthetic), Superscript -> <sup>, Subscript -> <sub>,
//   Highlight -> <mark> (TMP default #FFFF0040).
// richText = false wraps the raw run in a literal <og-raw> span (NoParseParseRule, last close).
//
// The component auto-registers these modifier pairs (and the TMP layout tags <nobr> <align>
// <indent> <line-indent> <font> <noparse>) on itself, so they work without wiring a ModRegister.
//
// Clean-room: no TMP code copied; this mirrors TMP's documented fontStyle semantics
// only. See Documentation/GlyphMeshPro-Parity.md.

using System.Text;
using LightSide;

namespace OpenGlyph
{
    public partial class GlyphMeshProUGUI
    {
        // The user's raw text, before style-flag wrapping. SERIALIZED so it survives reload as the
        // source of truth; `text` get returns this, and `base.Text` holds the composed form the
        // engine parses. On enable we always re-compose from this.
        [UnityEngine.SerializeField, UnityEngine.HideInInspector] private string m_rawText = string.Empty;
        private readonly StringBuilder m_styleBuffer = new StringBuilder(64);

        /// <summary>Literal-run tag used when <c>richText</c> is off (closed at its LAST occurrence).</summary>
        internal const string RawTagName = "og-raw";

        /// <summary>True when the engine source needs a wrapper: a markup-realised style flag is set, or rich
        /// text is off (the run is then wrapped in a literal span so its tags are shown verbatim).</summary>
        private bool HasWrappingStyle =>
            !m_richText ||
            (m_fontStyle & (FontStyles.Underline | FontStyles.Strikethrough
                            | FontStyles.UpperCase | FontStyles.LowerCase | FontStyles.SmallCaps
                            | FontStyles.Superscript | FontStyles.Subscript | FontStyles.Highlight)) != 0;

        /// <summary>Composes the active style flags into a tag wrapper around <paramref name="raw"/>.
        /// Order is outer-to-inner: case transform (outermost, so it applies to the whole run), small caps,
        /// then baseline (sup/sub), then decoration (u/s), then highlight, then (richText off) the literal
        /// span. Bold/Italic are intentionally excluded — they are engine weight/axis, not tags.</summary>
        private string ComposeStyledSource(string raw)
        {
            if (string.IsNullOrEmpty(raw) || !HasWrappingStyle)
                return raw ?? string.Empty;

            var sb = m_styleBuffer;
            sb.Length = 0;

            // Opening tags, outermost first (case transform outermost so it covers the whole run).
            if ((m_fontStyle & FontStyles.UpperCase) != 0) sb.Append("<uppercase>");
            if ((m_fontStyle & FontStyles.LowerCase) != 0) sb.Append("<lowercase>");
            if ((m_fontStyle & FontStyles.SmallCaps) != 0) sb.Append("<smallcaps>");
            if ((m_fontStyle & FontStyles.Superscript) != 0) sb.Append("<sup>");
            if ((m_fontStyle & FontStyles.Subscript) != 0) sb.Append("<sub>");
            if ((m_fontStyle & FontStyles.Underline) != 0) sb.Append("<u>");
            if ((m_fontStyle & FontStyles.Strikethrough) != 0) sb.Append("<s>");
            if ((m_fontStyle & FontStyles.Highlight) != 0) sb.Append("<mark>");
            if (!m_richText) sb.Append('<').Append(RawTagName).Append('>');

            sb.Append(raw);

            // Closing tags, innermost first (reverse order).
            if (!m_richText) sb.Append("</").Append(RawTagName).Append('>');
            if ((m_fontStyle & FontStyles.Highlight) != 0) sb.Append("</mark>");
            if ((m_fontStyle & FontStyles.Strikethrough) != 0) sb.Append("</s>");
            if ((m_fontStyle & FontStyles.Underline) != 0) sb.Append("</u>");
            if ((m_fontStyle & FontStyles.Subscript) != 0) sb.Append("</sub>");
            if ((m_fontStyle & FontStyles.Superscript) != 0) sb.Append("</sup>");
            if ((m_fontStyle & FontStyles.SmallCaps) != 0) sb.Append("</smallcaps>");
            if ((m_fontStyle & FontStyles.LowerCase) != 0) sb.Append("</lowercase>");
            if ((m_fontStyle & FontStyles.UpperCase) != 0) sb.Append("</uppercase>");

            return sb.ToString();
        }
        /// <summary>Pushes the composed (style-wrapped) source into the engine. Called whenever the
        /// raw text or the style flags change.</summary>
        private void ApplyStyledSource()
        {
            EnsureStyleModifiersRegistered();
            base.Text = ComposeStyledSource(m_rawText);
        }

        // Guards one-time registration of the fontStyle-backing modifiers on this component.
        private bool m_styleModsRegistered;

        /// <summary>Registers the engine modifier/rule pairs that back the markup-wrapped
        /// <c>fontStyle</c> flags, so a user setting <c>fontStyle = Underline</c> gets a rendered
        /// line without manually wiring a <see cref="ModRegister"/> — matching TMP, where fontStyle
        /// "just works". Idempotent and only registers pairs not already present (a user who added
        /// their own &lt;u&gt;/&lt;s&gt; ModRegister keeps theirs). Only the engine-backed tags
        /// emitted by <see cref="ComposeStyledSource"/> are registered.</summary>
        private void EnsureStyleModifiersRegistered()
        {
            if (m_styleModsRegistered) return;
            m_styleModsRegistered = true;

            // Bold/Italic back both the <b>/<i> tags and the fontStyle Bold/Italic flags: the engine
            // writes the property request into the same buffers these modifiers consume, so without
            // them fontStyle = Bold rendered regular. The common TMP tags work out of the box too.
            if (!HasRule<BoldParseRule>())
                RegisterModifier(new ModRegister { Modifier = new BoldModifier(), Rule = new BoldParseRule() });
            if (!HasRule<ItalicParseRule>())
                RegisterModifier(new ModRegister { Modifier = new ItalicModifier(), Rule = new ItalicParseRule() });
            if (!HasRule<ColorParseRule>())
                RegisterModifier(new ModRegister { Modifier = new ColorModifier(), Rule = new ColorParseRule() });
            if (!HasRule<SizeParseRule>())
                RegisterModifier(new ModRegister { Modifier = new SizeModifier(), Rule = new SizeParseRule() });
            if (!HasRule<CSpaceParseRule>())
                RegisterModifier(new ModRegister { Modifier = new LetterSpacingModifier(), Rule = new CSpaceParseRule() });
            if (!HasRule<LineHeightParseRule>())
                RegisterModifier(new ModRegister { Modifier = new LineHeightModifier(), Rule = new LineHeightParseRule() });
            if (!HasRule<LinkTagParseRule>())
                RegisterModifier(new ModRegister { Modifier = new LinkModifier(), Rule = new LinkTagParseRule() });
            if (!HasRule<UnderlineParseRule>())
                RegisterModifier(new ModRegister { Modifier = new UnderlineModifier(), Rule = new UnderlineParseRule() });
            if (!HasRule<StrikethroughParseRule>())
                RegisterModifier(new ModRegister { Modifier = new StrikethroughModifier(), Rule = new StrikethroughParseRule() });
            if (!HasRule<UppercaseAliasParseRule>() && !HasRule<UppercaseParseRule>())
                RegisterModifier(new ModRegister { Modifier = new UppercaseModifier(), Rule = new UppercaseAliasParseRule() });
            if (!HasRule<LowercaseParseRule>())
                RegisterModifier(new ModRegister { Modifier = new LowercaseModifier(), Rule = new LowercaseParseRule() });
            if (!HasRule<SuperscriptParseRule>())
                RegisterModifier(new ModRegister { Modifier = new SuperSubscriptModifier(SuperSubscriptModifier.Kind.Superscript), Rule = new SuperscriptParseRule() });
            if (!HasRule<SubscriptParseRule>())
                RegisterModifier(new ModRegister { Modifier = new SuperSubscriptModifier(SuperSubscriptModifier.Kind.Subscript), Rule = new SubscriptParseRule() });
            // Markup-only TMP tags (no fontStyle flag) registered so <voffset=…> works out of the box.
            if (!HasRule<VOffsetParseRule>())
                RegisterModifier(new ModRegister { Modifier = new VOffsetModifier(), Rule = new VOffsetParseRule() });
            // Round 3: small caps, highlight, literal runs and the TMP layout tags.
            if (!HasRule<SmallCapsParseRule>())
                RegisterModifier(new ModRegister { Modifier = new SmallCapsModifier(), Rule = new SmallCapsParseRule() });
            if (!HasRule<MarkParseRule>())
                RegisterModifier(new ModRegister { Modifier = new MarkModifier(), Rule = new MarkParseRule() });
            if (!HasNoParseRule("noparse"))
                RegisterModifier(new ModRegister { Modifier = new EmptyModifier(), Rule = new NoParseParseRule() });
            if (!HasNoParseRule(RawTagName))
                RegisterModifier(new ModRegister
                {
                    Modifier = new EmptyModifier(),
                    Rule = new NoParseParseRule { TagName = RawTagName, CloseAtLastTag = true }
                });
            if (!HasRule<NoBreakParseRule>())
                RegisterModifier(new ModRegister { Modifier = new NoBreakModifier(), Rule = new NoBreakParseRule() });
            if (!HasRule<AlignParseRule>())
                RegisterModifier(new ModRegister { Modifier = new AlignModifier(), Rule = new AlignParseRule() });
            if (!HasRule<IndentParseRule>())
                RegisterModifier(new ModRegister { Modifier = new IndentModifier(false), Rule = new IndentParseRule() });
            if (!HasRule<LineIndentParseRule>())
                RegisterModifier(new ModRegister { Modifier = new IndentModifier(true), Rule = new LineIndentParseRule() });
            if (!HasRule<FontParseRule>())
                RegisterModifier(new ModRegister { Modifier = new FontModifier(), Rule = new FontParseRule() });
        }

        private bool HasNoParseRule(string tagName)
        {
            var mods = ModRegisters;
            if (mods == null) return false;
            for (int i = 0; i < mods.Count; i++)
                if (mods[i]?.Rule is NoParseParseRule r && string.Equals(r.TagName, tagName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private bool HasRule<T>() where T : IParseRule
        {
            var mods = ModRegisters;
            if (mods == null) return false;
            for (int i = 0; i < mods.Count; i++)
                if (mods[i]?.Rule is T) return true;
            return false;
        }
    }
}
