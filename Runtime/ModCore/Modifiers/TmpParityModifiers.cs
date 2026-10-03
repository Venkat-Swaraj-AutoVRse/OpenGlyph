using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// <![CDATA[<nobr>]]>: no soft line breaks inside the span (TextMeshPro no-break). Hard breaks
    /// (newlines) still break; a span wider than the line still overflows character-wise as a last resort.
    /// </summary>
    [Serializable]
    [TypeGroup("Layout", 3)]
    public class NoBreakModifier : BaseModifier
    {
        protected override void OnEnable() { }
        protected override void OnDisable() { }
        protected override void OnDestroy() { }

        protected override void OnApply(int start, int end, string parameter)
        {
            // breakOpportunities[k] is the break between codepoint k-1 and k; clear those strictly inside.
            var br = buffers.breakOpportunities.data;
            if (br == null) return;
            var last = Math.Min(end, Math.Min(buffers.codepoints.count, br.Length - 1));
            for (var k = start + 1; k < last; k++)
                if (br[k] == LineBreakType.Optional) br[k] = LineBreakType.None;
        }
    }

    /// <summary>
    /// <![CDATA[<align=left|center|right|justified|flush>]]>: per-paragraph horizontal alignment. The first
    /// codepoint of a line that carries an override decides that line's alignment.
    /// </summary>
    [Serializable]
    [TypeGroup("Layout", 3)]
    public class AlignModifier : BaseModifier
    {
        private PooledArrayAttribute<byte> attribute;

        protected override void OnEnable()
        {
            attribute ??= buffers.GetOrCreateAttributeData<PooledArrayAttribute<byte>>(AttributeKeys.LineAlignment);
            attribute.EnsureCountAndClear(buffers.codepoints.count);
        }

        protected override void OnDisable() { }

        protected override void OnDestroy()
        {
            buffers?.ReleaseAttributeData(AttributeKeys.LineAlignment);
            attribute = null;
        }

        protected override void OnApply(int start, int end, string parameter)
        {
            if (!TryParse(parameter, out var align)) return;
            var data = attribute.buffer.data;
            var e = Math.Min(end, Math.Min(buffers.codepoints.count, data.Length));
            var v = (byte)((int)align + 1);
            for (var i = start; i < e; i++) data[i] = v;
        }

        internal static bool TryParse(string p, out HorizontalAlignment align)
        {
            align = HorizontalAlignment.Left;
            if (string.IsNullOrEmpty(p)) return false;
            switch (p.Trim().ToLowerInvariant())
            {
                case "left": align = HorizontalAlignment.Left; return true;
                case "center": case "centre": align = HorizontalAlignment.Center; return true;
                case "right": align = HorizontalAlignment.Right; return true;
                case "justified": case "justify": align = HorizontalAlignment.Justified; return true;
                case "flush": align = HorizontalAlignment.Flush; return true;
                default: return false;
            }
        }
    }

    /// <summary>
    /// <![CDATA[<indent=N|N%|Nem>]]> and <![CDATA[<line-indent=...>]]> (TextMeshPro). <c>indent</c> moves the
    /// pen to the indent position where the tag opens and starts every following line of the span there;
    /// <c>line-indent</c> indents the first line of each paragraph in the span. Units: pixels (no suffix
    /// or <c>px</c>), <c>em</c> (font size), <c>%</c> (of the text-area width).
    /// </summary>
    [Serializable]
    [TypeGroup("Layout", 3)]
    public class IndentModifier : BaseModifier
    {
        [SerializeField, Tooltip("True for <line-indent> (first line of each paragraph only).")]
        private bool lineIndent;

        public IndentModifier() { }
        public IndentModifier(bool lineIndent) { this.lineIndent = lineIndent; }

        /// <summary>True for <c>&lt;line-indent&gt;</c>.</summary>
        public bool LineIndent { get => lineIndent; set => lineIndent = value; }

        protected override void OnEnable() { }
        protected override void OnDisable() { }
        protected override void OnDestroy() { }

        protected override void OnApply(int start, int end, string parameter)
        {
            if (!TryParseLength(parameter, out var value, out var unit)) return;
            uniText.TextProcessor?.AddIndentSpan(new TextProcessor.IndentSpan
            {
                start = start, end = end, value = value, unit = unit, lineIndent = lineIndent
            });
        }

        /// <summary>Parses "N", "Npx", "Nem" or "N%"; unit 0 = px, 1 = em, 2 = percent.</summary>
        internal static bool TryParseLength(string p, out float value, out byte unit)
        {
            value = 0f; unit = 0;
            if (string.IsNullOrEmpty(p)) return false;
            var s = p.AsSpan().Trim();
            if (s.Length > 1 && s[s.Length - 1] == '%') { unit = 2; s = s.Slice(0, s.Length - 1); }
            else if (s.Length > 2 && s.EndsWith("em".AsSpan(), StringComparison.OrdinalIgnoreCase)) { unit = 1; s = s.Slice(0, s.Length - 2); }
            else if (s.Length > 2 && s.EndsWith("px".AsSpan(), StringComparison.OrdinalIgnoreCase)) { s = s.Slice(0, s.Length - 2); }
            return ModifierNumberParse.TryParseFloat(s, out value);
        }
    }

    /// <summary>
    /// <![CDATA[<font="Name">]]>: renders the span with another font, resolved by name through
    /// <see cref="UniTextFontRegistry"/> (explicit registrations, then the component's font stack, then
    /// the project default stack). Characters the font lacks fall back to the normal stack.
    /// <c>default</c> (or an unknown name) leaves the span on the component font.
    /// </summary>
    [Serializable]
    [TypeGroup("Text Style", 0)]
    public class FontModifier : BaseModifier
    {
        private PooledArrayAttribute<int> attribute;

        protected override void OnEnable()
        {
            attribute ??= buffers.GetOrCreateAttributeData<PooledArrayAttribute<int>>(AttributeKeys.FontOverride);
            attribute.EnsureCountAndClear(buffers.codepoints.count);
        }

        protected override void OnDisable() { }

        protected override void OnDestroy()
        {
            buffers?.ReleaseAttributeData(AttributeKeys.FontOverride);
            attribute = null;
        }

        public override void PrepareForParallel()
        {
            // Main thread: snapshot asset names (Object.name is main-thread only) for worker lookups.
            UniTextFontRegistry.PrepareNames(uniText);
        }

        protected override void OnApply(int start, int end, string parameter)
        {
            var name = ExtractName(parameter);
            if (string.IsNullOrEmpty(name) || string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
                return;
            if (!UniTextFontRegistry.TryResolve(name, uniText, out var font) || font == null) return;
            var tp = uniText.TextProcessor;
            if (tp == null) return;
            var idx = tp.RegisterSpanFont(font);
            if (idx <= 0) return;
            var data = attribute.buffer.data;
            var e = Math.Min(end, Math.Min(buffers.codepoints.count, data.Length));
            for (var i = start; i < e; i++) data[i] = idx;
        }

        /// <summary>The font name from a TMP font parameter: <c>"Name"</c>, <c>Name</c>, or
        /// <c>"Name" material="M"</c> (the material attribute is ignored).</summary>
        internal static string ExtractName(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            p = p.Trim();
            if (p.Length > 0 && (p[0] == '"' || p[0] == '\''))
            {
                var q = p[0];
                var close = p.IndexOf(q, 1);
                return close > 1 ? p.Substring(1, close - 1) : p.Substring(1);
            }
            var sp = p.IndexOf(' ');
            return sp > 0 ? p.Substring(0, sp) : p;
        }
    }

    /// <summary>
    /// <![CDATA[<smallcaps>]]> (and GlyphMeshPro <c>FontStyles.SmallCaps</c>): uses the font's OpenType
    /// <c>smcp</c> small capitals when it has them; otherwise synthesises them TMP's way — lowercase
    /// letters become capitals drawn at 0.8× size.
    /// </summary>
    [Serializable]
    [TypeGroup("Text Style", 0)]
    public class SmallCapsModifier : BaseModifier
    {
        /// <summary>Synthetic small-caps scale for lowercase letters (TextMeshPro uses 0.8).</summary>
        public const float SyntheticScale = 0.8f;
        private static readonly uint SmcpTag = HB.Tag('s', 'm', 'c', 'p');

        private PooledArrayAttribute<byte> feature;
        private PooledArrayAttribute<float> scale;
        private bool anySynthetic;

        /// <summary>True when the last applied span used the real OpenType feature (diagnostics/tests).</summary>
        public bool LastUsedFontFeature { get; private set; }

        protected override void OnEnable()
        {
            feature ??= buffers.GetOrCreateAttributeData<PooledArrayAttribute<byte>>(AttributeKeys.SmallCapsFeature);
            feature.EnsureCountAndClear(buffers.codepoints.count);
            scale ??= buffers.GetOrCreateAttributeData<PooledArrayAttribute<float>>(AttributeKeys.SmallCapsScale);
            scale.EnsureCountAndClear(buffers.codepoints.count);
            anySynthetic = false;
            uniText.TextProcessor.Shaped += OnShaped;
            uniText.MeshGenerator.OnGlyph += OnGlyph;
        }

        protected override void OnDisable()
        {
            uniText.TextProcessor.Shaped -= OnShaped;
            uniText.MeshGenerator.OnGlyph -= OnGlyph;
        }

        protected override void OnDestroy()
        {
            buffers?.ReleaseAttributeData(AttributeKeys.SmallCapsFeature);
            buffers?.ReleaseAttributeData(AttributeKeys.SmallCapsScale);
            feature = null;
            scale = null;
        }

        protected override void OnApply(int start, int end, string parameter)
        {
            var cps = buffers.codepoints.data;
            var e = Math.Min(end, buffers.codepoints.count);
            var font = uniText.FontProvider?.MainFont;
            var useFeature = font != null && Shaper.FontSubstitutesFeature(font, SmcpTag);
            LastUsedFontFeature = useFeature;

            if (useFeature)
            {
                var f = feature.buffer.data;
                for (var i = start; i < e && i < f.Length; i++) f[i] = 1;
                return;
            }

            var s = scale.buffer.data;
            for (var i = start; i < e && i < s.Length; i++)
            {
                var cp = cps[i];
                if (cp > 0xFFFF || !char.IsLower((char)cp)) continue;
                cps[i] = char.ToUpperInvariant((char)cp);
                s[i] = SyntheticScale;
                anySynthetic = true;
            }
        }

        private void OnShaped()
        {
            if (!anySynthetic) return;
            var s = scale.buffer.data;
            var glyphs = buffers.shapedGlyphs.data;
            var runs = buffers.shapedRuns.data;
            for (var r = 0; r < buffers.shapedRuns.count; r++)
            {
                ref var run = ref runs[r];
                var end = run.glyphStart + run.glyphCount;
                var width = 0f;
                for (var g = run.glyphStart; g < end; g++)
                {
                    var cl = glyphs[g].cluster;
                    if ((uint)cl < (uint)s.Length && s[cl] > 0f) glyphs[g].advanceX *= s[cl];
                    width += glyphs[g].advanceX;
                }
                run.width = width;
            }
        }

        private void OnGlyph()
        {
            if (!anySynthetic) return;
            var gen = UniTextMeshGenerator.Current;
            var cl = gen.currentCluster;
            var s = scale.buffer.data;
            if ((uint)cl >= (uint)s.Length) return;
            var k = s[cl];
            if (k <= 0f) return;
            var verts = gen.Vertices;
            var b = gen.vertexCount - 4;
            var left = verts[b].x;
            var baseline = gen.baselineY;
            for (var i = 0; i < 4; i++)
            {
                ref var v = ref verts[b + i];
                v.x = left + (v.x - left) * k;
                v.y = baseline + (v.y - baseline) * k;
            }
        }
    }

    /// <summary>
    /// <![CDATA[<mark=#RRGGBBAA>]]> (and GlyphMeshPro <c>FontStyles.Highlight</c>): a solid background box
    /// behind the run, one per line, spanning the run's advances horizontally and the font's
    /// ascender..descender vertically (TextMeshPro highlight geometry). Drawn behind the glyphs of the same
    /// mesh, in both the unified and the legacy renderer.
    /// </summary>
    [Serializable]
    [TypeGroup("Decoration", 1)]
    public class MarkModifier : BaseModifier
    {
        /// <summary>TextMeshPro's default highlight colour (#FFFF0040).</summary>
        public static readonly Color32 DefaultColor = new Color32(255, 255, 0, 64);

        private PooledArrayAttribute<uint> attribute;
        private int segmentTriStart;
        [ThreadStatic] private static int[] rotateScratch;

        protected override void OnEnable()
        {
            attribute ??= buffers.GetOrCreateAttributeData<PooledArrayAttribute<uint>>(AttributeKeys.Mark);
            attribute.EnsureCountAndClear(buffers.codepoints.count);
            uniText.MeshGenerator.OnBeforeMesh += OnBeforeMesh;
            uniText.MeshGenerator.OnAfterGlyphsPerFont += OnAfterGlyphs;
        }

        protected override void OnDisable()
        {
            uniText.MeshGenerator.OnBeforeMesh -= OnBeforeMesh;
            uniText.MeshGenerator.OnAfterGlyphsPerFont -= OnAfterGlyphs;
        }

        protected override void OnDestroy()
        {
            buffers?.ReleaseAttributeData(AttributeKeys.Mark);
            attribute = null;
        }

        protected override void OnApply(int start, int end, string parameter)
        {
            var color = DefaultColor;
            if (!string.IsNullOrEmpty(parameter) && !TryParseHex(parameter.Trim(), out color)) color = DefaultColor;
            var packed = ((uint)Math.Max((byte)1, color.a) << 24) | ((uint)color.r << 16) | ((uint)color.g << 8) | color.b;
            var data = attribute.buffer.data;
            var e = Math.Min(end, Math.Min(buffers.codepoints.count, data.Length));
            for (var i = start; i < e; i++) data[i] = packed;
            buffers.virtualCodepoints.Add('_');
        }

        internal static bool TryParseHex(string s, out Color32 c)
        {
            c = DefaultColor;
            if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
            int H(char ch) => ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1;
            byte B(int i) => (byte)(H(s[i]) * 16 + H(s[i + 1]));
            for (var i = 1; i < s.Length; i++) if (H(s[i]) < 0) return false;
            switch (s.Length - 1)
            {
                case 3: c = new Color32((byte)(H(s[1]) * 17), (byte)(H(s[2]) * 17), (byte)(H(s[3]) * 17), 255); return true;
                case 4: c = new Color32((byte)(H(s[1]) * 17), (byte)(H(s[2]) * 17), (byte)(H(s[3]) * 17), (byte)(H(s[4]) * 17)); return true;
                case 6: c = new Color32(B(1), B(3), B(5), 255); return true;
                case 8: c = new Color32(B(1), B(3), B(5), B(7)); return true;
                default: return false;
            }
        }

        private void OnBeforeMesh()
        {
            segmentTriStart = UniTextMeshGenerator.Current?.triangleCount ?? 0;
        }

        private void OnAfterGlyphs()
        {
            var gen = UniTextMeshGenerator.Current;
            if (gen == null || gen.font == null || attribute == null) return;
            var fp = uniText.FontProvider;
            if (fp == null) return;
            // Draw once, in the segment of the font that holds the underscore used for solid fill.
            var underscoreFont = fp.GetFontAsset(fp.FindFontForCodepoint('_'));
            if (underscoreFont != gen.font) return;

            var colors = attribute.buffer.data;
            if (colors == null) return;
            var glyphs = buffers.positionedGlyphs.data;
            var count = buffers.positionedGlyphs.count;
            if (count == 0) return;

            fp.GetLineMetrics(gen.FontSize, out var asc, out var desc, out _);
            var offX = gen.offsetX;
            var offY = gen.offsetY;
            var alpha = gen.defaultColor.a;
            var trisBefore = gen.triangleCount;

            var active = false;
            uint activeColor = 0;
            float x0 = 0, x1 = 0, baseY = 0;
            for (var i = 0; i <= count; i++)
            {
                uint packed = 0;
                float left = 0, right = 0, by = 0;
                if (i < count)
                {
                    ref readonly var g = ref glyphs[i];
                    if ((uint)g.cluster < (uint)colors.Length && !gen.IsClusterHidden(g.cluster))
                        packed = colors[g.cluster];
                    left = offX + g.left;
                    right = offX + g.right;
                    by = offY - g.y;
                }

                var continues = active && packed == activeColor && Mathf.Abs(by - baseY) < 0.5f;
                if (active && !continues)
                {
                    Draw(fp, x0, x1, baseY, asc, desc, activeColor, alpha);
                    active = false;
                }
                if (packed == 0) continue;
                if (!active)
                {
                    active = true; activeColor = packed; x0 = left; x1 = right; baseY = by;
                }
                else
                {
                    if (left < x0) x0 = left;
                    if (right > x1) x1 = right;
                }
            }

            // Move the highlight triangles in front of this segment's glyph triangles so the box is
            // drawn BEHIND the text (triangle order is draw order within one mesh).
            var added = gen.triangleCount - trisBefore;
            if (added <= 0) return;
            var tris = gen.Triangles;
            var start = segmentTriStart;
            var before = trisBefore - start;
            if (before <= 0) return;
            if (rotateScratch == null || rotateScratch.Length < added) rotateScratch = new int[Math.Max(added, 64)];
            Array.Copy(tris, trisBefore, rotateScratch, 0, added);
            Array.Copy(tris, start, tris, start + added, before);
            Array.Copy(rotateScratch, 0, tris, start, added);
        }

        private static void Draw(UniTextFontProvider fp, float x0, float x1, float baseline, float asc, float desc,
            uint packed, byte componentAlpha)
        {
            var c = new Color32((byte)((packed >> 16) & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)(packed & 0xFF),
                (byte)((packed >> 24) & 0xFF));
            if (componentAlpha < c.a) c.a = componentAlpha; // TMP: min(html alpha, highlight alpha)
            LineRenderHelper.DrawSolidQuad(fp, x0, baseline + desc, x1, baseline + asc, c);
        }
    }

    /// <summary>
    /// Name → font lookup for <c>&lt;font="Name"&gt;</c>. Order: fonts registered with
    /// <see cref="Register(string, UniTextFont)"/>, then fonts (and fallback stacks) of the component's
    /// <see cref="UniText.FontStack"/> by asset name, then the project default stack. Case-insensitive.
    /// </summary>
    public static class UniTextFontRegistry
    {
        private static readonly Dictionary<string, UniTextFont> registered = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<UniTextFontStack, Dictionary<string, UniTextFont>> stackNames = new();
        private static readonly object gate = new();

        /// <summary>Registers <paramref name="font"/> under <paramref name="name"/> (replaces an existing entry).</summary>
        public static void Register(string name, UniTextFont font)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                if (font == null) registered.Remove(name);
                else registered[name] = font;
            }
        }

        /// <summary>Registers a stack's main font under the stack's asset name (main thread).</summary>
        public static void Register(UniTextFontStack stack)
        {
            if (stack == null) return;
            Register(stack.name, stack.MainFont);
        }

        /// <summary>Removes a registration.</summary>
        public static void Unregister(string name) => Register(name, null);

        /// <summary>Removes every explicit registration.</summary>
        public static void Clear()
        {
            lock (gate) { registered.Clear(); stackNames.Clear(); }
        }

        /// <summary>MAIN THREAD: snapshots the asset names of the component's stack and the default stack
        /// so worker-thread lookups never read <c>Object.name</c>.</summary>
        internal static void PrepareNames(UniText owner)
        {
            if (!UniTextThreadGuard.IsMainThread) return;
            Snapshot(owner != null ? owner.FontStack : null);
            defaultStack = UniTextSettings.DefaultFontStack;
            Snapshot(defaultStack);
        }

        private static UniTextFontStack defaultStack;

        /// <summary>Drops the cached stack name snapshots (call after renaming fonts or editing stacks).</summary>
        public static void InvalidateNames()
        {
            lock (gate) { stackNames.Clear(); }
        }

        private static void Snapshot(UniTextFontStack stack)
        {
            if (stack == null) return;
            lock (gate) { if (stackNames.ContainsKey(stack)) return; }
            var visited = new HashSet<UniTextFontStack>();
            var root = stack;
            var names = new Dictionary<string, UniTextFont>(StringComparer.OrdinalIgnoreCase);
            while (stack != null && visited.Add(stack))
            {
                for (var i = 0; i < stack.fonts.Count; i++)
                {
                    var f = stack.fonts[i];
                    if (f != null && !names.ContainsKey(f.name)) names[f.name] = f;
                }
                if (stack.family != null)
                    for (var i = 0; i < stack.family.faces.Count; i++)
                    {
                        var f = stack.family.faces[i].font;
                        if (f != null && !names.ContainsKey(f.name)) names[f.name] = f;
                    }
                stack = stack.fallbackStack;
            }
            if (root == null) return;
            lock (gate) { stackNames[root] = names; }
        }

        /// <summary>Resolves <paramref name="name"/> for <paramref name="owner"/> (see the class remarks).</summary>
        public static bool TryResolve(string name, UniText owner, out UniTextFont font)
        {
            font = null;
            if (string.IsNullOrEmpty(name)) return false;
            if (UniTextThreadGuard.IsMainThread) PrepareNames(owner);
            lock (gate)
            {
                if (registered.TryGetValue(name, out font) && font != null) return true;
                var own = owner != null ? owner.FontStack : null;
                if (own != null && stackNames.TryGetValue(own, out var n1) && n1.TryGetValue(name, out font)) return true;
                var def = defaultStack;
                if (def != null && stackNames.TryGetValue(def, out var n2) && n2.TryGetValue(name, out font)) return true;
            }
            font = null;
            return false;
        }
    }
}
