/* ut_ft.c — FreeType core wrappers (ut_ft_*).
 * Clean-room from the C# usage in Runtime/Native/FT.cs + FreeType public API.
 */
#include "ut_native.h"

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_MODULE_H
#include FT_OUTLINE_H
#include FT_COLOR_H
#include FT_TRUETYPE_TABLES_H
#include FT_SFNT_NAMES_H
#include FT_TRUETYPE_IDS_H

#include <stdlib.h>
#include <string.h>

/* ---- FreeType error passthrough: FT_Error is already 0==ok. --------------- */

UT_API int ut_ft_init(FT_Library* out_lib) {
    if (!out_lib) return 1;
    return (int)FT_Init_FreeType(out_lib);
}

UT_API int ut_ft_done(FT_Library lib) {
    return (int)FT_Done_FreeType(lib);
}

UT_API int ut_ft_new_memory_face(FT_Library lib, const FT_Byte* base, FT_Long size,
                                 FT_Long face_index, FT_Face* out_face) {
    if (!out_face) return 1;
    return (int)FT_New_Memory_Face(lib, base, size, face_index, out_face);
}

UT_API int ut_ft_done_face(FT_Face face) {
    return (int)FT_Done_Face(face);
}

UT_API FT_UInt ut_ft_get_char_index(FT_Face face, FT_ULong charcode) {
    return FT_Get_Char_Index(face, charcode);
}

UT_API int ut_ft_set_pixel_sizes(FT_Face face, FT_UInt w, FT_UInt h) {
    return (int)FT_Set_Pixel_Sizes(face, w, h);
}

UT_API int ut_ft_select_size(FT_Face face, int strike_index) {
    return (int)FT_Select_Size(face, strike_index);
}

UT_API int ut_ft_load_glyph(FT_Face face, FT_UInt gid, FT_Int32 load_flags) {
    if (!face) return 1;
    return (int)FT_Load_Glyph(face, gid, load_flags);
}

UT_API int ut_ft_render_glyph(FT_GlyphSlot slot, int render_mode) {
    if (!slot) return 1;
    return (int)FT_Render_Glyph(slot, (FT_Render_Mode)render_mode);
}

UT_API FT_GlyphSlot ut_ft_get_glyph_slot(FT_Face face) {
    return face ? face->glyph : NULL;
}

UT_API void ut_ft_get_face_info(FT_Face face, long* faceFlags, int* numGlyphs,
                                int* unitsPerEm, int* numFixedSizes, int* numFaces,
                                int* faceIndex, short* ascender, short* descender, short* height) {
    if (!face) return;
    if (faceFlags)     *faceFlags     = (long)face->face_flags;
    if (numGlyphs)     *numGlyphs     = (int)face->num_glyphs;
    if (unitsPerEm)    *unitsPerEm    = (int)face->units_per_EM;
    if (numFixedSizes) *numFixedSizes = (int)face->num_fixed_sizes;
    if (numFaces)      *numFaces      = (int)face->num_faces;
    if (faceIndex)     *faceIndex     = (int)(face->face_index & 0xFFFF);
    if (ascender)      *ascender      = face->ascender;
    if (descender)     *descender     = face->descender;
    if (height)        *height        = face->height;
}

UT_API int ut_ft_get_extended_face_info(FT_Face face,
        short* capHeight, short* xHeight,
        short* superscriptYOffset, short* superscriptYSize,
        short* subscriptYOffset, short* subscriptYSize,
        short* strikeoutPosition, short* strikeoutSize,
        short* underlinePosition, short* underlineThickness,
        const char** familyName, const char** styleName) {
    if (!face) return 0;
    if (familyName) *familyName = face->family_name;
    if (styleName)  *styleName  = face->style_name;
    if (underlinePosition)  *underlinePosition  = face->underline_position;
    if (underlineThickness) *underlineThickness = face->underline_thickness;

    TT_OS2* os2 = (TT_OS2*)FT_Get_Sfnt_Table(face, FT_SFNT_OS2);
    if (os2 && os2->version != 0xFFFF) {
        if (capHeight)          *capHeight          = os2->sCapHeight;
        if (xHeight)            *xHeight            = os2->sxHeight;
        if (superscriptYOffset) *superscriptYOffset = os2->ySuperscriptYOffset;
        if (superscriptYSize)   *superscriptYSize   = os2->ySuperscriptYSize;
        if (subscriptYOffset)   *subscriptYOffset   = os2->ySubscriptYOffset;
        if (subscriptYSize)     *subscriptYSize     = os2->ySubscriptYSize;
        if (strikeoutPosition)  *strikeoutPosition  = os2->yStrikeoutPosition;
        if (strikeoutSize)      *strikeoutSize      = os2->yStrikeoutSize;
        return 1;
    }
    return 0;
}

UT_API int ut_ft_get_fixed_size(FT_Face face, int index) {
    if (!face || index < 0 || index >= face->num_fixed_sizes) return 0;
    /* C# uses this as a ppem-ish value; return y_ppem in whole px (26.6 -> px). */
    return (int)(face->available_sizes[index].y_ppem >> 6);
}

UT_API void ut_ft_get_glyph_metrics(FT_Face face, int* width, int* height,
                                    int* bearingX, int* bearingY, int* advanceX, int* advanceY) {
    if (!face || !face->glyph) return;
    FT_Glyph_Metrics* m = &face->glyph->metrics;
    if (width)    *width    = (int)m->width;
    if (height)   *height   = (int)m->height;
    if (bearingX) *bearingX = (int)m->horiBearingX;
    if (bearingY) *bearingY = (int)m->horiBearingY;
    if (advanceX) *advanceX = (int)m->horiAdvance;
    if (advanceY) *advanceY = (int)m->vertAdvance;
}

UT_API void ut_ft_get_bitmap_info(FT_Face face, int* width, int* height, int* pitch,
                                  int* pixelMode, void** buffer) {
    if (!face || !face->glyph) return;
    FT_Bitmap* b = &face->glyph->bitmap;
    if (width)     *width     = (int)b->width;
    if (height)    *height    = (int)b->rows;
    if (pitch)     *pitch     = b->pitch;
    if (pixelMode) *pixelMode = b->pixel_mode;
    if (buffer)    *buffer    = b->buffer;
}

UT_API int ut_ft_get_bitmap_left(FT_Face face) {
    return (face && face->glyph) ? face->glyph->bitmap_left : 0;
}

UT_API int ut_ft_get_bitmap_top(FT_Face face) {
    return (face && face->glyph) ? face->glyph->bitmap_top : 0;
}

UT_API int ut_ft_set_sdf_spread(FT_Library lib, int spread) {
    if (!lib) return 1;
    FT_UInt s = (FT_UInt)spread;
    FT_Error e1 = FT_Property_Set(lib, "sdf",  "spread", &s);
    FT_Error e2 = FT_Property_Set(lib, "bsdf", "spread", &s);
    return (e1 == 0 && e2 == 0) ? 0 : 1;
}

/* Load + render SDF in one call; malloc a copy of the bitmap the caller frees. */
UT_API int ut_ft_render_sdf_glyph(FT_Face face, FT_UInt gid, int load_flags, int spread,
                                  ut_sdf_glyph_result* out) {
    if (!face || !out) return 1;
    memset(out, 0, sizeof(*out));

    if (spread > 0 && face->glyph) {
        FT_UInt s = (FT_UInt)spread;
        FT_Property_Set(face->glyph->library, "sdf",  "spread", &s);
        FT_Property_Set(face->glyph->library, "bsdf", "spread", &s);
    }

    FT_Error err = FT_Load_Glyph(face, gid, load_flags);
    if (err) return (int)err;

    FT_GlyphSlot slot = face->glyph;
    FT_Glyph_Metrics* m = &slot->metrics;
    out->metricWidth    = (int)m->width;
    out->metricHeight   = (int)m->height;
    out->metricBearingX = (int)m->horiBearingX;
    out->metricBearingY = (int)m->horiBearingY;
    out->metricAdvanceX = (int)m->horiAdvance;

    err = FT_Render_Glyph(slot, FT_RENDER_MODE_SDF);
    if (err) return (int)err;

    FT_Bitmap* b = &slot->bitmap;
    int pitch = b->pitch < 0 ? -b->pitch : b->pitch;
    size_t sz = (size_t)pitch * b->rows;
    out->bmpWidth  = (int)b->width;
    out->bmpHeight = (int)b->rows;
    out->bmpPitch  = b->pitch;
    out->bitmapLeft = slot->bitmap_left;
    out->bitmapTop  = slot->bitmap_top;
    if (sz > 0 && b->buffer) {
        void* copy = malloc(sz);
        if (!copy) return 1;
        memcpy(copy, b->buffer, sz);
        out->bmpBuffer = copy;
    }
    out->success = 1;
    return 0;
}

UT_API void ut_ft_free_sdf_buffer(void* buffer) {
    if (buffer) free(buffer);
}

UT_API int ut_ft_get_outline_info(FT_Face face, int* numContours, int* numPoints) {
    if (!face || !face->glyph) return 0;
    if (face->glyph->format != FT_GLYPH_FORMAT_OUTLINE) return 0;
    FT_Outline* o = &face->glyph->outline;
    if (numContours) *numContours = o->n_contours;
    if (numPoints)   *numPoints   = o->n_points;
    return 1;
}

/* NEW additive (MSDF worker): copy the loaded slot outline into caller buffers.
 * Returns 1 on success, 0 if no outline or capacity too small (counts still filled). */
UT_API int ut_ft_get_outline_data(FT_Face face,
        int32_t* xy, uint8_t* tags, int16_t* contourEnds,
        int pointCapacity, int contourCapacity,
        int* outNumPoints, int* outNumContours, int* outFlags) {
    if (outNumPoints)   *outNumPoints = 0;
    if (outNumContours) *outNumContours = 0;
    if (outFlags)       *outFlags = 0;
    if (!face || !face->glyph) return 0;
    if (face->glyph->format != FT_GLYPH_FORMAT_OUTLINE) return 0;

    FT_Outline* o = &face->glyph->outline;
    if (outNumPoints)   *outNumPoints   = o->n_points;
    if (outNumContours) *outNumContours = o->n_contours;
    if (outFlags)       *outFlags       = (int)o->flags;

    if (pointCapacity < o->n_points || contourCapacity < o->n_contours)
        return 0; /* counts filled; caller resizes and retries */
    if (!xy || !tags || !contourEnds) return 0;

    for (int i = 0; i < o->n_points; ++i) {
        xy[2*i]   = (int32_t)o->points[i].x; /* 26.6 fixed */
        xy[2*i+1] = (int32_t)o->points[i].y;
        tags[i]   = (uint8_t)o->tags[i];
    }
    for (int c = 0; c < o->n_contours; ++c)
        contourEnds[c] = (int16_t)o->contours[c];
    return 1;
}

/* ut_ft_outline_decompose — inferred pass-through of FT_Outline_Decompose.
 * See ABI_INVENTORY.md: no C# caller; signature reconstructed from FreeType. */
UT_API int ut_ft_outline_decompose(FT_Face face, const FT_Outline_Funcs* funcs, void* user) {
    if (!face || !face->glyph || !funcs) return 1;
    if (face->glyph->format != FT_GLYPH_FORMAT_OUTLINE) return 1;
    return (int)FT_Outline_Decompose(&face->glyph->outline, funcs, user);
}

/* ---- Palette / color (COLRv0 layer + palette live here; COLRv1 paint tree in ut_colr) --- */

UT_API int ut_ft_palette_data_get(FT_Face face, ut_palette_data* out) {
    if (!face || !out) return 1;
    FT_Palette_Data pd;
    FT_Error err = FT_Palette_Data_Get(face, &pd);
    if (err) return (int)err;
    out->num_palettes          = pd.num_palettes;
    out->palette_name_ids      = (void*)pd.palette_name_ids;
    out->palette_flags         = (void*)pd.palette_flags;
    out->num_palette_entries   = pd.num_palette_entries;
    out->palette_entry_name_ids= (void*)pd.palette_entry_name_ids;
    return 0;
}

UT_API int ut_ft_palette_select(FT_Face face, uint16_t palette_index, void** out_palette) {
    if (!face || !out_palette) return 1;
    FT_Color* colors = NULL;
    FT_Error err = FT_Palette_Select(face, palette_index, &colors);
    if (err) return (int)err;
    *out_palette = colors;
    return 0;
}

UT_API int ut_ft_get_color_glyph_layer(FT_Face face, FT_UInt base_glyph,
        FT_UInt* out_gid, FT_UInt* out_color, ut_layer_iterator* iter) {
    if (!face || !iter) return 0;
    FT_LayerIterator it;
    it.num_layers = iter->num_layers;
    it.layer      = iter->layer;
    it.p          = (FT_Byte*)iter->p;
    FT_Bool r = FT_Get_Color_Glyph_Layer(face, base_glyph, out_gid, out_color, &it);
    iter->num_layers = it.num_layers;
    iter->layer      = it.layer;
    iter->p          = it.p;
    return r ? 1 : 0;
}

UT_API int ut_ft_get_color_glyph_clipbox(FT_Face face, FT_UInt base_glyph, void* out_clipbox) {
    if (!face || !out_clipbox) return 1;
    FT_ClipBox cb;
    if (!FT_Get_Color_Glyph_ClipBox(face, base_glyph, &cb)) return 1;
    /* C# FT_ClipBox is 8 x int (26.6): bl,tl,tr,br pairs. FT_ClipBox stores FT_Vector (nint). */
    int32_t* o = (int32_t*)out_clipbox;
    o[0]=(int32_t)cb.bottom_left.x;  o[1]=(int32_t)cb.bottom_left.y;
    o[2]=(int32_t)cb.top_left.x;     o[3]=(int32_t)cb.top_left.y;
    o[4]=(int32_t)cb.top_right.x;    o[5]=(int32_t)cb.top_right.y;
    o[6]=(int32_t)cb.bottom_right.x; o[7]=(int32_t)cb.bottom_right.y;
    return 0;
}

/* ---- sbix diagnostic ------------------------------------------------------ */
UT_API int ut_debug_sbix_graphic_type(FT_Face face, char out_type[5], int* out_num_strikes) {
    if (out_type) memset(out_type, 0, 5);
    if (out_num_strikes) *out_num_strikes = 0;
    if (!face) return 0;
    /* sbix presence approximated via fixed sizes + color flag; graphicType read would
     * require raw table access. Report strikes; leave type empty when unknown. */
    if (out_num_strikes) *out_num_strikes = face->num_fixed_sizes;
    return (face->face_flags & FT_FACE_FLAG_SBIX) ? 1 : 0;
}
