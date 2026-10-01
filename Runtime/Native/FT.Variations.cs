using System;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.CallingConvention;

namespace LightSide
{
    /// <summary>
    /// ADDITIVE (Phase 0, Task 3) variable-font ABI over FreeType's Multiple Master /
    /// GX-var API plus a HarfBuzz variations passthrough. New native exports; does NOT
    /// modify the existing <see cref="FT"/> / <see cref="HB"/> classes.
    ///
    /// The existing FT class is NOT declared partial, so per the Phase 0 brief these
    /// declarations live in a SEPARATE static class rather than extending FT.
    ///
    /// New native exports these bind to (implemented in NativeSource~/src/ut_ft_variations.c):
    ///   ut_ft_get_mm_var             -> axis + named-instance counts
    ///   ut_ft_get_var_axis           -> per-axis tag/min/default/max + name id
    ///   ut_ft_get_var_named_instance -> per-instance name id + coords
    ///   ut_ft_set_var_design_coordinates
    ///   ut_ft_set_named_instance
    ///   ut_hb_font_set_variations
    /// Coordinates are FreeType 16.16 fixed point (FT_Fixed) unless noted.
    /// </summary>
    internal static unsafe class FTVar
    {
    #if (UNITY_IOS || UNITY_TVOS || UNITY_WEBGL) && !UNITY_EDITOR
        private const string LibraryName = "__Internal";
    #else
        private const string LibraryName = "unitext_native";
    #endif

        /// <summary>One variation axis (design-space).</summary>
        public struct VarAxis
        {
            public uint tag;         // OpenType axis tag, e.g. 'wght' as a 4CC
            public float min;        // design min (float, converted from 16.16)
            public float def;        // design default
            public float max;        // design max
            public uint nameId;      // 'name' table id for the axis label
        }

        /// <summary>A named instance (e.g. "Bold", "Condensed").</summary>
        public struct NamedInstance
        {
            public uint nameId;      // 'name' table id for the instance
            public int coordCount;   // number of design coords (== axis count)
        }

        // ---- Raw exports ---------------------------------------------------------
        [DllImport(LibraryName, CallingConvention = Cdecl)]
        private static extern int ut_ft_get_mm_var(IntPtr face, out int numAxis, out int numNamedInstances);

        [DllImport(LibraryName, CallingConvention = Cdecl)]
        private static extern int ut_ft_get_var_axis(IntPtr face, int axisIndex,
            out uint tag, out int min1616, out int def1616, out int max1616, out uint nameId);

        [DllImport(LibraryName, CallingConvention = Cdecl)]
        private static extern int ut_ft_get_var_named_instance(IntPtr face, int instanceIndex,
            out uint nameId, int* outCoords1616, int coordCapacity, out int coordCount);

        [DllImport(LibraryName, CallingConvention = Cdecl)]
        private static extern int ut_ft_set_var_design_coordinates(IntPtr face, int* coords1616, int coordCount);

        [DllImport(LibraryName, CallingConvention = Cdecl)]
        private static extern int ut_ft_set_named_instance(IntPtr face, int instanceIndex);

        // HarfBuzz variations passthrough. tags are 4CCs; values are user-space floats.
        [DllImport(LibraryName, CallingConvention = Cdecl)]
        private static extern void ut_hb_font_set_variations(IntPtr hbFont, uint* tags, float* values, int count);

        private const float Fixed16 = 65536f;

        // ---- Managed API ---------------------------------------------------------

        /// <summary>Returns true and fills counts if the face is variable.</summary>
        public static bool TryGetVariationInfo(IntPtr face, out int axisCount, out int namedInstanceCount)
        {
            axisCount = namedInstanceCount = 0;
            if (face == IntPtr.Zero) return false;
            return ut_ft_get_mm_var(face, out axisCount, out namedInstanceCount) == 0;
        }

        /// <summary>Reads one axis (tag/min/default/max/nameId).</summary>
        public static bool TryGetAxis(IntPtr face, int axisIndex, out VarAxis axis)
        {
            axis = default;
            if (face == IntPtr.Zero) return false;
            if (ut_ft_get_var_axis(face, axisIndex, out uint tag, out int min, out int def, out int max, out uint nameId) != 0)
                return false;
            axis = new VarAxis { tag = tag, min = min / Fixed16, def = def / Fixed16, max = max / Fixed16, nameId = nameId };
            return true;
        }

        /// <summary>Reads a named instance's coordinates (in design units, floats).</summary>
        public static bool TryGetNamedInstance(IntPtr face, int instanceIndex, out uint nameId, out float[] coords)
        {
            nameId = 0; coords = Array.Empty<float>();
            if (face == IntPtr.Zero) return false;
            if (!TryGetVariationInfo(face, out int axisCount, out _)) return false;
            var raw = new int[axisCount];
            int count;
            fixed (int* p = raw)
            {
                if (ut_ft_get_var_named_instance(face, instanceIndex, out nameId, p, axisCount, out count) != 0)
                    return false;
            }
            coords = new float[count];
            for (int i = 0; i < count; i++) coords[i] = raw[i] / Fixed16;
            return true;
        }

        /// <summary>Sets design coordinates (one per axis, in design units / floats).</summary>
        public static bool SetDesignCoordinates(IntPtr face, ReadOnlySpan<float> coords)
        {
            if (face == IntPtr.Zero || coords.Length == 0) return false;
            Span<int> fixed16 = coords.Length <= 32 ? stackalloc int[coords.Length] : new int[coords.Length];
            for (int i = 0; i < coords.Length; i++) fixed16[i] = (int)(coords[i] * Fixed16 + (coords[i] >= 0 ? 0.5f : -0.5f));
            fixed (int* p = fixed16)
            {
                return ut_ft_set_var_design_coordinates(face, p, coords.Length) == 0;
            }
        }

        /// <summary>Applies a named instance by index (1-based per FreeType convention; 0 resets).</summary>
        public static bool SetNamedInstance(IntPtr face, int instanceIndex)
        {
            if (face == IntPtr.Zero) return false;
            return ut_ft_set_named_instance(face, instanceIndex) == 0;
        }

        /// <summary>HarfBuzz-side variations passthrough for shaping a variable font.</summary>
        public static void SetHbVariations(IntPtr hbFont, ReadOnlySpan<uint> tags, ReadOnlySpan<float> values)
        {
            if (hbFont == IntPtr.Zero || tags.Length == 0 || tags.Length != values.Length) return;
            fixed (uint* t = tags)
            fixed (float* v = values)
            {
                ut_hb_font_set_variations(hbFont, t, v, tags.Length);
            }
        }

        /// <summary>Build a 4CC tag from a string like "wght".</summary>
        public static uint Tag(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            uint t = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = i < s.Length ? s[i] : ' ';
                t = (t << 8) | (byte)c;
            }
            return t;
        }
    }
}
