using System;
using System.Runtime.InteropServices;

namespace LightSide.Msdf
{
    /// <summary>
    /// Private P/Invoke to the phase-0 native outline export <c>ut_ft_get_outline_data</c>.
    /// Declared here (NOT in FT.cs) to avoid a merge conflict with the native worker's branch,
    /// which adds the canonical <c>FT.TryGetOutlineData</c> wrapper. When the loaded binary
    /// predates the export, the P/Invoke throws <see cref="EntryPointNotFoundException"/> on
    /// first call and this wrapper reports the source unavailable, so the Msdf render mode can
    /// fall back to SDF.
    /// </summary>
    internal static class MsdfNative
    {
#if (UNITY_IOS || UNITY_TVOS || UNITY_WEBGL) && !UNITY_EDITOR
        private const string LibraryName = "__Internal";
#else
        private const string LibraryName = "unitext_native";
#endif

        // Matches: int ut_ft_get_outline_data(FT_Face face, int32* xy(26.6), uint8* tags,
        //   int16* contourEnds, int pointCap, int contourCap,
        //   int* outNumPoints, int* outNumContours, int* outFlags)
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ut_ft_get_outline_data")]
        private static extern unsafe int ut_ft_get_outline_data(
            IntPtr face, int* xy, byte* tags, short* contourEnds,
            int pointCap, int contourCap,
            out int outNumPoints, out int outNumContours, out int outFlags);

        private static bool _probed;
        private static bool _available;

        /// <summary>True if the native export is present in the loaded binary.</summary>
        public static bool IsExportAvailable
        {
            get
            {
                if (_probed) return _available;
                return true; // Unknown until first real call; treat optimistically then latch.
            }
        }

        /// <summary>
        /// FreeType's FT_OUTLINE_REVERSE_FILL flag (bit 2). Set for glyphs whose fill orientation
        /// is the PostScript convention. Kept here so callers need not depend on FT.cs internals.
        /// </summary>
        public const int FT_OUTLINE_REVERSE_FILL = 0x4;

        /// <summary>
        /// Reads the current glyph slot's outline for <paramref name="face"/> into the supplied
        /// buffers. The glyph must already be loaded (FT_Load_Glyph with FT_LOAD_NO_SCALE clear /
        /// scaled to the target size) by the caller. Returns false when the export is missing or
        /// the buffers are too small.
        /// </summary>
        public static unsafe bool TryGetOutlineData(
            IntPtr face, int[] xy, byte[] tags, short[] contourEnds,
            out int numPoints, out int numContours, out int flags)
        {
            numPoints = 0; numContours = 0; flags = 0;
            if (face == IntPtr.Zero) return false;

            int pointCap = tags != null ? tags.Length : 0;
            int contourCap = contourEnds != null ? contourEnds.Length : 0;

            try
            {
                fixed (int* pXy = xy)
                fixed (byte* pTags = tags)
                fixed (short* pEnds = contourEnds)
                {
                    int rc = ut_ft_get_outline_data(face, pXy, pTags, pEnds,
                        pointCap, contourCap, out numPoints, out numContours, out flags);
                    _probed = true;
                    _available = true;
                    return rc == 0;
                }
            }
            catch (EntryPointNotFoundException)
            {
                _probed = true;
                _available = false;
                return false;
            }
            catch (DllNotFoundException)
            {
                _probed = true;
                _available = false;
                return false;
            }
        }
    }
}
