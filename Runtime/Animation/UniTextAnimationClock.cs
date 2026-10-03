using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// The clock every text vertex effect (span animations, <see cref="UniTextReveal"/>) reads.
    /// </summary>
    /// <remarks>
    /// <para>By default it follows <see cref="Time.time"/> (scaled) or <see cref="Time.unscaledTime"/>,
    /// picked per component with <see cref="UniText.AnimationUnscaledTime"/>; outside Play Mode it
    /// follows <see cref="Time.realtimeSinceStartup"/> so effects preview in the editor.</para>
    /// <para>Set <see cref="UseManualTime"/> and drive <see cref="ManualTime"/> to step every effect
    /// deterministically (tests, offline capture, a custom sequencer). Components integrate the clock
    /// per tick (<c>time += delta × AnimationTimeScale</c>), so changing the time scale never jumps.</para>
    /// </remarks>
    public static class UniTextAnimationClock
    {
        /// <summary>When true, every component reads <see cref="ManualTime"/> instead of Unity's time.</summary>
        public static bool UseManualTime { get; set; }

        /// <summary>The manual clock in seconds (used for both scaled and unscaled time while
        /// <see cref="UseManualTime"/> is on).</summary>
        public static float ManualTime { get; set; }

        /// <summary>The current clock value in seconds.</summary>
        /// <param name="unscaled">Read unscaled time (ignores <see cref="Time.timeScale"/>).</param>
        public static float Now(bool unscaled)
        {
            if (UseManualTime) return ManualTime;
            if (!Application.isPlaying) return Time.realtimeSinceStartup;
            return unscaled ? Time.unscaledTime : Time.time;
        }
    }
}
