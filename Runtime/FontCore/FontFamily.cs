using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// A font family: a set of related faces (<see cref="UniTextFont"/>) distinguished by weight,
    /// width and style, with CSS-style nearest-match selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selection follows the CSS Fonts Level&#160;4 <em>font matching algorithm</em>, applied in the
    /// fixed order <c>width → style → weight</c>, each stage narrowing the surviving candidates:
    /// </para>
    /// <list type="number">
    /// <item><b>Width</b> (<c>font-stretch</c>): exact; else, if the request is ≤ 100%, the nearest
    /// narrower then the nearest wider; if &gt; 100%, the nearest wider then the nearest narrower.</item>
    /// <item><b>Style</b> (<c>font-style</c>): exact; italic and oblique substitute for each other
    /// before falling back to normal.</item>
    /// <item><b>Weight</b> (<c>font-weight</c>): the CSS ladder —
    /// 400→400,500,&lt;400 desc,&gt;500 asc; 500→500,400,&lt;400 desc,&gt;500 asc;
    /// &lt;400→desc then asc; &gt;500→asc then desc.</item>
    /// </list>
    /// <para>
    /// A family is usable as a member inside a <see cref="UniTextFontStack"/>; codepoint fallback
    /// across stack members is unchanged, and family selection happens <em>within</em> a member.
    /// This type is additive — plain <see cref="UniTextFont"/> faces and existing stacks keep
    /// working exactly as before.
    /// </para>
    /// </remarks>
    /// <seealso cref="FontStyleSpec"/>
    /// <seealso cref="UniTextFontStack"/>
    [CreateAssetMenu(fileName = "FontFamily", menuName = "UniText/Font Family", order = 2)]
    public class FontFamily : ScriptableObject
    {
        /// <summary>One face plus the style coordinates it occupies in the family matrix.</summary>
        [Serializable]
        public struct FamilyFace
        {
            public UniTextFont font;
            public FaceStyle style;

            public FamilyFace(UniTextFont font, FaceStyle style)
            {
                this.font = font;
                this.style = style;
            }
        }

        [Tooltip("Human-readable family name (e.g. \"Noto Sans\").")]
        public string familyName;

        [Tooltip("Member faces. Each carries the weight/width/style coordinates it occupies.")]
        public List<FamilyFace> faces = new();

        /// <summary>Number of non-null member faces.</summary>
        public int Count
        {
            get
            {
                var n = 0;
                for (var i = 0; i < faces.Count; i++)
                    if (faces[i].font != null) n++;
                return n;
            }
        }

        /// <summary>
        /// Selects the best face for <paramref name="spec"/> using the CSS matching algorithm.
        /// </summary>
        /// <param name="spec">The requested weight/width/style.</param>
        /// <param name="how">Output: whether the match was exact and whether synthesis is needed.</param>
        /// <returns>The chosen face, or null when the family has no usable faces.</returns>
        public UniTextFont Match(FontStyleSpec spec, out FaceMatch how)
        {
            how = default;

            // Collect usable candidates.
            var pool = new List<FamilyFace>(faces.Count);
            for (var i = 0; i < faces.Count; i++)
                if (faces[i].font != null)
                    pool.Add(faces[i]);

            if (pool.Count == 0)
                return null;

            // Stage 1 — width (font-stretch).
            pool = FilterByWidth(pool, spec.width);
            // Stage 2 — style (font-style).
            pool = FilterByStyle(pool, spec.style);
            // Stage 3 — weight (font-weight CSS ladder).
            var chosen = PickByWeight(pool, spec.weight);

            var cs = chosen.style;
            var exact = cs.weight == spec.weight
                        && Mathf.Approximately(cs.width, spec.width)
                        && cs.style == spec.style;

            // Synthesis is needed when the chosen real face is lighter than a bold request,
            // or upright when an italic/oblique was requested.
            var synthBold = spec.weight >= FontStyleSpec.BoldWeight && cs.weight < FontStyleSpec.BoldWeight;
            var wantSlanted = spec.style == StyleAxis.Italic || spec.style == StyleAxis.Oblique;
            var synthItalic = wantSlanted && cs.style == StyleAxis.Normal;

            how = new FaceMatch(exact, synthBold, synthItalic);
            return chosen.font;
        }

        // ---- Stage 1: width ------------------------------------------------------
        // Exact if present; otherwise, per CSS: desired <= 100% prefers narrower then wider,
        // desired > 100% prefers wider then narrower. We reduce the pool to the single nearest
        // width value under that rule, keeping every face that shares it (so style/weight can
        // still discriminate).
        private static List<FamilyFace> FilterByWidth(List<FamilyFace> pool, float desired)
        {
            float best = float.NaN;
            foreach (var f in pool)
            {
                var w = f.style.width;
                if (float.IsNaN(best) || WidthBetter(w, best, desired))
                    best = w;
            }

            var res = new List<FamilyFace>(pool.Count);
            foreach (var f in pool)
                if (Mathf.Approximately(f.style.width, best))
                    res.Add(f);
            return res;
        }

        // True when candidate width 'a' is a better font-stretch match than 'b' for 'desired'.
        private static bool WidthBetter(float a, float b, float desired)
        {
            if (Mathf.Approximately(a, desired)) return !Mathf.Approximately(b, desired);
            if (Mathf.Approximately(b, desired)) return false;

            bool aNarrower = a <= desired, bNarrower = b <= desired;

            if (desired <= FontStyleSpec.NormalWidth)
            {
                // Prefer narrower (<= desired). Among narrower, the largest (closest) wins.
                if (aNarrower != bNarrower) return aNarrower;
                return aNarrower ? a > b : a < b; // among narrower: larger; among wider: smaller
            }
            else
            {
                // Prefer wider (>= desired). Among wider, the smallest (closest) wins.
                bool aWider = a >= desired, bWider = b >= desired;
                if (aWider != bWider) return aWider;
                return aWider ? a < b : a > b;
            }
        }

        // ---- Stage 2: style ------------------------------------------------------
        // CSS substitution: Italic and Oblique stand in for each other before Normal; Normal
        // falls back to Oblique then Italic only if no upright exists.
        private static List<FamilyFace> FilterByStyle(List<FamilyFace> pool, StyleAxis desired)
        {
            StyleAxis[] order = desired switch
            {
                StyleAxis.Italic => new[] { StyleAxis.Italic, StyleAxis.Oblique, StyleAxis.Normal },
                StyleAxis.Oblique => new[] { StyleAxis.Oblique, StyleAxis.Italic, StyleAxis.Normal },
                _ => new[] { StyleAxis.Normal, StyleAxis.Oblique, StyleAxis.Italic },
            };

            foreach (var want in order)
            {
                var res = new List<FamilyFace>();
                foreach (var f in pool)
                    if (f.style.style == want)
                        res.Add(f);
                if (res.Count > 0) return res;
            }
            return pool; // unreachable when pool is non-empty, but safe
        }

        // ---- Stage 3: weight -----------------------------------------------------
        // The exact CSS font-weight fallback ladder.
        private static FamilyFace PickByWeight(List<FamilyFace> pool, int desired)
        {
            // Exact first.
            foreach (var f in pool)
                if (f.style.weight == desired)
                    return f;

            // Build the CSS preference order of weights to try.
            // 400: 400,500, then <400 desc, then >500 asc.
            // 500: 500,400, then <400 desc, then >500 asc.
            // <400: desc from desired, then asc above desired.
            // >500: asc from desired, then desc below desired.
            FamilyFace lighterNearest = default; bool haveLighter = false; int lighterW = int.MinValue;
            FamilyFace heavierNearest = default; bool haveHeavier = false; int heavierW = int.MaxValue;
            FamilyFace nearest = default; int nearestDist = int.MaxValue;

            foreach (var f in pool)
            {
                var w = f.style.weight;
                var dist = Math.Abs(w - desired);
                if (dist < nearestDist) { nearestDist = dist; nearest = f; }

                if (w < desired && w > lighterW) { lighterW = w; lighterNearest = f; haveLighter = true; }
                if (w > desired && w < heavierW) { heavierW = w; heavierNearest = f; haveHeavier = true; }
            }

            // Special band 400..500 (CSS carves this out explicitly).
            if (desired >= FontStyleSpec.NormalWeight && desired <= 500)
            {
                // Try 500 (for 400) / 400 (for 500) first, then lighter desc, then heavier asc.
                int firstSibling = desired == 500 ? 400 : 500;
                foreach (var f in pool)
                    if (f.style.weight == firstSibling)
                        return f;
                if (haveLighter) return lighterNearest; // nearest below
                if (haveHeavier) return heavierNearest; // nearest above
                return nearest;
            }

            if (desired < FontStyleSpec.NormalWeight)
            {
                // Prefer lighter (desc), then heavier (asc).
                if (haveLighter) return lighterNearest;
                if (haveHeavier) return heavierNearest;
                return nearest;
            }

            // desired > 500: prefer heavier (asc), then lighter (desc).
            if (haveHeavier) return heavierNearest;
            if (haveLighter) return lighterNearest;
            return nearest;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrEmpty(familyName))
                familyName = name;
        }
#endif
    }
}
