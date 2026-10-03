using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Time-driven per-glyph span animation: <c>&lt;wave&gt;</c>, <c>&lt;shake&gt;</c>, <c>&lt;pulse&gt;</c>,
    /// <c>&lt;fade&gt;</c>, <c>&lt;rainbow&gt;</c>, <c>&lt;bounce&gt;</c>. One modifier instance per tag
    /// (<see cref="AnimationKind"/>); pair it with the matching <see cref="KeyValueTagParseRule"/>.
    /// </summary>
    /// <remarks>
    /// <para>Parameters (all optional; key=value or positional in the order listed):</para>
    /// <list type="table">
    /// <item><term>wave</term><description><c>amp</c> px (default 0.12 em), <c>freq</c> Hz (1.2), <c>phase</c> rad per character (0.6)</description></item>
    /// <item><term>bounce</term><description><c>amp</c> px (0.2 em), <c>freq</c> hops/s (1.4), <c>phase</c> (0.45)</description></item>
    /// <item><term>shake</term><description><c>amp</c> px (0.05 em), <c>freq</c> jitters/s (18)</description></item>
    /// <item><term>pulse</term><description><c>amp</c> scale fraction (0.15), <c>freq</c> Hz (1.2), <c>phase</c> (0.4)</description></item>
    /// <item><term>fade</term><description><c>min</c> opacity (0.2), <c>freq</c> Hz (0.8), <c>phase</c> (0.4)</description></item>
    /// <item><term>rainbow</term><description><c>freq</c> hue cycles/s (0.4), <c>phase</c> hue per character (0.06), <c>sat</c> (0.75)</description></item>
    /// </list>
    /// <para>The phase offset is per codepoint from the start of the span (in logical order), so a base
    /// letter and its combining marks share it and move together. Animation runs as a vertex effect on
    /// the captured mesh (<see cref="IUniTextVertexEffect"/>): no re-shaping, re-layout or mesh
    /// generation per frame, no per-frame allocation, both renderers. Decorations (underline,
    /// strikethrough, mark) are not animated.</para>
    /// </remarks>
    [Serializable]
    [TypeGroup("Animation", 0)]
    public sealed class TextAnimationModifier : BaseModifier, IUniTextVertexEffect
    {
        /// <summary>The animation a modifier instance applies.</summary>
        public enum Kind { Wave, Shake, Pulse, Fade, Rainbow, Bounce }

        [SerializeField] private Kind kind = Kind.Wave;

        /// <summary>The animation this instance applies to its spans.</summary>
        public Kind AnimationKind { get => kind; set => kind = value; }

        public TextAnimationModifier() { }
        public TextAnimationModifier(Kind kind) { this.kind = kind; }

        private struct Def
        {
            public int start, end;
            public float a, freq, phase, extra;
            public bool aInEm;
        }

        [NonSerialized] private readonly List<Def> defs = new();
        [NonSerialized] private ushort[] index = Array.Empty<ushort>();

        /// <summary>Number of animated spans parsed for the current text.</summary>
        public int SpanCount => defs.Count;

        protected override void OnEnable()
        {
            defs.Clear();
            var n = buffers.codepoints.count;
            if (index.Length < n) index = new ushort[Mathf.NextPowerOfTwo(Mathf.Max(n, 16))];
            else Array.Clear(index, 0, index.Length);
            uniText.AddVertexEffectInternal(this);
        }

        protected override void OnDisable()
        {
            uniText?.RemoveVertexEffectInternal(this);
        }

        protected override void OnDestroy()
        {
            index = Array.Empty<ushort>();
            defs.Clear();
        }

        protected override void OnApply(int start, int end, string parameter)
        {
            if (end <= start || defs.Count >= ushort.MaxValue) return;
            var d = new Def { start = start, end = end };
            switch (kind)
            {
                case Kind.Wave: Read(parameter, ref d, 0.12f, true, 1.2f, 0.6f); break;
                case Kind.Bounce: Read(parameter, ref d, 0.2f, true, 1.4f, 0.45f); break;
                case Kind.Shake: Read(parameter, ref d, 0.05f, true, 18f, 0f); break;
                case Kind.Pulse: Read(parameter, ref d, 0.15f, false, 1.2f, 0.4f); break;
                case Kind.Fade:
                    d.a = Get(parameter, "min", 0, 0.2f);
                    d.freq = Get(parameter, "freq", 1, 0.8f);
                    d.phase = Get(parameter, "phase", 2, 0.4f);
                    break;
                case Kind.Rainbow:
                    d.freq = Get(parameter, "freq", 0, 0.4f);
                    d.phase = Get(parameter, "phase", 1, 0.06f);
                    d.extra = Mathf.Clamp01(Get(parameter, "sat", 2, 0.75f));
                    break;
            }
            defs.Add(d);
            var id = (ushort)defs.Count;
            var e = Math.Min(end, index.Length);
            for (var i = Math.Max(0, start); i < e; i++) index[i] = id;
        }

        private static void Read(string p, ref Def d, float defAmpEm, bool ampIsLength, float defFreq, float defPhase)
        {
            if (KeyValueTagParseRule.TryGetValue(p, "amp", -1, out var a) || KeyValueTagParseRule.TryGetValue(p, null, 0, out a))
            {
                d.a = a;
                d.aInEm = false;
            }
            else
            {
                d.a = defAmpEm;
                d.aInEm = ampIsLength;
            }
            d.freq = Get(p, "freq", 1, defFreq);
            d.phase = Get(p, "phase", 2, defPhase);
        }

        private static float Get(string p, string key, int pos, float def)
        {
            if (KeyValueTagParseRule.TryGetValue(p, key, -1, out var v)) return v;
            if (key == "freq" && KeyValueTagParseRule.TryGetValue(p, "speed", -1, out v)) return v;
            return KeyValueTagParseRule.TryGetValue(p, null, pos, out v) ? v : def;
        }

        // ---------------------------------------------------------------- IUniTextVertexEffect

        public int Order => 0;

        public void OnCaptured(UniTextVertexEffectContext context) { }

        public bool Prepare(UniTextVertexEffectContext context) => defs.Count > 0 && context.DeltaTime > 0f;

        public void Apply(UniTextVertexEffectContext context)
        {
            if (defs.Count == 0) return;
            var t = context.Time;
            var em = uniText != null ? uniText.CurrentFontSize : 36f;
            var idx = index;
            const float Tau = Mathf.PI * 2f;
            var count = context.GlyphCount;
            for (var g = 0; g < count; g++)
            {
                var c = context.Glyph(g).cluster;
                if ((uint)c >= (uint)idx.Length) continue;
                var id = idx[c];
                if (id == 0) continue;
                var d = defs[id - 1];
                var local = c - d.start;
                var amp = d.aInEm ? d.a * em : d.a;
                switch (kind)
                {
                    case Kind.Wave:
                        context.Offset(g, new Vector2(0f, amp * Mathf.Sin(Tau * d.freq * t - d.phase * local)));
                        break;
                    case Kind.Bounce:
                        context.Offset(g, new Vector2(0f, amp * Mathf.Abs(Mathf.Sin(Mathf.PI * d.freq * t - d.phase * local))));
                        break;
                    case Kind.Shake:
                    {
                        var step = Mathf.FloorToInt(t * d.freq);
                        var hx = Hash(local, step, 0x9E37);
                        var hy = Hash(local, step, 0x85EB);
                        context.Offset(g, new Vector2(amp * (hx * 2f - 1f), amp * (hy * 2f - 1f)));
                        break;
                    }
                    case Kind.Pulse:
                        context.Scale(g, 1f + amp * (0.5f + 0.5f * Mathf.Sin(Tau * d.freq * t - d.phase * local)));
                        break;
                    case Kind.Fade:
                    {
                        var w = 0.5f + 0.5f * Mathf.Cos(Tau * d.freq * t - d.phase * local);
                        context.MultiplyAlpha(g, Mathf.Lerp(Mathf.Clamp01(d.a), 1f, w));
                        break;
                    }
                    case Kind.Rainbow:
                    {
                        var h = d.phase * local - d.freq * t;
                        h -= Mathf.Floor(h);
                        context.SetRgb(g, (Color32)Color.HSVToRGB(h, d.extra, 1f));
                        break;
                    }
                }
            }
        }

        // Deterministic [0,1) hash of (glyph, time step) for shake.
        private static float Hash(int a, int b, int salt)
        {
            unchecked
            {
                var h = (uint)(a * 374761393 + b * 668265263 + salt * 2246822519u);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        /// <summary>
        /// Registers all six animation tags (<c>wave shake pulse fade rainbow bounce</c>) on
        /// <paramref name="target"/>, each with its own modifier instance.
        /// </summary>
        public static void RegisterAll(UniText target)
        {
            if (target == null) return;
            target.RegisterModifier(new ModRegister { Rule = new WaveParseRule(), Modifier = new TextAnimationModifier(Kind.Wave) });
            target.RegisterModifier(new ModRegister { Rule = new ShakeParseRule(), Modifier = new TextAnimationModifier(Kind.Shake) });
            target.RegisterModifier(new ModRegister { Rule = new PulseParseRule(), Modifier = new TextAnimationModifier(Kind.Pulse) });
            target.RegisterModifier(new ModRegister { Rule = new FadeParseRule(), Modifier = new TextAnimationModifier(Kind.Fade) });
            target.RegisterModifier(new ModRegister { Rule = new RainbowParseRule(), Modifier = new TextAnimationModifier(Kind.Rainbow) });
            target.RegisterModifier(new ModRegister { Rule = new BounceParseRule(), Modifier = new TextAnimationModifier(Kind.Bounce) });
        }
    }
}
