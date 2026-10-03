using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    // OpenType features for the whole text (UniText.FontFeatures). <feature> spans: FeatureModifier.
    public partial class UniText
    {
        [NonSerialized] private HBFeature[] parsedFeatures = Array.Empty<HBFeature>();
        [NonSerialized] private int parsedFeatureCount;
        [NonSerialized] private bool featuresParsed;
        [NonSerialized] private string appliedTypographyKey;
        [NonSerialized] private readonly List<uint> featureTagScratch = new(8);
        [NonSerialized] private readonly List<uint> featureValueScratch = new(8);

        /// <summary>
        /// OpenType features for the whole text, as HarfBuzz-style settings: <c>"tnum"</c>, <c>"onum"</c>,
        /// <c>"smcp"</c>, <c>"ss01"</c>, <c>"cv01"</c>, <c>"liga=0"</c>, <c>"-kern"</c>, <c>"aalt=2"</c>
        /// (see <see cref="OpenTypeFeatures"/>). Applied over the whole text; <c>&lt;feature=…&gt;</c> spans
        /// override them on their range. Invalid entries are ignored. Setting it reshapes the text.
        /// </summary>
        public IReadOnlyList<string> FontFeatures
        {
            get => fontFeatures;
            set
            {
                fontFeatures ??= new List<string>();
                if (SameFeatures(fontFeatures, value)) return;
                fontFeatures.Clear();
                if (value != null) fontFeatures.AddRange(value);
                featuresParsed = false;
                SetDirty(DirtyFlags.Text);
            }
        }

        private static bool SameFeatures(List<string> a, IReadOnlyList<string> b)
        {
            var bc = b?.Count ?? 0;
            if (a.Count != bc) return false;
            for (var i = 0; i < bc; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private void EnsureFeaturesParsed()
        {
            if (featuresParsed) return;
            featureTagScratch.Clear();
            featureValueScratch.Clear();
            if (fontFeatures != null)
                for (var i = 0; i < fontFeatures.Count; i++)
                {
                    var f = fontFeatures[i];
                    if (string.IsNullOrEmpty(f)) continue;
                    OpenTypeFeatures.ParseList(f.AsSpan(), featureTagScratch, featureValueScratch);
                }
            var n = featureTagScratch.Count;
            if (parsedFeatures.Length < n) parsedFeatures = new HBFeature[n];
            for (var i = 0; i < n; i++)
                parsedFeatures[i] = new HBFeature { tag = featureTagScratch[i], value = featureValueScratch[i], start = 0, end = uint.MaxValue };
            parsedFeatureCount = n;
            featuresParsed = true;
        }

        /// <summary>Main thread: pushes the component features and language onto the processor.</summary>
        private void ConfigureTypography(TextProcessor processor)
        {
            EnsureFeaturesParsed();
            processor.SetGlobalFeatures(new ReadOnlySpan<HBFeature>(parsedFeatures, 0, parsedFeatureCount));
            ConfigureLanguage(processor);
            appliedTypographyKey = TypographyKey();
        }

        /// <summary>Pushes the component language onto the processor (UniText_Language).</summary>
        partial void ConfigureLanguage(TextProcessor processor);

        private string TypographyKey()
        {
            var key = language ?? "";
            if (fontFeatures != null)
                for (var i = 0; i < fontFeatures.Count; i++) key += "|" + fontFeatures[i];
            return key;
        }

        /// <summary>Editor: the Inspector wrote the fields directly. True when a reshape is needed.</summary>
        private bool OnValidateTypography()
        {
            OnValidateLayoutInputs();
            featuresParsed = false;
            var key = TypographyKey();
            return appliedTypographyKey != null && key != appliedTypographyKey;
        }

        /// <summary>Clamps the layout inputs written by the Inspector (padding, fit step).</summary>
        partial void OnValidateLayoutInputs();
    }
}
