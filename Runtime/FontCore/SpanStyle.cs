using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2: parsing + composition for the per-span style markup
    /// on the unified render path. The span tags each layer a PARTIAL override onto the component base
    /// style; a glyph's composed <see cref="GlyphStyle"/> is deduped to a per-component LOCAL style id,
    /// and that id is carried in the glyph's UV1.w through to <see cref="UnifiedRenderBuilder"/>, which
    /// maps it to a shared <see cref="StyleTable"/> row. One distinct composed style = one row, chosen
    /// per glyph — still a single renderer.
    /// </summary>
    /// <remarks>
    /// <para><b>Grammar</b> (follows the existing <c>&lt;tag=params&gt;…&lt;/tag&gt;</c> convention):</para>
    /// <code>
    ///   &lt;outline=#RRGGBB,w&gt;            outline colour + width (w in [0,1])
    ///   &lt;outline=#RRGGBBAA,w&gt;          outline colour with explicit alpha + width
    ///   &lt;underlay=#RRGGBBAA,x,y,dilate,softness&gt;   drop-shadow colour + offset + dilate + softness
    ///   &lt;dilate=v&gt;                     face dilation v in [-1,1]
    ///   &lt;softness=v&gt;                   edge softness v in [0,1]
    ///   &lt;style=Name&gt;                   a whole named UniTextStyle from the component StyleSheet
    /// </code>
    /// Tags nest and compose: an inner tag layers its field(s) over the style the outer tags produced.
    /// </remarks>
    public struct SpanStyleOverride : IEquatable<SpanStyleOverride>
    {
        // Which fields this override actually sets (nesting layers only the set fields).
        [Flags]
        public enum F
        {
            None = 0,
            Face = 1 << 0,       // whole-style replace (from <style=Name>)
            Outline = 1 << 1,
            Underlay = 1 << 2,
            Dilate = 1 << 3,
            Softness = 1 << 4,
        }

        public F set;
        public GlyphStyle whole;       // for F.Face (a full named style)
        public Color outlineColor; public float outlineWidth;
        public Color underlayColor; public float underlayOffsetX, underlayOffsetY, underlayDilate, underlaySoftness;
        public float faceDilate;
        public float softness;

        public bool IsNone => set == F.None;

        /// <summary>Layers this override's set fields onto <paramref name="baseStyle"/>, returning the composed style.</summary>
        public GlyphStyle ComposeOnto(in GlyphStyle baseStyle)
        {
            var s = (set & F.Face) != 0 ? whole : baseStyle;
            if ((set & F.Outline) != 0)
            {
                s.outlineColor = outlineColor;
                s.outlineWidth = outlineWidth;
            }
            if ((set & F.Underlay) != 0)
            {
                s.underlayColor = underlayColor;
                s.underlayOffsetX = underlayOffsetX;
                s.underlayOffsetY = underlayOffsetY;
                s.underlayDilate = underlayDilate;
                s.underlaySoftness = underlaySoftness;
            }
            if ((set & F.Dilate) != 0) s.faceDilate = faceDilate;
            if ((set & F.Softness) != 0) s.softness = softness;
            return s;
        }

        /// <summary>Merges <paramref name="inner"/> over this one (inner wins on fields it sets). Used so nested spans accumulate.</summary>
        public SpanStyleOverride MergeOver(in SpanStyleOverride inner)
        {
            var r = this;
            if ((inner.set & F.Face) != 0) { r.whole = inner.whole; r.set |= F.Face; }
            if ((inner.set & F.Outline) != 0) { r.outlineColor = inner.outlineColor; r.outlineWidth = inner.outlineWidth; r.set |= F.Outline; }
            if ((inner.set & F.Underlay) != 0)
            {
                r.underlayColor = inner.underlayColor; r.underlayOffsetX = inner.underlayOffsetX; r.underlayOffsetY = inner.underlayOffsetY;
                r.underlayDilate = inner.underlayDilate; r.underlaySoftness = inner.underlaySoftness; r.set |= F.Underlay;
            }
            if ((inner.set & F.Dilate) != 0) { r.faceDilate = inner.faceDilate; r.set |= F.Dilate; }
            if ((inner.set & F.Softness) != 0) { r.softness = inner.softness; r.set |= F.Softness; }
            return r;
        }

        public bool Equals(SpanStyleOverride o) =>
            set == o.set && whole.Equals(o.whole) &&
            outlineColor == o.outlineColor && outlineWidth == o.outlineWidth &&
            underlayColor == o.underlayColor && underlayOffsetX == o.underlayOffsetX && underlayOffsetY == o.underlayOffsetY &&
            underlayDilate == o.underlayDilate && underlaySoftness == o.underlaySoftness &&
            faceDilate == o.faceDilate && softness == o.softness;
        public override bool Equals(object obj) => obj is SpanStyleOverride o && Equals(o);
        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add((int)set); h.Add(whole); h.Add(outlineColor); h.Add(outlineWidth);
            h.Add(underlayColor); h.Add(underlayOffsetX); h.Add(underlayOffsetY); h.Add(underlayDilate); h.Add(underlaySoftness);
            h.Add(faceDilate); h.Add(softness);
            return h.ToHashCode();
        }
    }

    /// <summary>
    /// Parses the per-span markup parameter strings into a <see cref="SpanStyleOverride"/> field.
    /// Pure, allocation-light; shared by the span modifiers and directly unit-tested.
    /// </summary>
    public static class SpanStyleMarkup
    {
        /// <summary><c>outline=#RRGGBB,w</c> or <c>#RRGGBBAA,w</c>. Returns false on malformed input.</summary>
        public static bool TryParseOutline(string param, out Color color, out float width)
        {
            color = Color.black; width = 0f;
            if (string.IsNullOrEmpty(param)) return false;
            var parts = param.Split(',');
            if (parts.Length < 1) return false;
            if (!TryParseHtmlColor(parts[0].Trim(), out color)) return false;
            width = parts.Length >= 2 && TryParseFloat(parts[1], out var w) ? w : 0f;
            return true;
        }

        /// <summary><c>underlay=#RRGGBBAA,x,y,dilate,softness</c> (trailing params optional, default 0).</summary>
        public static bool TryParseUnderlay(string param, out Color color, out float x, out float y, out float dilate, out float softness)
        {
            color = new Color(0, 0, 0, 1); x = y = dilate = softness = 0f;
            if (string.IsNullOrEmpty(param)) return false;
            var parts = param.Split(',');
            if (!TryParseHtmlColor(parts[0].Trim(), out color)) return false;
            if (parts.Length > 1) TryParseFloat(parts[1], out x);
            if (parts.Length > 2) TryParseFloat(parts[2], out y);
            if (parts.Length > 3) TryParseFloat(parts[3], out dilate);
            if (parts.Length > 4) TryParseFloat(parts[4], out softness);
            return true;
        }

        public static bool TryParseScalar(string param, out float v) => TryParseFloat(param, out v);

        private static bool TryParseFloat(string s, out float v) =>
            float.TryParse((s ?? string.Empty).Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);

        /// <summary>Parses #RGB / #RRGGBB / #RRGGBBAA (alpha defaults to 1 when absent). Named colours deferred to ColorModifier's set — hex is the documented span form.</summary>
        public static bool TryParseHtmlColor(string s, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
            int n = s.Length - 1;
            byte Hex(char c) => (byte)(c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0);
            byte B(char h, char l) => (byte)(Hex(h) * 16 + Hex(l));
            if (n == 3) { color = new Color((Hex(s[1]) * 17) / 255f, (Hex(s[2]) * 17) / 255f, (Hex(s[3]) * 17) / 255f, 1f); return true; }
            if (n == 6) { color = new Color(B(s[1], s[2]) / 255f, B(s[3], s[4]) / 255f, B(s[5], s[6]) / 255f, 1f); return true; }
            if (n == 8) { color = new Color(B(s[1], s[2]) / 255f, B(s[3], s[4]) / 255f, B(s[5], s[6]) / 255f, B(s[7], s[8]) / 255f); return true; }
            return false;
        }
    }

    /// <summary>
    /// Per-component collector that dedups composed <see cref="GlyphStyle"/>s into LOCAL style ids
    /// (0 = base/no-span). The mesh-generation coordinator writes <c>localId</c> into a glyph's UV1.w;
    /// <see cref="UnifiedRenderBuilder"/> then maps each local id to a shared <see cref="StyleTable"/>
    /// row. One distinct composed style per local id → one row → one renderer.
    /// </summary>
    public sealed class SpanStyleCollector
    {
        private readonly List<GlyphStyle> _styles = new();     // index 0 is always the base style
        private readonly Dictionary<GlyphStyle, int> _index = new();

        /// <summary>Resets to just the base style at local id 0.</summary>
        public void Reset(in GlyphStyle baseStyle)
        {
            _styles.Clear();
            _index.Clear();
            _styles.Add(baseStyle);
            _index[baseStyle] = 0;
        }

        /// <summary>Local id of a composed style (0 = base). Dedups identical styles.</summary>
        public int GetOrAdd(in GlyphStyle style)
        {
            if (_index.TryGetValue(style, out int i)) return i;
            i = _styles.Count;
            _styles.Add(style);
            _index[style] = i;
            return i;
        }

        public int Count => _styles.Count;
        public GlyphStyle StyleAt(int localId) => _styles[(uint)localId < (uint)_styles.Count ? localId : 0];
    }
}
