using System;

namespace LightSide.Msdf
{
    /// <summary>
    /// Access to the phase-0 native outline export <c>ut_ft_get_outline_data</c> for the MSDF
    /// worker. This type holds NO P/Invoke of its own: the single canonical declaration lives in
    /// <see cref="FT"/> (FT.Outline.cs, added by the native worker's branch) and this class is a
    /// thin, MSDF-namespace-local facade over <see cref="FT.TryGetOutlineData"/> plus availability
    /// latching. Keeping one declaration avoids two P/Invokes of the same entry point drifting
    /// apart — which is exactly how the pre-consolidation copy shipped with an INVERTED return
    /// (it reported success as failure), silently forcing the SDF fallback even where the export
    /// worked.
    /// </summary>
    internal static class MsdfNative
    {
        private static bool _probed;
        private static bool _available;

        /// <summary>
        /// FreeType's FT_OUTLINE_REVERSE_FILL flag (bit 2). Set for glyphs whose fill orientation
        /// is the PostScript convention. Kept here so callers need not depend on FT.cs internals.
        /// </summary>
        public const int FT_OUTLINE_REVERSE_FILL = 0x4;

        /// <summary>
        /// True if the native export is present. Optimistic (true) until a call or <see cref="Probe"/>
        /// latches a definitive answer, so a first real attempt is always made rather than pre-empted.
        /// </summary>
        public static bool IsExportAvailable => _probed ? _available : true;

        /// <summary>True once <see cref="Probe"/> or a real call has determined availability.</summary>
        public static bool HasProbed => _probed;

        /// <summary>
        /// Definitively determines whether the export exists (without needing a loaded glyph slot)
        /// and latches the result. Delegates to <see cref="FT.OutlineExportAvailable"/> so the
        /// entry-point resolution lives with the single P/Invoke declaration. Call this BEFORE
        /// choosing an atlas format so a missing export never yields a half-written RGB atlas.
        /// </summary>
        public static bool Probe(IntPtr face)
        {
            if (_probed) return _available;
            if (face == IntPtr.Zero) return false; // don't latch on a null face
            _available = FT.OutlineExportAvailable(face);
            _probed = true;
            return _available;
        }

        /// <summary>
        /// Reads the current glyph slot's outline for <paramref name="face"/> into the supplied
        /// buffers. The glyph must already be loaded by the caller. Returns true on SUCCESS
        /// (matching the native contract: <c>ut_ft_get_outline_data</c> returns 1 on success),
        /// false when the export is missing, the glyph has no outline, or the buffers are too small
        /// (in which case <paramref name="numPoints"/>/<paramref name="numContours"/> are still
        /// filled so the caller can resize and retry).
        /// </summary>
        public static bool TryGetOutlineData(
            IntPtr face, int[] xy, byte[] tags, short[] contourEnds,
            out int numPoints, out int numContours, out int flags)
        {
            bool ok = FT.TryGetOutlineData(face, xy, tags, contourEnds,
                out numPoints, out numContours, out flags);

            // A completed call (success OR a plain false with counts filled) proves the entry point
            // resolved. Only FT.TryGetOutlineData swallowing EntryPointNotFound would leave the
            // export genuinely unavailable — detect that separately via a probe on first failure.
            if (ok)
            {
                _available = true;
                _probed = true;
            }
            else if (!_probed)
            {
                _available = FT.OutlineExportAvailable(face);
                _probed = true;
            }
            return ok;
        }
    }
}
