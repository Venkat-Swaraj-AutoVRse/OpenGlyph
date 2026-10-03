using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2: one text style record (face / outline / underlay-shadow
    /// / glow + softness &amp; dilation), the thing a per-glyph <c>styleIdx</c> selects. A span with its
    /// own outline or shadow is simply a different <see cref="GlyphStyle"/> at a different index — no
    /// extra geometry, no extra pass (design §3.4).
    /// </summary>
    [Serializable]
    public struct GlyphStyle : IEquatable<GlyphStyle>
    {
        public Color faceColor;
        public float faceDilate;     // v[-1,1]
        public float softness;       // v[0,1]  (outline softness / edge AA)

        public Color outlineColor;
        public float outlineWidth;   // v[0,1]
        public float outlineDilate;  // v[-1,1]

        public Color underlayColor;  // shadow
        public float underlayOffsetX;
        public float underlayOffsetY;
        public float underlayDilate;
        public float underlaySoftness;

        public Color glowColor;
        public float glowOffset;
        public float glowOuter;
        public float glowInner;
        public float glowPower;

        // Wave 2: a second stroke band outside the outline, and an inner shadow inside the face.
        public Color outline2Color;
        public float outline2Width;    // v[0,1], added outside outlineWidth
        public float outline2Softness; // v[0,1]

        public Color innerShadowColor;
        public float innerShadowOffsetX;
        public float innerShadowOffsetY;
        public float innerShadowDilate;
        public float innerShadowSoftness;

        /// <summary>A plain opaque-white face with no outline/underlay/glow — the "default appearance".</summary>
        public static GlyphStyle Default => new()
        {
            faceColor = Color.white,
            faceDilate = 0f,
            softness = 0f,
            outlineColor = new Color(0, 0, 0, 0),
            outlineWidth = 0f,
            outlineDilate = 0f,
            underlayColor = new Color(0, 0, 0, 0),
            underlayOffsetX = 0f, underlayOffsetY = 0f, underlayDilate = 0f, underlaySoftness = 0f,
            glowColor = new Color(0, 0, 0, 0),
            glowOffset = 0f, glowOuter = 0f, glowInner = 0f, glowPower = 1f,
            outline2Color = new Color(0, 0, 0, 0), outline2Width = 0f, outline2Softness = 0f,
            innerShadowColor = new Color(0, 0, 0, 0),
            innerShadowOffsetX = 0f, innerShadowOffsetY = 0f, innerShadowDilate = 0f, innerShadowSoftness = 0f,
        };

        public bool Equals(GlyphStyle o) =>
            faceColor == o.faceColor && faceDilate == o.faceDilate && softness == o.softness &&
            outlineColor == o.outlineColor && outlineWidth == o.outlineWidth && outlineDilate == o.outlineDilate &&
            underlayColor == o.underlayColor && underlayOffsetX == o.underlayOffsetX && underlayOffsetY == o.underlayOffsetY &&
            underlayDilate == o.underlayDilate && underlaySoftness == o.underlaySoftness &&
            glowColor == o.glowColor && glowOffset == o.glowOffset && glowOuter == o.glowOuter &&
            glowInner == o.glowInner && glowPower == o.glowPower &&
            outline2Color == o.outline2Color && outline2Width == o.outline2Width && outline2Softness == o.outline2Softness &&
            innerShadowColor == o.innerShadowColor && innerShadowOffsetX == o.innerShadowOffsetX &&
            innerShadowOffsetY == o.innerShadowOffsetY && innerShadowDilate == o.innerShadowDilate &&
            innerShadowSoftness == o.innerShadowSoftness;

        public override bool Equals(object obj) => obj is GlyphStyle g && Equals(g);
        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(faceColor); h.Add(faceDilate); h.Add(softness);
            h.Add(outlineColor); h.Add(outlineWidth); h.Add(outlineDilate);
            h.Add(underlayColor); h.Add(underlayOffsetX); h.Add(underlayOffsetY);
            h.Add(underlayDilate); h.Add(underlaySoftness);
            h.Add(glowColor); h.Add(glowOffset); h.Add(glowOuter); h.Add(glowInner); h.Add(glowPower);
            h.Add(outline2Color); h.Add(outline2Width); h.Add(outline2Softness);
            h.Add(innerShadowColor); h.Add(innerShadowOffsetX); h.Add(innerShadowOffsetY);
            h.Add(innerShadowDilate); h.Add(innerShadowSoftness);
            return h.ToHashCode();
        }
    }

    /// <summary>
    /// A per-component <b>float-texture</b> style table (decision §5.2): the distinct
    /// <see cref="GlyphStyle"/>s used by a text, packed one-style-per-ROW into an
    /// <see cref="TextureFormat.RGBAFloat"/> <see cref="Texture2D"/>. The uber-shader samples row
    /// <c>styleIdx</c> to shade a glyph. A float texture (not a <c>StructuredBuffer</c>) is used so
    /// the path works on GLES3.0 / WebGL2 with no compute-buffer requirement.
    /// </summary>
    /// <remarks>
    /// <para><b>Layout.</b> Each style is <see cref="ColumnsPerStyle"/> RGBAFloat texels wide (one row):
    /// <code>
    ///   col 0: faceColor.rgba
    ///   col 1: outlineColor.rgba
    ///   col 2: underlayColor.rgba
    ///   col 3: glowColor.rgba
    ///   col 4: (faceDilate, softness, outlineWidth, outlineDilate)
    ///   col 5: (underlayOffsetX, underlayOffsetY, underlayDilate, underlaySoftness)
    ///   col 6: (glowOffset, glowOuter, glowInner, glowPower)
    ///   col 7: outline2Color.rgba
    ///   col 8: innerShadowColor.rgba
    ///   col 9: (outline2Width, outline2Softness, 0, 0)
    ///   col 10: (innerShadowOffsetX, innerShadowOffsetY, innerShadowDilate, innerShadowSoftness)
    ///   col 11: reserved (0,0,0,0)
    /// </code>
    /// The shader reads a texel with <c>(col + 0.5)/width, (styleIdx + 0.5)/height</c> point-sampled.</para>
    /// <para><b>Dedup.</b> Identical styles collapse to one row via <see cref="GetOrAdd"/>, so the row
    /// count equals the number of DISTINCT styles in the text (typically 1–8).</para>
    /// </remarks>
    public sealed class StyleTable : IDisposable
    {
        public const int ColumnsPerStyle = 12;

        private readonly List<GlyphStyle> _styles = new();
        private readonly Dictionary<GlyphStyle, int> _index = new();
        private Texture2D _tex;
        private bool _dirty;

        /// <summary>Number of distinct styles currently in the table.</summary>
        public int Count => _styles.Count;

        /// <summary>The backing float texture (built/refreshed by <see cref="Apply"/>). May be null before first Apply.</summary>
        public Texture2D Texture => _tex;

        /// <summary>Width of the style texture in texels (constant <see cref="ColumnsPerStyle"/>).</summary>
        public int Width => ColumnsPerStyle;

        /// <summary>Returns the row index of <paramref name="style"/>, adding it if new. Dedups identical styles.</summary>
        public int GetOrAdd(in GlyphStyle style)
        {
            if (_index.TryGetValue(style, out int i)) return i;
            i = _styles.Count;
            _styles.Add(style);
            _index[style] = i;
            _dirty = true;
            return i;
        }

        /// <summary>The style stored at <paramref name="row"/>.</summary>
        public GlyphStyle StyleAt(int row) => _styles[row];

        /// <summary>Clears all styles (keeps the texture object for reuse).</summary>
        public void Reset()
        {
            _styles.Clear();
            _index.Clear();
            _dirty = true;
        }

        /// <summary>
        /// Builds / refreshes the float texture from the current styles and uploads it. Call once after
        /// a batch of <see cref="GetOrAdd"/>. Height grows to the style count (min 1). Returns the texture.
        /// </summary>
        public Texture2D Apply()
        {
            int rows = Mathf.Max(1, _styles.Count);
            if (_tex == null || _tex.height != rows)
            {
                if (_tex != null) UnityEngine.Object.DestroyImmediate(_tex);
                _tex = new Texture2D(ColumnsPerStyle, rows, TextureFormat.RGBAFloat, false, true)
                {
                    name = "UniText StyleTable",
                    hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _dirty = true;
            }
            if (!_dirty) return _tex;

            var px = new Color[ColumnsPerStyle * rows];
            for (int r = 0; r < _styles.Count; r++)
                WriteRow(px, r, _styles[r]);
            // Any padding row (when styles==0) stays zeroed.
            _tex.SetPixels(px);
            _tex.Apply(false, false);
            _dirty = false;
            return _tex;
        }

        private static void WriteRow(Color[] px, int row, in GlyphStyle s)
        {
            int b = row * ColumnsPerStyle;
            px[b + 0] = s.faceColor;
            px[b + 1] = s.outlineColor;
            px[b + 2] = s.underlayColor;
            px[b + 3] = s.glowColor;
            px[b + 4] = new Color(s.faceDilate, s.softness, s.outlineWidth, s.outlineDilate);
            px[b + 5] = new Color(s.underlayOffsetX, s.underlayOffsetY, s.underlayDilate, s.underlaySoftness);
            px[b + 6] = new Color(s.glowOffset, s.glowOuter, s.glowInner, s.glowPower);
            px[b + 7] = s.outline2Color;
            px[b + 8] = s.innerShadowColor;
            px[b + 9] = new Color(s.outline2Width, s.outline2Softness, 0, 0);
            px[b + 10] = new Color(s.innerShadowOffsetX, s.innerShadowOffsetY, s.innerShadowDilate, s.innerShadowSoftness);
            px[b + 11] = new Color(0, 0, 0, 0);
        }

        /// <summary>
        /// TEST / diagnostics: reads back the packed value for (row, column) WITHOUT a GPU round-trip,
        /// reconstructed from the stored style so the packing layout is verifiable deterministically.
        /// </summary>
        internal Color ReadPacked(int row, int col)
        {
            var tmp = new Color[ColumnsPerStyle];
            WriteRow(tmp, 0, _styles[row]);
            return tmp[col];
        }

        public void Dispose()
        {
            if (_tex != null) { UnityEngine.Object.DestroyImmediate(_tex); _tex = null; }
            _styles.Clear();
            _index.Clear();
        }
    }
}
