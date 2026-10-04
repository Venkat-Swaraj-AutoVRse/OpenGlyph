using System;
using System.Collections.Generic;
using OpenGlyph;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// The common markup tags as modifier/rule pairs, for <see cref="UniText.RegisterDefaultMarkup"/> and the
    /// project option <see cref="UniTextSettings.DefaultMarkupOnNewComponents"/>.
    /// </summary>
    /// <remarks>
    /// Markup in OpenGlyph is opt-in per component: a plain <see cref="UniText"/> / <see cref="UniTextWorld"/>
    /// parses no tags until rules are registered (in the Inspector's Mod Registers list, a
    /// <see cref="ModRegisterConfig"/>, or from code). The default set covers the styling and layout tags:
    /// <c>b i u s color size cspace line-height line-spacing link sup sub gradient outline outline2 underlay
    /// glow innershadow dilate softness style mark smallcaps upper uppercase lowercase voffset align indent
    /// line-indent nobr noparse lang feature</c>. Not included: animation tags (register them with
    /// <see cref="TextAnimationModifier.RegisterAll"/>), <c>obj</c> (needs inline objects), <c>font</c>,
    /// <c>ellipsis</c>, and the Markdown / raw-URL rules, which change how plain text is read.
    /// </remarks>
    public static class UniTextDefaultMarkup
    {
        /// <summary>The tag names registered by <see cref="CreateRegisters"/>.</summary>
        public static readonly IReadOnlyList<string> Tags = new[]
        {
            "b", "i", "u", "s", "color", "size", "cspace", "line-height", "line-spacing", "link", "sup", "sub",
            "gradient", "outline", "outline2", "underlay", "glow", "innershadow", "dilate", "softness", "style",
            "mark", "smallcaps", "upper", "uppercase", "lowercase", "voffset", "align", "indent", "line-indent",
            "nobr", "noparse", "lang", "feature",
        };

        /// <summary>New modifier/rule pairs for the default tags (one set per component: a pair is owned by one component).</summary>
        public static List<ModRegister> CreateRegisters()
        {
            var l = new List<ModRegister>(40);
            void Add(BaseModifier m, IParseRule r) => l.Add(new ModRegister { Modifier = m, Rule = r });
            Add(new BoldModifier(), new BoldParseRule());
            Add(new ItalicModifier(), new ItalicParseRule());
            Add(new UnderlineModifier(), new UnderlineParseRule());
            Add(new StrikethroughModifier(), new StrikethroughParseRule());
            Add(new ColorModifier(), new ColorParseRule());
            Add(new SizeModifier(), new SizeParseRule());
            Add(new LetterSpacingModifier(), new CSpaceParseRule());
            Add(new LineHeightModifier(), new LineHeightParseRule());
            Add(new LineHeightModifier(), new LineSpacingParseRule());
            Add(new LinkModifier(), new LinkTagParseRule());
            Add(new SuperSubscriptModifier(SuperSubscriptModifier.Kind.Superscript), new SuperscriptParseRule());
            Add(new SuperSubscriptModifier(SuperSubscriptModifier.Kind.Subscript), new SubscriptParseRule());
            Add(new GradientModifier(), new GradientParseRule());
            Add(new SpanStyleModifier(SpanStyleModifier.Kind.Outline), new OutlineParseRule());
            Add(new SpanStyleModifier(SpanStyleModifier.Kind.Outline2), new Outline2ParseRule());
            Add(new SpanStyleModifier(SpanStyleModifier.Kind.Underlay), new UnderlayParseRule());
            Add(new SpanStyleModifier(SpanStyleModifier.Kind.Glow), new GlowParseRule());
            Add(new SpanStyleModifier(SpanStyleModifier.Kind.InnerShadow), new InnerShadowParseRule());
            Add(new SpanStyleModifier(SpanStyleModifier.Kind.Dilate), new DilateParseRule());
            Add(new SpanStyleModifier(SpanStyleModifier.Kind.Softness), new SoftnessParseRule());
            Add(new SpanStyleModifier(SpanStyleModifier.Kind.Style), new StyleSheetParseRule());
            Add(new MarkModifier(), new MarkParseRule());
            Add(new SmallCapsModifier(), new SmallCapsParseRule());
            Add(new UppercaseModifier(), new UppercaseParseRule());
            Add(new UppercaseModifier(), new UppercaseAliasParseRule());
            Add(new LowercaseModifier(), new LowercaseParseRule());
            Add(new VOffsetModifier(), new VOffsetParseRule());
            Add(new AlignModifier(), new AlignParseRule());
            Add(new IndentModifier(false), new IndentParseRule());
            Add(new IndentModifier(true), new LineIndentParseRule());
            Add(new NoBreakModifier(), new NoBreakParseRule());
            Add(new EmptyModifier(), new NoParseParseRule());
            Add(new LanguageModifier(), new LanguageParseRule());
            Add(new FeatureModifier(), new FeatureParseRule());
            return l;
        }

        /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> parse the same tag (same rule type; NoParse rules compare their tag).</summary>
        internal static bool SameRule(IParseRule a, IParseRule b)
        {
            if (a == null || b == null || a.GetType() != b.GetType()) return false;
            if (a is NoParseParseRule na && b is NoParseParseRule nb)
                return string.Equals(na.TagName, nb.TagName, StringComparison.OrdinalIgnoreCase);
            return true;
        }
    }

    // UniText partial: the default markup set.
    public partial class UniText
    {
        // Project-default markup (UniTextSettings.DefaultMarkupOnNewComponents): registered at runtime only,
        // never written into the serialized Mod Registers list, so turning the option on does not modify scenes.
        [NonSerialized] private List<ModRegister> implicitMarkup;
        [NonSerialized] private bool implicitMarkupResolved;

        /// <summary>
        /// Registers the common markup tags (<see cref="UniTextDefaultMarkup.Tags"/>: <c>&lt;b&gt;</c>,
        /// <c>&lt;i&gt;</c>, <c>&lt;color&gt;</c>, <c>&lt;size&gt;</c>, <c>&lt;link&gt;</c>, <c>&lt;outline&gt;</c>,
        /// <c>&lt;gradient&gt;</c>, ...) on this component, skipping tags it already parses. Without this (or
        /// rules of your own) a component shows tags as literal text. Returns this component.
        /// </summary>
        /// <example><code>
        /// var label = go.AddComponent&lt;UniTextWorld&gt;().RegisterDefaultMarkup();
        /// label.Text = "Valve &lt;b&gt;A&lt;/b&gt; · &lt;color=#38BDF8&gt;open&lt;/color&gt;";
        /// </code></example>
        public UniText RegisterDefaultMarkup()
        {
            var regs = UniTextDefaultMarkup.CreateRegisters();
            for (var i = 0; i < regs.Count; i++)
                if (!ParsesRuleLike(regs[i].Rule)) RegisterModifier(regs[i]);
            return this;
        }

        /// <summary>Whether this component has any markup rules (its own, from configs, or the project default).</summary>
        internal bool HasAnyMarkupRules
        {
            get
            {
                ResolveImplicitMarkup();
                return modRegisters.Count > 0 || HasAnyModRegisterConfigs() || implicitMarkup is { Count: > 0 };
            }
        }

        /// <summary>Whether the project-default markup (<see cref="UniTextSettings.DefaultMarkupOnNewComponents"/>) is active on this component.</summary>
        public bool UsesProjectDefaultMarkup => implicitMarkup is { Count: > 0 };

        private bool ParsesRuleLike(IParseRule rule)
        {
            for (var i = 0; i < modRegisters.Count; i++)
                if (UniTextDefaultMarkup.SameRule(modRegisters[i]?.Rule, rule)) return true;
            for (var c = 0; c < modRegisterConfigs.Count; c++)
            {
                var cfg = modRegisterConfigs[c];
                if (cfg == null || cfg.modRegisters == null) continue;
                for (var i = 0; i < cfg.modRegisters.Count; i++)
                    if (UniTextDefaultMarkup.SameRule(cfg.modRegisters[i]?.Rule, rule)) return true;
            }
            return false;
        }

        /// <summary>
        /// Decides once per component whether the project-default markup applies: the option is on and the
        /// component has no markup of its own (GlyphMeshPro components register their own TMP tag set).
        /// </summary>
        private void ResolveImplicitMarkup()
        {
            if (implicitMarkupResolved) return;
            implicitMarkupResolved = true;
            if (!UniTextSettings.DefaultMarkupOnNewComponents) return;
            if (this is IGlyphMeshText) return;
            if (modRegisters.Count > 0 || HasAnyModRegisterConfigs()) return;
            implicitMarkup = UniTextDefaultMarkup.CreateRegisters();
        }

#if UNITY_EDITOR
        [ContextMenu("Register Default Markup")]
        private void RegisterDefaultMarkupMenu()
        {
            UnityEditor.Undo.RecordObject(this, "Register Default Markup");
            RegisterDefaultMarkup();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
