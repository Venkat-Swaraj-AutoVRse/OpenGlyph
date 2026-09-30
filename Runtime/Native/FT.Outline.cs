using System;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.CallingConvention;

namespace LightSide
{
    /// <summary>
    /// ADDITIVE (Phase 0) raw glyph-outline access, required by the MSDF worker.
    /// Partial of <see cref="FT"/> so it reads as FT.TryGetOutlineData(...) without
    /// restructuring FT.cs (FT.cs only gained the `partial` keyword).
    ///
    /// New native export (implemented in NativeSource~/src/ut_ft.c):
    ///   int ut_ft_get_outline_data(FT_Face face,
    ///       int32_t* xy,          // 2*numPoints, 26.6 fixed, current glyph slot outline
    ///       uint8_t* tags,        // numPoints, FT_CURVE_TAG bits
    ///       int16_t* contourEnds, // numContours (last-point index per contour)
    ///       int pointCapacity, int contourCapacity,
    ///       int* outNumPoints, int* outNumContours, int* outFlags /* FT_Outline.flags */);
    ///   Returns 1 on success, 0 if no outline OR capacity too small — out counts are
    ///   STILL filled in the capacity-too-small case so the caller can resize and retry.
    ///
    /// Call order: ut_ft_load_glyph(face, gid, LOAD_NO_SCALE|LOAD_NO_BITMAP) first;
    /// size the buffers with ut_ft_get_outline_info(face, out contours, out points).
    /// </summary>
    internal static unsafe partial class FT
    {
        [DllImport(LibraryName, CallingConvention = Cdecl, EntryPoint = "ut_ft_get_outline_data")]
        private static extern int ut_ft_get_outline_data(
            IntPtr face,
            int* xy, byte* tags, short* contourEnds,
            int pointCapacity, int contourCapacity,
            out int outNumPoints, out int outNumContours, out int outFlags);

        /// <summary>
        /// Copies the currently loaded glyph slot's outline into caller-provided buffers.
        /// Degrades gracefully on binaries that predate this export: catches
        /// <see cref="EntryPointNotFoundException"/> and returns false.
        /// </summary>
        /// <param name="face">Face whose glyph slot outline is read (load a glyph first).</param>
        /// <param name="xy">Buffer of length >= 2*numPoints; filled with 26.6 fixed x,y pairs.</param>
        /// <param name="tags">Buffer of length >= numPoints; filled with FT_CURVE_TAG bytes.</param>
        /// <param name="contourEnds">Buffer of length >= numContours; last-point index per contour.</param>
        /// <param name="numPoints">Out: actual point count (filled even if capacity too small).</param>
        /// <param name="numContours">Out: actual contour count (filled even if capacity too small).</param>
        /// <param name="flags">Out: FT_Outline.flags.</param>
        /// <returns>True on success; false if no outline, capacity too small, or export missing.</returns>
        public static bool TryGetOutlineData(IntPtr face, int[] xy, byte[] tags, short[] contourEnds,
            out int numPoints, out int numContours, out int flags)
        {
            numPoints = numContours = flags = 0;
            if (!initialized || face == IntPtr.Zero) return false;

            int pointCap = xy != null ? xy.Length / 2 : 0;
            int contourCap = contourEnds != null ? contourEnds.Length : 0;

            try
            {
                fixed (int* pxy = xy)
                fixed (byte* ptags = tags)
                fixed (short* pends = contourEnds)
                {
                    return ut_ft_get_outline_data(face, pxy, ptags, pends,
                        pointCap, contourCap, out numPoints, out numContours, out flags) != 0;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Old binary without this export — degrade rather than crash.
                return false;
            }
        }

        /// <summary>
        /// Determines whether the native <c>ut_ft_get_outline_data</c> export is resolvable in the
        /// loaded binary, WITHOUT requiring a loaded glyph slot. Invokes the export once with
        /// deliberately tiny buffers on <paramref name="face"/>: a missing entry point throws
        /// <see cref="EntryPointNotFoundException"/> before touching the slot (returns false); any
        /// other outcome (success, capacity-too-small, or an in-native fault on the tiny buffers)
        /// means the entry point resolved (returns true). Latch the result in the caller — this is
        /// the single point of truth for "does the phase-0 export exist", so callers never redeclare
        /// the P/Invoke themselves.
        /// </summary>
        public static bool OutlineExportAvailable(IntPtr face)
        {
            if (!initialized || face == IntPtr.Zero) return false;

            var xy = new int[2];
            var tags = new byte[1];
            var ends = new short[1];
            try
            {
                fixed (int* pxy = xy)
                fixed (byte* ptags = tags)
                fixed (short* pends = ends)
                {
                    // Return code and out-counts are irrelevant here; the only thing learned is
                    // whether the entry point resolves.
                    ut_ft_get_outline_data(face, pxy, ptags, pends, 1, 1, out _, out _, out _);
                }
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false; // old binary predating this export
            }
            catch (DllNotFoundException)
            {
                return false; // native library missing entirely
            }
            catch
            {
                // Entry point resolved and faulted on the tiny buffers — the export exists.
                return true;
            }
        }
    }
}
