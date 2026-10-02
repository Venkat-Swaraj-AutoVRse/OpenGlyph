using System;

namespace LightSide
{
    /// <summary>
    /// Which style axis a face sits on, in the CSS <c>font-style</c> sense.
    /// </summary>
    public enum StyleAxis
    {
        /// <summary>Upright.</summary>
        Normal = 0,
        /// <summary>A designed italic (cursive) face.</summary>
        Italic = 1,
        /// <summary>A slanted (oblique) face, optionally with a specific angle.</summary>
        Oblique = 2
    }

    /// <summary>
    /// The style attributes a <see cref="FontFamily"/> matches on, mirroring the CSS Fonts
    /// font-matching algorithm's three descriptors: <c>font-stretch</c> (width),
    /// <c>font-style</c> (style/slant) and <c>font-weight</c> (weight).
    /// </summary>
    /// <remarks>
    /// Weight uses the OpenType <c>usWeightClass</c> scale (1..1000; 400 == normal, 700 == bold).
    /// Width is a percentage of normal (100 == normal, &lt;100 condensed, &gt;100 expanded), mapped
    /// from <c>OS/2.usWidthClass</c>. Slant is only meaningful for <see cref="StyleAxis.Oblique"/>.
    /// </remarks>
    [Serializable]
    public readonly struct FontStyleSpec : IEquatable<FontStyleSpec>
    {
        /// <summary>Requested weight on the usWeightClass scale (1..1000).</summary>
        public readonly int weight;
        /// <summary>Requested width as a percentage of normal (e.g. 75 == condensed, 100 == normal).</summary>
        public readonly float width;
        /// <summary>Requested style axis.</summary>
        public readonly StyleAxis style;
        /// <summary>Oblique angle in degrees (negative == forward slant); 0 for Normal/Italic.</summary>
        public readonly float slant;

        public const int NormalWeight = 400;
        public const int BoldWeight = 700;
        public const float NormalWidth = 100f;

        public FontStyleSpec(int weight, float width, StyleAxis style, float slant = 0f)
        {
            this.weight = weight;
            this.width = width;
            this.style = style;
            this.slant = slant;
        }

        /// <summary>The default request: regular upright 400 at normal width.</summary>
        public static FontStyleSpec Normal => new FontStyleSpec(NormalWeight, NormalWidth, StyleAxis.Normal, 0f);

        /// <summary>Returns a copy with <paramref name="w"/> as the weight.</summary>
        public FontStyleSpec WithWeight(int w) => new FontStyleSpec(w, width, style, slant);

        /// <summary>Returns a copy with <paramref name="s"/> as the style axis (and slant for oblique).</summary>
        public FontStyleSpec WithStyle(StyleAxis s, float slantDeg = 0f) => new FontStyleSpec(weight, width, s, s == StyleAxis.Oblique ? slantDeg : 0f);

        /// <summary>Returns a copy with <paramref name="w"/> as the width percentage.</summary>
        public FontStyleSpec WithWidth(float w) => new FontStyleSpec(weight, w, style, slant);

        /// <summary>
        /// Applies a <c>&lt;b&gt;</c> request: raises the weight to at least <see cref="BoldWeight"/>
        /// (never lowering an already-heavier request).
        /// </summary>
        public FontStyleSpec AsBold() => weight >= BoldWeight ? this : WithWeight(BoldWeight);

        /// <summary>Applies an <c>&lt;i&gt;</c> request: switches to <see cref="StyleAxis.Italic"/>.</summary>
        public FontStyleSpec AsItalic() => style == StyleAxis.Italic ? this : WithStyle(StyleAxis.Italic);

        public bool Equals(FontStyleSpec other) =>
            weight == other.weight && width.Equals(other.width) && style == other.style && slant.Equals(other.slant);

        public override bool Equals(object obj) => obj is FontStyleSpec o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(weight, width, (int)style, slant);
        public override string ToString() => $"{weight}/{width:0.#}%/{style}{(style == StyleAxis.Oblique ? $" {slant:0.#}deg" : "")}";
    }

    /// <summary>
    /// The style metadata attached to one face inside a <see cref="FontFamily"/>. For a static
    /// face these are fixed; for a variable face they describe the face's <em>default</em>
    /// position (the variation axes carry the rest).
    /// </summary>
    [Serializable]
    public struct FaceStyle
    {
        /// <summary>Weight on the usWeightClass scale (1..1000).</summary>
        public int weight;
        /// <summary>Width as a percentage of normal.</summary>
        public float width;
        /// <summary>Style axis.</summary>
        public StyleAxis style;
        /// <summary>Oblique angle in degrees (0 unless <see cref="style"/> is Oblique).</summary>
        public float slant;

        public FaceStyle(int weight, float width, StyleAxis style, float slant = 0f)
        {
            this.weight = weight;
            this.width = width;
            this.style = style;
            this.slant = slant;
        }

        public static FaceStyle Regular => new FaceStyle(FontStyleSpec.NormalWeight, FontStyleSpec.NormalWidth, StyleAxis.Normal, 0f);

        /// <summary>
        /// Derives a <see cref="FaceStyle"/> from an OpenType <c>name</c>-table style string such as
        /// "Regular", "Bold", "Italic", "Bold Italic", "Condensed Bold", "Oblique".
        /// </summary>
        /// <remarks>
        /// This is a name heuristic covering the common Regular/Bold/Italic/BoldItalic matrix plus a
        /// few width keywords. It intentionally uses only the style name already returned by the
        /// native face-info read (no new native exports). The authoritative numeric
        /// <c>OS/2.usWeightClass</c>/<c>usWidthClass</c> can be set explicitly in the family inspector
        /// to override this, and a future native export can supply them directly.
        /// </remarks>
        public static FaceStyle FromStyleName(string styleName)
        {
            var fs = Regular;
            if (string.IsNullOrEmpty(styleName)) return fs;
            var s = styleName.ToLowerInvariant();

            // Weight keywords (checked longest/most-specific first).
            if (s.Contains("thin")) fs.weight = 100;
            else if (s.Contains("extralight") || s.Contains("ultralight")) fs.weight = 200;
            else if (s.Contains("semibold") || s.Contains("demibold")) fs.weight = 600;
            else if (s.Contains("extrabold") || s.Contains("ultrabold")) fs.weight = 800;
            else if (s.Contains("light")) fs.weight = 300;
            else if (s.Contains("medium")) fs.weight = 500;
            else if (s.Contains("black") || s.Contains("heavy")) fs.weight = 900;
            else if (s.Contains("bold")) fs.weight = FontStyleSpec.BoldWeight;

            // Width keywords.
            if (s.Contains("ultracondensed")) fs.width = 50;
            else if (s.Contains("extracondensed")) fs.width = 62.5f;
            else if (s.Contains("semicondensed")) fs.width = 87.5f;
            else if (s.Contains("condensed") || s.Contains("narrow")) fs.width = 75;
            else if (s.Contains("ultraexpanded")) fs.width = 200;
            else if (s.Contains("extraexpanded")) fs.width = 150;
            else if (s.Contains("semiexpanded")) fs.width = 112.5f;
            else if (s.Contains("expanded") || s.Contains("wide")) fs.width = 125;

            // Style axis.
            if (s.Contains("italic")) fs.style = StyleAxis.Italic;
            else if (s.Contains("oblique")) { fs.style = StyleAxis.Oblique; fs.slant = -12f; }

            return fs;
        }
    }

    /// <summary>
    /// How a <see cref="FontFamily"/> satisfied a <see cref="FontStyleSpec"/> request. Reports
    /// whether the chosen face was an exact match and whether synthetic emboldening/slanting must
    /// still be applied on top of the chosen real face.
    /// </summary>
    public readonly struct FaceMatch
    {
        /// <summary>True when a face matched the request exactly (weight, width and style).</summary>
        public readonly bool exact;
        /// <summary>True when the result requires synthetic bold on top of the chosen face.</summary>
        public readonly bool synthesizeBold;
        /// <summary>True when the result requires synthetic italic/oblique on top of the chosen face.</summary>
        public readonly bool synthesizeItalic;

        public FaceMatch(bool exact, bool synthesizeBold, bool synthesizeItalic)
        {
            this.exact = exact;
            this.synthesizeBold = synthesizeBold;
            this.synthesizeItalic = synthesizeItalic;
        }

        public static FaceMatch Exact => new FaceMatch(true, false, false);
    }
}
