using System;
using System.Collections.Generic;

namespace LightSide
{
    // OpenType features: the component list (UniText.FontFeatures) and <feature> spans, passed to HarfBuzz
    // with the span's cluster range. Empty by default, so plain shaping is unchanged.
    public sealed partial class TextProcessor
    {
        /// <summary>An OpenType feature applied to a codepoint range (<c>&lt;feature&gt;</c> span).</summary>
        public struct FeatureSpan
        {
            public int start;
            public int end;
            public uint tag;
            public uint value;
        }

        private HBFeature[] globalFeatures = Array.Empty<HBFeature>();
        private int globalFeatureCount;
        private readonly List<FeatureSpan> featureSpans = new();

        /// <summary>
        /// Component-wide OpenType features, applied to the whole text before any <c>&lt;feature&gt;</c>
        /// span (a span overrides them on its range). A change requires a first-pass rebuild.
        /// </summary>
        public void SetGlobalFeatures(ReadOnlySpan<HBFeature> features)
        {
            if (globalFeatures.Length < features.Length) globalFeatures = new HBFeature[features.Length];
            features.CopyTo(globalFeatures);
            globalFeatureCount = features.Length;
        }

        /// <summary>The component-wide features currently set (diagnostics/tests).</summary>
        public ReadOnlySpan<HBFeature> GlobalFeatures => globalFeatures.AsSpan(0, globalFeatureCount);

        /// <summary>Registers a ranged OpenType feature for this text (called by <see cref="FeatureModifier"/> while parsing).</summary>
        public void AddFeatureSpan(FeatureSpan span) => featureSpans.Add(span);

        /// <summary>Clears the per-text typography spans; called at the start of every first pass.</summary>
        private void ClearTypographySpans()
        {
            featureSpans.Clear();
            OnClearTypographySpans();
        }

        /// <summary>Clears the spans of the other typography inputs (language).</summary>
        partial void OnClearTypographySpans();

        /// <summary>
        /// Appends the component features (whole run) and the <c>&lt;feature&gt;</c> spans that intersect
        /// <paramref name="range"/> (clipped to it) after <paramref name="count"/> existing entries.
        /// HarfBuzz applies later entries over earlier ones for the same tag, so spans override the
        /// component list, and nested (later-starting) spans override outer ones.
        /// </summary>
        private int AppendUserFeatures(int count, TextRange range)
        {
            var extra = globalFeatureCount + featureSpans.Count;
            if (extra == 0) return count;
            featureScratch ??= new HBFeature[8];
            if (count + extra > featureScratch.Length)
                Array.Resize(ref featureScratch, Math.Max(featureScratch.Length * 2, count + extra));

            for (var i = 0; i < globalFeatureCount; i++)
            {
                var f = globalFeatures[i];
                featureScratch[count++] = new HBFeature
                {
                    tag = f.tag, value = f.value, start = (uint)range.start, end = (uint)range.End
                };
            }

            var rs = range.start;
            var re = range.End;
            for (var i = 0; i < featureSpans.Count; i++)
            {
                var s = featureSpans[i];
                var a = Math.Max(s.start, rs);
                var b = Math.Min(s.end, re);
                if (a >= b) continue;
                featureScratch[count++] = new HBFeature { tag = s.tag, value = s.value, start = (uint)a, end = (uint)b };
            }
            return count;
        }

        /// <summary>Sorts feature spans so nested spans (later start) come after their parents.</summary>
        private void SortFeatureSpans()
        {
            if (featureSpans.Count > 1)
                featureSpans.Sort(static (a, b) => a.start != b.start ? a.start.CompareTo(b.start) : b.end.CompareTo(a.end));
        }
    }
}
