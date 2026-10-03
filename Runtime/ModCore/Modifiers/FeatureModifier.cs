using System;
using System.Collections.Generic;

namespace LightSide
{
    /// <summary>
    /// <![CDATA[<feature=smcp,onum,tnum,liga=0>]]>: turns OpenType features on or off for the span. The
    /// parameter is a comma-separated list of feature settings (<c>tnum</c>, <c>liga=0</c>, <c>-kern</c>,
    /// <c>ss01</c>, <c>aalt=2</c>; see <see cref="OpenTypeFeatures"/>). The features are passed to
    /// HarfBuzz for exactly the span's codepoints, after the component's
    /// <see cref="UniText.FontFeatures"/>, so the span wins on its range; nested spans win over outer ones.
    /// </summary>
    [Serializable]
    [TypeGroup("Text Style", 0)]
    public class FeatureModifier : BaseModifier
    {
        [ThreadStatic] private static List<uint> tags;
        [ThreadStatic] private static List<uint> values;

        protected override void OnEnable() { }
        protected override void OnDisable() { }
        protected override void OnDestroy() { }

        protected override void OnApply(int start, int end, string parameter)
        {
            if (string.IsNullOrEmpty(parameter) || end <= start) return;
            var tp = uniText.TextProcessor;
            if (tp == null) return;
            tags ??= new List<uint>(8);
            values ??= new List<uint>(8);
            tags.Clear();
            values.Clear();
            OpenTypeFeatures.ParseList(parameter.AsSpan(), tags, values);
            for (var i = 0; i < tags.Count; i++)
                tp.AddFeatureSpan(new TextProcessor.FeatureSpan { start = start, end = end, tag = tags[i], value = values[i] });
        }
    }
}
