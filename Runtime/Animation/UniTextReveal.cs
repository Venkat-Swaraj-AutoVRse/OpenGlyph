using System;
using UnityEngine;
using UnityEngine.Events;

namespace LightSide
{
    /// <summary>What one reveal step shows.</summary>
    public enum RevealUnit
    {
        /// <summary>One grapheme cluster (user-perceived character, incl. spaces) per step.</summary>
        Character,
        /// <summary>One word (a run of non-whitespace) per step; whitespace appears with the word before it.</summary>
        Word,
        /// <summary>One laid-out line per step.</summary>
        Line,
    }

    /// <summary>Easing applied to each unit's fade / slide.</summary>
    public enum RevealEasing { Linear, EaseIn, EaseOut, EaseInOut, Smooth }

    /// <summary>
    /// Typewriter / progressive reveal for a <see cref="UniText"/> (incl. <c>GlyphMeshProUGUI</c>), by
    /// grapheme cluster, word or line, with speed, delay, per-unit fade and slide, easing, events and
    /// <c>&lt;pause=seconds&gt;</c> markers.
    /// </summary>
    /// <remarks>
    /// <para>Runs as a vertex effect (<see cref="IUniTextVertexEffect"/>): it changes vertex alpha and
    /// position of the existing mesh, so revealing never re-shapes or re-lays out the text and allocates
    /// nothing per frame. Units are taken in LOGICAL order (codepoint order), so right-to-left and mixed
    /// bidirectional text reveals in reading order. Works with the unified and the legacy renderer.</para>
    /// <para>It composes with <c>GlyphMeshProUGUI.maxVisibleCharacters</c>/<c>Words</c>/<c>Lines</c>: those
    /// remove glyphs from the mesh; the reveal only fades what is left.</para>
    /// <para>Decorations (underline, strikethrough, mark) appear with the last unit. Use
    /// <see cref="RevealPauseModifier"/> + <see cref="PauseParseRule"/> (or <see cref="RegisterPauseTag"/>)
    /// for <c>&lt;pause&gt;</c>.</para>
    /// </remarks>
    [RequireComponent(typeof(UniText))]
    [AddComponentMenu("UI/OpenGlyph/UniText Reveal")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class UniTextReveal : MonoBehaviour, IUniTextVertexEffect
    {
        /// <summary>UnityEvent with the revealed unit index.</summary>
        [Serializable] public sealed class UnitEvent : UnityEvent<int> { }

        [SerializeField, Tooltip("What one reveal step shows.")]
        private RevealUnit unit = RevealUnit.Character;
        [SerializeField, Min(0.001f), Tooltip("Units revealed per second.")]
        private float unitsPerSecond = 30f;
        [SerializeField, Min(0f), Tooltip("Seconds before the first unit.")]
        private float delay;
        [SerializeField, Min(0f), Tooltip("Seconds each unit takes to fade in (0 = appears at once).")]
        private float fadeDuration = 0.08f;
        [SerializeField, Tooltip("Offset (mesh-local units) a unit slides in from, e.g. (0, -8).")]
        private Vector2 slideOffset;
        [SerializeField, Tooltip("Easing of the per-unit fade and slide.")]
        private RevealEasing easing = RevealEasing.EaseOut;
        [SerializeField, Tooltip("Start revealing when the component is enabled (in Play Mode).")]
        private bool playOnEnable = true;
        [SerializeField, Tooltip("Restart from the beginning when the text content changes.")]
        private bool restartOnTextChange = true;
        [SerializeField, Tooltip("Also play in Edit Mode when enabled (otherwise Edit Mode shows the full text until Play/Restart is called).")]
        private bool previewInEditor;

        [SerializeField] private UnityEvent onRevealStarted = new();
        [SerializeField] private UnitEvent onUnitRevealed = new();
        [SerializeField] private UnityEvent onRevealCompleted = new();

        /// <summary>Raised when the first unit is about to appear (after <see cref="Delay"/>).</summary>
        public event Action RevealStarted;
        /// <summary>Raised when unit <c>index</c> starts appearing (logical order).</summary>
        public event Action<int> UnitRevealed;
        /// <summary>Raised when every unit is fully visible.</summary>
        public event Action RevealCompleted;

        public UnityEvent OnRevealStarted => onRevealStarted;
        public UnitEvent OnUnitRevealed => onUnitRevealed;
        public UnityEvent OnRevealCompleted => onRevealCompleted;

        public RevealUnit Unit { get => unit; set { if (unit == value) return; unit = value; Invalidate(); } }
        public float UnitsPerSecond { get => unitsPerSecond; set { unitsPerSecond = Mathf.Max(0.001f, value); Invalidate(); } }
        public float Delay { get => delay; set { delay = Mathf.Max(0f, value); Invalidate(); } }
        public float FadeDuration { get => fadeDuration; set { fadeDuration = Mathf.Max(0f, value); Invalidate(); } }
        public Vector2 SlideOffset { get => slideOffset; set { slideOffset = value; stateDirty = true; } }
        public RevealEasing Easing { get => easing; set { easing = value; stateDirty = true; } }
        public bool PlayOnEnable { get => playOnEnable; set => playOnEnable = value; }
        public bool RestartOnTextChange { get => restartOnTextChange; set => restartOnTextChange = value; }
        public bool PreviewInEditor { get => previewInEditor; set => previewInEditor = value; }

        /// <summary>True while the reveal advances with time.</summary>
        public bool IsPlaying => playing;
        /// <summary>True once every unit is fully visible.</summary>
        public bool IsComplete => hasLayout && time >= completionTime;
        /// <summary>Playback position in seconds (0 = start, including <see cref="Delay"/>).</summary>
        public float PlaybackTime => time;
        /// <summary>Number of units in the current text.</summary>
        public int UnitCount => unitCount;
        /// <summary>Number of units that have started appearing.</summary>
        public int RevealedUnitCount => revealedUnits;
        /// <summary>Seconds from the start until the text is fully visible.</summary>
        public float Duration => completionTime;

        private UniText target;
        private bool registered;
        private bool playing;
        private bool stateDirty = true;
        private float time;
        private bool startedFired, completedFired;
        private int revealedUnits;
        private bool skipRequested;

        // Layout of the current capture.
        private bool hasLayout;
        private int unitCount;
        private float completionTime;
        private float[] unitStart = Array.Empty<float>();
        private int[] cpUnit = Array.Empty<int>();
        private int[] glyphUnit = Array.Empty<int>();
        private int textHash;
        private bool hasTextHash;
        private bool layoutInvalid;

        public int Order => 1000;

        private void OnEnable()
        {
            target = GetComponent<UniText>();
            if (target == null) return;
            if (playOnEnable && (Application.isPlaying || previewInEditor)) Restart();
            else ShowAllState();
            target.AddVertexEffect(this);
            registered = true;
        }

        private void OnDisable()
        {
            if (registered && target != null) target.RemoveVertexEffect(this);
            registered = false;
        }

        /// <summary>Plays from the beginning.</summary>
        public void Restart()
        {
            time = 0f;
            playing = true;
            startedFired = completedFired = false;
            revealedUnits = 0;
            skipRequested = false;
            stateDirty = true;
        }

        /// <summary>Resumes (or starts) playback from the current position.</summary>
        public void Play()
        {
            if (IsComplete && completedFired) return;
            playing = true;
            stateDirty = true;
        }

        /// <summary>Stops advancing; the text stays as it is.</summary>
        public void Pause()
        {
            playing = false;
        }

        /// <summary>Jumps to the end: everything visible. Raises <see cref="RevealCompleted"/> (and
        /// <see cref="RevealStarted"/> if it had not fired) but no per-unit events.</summary>
        public void Skip()
        {
            skipRequested = true;
            playing = true;
            stateDirty = true;
            if (hasLayout) DoSkip();
        }

        /// <summary>Moves the playback position without raising events (scrubbing / tools).</summary>
        public void SetPlaybackTime(float seconds)
        {
            time = Mathf.Max(0f, seconds);
            revealedUnits = CountStarted(time);
            startedFired = time >= delay;
            completedFired = hasLayout && time >= completionTime;
            stateDirty = true;
        }

        /// <summary>Registers <c>&lt;pause=seconds&gt;</c> on <paramref name="uniText"/>.</summary>
        public static void RegisterPauseTag(UniText uniText)
        {
            uniText?.RegisterModifier(new ModRegister { Rule = new PauseParseRule(), Modifier = new RevealPauseModifier() });
        }

        private void ShowAllState()
        {
            playing = false;
            time = float.MaxValue / 4f;
            startedFired = completedFired = true;
            revealedUnits = unitCount;
            stateDirty = true;
        }

        private void Invalidate()
        {
            layoutInvalid = true;
            stateDirty = true;
        }

        private void DoSkip()
        {
            skipRequested = false;
            time = completionTime;
            revealedUnits = unitCount;
            if (!startedFired) { startedFired = true; RaiseStarted(); }
            if (!completedFired) { completedFired = true; RaiseCompleted(); }
            playing = false;
        }

        // ---------------------------------------------------------------- IUniTextVertexEffect

        public void OnCaptured(UniTextVertexEffectContext context)
        {
            var cps = context.Codepoints;
            var hash = 17;
            unchecked { for (var i = 0; i < cps.Length; i++) hash = hash * 31 + cps[i]; hash = hash * 31 + cps.Length; }
            var changedText = hasTextHash && hash != textHash;
            textHash = hash;
            hasTextHash = true;

            BuildLayout(context);
            if (changedText && restartOnTextChange) Restart();
            else if (time < float.MaxValue / 8f) revealedUnits = Mathf.Min(revealedUnits, unitCount);
            else revealedUnits = unitCount;
            stateDirty = true;
        }

        private void BuildLayout(UniTextVertexEffectContext context)
        {
            layoutInvalid = false;
            var cps = context.Codepoints;
            var n = cps.Length;
            if (cpUnit.Length < n) cpUnit = new int[Mathf.NextPowerOfTwo(Mathf.Max(n, 16))];
            var buf = target != null ? target.Buffers : null;

            var units = 0;
            switch (unit)
            {
                case RevealUnit.Character:
                {
                    var breaks = buf != null && buf.graphemeBreaks.count >= n + 1 ? buf.graphemeBreaks.data : null;
                    var u = -1;
                    for (var i = 0; i < n; i++)
                    {
                        if (breaks == null || i == 0 || breaks[i]) u++;
                        cpUnit[i] = u;
                    }
                    units = u + 1;
                    break;
                }
                case RevealUnit.Word:
                {
                    var u = -1;
                    var inWord = false;
                    for (var i = 0; i < n; i++)
                    {
                        var ws = IsSpace(cps[i]);
                        if (!ws && !inWord) u++;
                        inWord = !ws;
                        cpUnit[i] = u < 0 ? 0 : u;
                    }
                    units = u + 1;
                    break;
                }
                case RevealUnit.Line:
                {
                    var lineCount = buf != null ? buf.lines.count : 0;
                    for (var i = 0; i < n; i++) cpUnit[i] = Mathf.Max(0, lineCount - 1);
                    for (var l = 0; l < lineCount; l++)
                    {
                        var r = buf.lines.data[l].range;
                        var e = Math.Min(n, r.start + r.length);
                        for (var i = Math.Max(0, r.start); i < e; i++) cpUnit[i] = l;
                    }
                    units = n > 0 ? Mathf.Max(1, lineCount) : 0;
                    break;
                }
            }

            unitCount = units;
            if (unitStart.Length < units + 1) unitStart = new float[Mathf.NextPowerOfTwo(units + 1)];

            // Pauses: <pause> before codepoint i holds the unit containing i; one at the end holds completion.
            var pauses = buf?.GetAttributeData<PooledArrayAttribute<float>>(AttributeKeys.RevealPause)?.buffer.data;
            for (var k = 0; k <= units; k++) unitStart[k] = 0f;
            var endPause = 0f;
            if (pauses != null)
            {
                var pn = Math.Min(pauses.Length, n + 1);
                for (var i = 0; i < pn; i++)
                {
                    var p = pauses[i];
                    if (p <= 0f) continue;
                    if (i < n) unitStart[cpUnit[i]] += p; // accumulated below
                    else endPause += p;
                }
            }
            var step = 1f / Mathf.Max(0.001f, unitsPerSecond);
            var acc = 0f;
            for (var k = 0; k < units; k++)
            {
                acc += unitStart[k];
                unitStart[k] = delay + k * step + acc;
            }
            completionTime = (units > 0 ? unitStart[units - 1] + fadeDuration : delay) + endPause;

            var gc = context.GlyphCount;
            if (glyphUnit.Length < gc) glyphUnit = new int[Mathf.NextPowerOfTwo(Mathf.Max(gc, 16))];
            for (var g = 0; g < gc; g++)
            {
                var c = context.Glyph(g).cluster;
                glyphUnit[g] = (uint)c < (uint)n ? cpUnit[c] : -1;
            }
            hasLayout = true;
        }

        private static bool IsSpace(int cp) =>
            cp == ' ' || cp == '\t' || cp == '\n' || cp == '\r' || cp == 0x00A0 || cp == 0x3000 || cp == 0x2028 || cp == 0x2029 ||
            (cp >= 0x2000 && cp <= 0x200A);

        public bool Prepare(UniTextVertexEffectContext context)
        {
            if (layoutInvalid && hasLayout) { BuildLayout(context); stateDirty = true; }
            if (!hasLayout) return false;
            if (skipRequested) DoSkip();

            var changed = stateDirty;
            stateDirty = false;
            if (!playing) return changed;

            time += context.DeltaTime;
            if (!startedFired && time >= delay)
            {
                startedFired = true;
                RaiseStarted();
            }
            while (revealedUnits < unitCount && time >= unitStart[revealedUnits])
            {
                var u = revealedUnits++;
                UnitRevealed?.Invoke(u);
                onUnitRevealed?.Invoke(u);
            }
            if (!completedFired && time >= completionTime)
            {
                completedFired = true;
                playing = false;
                RaiseCompleted();
            }
            return true;
        }

        public void Apply(UniTextVertexEffectContext context)
        {
            if (!hasLayout || time >= completionTime) return;
            var gc = context.GlyphCount;
            var slide = slideOffset;
            var hasSlide = slide.x != 0f || slide.y != 0f;
            for (var g = 0; g < gc && g < glyphUnit.Length; g++)
            {
                var u = glyphUnit[g];
                if (u < 0) continue;
                var e = Visibility(u);
                if (e >= 1f) continue;
                context.MultiplyAlpha(g, e);
                if (hasSlide) context.Offset(g, slide * (1f - e));
            }
            if (context.DecorationCount > 0)
                context.MultiplyDecorationAlpha(unitCount > 0 ? Visibility(unitCount - 1) : 1f);
        }

        /// <summary>Eased visibility 0..1 of unit <paramref name="u"/> at the current playback time.</summary>
        public float Visibility(int u)
        {
            if ((uint)u >= (uint)unitCount) return 1f;
            var s = unitStart[u];
            float f;
            if (fadeDuration <= 0f) f = time >= s ? 1f : 0f;
            else f = Mathf.Clamp01((time - s) / fadeDuration);
            return Ease(f, easing);
        }

        /// <summary>Applies <paramref name="e"/> to a 0..1 value.</summary>
        public static float Ease(float t, RevealEasing e)
        {
            switch (e)
            {
                case RevealEasing.EaseIn: return t * t;
                case RevealEasing.EaseOut: return 1f - (1f - t) * (1f - t);
                case RevealEasing.EaseInOut: return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);
                case RevealEasing.Smooth: return t * t * (3f - 2f * t);
                default: return t;
            }
        }

        private int CountStarted(float t)
        {
            var c = 0;
            while (c < unitCount && t >= unitStart[c]) c++;
            return c;
        }

        private void RaiseStarted()
        {
            RevealStarted?.Invoke();
            onRevealStarted?.Invoke();
        }

        private void RaiseCompleted()
        {
            RevealCompleted?.Invoke();
            onRevealCompleted?.Invoke();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            unitsPerSecond = Mathf.Max(0.001f, unitsPerSecond);
            Invalidate();
        }
#endif
    }
}
