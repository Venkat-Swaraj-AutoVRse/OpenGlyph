/* subset.c — editor DLL font ops (6 of 8 exports), Milestone 2.
 * Clean-room over hb-subset + HarfBuzz, matching Editor/FontSubsetter.cs.
 * All return a uint = bytes written (subset ops) or count (glyph/shape/codepoints).
 * When outData/out arrays are too small, subset ops still return the needed size so the
 * C# can grow the buffer and retry (matches its usage). */
#include "ut_native.h"

#include <hb.h>
#include <hb-subset.h>
#include <hb-ot.h>
#include <string.h>

static hb_face_t* face_from(const void* fontData, unsigned size) {
    hb_blob_t* blob = hb_blob_create((const char*)fontData, size, HB_MEMORY_MODE_READONLY, NULL, NULL);
    hb_face_t* face = hb_face_create(blob, 0);
    hb_blob_destroy(blob);
    return face;
}

/* Run a subset plan and copy result into outData (up to capacity); return needed size. */
static unsigned do_subset(hb_face_t* face, hb_subset_input_t* input,
                          void* outData, unsigned outCapacity) {
    hb_face_t* result = hb_subset_or_fail(face, input);
    hb_subset_input_destroy(input);
    if (!result) { hb_face_destroy(face); return 0; }
    hb_blob_t* blob = hb_face_reference_blob(result);
    unsigned len = 0;
    const char* data = hb_blob_get_data(blob, &len);
    if (outData && outCapacity >= len && data) memcpy(outData, data, len);
    hb_blob_destroy(blob);
    hb_face_destroy(result);
    hb_face_destroy(face);
    return len;
}

UT_API unsigned subset_font(const void* fontData, unsigned fontDataSize,
                            const uint32_t* codepoints, unsigned codepointCount,
                            void* outData, unsigned outCapacity) {
    if (!fontData || fontDataSize == 0) return 0;
    hb_face_t* face = face_from(fontData, fontDataSize);
    hb_subset_input_t* input = hb_subset_input_create_or_fail();
    if (!input) { hb_face_destroy(face); return 0; }
    hb_set_t* uset = hb_subset_input_unicode_set(input);
    for (unsigned i = 0; i < codepointCount; ++i) hb_set_add(uset, codepoints[i]);
    return do_subset(face, input, outData, outCapacity);
}

UT_API unsigned subset_font_remove_codepoints(const void* fontData, unsigned fontDataSize,
                            const uint32_t* codepoints, unsigned codepointCount,
                            void* outData, unsigned outCapacity) {
    if (!fontData || fontDataSize == 0) return 0;
    hb_face_t* face = face_from(fontData, fontDataSize);
    /* Keep everything, then drop the requested codepoints: start from the face's full
     * unicode set and remove the given ones. */
    hb_subset_input_t* input = hb_subset_input_create_or_fail();
    if (!input) { hb_face_destroy(face); return 0; }
    hb_set_t* full = hb_set_create();
    hb_face_collect_unicodes(face, full);
    for (unsigned i = 0; i < codepointCount; ++i) hb_set_del(full, codepoints[i]);
    hb_set_t* uset = hb_subset_input_unicode_set(input);
    hb_set_union(uset, full);
    hb_set_destroy(full);
    return do_subset(face, input, outData, outCapacity);
}

UT_API unsigned subset_font_remove_glyphs(const void* fontData, unsigned fontDataSize,
                            const uint32_t* glyphIds, unsigned glyphCount,
                            void* outData, unsigned outCapacity) {
    if (!fontData || fontDataSize == 0) return 0;
    hb_face_t* face = face_from(fontData, fontDataSize);
    unsigned total = hb_face_get_glyph_count(face);
    hb_subset_input_t* input = hb_subset_input_create_or_fail();
    if (!input) { hb_face_destroy(face); return 0; }
    hb_set_t* gset = hb_subset_input_glyph_set(input);
    for (unsigned g = 0; g < total; ++g) hb_set_add(gset, g);
    for (unsigned i = 0; i < glyphCount; ++i) hb_set_del(gset, glyphIds[i]);
    /* retain gids so remaining glyph ids stay stable */
    hb_subset_input_set_flags(input, HB_SUBSET_FLAGS_RETAIN_GIDS);
    return do_subset(face, input, outData, outCapacity);
}

UT_API unsigned get_glyph_count(const void* fontData, unsigned fontDataSize) {
    if (!fontData || fontDataSize == 0) return 0;
    hb_face_t* face = face_from(fontData, fontDataSize);
    unsigned n = hb_face_get_glyph_count(face);
    hb_face_destroy(face);
    return n;
}

UT_API unsigned shape_text(const void* fontData, unsigned fontDataSize,
                           const uint32_t* codepoints, unsigned codepointCount,
                           uint32_t* outGlyphIds, unsigned outCapacity) {
    if (!fontData || fontDataSize == 0) return 0;
    hb_face_t* face = face_from(fontData, fontDataSize);
    hb_font_t* font = hb_font_create(face);
    hb_ot_font_set_funcs(font);
    hb_buffer_t* buf = hb_buffer_create();
    hb_buffer_add_codepoints(buf, codepoints, codepointCount, 0, codepointCount);
    hb_buffer_guess_segment_properties(buf);
    hb_shape(font, buf, NULL, 0);
    unsigned n = hb_buffer_get_length(buf);
    hb_glyph_info_t* infos = hb_buffer_get_glyph_infos(buf, NULL);
    if (outGlyphIds && outCapacity >= n)
        for (unsigned i = 0; i < n; ++i) outGlyphIds[i] = infos[i].codepoint;
    hb_buffer_destroy(buf);
    hb_font_destroy(font);
    hb_face_destroy(face);
    return n;
}

UT_API unsigned get_font_codepoints(const void* fontData, unsigned fontDataSize,
                                    uint32_t* outCodepoints, unsigned outCapacity) {
    if (!fontData || fontDataSize == 0) return 0;
    hb_face_t* face = face_from(fontData, fontDataSize);
    hb_set_t* set = hb_set_create();
    hb_face_collect_unicodes(face, set);
    unsigned n = hb_set_get_population(set);
    if (outCodepoints && outCapacity >= n) {
        unsigned i = 0;
        hb_codepoint_t cp = HB_SET_VALUE_INVALID;
        while (hb_set_next(set, &cp) && i < outCapacity) outCodepoints[i++] = cp;
    }
    hb_set_destroy(set);
    hb_face_destroy(face);
    return n;
}
