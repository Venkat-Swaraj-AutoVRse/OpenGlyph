/* OpenGlyph native ABI — shared definitions.
 * Struct layouts here MUST match the C# [StructLayout] definitions in
 * Runtime/Native/FT.cs (see NativeSource~/ABI_INVENTORY.md). static_asserts below
 * lock sizeof/offsetof so a layout drift fails the build, not at runtime.
 *
 * Clean-room: implemented from the C# P/Invoke usage + upstream FreeType/HarfBuzz
 * public APIs only. No closed unitext_native source was consulted.
 */
#ifndef UT_NATIVE_H
#define UT_NATIVE_H

#include <stdint.h>
#include <stddef.h>

#if defined(_MSC_VER)
#  define UT_API __declspec(dllexport)
#else
#  define UT_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
#  define UT_STATIC_ASSERT(cond, msg) static_assert(cond, msg)
extern "C" {
#else
#  define UT_STATIC_ASSERT(cond, msg) _Static_assert(cond, msg)
#endif

/* --- ut_sdf_glyph_result: 11 x int32 + pointer (C# SdfGlyphResult) --------- */
#pragma pack(push, 8)
typedef struct ut_sdf_glyph_result {
    int32_t success;
    int32_t metricWidth;
    int32_t metricHeight;
    int32_t metricBearingX;
    int32_t metricBearingY;
    int32_t metricAdvanceX;
    int32_t bmpWidth;
    int32_t bmpHeight;
    int32_t bmpPitch;
    int32_t bitmapLeft;
    int32_t bitmapTop;
    void*   bmpBuffer;
} ut_sdf_glyph_result;
#pragma pack(pop)

/* On x64: 11*4 = 44 bytes ints, padded to 8 for the pointer -> 48+8 = 56.
 * C# Sequential layout with an IntPtr trailing does the same: verify size. */
UT_STATIC_ASSERT(sizeof(ut_sdf_glyph_result) == 56, "ut_sdf_glyph_result size mismatch");
UT_STATIC_ASSERT(offsetof(ut_sdf_glyph_result, bmpBuffer) == 48, "bmpBuffer offset mismatch");

/* --- FT_Palette_Data mirror (C# FT_Palette_Data) --------------------------- */
typedef struct ut_palette_data {
    uint16_t num_palettes;
    void*    palette_name_ids;
    void*    palette_flags;
    uint16_t num_palette_entries;
    void*    palette_entry_name_ids;
} ut_palette_data;
/* natural x64 alignment: u16 + pad6 + ptr + ptr + u16 + pad6 + ptr = 40 */
UT_STATIC_ASSERT(sizeof(ut_palette_data) == 40, "ut_palette_data size mismatch");
UT_STATIC_ASSERT(offsetof(ut_palette_data, palette_name_ids) == 8, "palette_name_ids offset");
UT_STATIC_ASSERT(offsetof(ut_palette_data, palette_entry_name_ids) == 32, "palette_entry_name_ids offset");

/* --- FT_Color (BGRA) ------------------------------------------------------- */
typedef struct ut_ft_color { uint8_t blue, green, red, alpha; } ut_ft_color;
UT_STATIC_ASSERT(sizeof(ut_ft_color) == 4, "ut_ft_color size mismatch");

/* --- FT_LayerIterator (C# FT_LayerIterator, Pack=8) ------------------------ */
#pragma pack(push, 8)
typedef struct ut_layer_iterator {
    uint32_t num_layers;
    uint32_t layer;
    void*    p;
} ut_layer_iterator;
#pragma pack(pop)
UT_STATIC_ASSERT(sizeof(ut_layer_iterator) == 16, "ut_layer_iterator size mismatch");

/* HarfBuzz glyph info/pos structs are used as upstream; the C# mirrors
 * hb_glyph_info_t (20 bytes) and hb_glyph_position_t (20 bytes) exactly, so the
 * wrapper returns upstream pointers directly — no bespoke struct needed. */

#ifdef __cplusplus
}
#endif

#endif /* UT_NATIVE_H */
