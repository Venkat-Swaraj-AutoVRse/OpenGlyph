using System;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2: the per-span style modifier. One modifier type handles
    /// every span tag, distinguished by <see cref="kind"/>: it parses its parameter and MERGES the
    /// resulting <see cref="SpanStyleOverride"/> into the ONE shared per-cluster buffer (key
    /// <see cref="AttributeKeys.SpanStyle"/>), so nested spans accumulate (inner wins per field).
    /// </summary>
    /// <remarks>
    /// This modifier is DATA-ONLY: it does not touch vertices. The <see cref="UniText"/> unified render
    /// path owns the single per-glyph <c>OnGlyph</c> that composes the cluster's override over the
    /// component base style, dedups it to a local id, and writes that id into the glyph's UV1.w (which
    /// <see cref="UnifiedRenderBuilder"/> maps to a shared <see cref="StyleTable"/> row). Keeping the
    /// vertex stamp in ONE place (not N modifiers) means a glyph is composed exactly once.
    /// <para>
    /// LIMITATION: span styles render ONLY with the unified renderer (<see cref="UniText.UseUnifiedRenderer"/>).
    /// The legacy renderer draws per font material and ignores them; a component that renders via the
    /// legacy path and carries span styles logs one warning per instance.
    /// </para>
    /// </remarks>
    [Serializable]
    [TypeGroup("Appearance", 3)]
    public sealed class SpanStyleModifier : GlyphModifier<SpanStyleOverride>
    {
        public enum Kind { Outline, Underlay, Dilate, Softness, Style }

        [UnityEngine.SerializeField]
        private Kind kind = Kind.Outline;

        /// <summary>Which span tag this instance handles. One modifier per tag; all share the SpanStyle buffer.</summary>
        public Kind TagKind { get => kind; set => kind = value; }

        public SpanStyleModifier() { }
        public SpanStyleModifier(Kind k) { kind = k; }

        protected override string AttributeKey => AttributeKeys.SpanStyle;

        // Data-only: no vertex callback. The component's unified path owns the single OnGlyph.
        protected override Action GetOnGlyphCallback() => NoOp;
        private static readonly Action NoOp = () => { };

        protected override void DoApply(int start, int end, string parameter)
        {
            if (!TryBuildOverride(parameter, out var ov)) return;

            int cpCount = buffers.codepoints.count;
            int e = Math.Min(end, cpCount);
            var buf = attribute.buffer.data;
            if (buf == null) return;
            if (start < 0) start = 0;
            for (int i = start; i < e && i < buf.Length; i++)
                buf[i] = buf[i].MergeOver(ov);     // nesting: inner span layers over outer
        }

        private bool TryBuildOverride(string parameter, out SpanStyleOverride ov)
        {
            ov = default;
            switch (kind)
            {
                case Kind.Outline:
                    if (!SpanStyleMarkup.TryParseOutline(parameter, out var oc, out var ow)) return false;
                    ov.set = SpanStyleOverride.F.Outline; ov.outlineColor = oc; ov.outlineWidth = ow;
                    return true;
                case Kind.Underlay:
                    if (!SpanStyleMarkup.TryParseUnderlay(parameter, out var uc, out var ux, out var uy, out var ud, out var us)) return false;
                    ov.set = SpanStyleOverride.F.Underlay; ov.underlayColor = uc; ov.underlayOffsetX = ux; ov.underlayOffsetY = uy;
                    ov.underlayDilate = ud; ov.underlaySoftness = us;
                    return true;
                case Kind.Dilate:
                    if (!SpanStyleMarkup.TryParseScalar(parameter, out var d)) return false;
                    ov.set = SpanStyleOverride.F.Dilate; ov.faceDilate = d;
                    return true;
                case Kind.Softness:
                    if (!SpanStyleMarkup.TryParseScalar(parameter, out var s)) return false;
                    ov.set = SpanStyleOverride.F.Softness; ov.softness = s;
                    return true;
                case Kind.Style:
                    var sheet = uniText != null ? uniText.StyleSheet : null;
                    if (sheet == null || !sheet.TryGet(parameter, out var named)) return false;
                    ov.set = SpanStyleOverride.F.Face; ov.whole = named.ToGlyphStyle();
                    return true;
            }
            return false;
        }
    }
}
