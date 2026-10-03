/* ut_hb.c — HarfBuzz wrappers (ut_hb_*).
 * Clean-room from Runtime/Native/HB.cs + FT.Variations.cs + HarfBuzz public API. */
#include "ut_native.h"

#include <hb.h>
#include <hb-ot.h>

UT_API hb_blob_t* ut_hb_blob_create(const char* data, unsigned length, int mode,
                                    void* user_data, hb_destroy_func_t destroy) {
    return hb_blob_create(data, length, (hb_memory_mode_t)mode, user_data, destroy);
}
UT_API void ut_hb_blob_destroy(hb_blob_t* blob) { hb_blob_destroy(blob); }

UT_API hb_face_t* ut_hb_face_create(hb_blob_t* blob, unsigned index) {
    return hb_face_create(blob, index);
}
UT_API void ut_hb_face_destroy(hb_face_t* face) { hb_face_destroy(face); }
UT_API unsigned ut_hb_face_get_upem(hb_face_t* face) { return hb_face_get_upem(face); }

UT_API hb_font_t* ut_hb_font_create(hb_face_t* face) { return hb_font_create(face); }
UT_API void ut_hb_font_destroy(hb_font_t* font) { hb_font_destroy(font); }
UT_API hb_face_t* ut_hb_font_get_face(hb_font_t* font) { return hb_font_get_face(font); }
UT_API void ut_hb_ot_font_set_funcs(hb_font_t* font) { hb_ot_font_set_funcs(font); }

UT_API hb_bool_t ut_hb_font_get_glyph(hb_font_t* font, hb_codepoint_t unicode,
                                      hb_codepoint_t vs, hb_codepoint_t* glyph) {
    return hb_font_get_glyph(font, unicode, vs, glyph);
}
UT_API hb_position_t ut_hb_font_get_glyph_h_advance(hb_font_t* font, hb_codepoint_t glyph) {
    return hb_font_get_glyph_h_advance(font, glyph);
}

UT_API hb_buffer_t* ut_hb_buffer_create(void) { return hb_buffer_create(); }
UT_API void ut_hb_buffer_destroy(hb_buffer_t* b) { hb_buffer_destroy(b); }
UT_API void ut_hb_buffer_clear_contents(hb_buffer_t* b) { hb_buffer_clear_contents(b); }
UT_API void ut_hb_buffer_set_direction(hb_buffer_t* b, int d) {
    hb_buffer_set_direction(b, (hb_direction_t)d);
}
UT_API void ut_hb_buffer_set_script(hb_buffer_t* b, unsigned script) {
    hb_buffer_set_script(b, (hb_script_t)script);
}
/* BCP 47 language tag (ASCII, len bytes; len < 0 = NUL-terminated). NULL/empty clears it
 * (HB_LANGUAGE_INVALID), which is what a fresh/cleared buffer has. Drives OpenType language-system
 * selection (locl and other language-specific lookups). */
UT_API void ut_hb_buffer_set_language(hb_buffer_t* b, const char* tag, int len) {
    if (!b) return;
    if (!tag || len == 0 || !tag[0]) { hb_buffer_set_language(b, HB_LANGUAGE_INVALID); return; }
    hb_buffer_set_language(b, hb_language_from_string(tag, len));
}
UT_API void ut_hb_buffer_set_content_type(hb_buffer_t* b, int ct) {
    hb_buffer_set_content_type(b, (hb_buffer_content_type_t)ct);
}
UT_API void ut_hb_buffer_set_flags(hb_buffer_t* b, unsigned flags) {
    hb_buffer_set_flags(b, (hb_buffer_flags_t)flags);
}
UT_API void ut_hb_buffer_add_codepoints(hb_buffer_t* b, const uint32_t* text, int text_len,
                                        unsigned item_offset, int item_len) {
    hb_buffer_add_codepoints(b, (const hb_codepoint_t*)text, text_len, item_offset, item_len);
}
UT_API unsigned ut_hb_buffer_get_length(hb_buffer_t* b) { return hb_buffer_get_length(b); }

UT_API hb_glyph_info_t* ut_hb_buffer_get_glyph_infos(hb_buffer_t* b, unsigned* len) {
    return hb_buffer_get_glyph_infos(b, len);
}
UT_API hb_glyph_position_t* ut_hb_buffer_get_glyph_positions(hb_buffer_t* b, unsigned* len) {
    return hb_buffer_get_glyph_positions(b, len);
}
UT_API void ut_hb_shape(hb_font_t* font, hb_buffer_t* b,
                        const hb_feature_t* features, unsigned num_features) {
    hb_shape(font, b, features, num_features);
}

/* Variations passthrough (declared in FT.Variations.cs). tags are 4CC big-endian ints. */
UT_API void ut_hb_font_set_variations(hb_font_t* font, const uint32_t* tags,
                                      const float* values, int count) {
    if (!font || !tags || !values || count <= 0) return;
    enum { MAXV = 64 };
    hb_variation_t stackv[MAXV];
    hb_variation_t* v = stackv;
    if (count > MAXV) return; /* keep simple; callers use few axes */
    for (int i = 0; i < count; ++i) {
        v[i].tag   = (hb_tag_t)tags[i];
        v[i].value = values[i];
    }
    hb_font_set_variations(font, v, count);
}
