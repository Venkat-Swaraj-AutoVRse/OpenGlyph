using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2: a named-style asset for the <c>&lt;style=Name&gt;</c>
    /// per-span markup tag. Each entry pairs a name with a <see cref="UniTextStyle"/>; the span markup
    /// resolves <c>&lt;style=Heading&gt;…&lt;/style&gt;</c> to that style's <see cref="GlyphStyle"/>,
    /// which becomes a deduped <see cref="StyleTable"/> row chosen per glyph (still one renderer).
    /// </summary>
    /// <remarks>
    /// Create via Assets menu: Create → UniText → Style Sheet. Assign it on the <see cref="UniText"/>
    /// component (<c>StyleSheet</c>) so <c>&lt;style=Name&gt;</c> can look names up.
    /// </remarks>
    [CreateAssetMenu(fileName = "UniTextStyleSheet", menuName = "UniText/Style Sheet")]
    public sealed class UniTextStyleSheet : ScriptableObject, ISerializationCallbackReceiver
    {
        [Serializable]
        public struct NamedStyle
        {
            [Tooltip("Name used in <style=Name> markup (case-insensitive).")]
            public string name;
            [Tooltip("The style applied to the span.")]
            public UniTextStyle style;
        }

        [SerializeField]
        [Tooltip("Named styles addressable from <style=Name> markup.")]
        private NamedStyle[] styles = Array.Empty<NamedStyle>();

        private Dictionary<string, UniTextStyle> _lookup;

        private void BuildLookup()
        {
            _lookup = new Dictionary<string, UniTextStyle>(StringComparer.OrdinalIgnoreCase);
            if (styles == null) return;
            foreach (var s in styles)
                if (!string.IsNullOrEmpty(s.name))
                    _lookup[s.name] = s.style;
        }

        /// <summary>Looks up a named style. Returns false (and <paramref name="style"/> = default) if the name is unknown.</summary>
        public bool TryGet(string name, out UniTextStyle style)
        {
            if (_lookup == null) BuildLookup();
            if (!string.IsNullOrEmpty(name) && _lookup.TryGetValue(name, out style)) return true;
            style = UniTextStyle.Default;
            return false;
        }

        /// <summary>Adds or replaces a named style (editor/runtime authoring, e.g. the migration tool).</summary>
        public void Set(string name, in UniTextStyle style)
        {
            if (string.IsNullOrEmpty(name)) return;
            var list = new List<NamedStyle>(styles ?? Array.Empty<NamedStyle>());
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].name, name, StringComparison.OrdinalIgnoreCase))
                { list[i] = new NamedStyle { name = name, style = style }; styles = list.ToArray(); _lookup = null; return; }
            list.Add(new NamedStyle { name = name, style = style });
            styles = list.ToArray();
            _lookup = null;
        }

        public int Count => styles?.Length ?? 0;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() { _lookup = null; }

    #if UNITY_EDITOR
        private void OnValidate() => _lookup = null;
    #endif
    }
}
