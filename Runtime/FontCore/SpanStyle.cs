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
    ///   &lt;glow=#RRGGBBAA,size,softness,intensity&gt;   soft outer glow (wave 2)
    ///   &lt;innershadow=#RRGGBBAA,x,y,dilate,softness&gt; shadow inside the face (wave 2)
    ///   &lt;outline2=#RRGGBBAA,width,softness&gt;       second stroke band outside the outline (wave 2)
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
            Glow = 1 << 5,
            InnerShadow = 1 << 6,
            Outline2 = 1 << 7,
        }

        public F set;
        public GlyphStyle whole;       // for F.Face (a full named style)
        public Color outlineColor; public float outlineWidth;
        public Color underlayColor; public float underlayOffsetX, underlayOffsetY, underlayDilate, underlaySoftness;
        public float faceDilate;
        public float softness;
        public Color glowColor; public float glowOuter, glowPower, glowOffset;
        public Color innerShadowColor; public float innerShadowOffsetX, innerShadowOffsetY, innerShadowDilate, innerShadowSoftness;
        public Color outline2Color; public float outline2Width, outline2Softness;

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
            if ((set & F.Glow) != 0)
            {
                s.glowColor = glowColor; s.glowOuter = glowOuter; s.glowPower = glowPower;
                s.glowOffset = glowOffset; s.glowInner = 0f;
            }
            if ((set & F.InnerShadow) != 0)
            {
                s.innerShadowColor = innerShadowColor; s.innerShadowOffsetX = innerShadowOffsetX;
                s.innerShadowOffsetY = innerShadowOffsetY; s.innerShadowDilate = innerShadowDilate;
                s.innerShadowSoftness = innerShadowSoftness;
            }
            if ((set & F.Outline2) != 0)
            {
                s.outline2Color = outline2Color; s.outline2Width = outline2Width; s.outline2Softness = outline2Softness;
            }
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
            if ((inner.set & F.Glow) != 0)
            {
                r.glowColor = inner.glowColor; r.glowOuter = inner.glowOuter; r.glowPower = inner.glowPower;
                r.glowOffset = inner.glowOffset; r.set |= F.Glow;
            }
            if ((inner.set & F.InnerShadow) != 0)
            {
                r.innerShadowColor = inner.innerShadowColor; r.innerShadowOffsetX = inner.innerShadowOffsetX;
                r.innerShadowOffsetY = inner.innerShadowOffsetY; r.innerShadowDilate = inner.innerShadowDilate;
                r.innerShadowSoftness = inner.innerShadowSoftness; r.set |= F.InnerShadow;
            }
            if ((inner.set & F.Outline2) != 0)
            {
                r.outline2Color = inner.outline2Color; r.outline2Width = inner.outline2Width;
                r.outline2Softness = inner.outline2Softness; r.set |= F.Outline2;
            }
            return r;
        }

        public bool Equals(SpanStyleOverride o) =>
            set == o.set && whole.Equals(o.whole) &&
            outlineColor == o.outlineColor && outlineWidth == o.outlineWidth &&
            underlayColor == o.underlayColor && underlayOffsetX == o.underlayOffsetX && underlayOffsetY == o.underlayOffsetY &&
            underlayDilate == o.underlayDilate && underlaySoftness == o.underlaySoftness &&
            faceDilate == o.faceDilate && softness == o.softness &&
            glowColor == o.glowColor && glowOuter == o.glowOuter && glowPower == o.glowPower && glowOffset == o.glowOffset &&
            innerShadowColor == o.innerShadowColor && innerShadowOffsetX == o.innerShadowOffsetX &&
            innerShadowOffsetY == o.innerShadowOffsetY && innerShadowDilate == o.innerShadowDilate &&
            innerShadowSoftness == o.innerShadowSoftness &&
            outline2Color == o.outline2Color && outline2Width == o.outline2Width && outline2Softness == o.outline2Softness;
        public override bool Equals(object obj) => obj is SpanStyleOverride o && Equals(o);
        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add((int)set); h.Add(whole); h.Add(outlineColor); h.Add(outlineWidth);
            h.Add(underlayColor); h.Add(underlayOffsetX); h.Add(underlayOffsetY); h.Add(underlayDilate); h.Add(underlaySoftness);
            h.Add(faceDilate); h.Add(softness);
            h.Add(glowColor); h.Add(glowOuter); h.Add(glowPower); h.Add(glowOffset);
            h.Add(innerShadowColor); h.Add(innerShadowOffsetX); h.Add(innerShadowOffsetY);
            h.Add(innerShadowDilate); h.Add(innerShadowSoftness);
            h.Add(outline2Color); h.Add(outline2Width); h.Add(outline2Softness);
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

        /// <summary>Maps a 0..1 glow softness to the falloff exponent the shaders use (0 = hard, 1 = soft).</summary>
        public static float GlowPowerFromSoftness(float softness) => Mathf.Lerp(0.5f, 3f, Mathf.Clamp01(softness));

        /// <summary>
        /// <c>glow=#RRGGBBAA,size,softness,intensity</c>: colour, size v[0,1] of the atlas spread (default 0.5),
        /// softness v[0,1] (default 0.5) and intensity (default 1, multiplies the colour alpha).
        /// </summary>
        public static bool TryParseGlow(string param, out Color color, out float size, out float power)
        {
            color = Color.white; size = 0.5f; power = GlowPowerFromSoftness(0.5f);
            if (string.IsNullOrEmpty(param)) return false;
            var parts = param.Split(',');
            if (!TryParseHtmlColor(parts[0].Trim(), out color)) return false;
            if (parts.Length > 1 && TryParseFloat(parts[1], out var s)) size = Mathf.Max(0f, s);
            if (parts.Length > 2 && TryParseFloat(parts[2], out var soft)) power = GlowPowerFromSoftness(soft);
            if (parts.Length > 3 && TryParseFloat(parts[3], out var intensity)) color.a *= Mathf.Max(0f, intensity);
            return true;
        }

        /// <summary><c>outline2=#RRGGBBAA,width,softness</c> (width default 0.1, softness default 0).</summary>
        public static bool TryParseOutline2(string param, out Color color, out float width, out float softness)
        {
            color = Color.black; width = 0.1f; softness = 0f;
            if (string.IsNullOrEmpty(param)) return false;
            var parts = param.Split(',');
            if (!TryParseHtmlColor(parts[0].Trim(), out color)) return false;
            if (parts.Length > 1 && TryParseFloat(parts[1], out var w)) width = Mathf.Max(0f, w);
            if (parts.Length > 2 && TryParseFloat(parts[2], out var s)) softness = Mathf.Clamp01(s);
            return true;
        }

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
