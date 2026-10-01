/* ut_colr.c — COLRv1 paint-tree wrappers (ut_colr_*), Milestone 2.
 * Clean-room over FreeType FT_COLOR_H (FT_Get_Color_Glyph_Paint, FT_Get_Paint,
 * FT_Get_Paint_Layers, FT_Get_Colorline_Stops). Matches the C# structs in FT.cs:
 * an FT_OpaquePaint is passed as (FT_Byte* p, int insert_root_transform), child paints
 * returned the same way; fixed-point outputs are FreeType's native units (16.16 / F2DOT14
 * / 26.6) exactly as the C# converts them. Non-zero return == "has value" per the C#
 * predicate convention. */
#include "ut_native.h"

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_COLOR_H
#include <string.h>

/* Rebuild an FT_OpaquePaint from the decomposed (p, insert) the C# passes. */
static FT_OpaquePaint mk_paint(void* p, int insert) {
    FT_OpaquePaint op; op.p = (FT_Byte*)p; op.insert_root_transform = (FT_Bool)insert; return op;
}

UT_API int ut_colr_get_glyph_paint(FT_Face face, uint32_t base_glyph, int root_transform,
        void** paintP, int* paintInsert) {
    if (paintP) *paintP = 0; if (paintInsert) *paintInsert = 0;
    if (!face) return 0;
    FT_OpaquePaint op; op.p = NULL; op.insert_root_transform = 0;
    FT_Color_Root_Transform rt = root_transform ? FT_COLOR_INCLUDE_ROOT_TRANSFORM
                                                 : FT_COLOR_NO_ROOT_TRANSFORM;
    if (!FT_Get_Color_Glyph_Paint(face, base_glyph, rt, &op)) return 0;
    if (paintP) *paintP = op.p;
    if (paintInsert) *paintInsert = op.insert_root_transform;
    return 1;
}

UT_API int ut_colr_debug_glyph_paint(FT_Face face, uint32_t base_glyph,
        int* hasColr, int* hasCpal, int* ftResult) {
    if (hasColr) *hasColr = 0; if (hasCpal) *hasCpal = 0; if (ftResult) *ftResult = 0;
    if (!face) return 0;
    FT_Palette_Data pd;
    if (hasCpal) *hasCpal = (FT_Palette_Data_Get(face, &pd) == 0 && pd.num_palettes > 0) ? 1 : 0;
    FT_OpaquePaint op; op.p = NULL; op.insert_root_transform = 0;
    FT_Bool r = FT_Get_Color_Glyph_Paint(face, base_glyph, FT_COLOR_INCLUDE_ROOT_TRANSFORM, &op);
    if (hasColr) *hasColr = r ? 1 : 0;
    if (ftResult) *ftResult = r ? 1 : 0;
    return 1;
}

UT_API int ut_colr_get_paint_format(FT_Face face, void* p, int insert) {
    if (!face) return -1;
    FT_OpaquePaint op = mk_paint(p, insert);
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, op, &paint)) return -1;
    return (int)paint.format;
}

UT_API int ut_colr_get_paint_solid(FT_Face face, void* p, int insert, uint16_t* ci, int* alpha) {
    if (ci) *ci = 0; if (alpha) *alpha = 0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_SOLID) return 0;
    if (ci) *ci = paint.u.solid.color.palette_index;
    if (alpha) *alpha = (int)paint.u.solid.color.alpha; /* F2DOT14 */
    return 1;
}

UT_API int ut_colr_get_paint_layers(FT_Face face, void* p, int insert,
        uint32_t* numLayers, uint32_t* layer, void** iterP) {
    if (numLayers) *numLayers = 0; if (layer) *layer = 0; if (iterP) *iterP = 0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_COLR_LAYERS) return 0;
    FT_LayerIterator* it = &paint.u.colr_layers.layer_iterator;
    if (numLayers) *numLayers = it->num_layers;
    if (layer) *layer = it->layer;
    if (iterP) *iterP = it->p;
    return 1;
}

UT_API int ut_colr_get_next_layer(FT_Face face, uint32_t* numLayers, uint32_t* layer,
        void** iterP, void** childP, int* childInsert) {
    if (childP) *childP = 0; if (childInsert) *childInsert = 0;
    if (!face) return 0;
    FT_LayerIterator it;
    it.num_layers = numLayers ? *numLayers : 0;
    it.layer = layer ? *layer : 0;
    it.p = iterP ? (FT_Byte*)*iterP : NULL;
    FT_OpaquePaint child; child.p = NULL; child.insert_root_transform = 0;
    FT_Bool r = FT_Get_Paint_Layers(face, &it, &child);
    if (numLayers) *numLayers = it.num_layers;
    if (layer) *layer = it.layer;
    if (iterP) *iterP = it.p;
    if (!r) return 0;
    if (childP) *childP = child.p;
    if (childInsert) *childInsert = child.insert_root_transform;
    return 1;
}

UT_API int ut_colr_get_paint_glyph(FT_Face face, void* p, int insert,
        uint32_t* gid, void** childP, int* childInsert) {
    if (gid) *gid = 0; if (childP) *childP = 0; if (childInsert) *childInsert = 0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_GLYPH) return 0;
    if (gid) *gid = paint.u.glyph.glyphID;
    if (childP) *childP = paint.u.glyph.paint.p;
    if (childInsert) *childInsert = paint.u.glyph.paint.insert_root_transform;
    return 1;
}

UT_API int ut_colr_get_paint_colr_glyph(FT_Face face, void* p, int insert, uint32_t* gid) {
    if (gid) *gid = 0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_COLR_GLYPH) return 0;
    if (gid) *gid = paint.u.colr_glyph.glyphID;
    return 1;
}

UT_API int ut_colr_get_paint_translate(FT_Face face, void* p, int insert,
        int* dx, int* dy, void** childP, int* childInsert) {
    if (dx) *dx = 0; if (dy) *dy = 0; if (childP) *childP = 0; if (childInsert) *childInsert = 0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_TRANSLATE) return 0;
    if (dx) *dx = (int)paint.u.translate.dx;
    if (dy) *dy = (int)paint.u.translate.dy;
    if (childP) *childP = paint.u.translate.paint.p;
    if (childInsert) *childInsert = paint.u.translate.paint.insert_root_transform;
    return 1;
}

UT_API int ut_colr_get_paint_scale(FT_Face face, void* p, int insert,
        int* sx, int* sy, int* cx, int* cy, void** childP, int* childInsert) {
    if (sx) *sx=0; if (sy) *sy=0; if (cx) *cx=0; if (cy) *cy=0;
    if (childP) *childP=0; if (childInsert) *childInsert=0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_SCALE) return 0;
    if (sx) *sx = (int)paint.u.scale.scale_x;
    if (sy) *sy = (int)paint.u.scale.scale_y;
    if (cx) *cx = (int)paint.u.scale.center_x;
    if (cy) *cy = (int)paint.u.scale.center_y;
    if (childP) *childP = paint.u.scale.paint.p;
    if (childInsert) *childInsert = paint.u.scale.paint.insert_root_transform;
    return 1;
}

UT_API int ut_colr_get_paint_rotate(FT_Face face, void* p, int insert,
        int* angle, int* cx, int* cy, void** childP, int* childInsert) {
    if (angle) *angle=0; if (cx) *cx=0; if (cy) *cy=0;
    if (childP) *childP=0; if (childInsert) *childInsert=0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_ROTATE) return 0;
    if (angle) *angle = (int)paint.u.rotate.angle;
    if (cx) *cx = (int)paint.u.rotate.center_x;
    if (cy) *cy = (int)paint.u.rotate.center_y;
    if (childP) *childP = paint.u.rotate.paint.p;
    if (childInsert) *childInsert = paint.u.rotate.paint.insert_root_transform;
    return 1;
}

UT_API int ut_colr_get_paint_skew(FT_Face face, void* p, int insert,
        int* xs, int* ys, int* cx, int* cy, void** childP, int* childInsert) {
    if (xs) *xs=0; if (ys) *ys=0; if (cx) *cx=0; if (cy) *cy=0;
    if (childP) *childP=0; if (childInsert) *childInsert=0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_SKEW) return 0;
    if (xs) *xs = (int)paint.u.skew.x_skew_angle;
    if (ys) *ys = (int)paint.u.skew.y_skew_angle;
    if (cx) *cx = (int)paint.u.skew.center_x;
    if (cy) *cy = (int)paint.u.skew.center_y;
    if (childP) *childP = paint.u.skew.paint.p;
    if (childInsert) *childInsert = paint.u.skew.paint.insert_root_transform;
    return 1;
}

UT_API int ut_colr_get_paint_transform(FT_Face face, void* p, int insert,
        int* xx, int* xy, int* dx, int* yx, int* yy, int* dy, void** childP, int* childInsert) {
    if (xx) *xx=0; if (xy) *xy=0; if (dx) *dx=0; if (yx) *yx=0; if (yy) *yy=0; if (dy) *dy=0;
    if (childP) *childP=0; if (childInsert) *childInsert=0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_TRANSFORM) return 0;
    FT_Affine23* a = &paint.u.transform.affine;
    if (xx) *xx = (int)a->xx; if (xy) *xy = (int)a->xy; if (dx) *dx = (int)a->dx;
    if (yx) *yx = (int)a->yx; if (yy) *yy = (int)a->yy; if (dy) *dy = (int)a->dy;
    if (childP) *childP = paint.u.transform.paint.p;
    if (childInsert) *childInsert = paint.u.transform.paint.insert_root_transform;
    return 1;
}

UT_API int ut_colr_get_paint_composite(FT_Face face, void* p, int insert,
        int* mode, void** backdropP, int* backdropInsert, void** sourceP, int* sourceInsert) {
    if (mode) *mode=0; if (backdropP) *backdropP=0; if (backdropInsert) *backdropInsert=0;
    if (sourceP) *sourceP=0; if (sourceInsert) *sourceInsert=0;
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_COMPOSITE) return 0;
    if (mode) *mode = (int)paint.u.composite.composite_mode;
    if (backdropP) *backdropP = paint.u.composite.backdrop_paint.p;
    if (backdropInsert) *backdropInsert = paint.u.composite.backdrop_paint.insert_root_transform;
    if (sourceP) *sourceP = paint.u.composite.source_paint.p;
    if (sourceInsert) *sourceInsert = paint.u.composite.source_paint.insert_root_transform;
    return 1;
}

static void fill_colorline(FT_ColorLine* cl, int* extend, uint32_t* numStops,
                           uint32_t* curStop, void** stopIterP, int* readVar) {
    if (extend) *extend = (int)cl->extend;
    FT_ColorStopIterator* it = &cl->color_stop_iterator;
    if (numStops) *numStops = it->num_color_stops;
    if (curStop) *curStop = it->current_color_stop;
    if (stopIterP) *stopIterP = it->p;
    if (readVar) *readVar = it->read_variable;
}

UT_API int ut_colr_get_paint_linear_gradient(FT_Face face, void* p, int insert,
        int* p0x,int* p0y,int* p1x,int* p1y,int* p2x,int* p2y,int* extend,
        uint32_t* numStops, uint32_t* curStop, void** stopIterP, int* readVar) {
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_LINEAR_GRADIENT) return 0;
    FT_PaintLinearGradient* g = &paint.u.linear_gradient;
    if (p0x) *p0x=(int)g->p0.x; if (p0y) *p0y=(int)g->p0.y;
    if (p1x) *p1x=(int)g->p1.x; if (p1y) *p1y=(int)g->p1.y;
    if (p2x) *p2x=(int)g->p2.x; if (p2y) *p2y=(int)g->p2.y;
    fill_colorline(&g->colorline, extend, numStops, curStop, stopIterP, readVar);
    return 1;
}

UT_API int ut_colr_get_paint_radial_gradient(FT_Face face, void* p, int insert,
        int* c0x,int* c0y,int* r0,int* c1x,int* c1y,int* r1,int* extend,
        uint32_t* numStops, uint32_t* curStop, void** stopIterP, int* readVar) {
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_RADIAL_GRADIENT) return 0;
    FT_PaintRadialGradient* g = &paint.u.radial_gradient;
    if (c0x) *c0x=(int)g->c0.x; if (c0y) *c0y=(int)g->c0.y; if (r0) *r0=(int)g->r0;
    if (c1x) *c1x=(int)g->c1.x; if (c1y) *c1y=(int)g->c1.y; if (r1) *r1=(int)g->r1;
    fill_colorline(&g->colorline, extend, numStops, curStop, stopIterP, readVar);
    return 1;
}

UT_API int ut_colr_get_paint_sweep_gradient(FT_Face face, void* p, int insert,
        int* cx,int* cy,int* startAngle,int* endAngle,int* extend,
        uint32_t* numStops, uint32_t* curStop, void** stopIterP, int* readVar) {
    if (!face) return 0;
    FT_COLR_Paint paint;
    if (!FT_Get_Paint(face, mk_paint(p, insert), &paint)) return 0;
    if (paint.format != FT_COLR_PAINTFORMAT_SWEEP_GRADIENT) return 0;
    FT_PaintSweepGradient* g = &paint.u.sweep_gradient;
    if (cx) *cx=(int)g->center.x; if (cy) *cy=(int)g->center.y;
    if (startAngle) *startAngle=(int)g->start_angle; if (endAngle) *endAngle=(int)g->end_angle;
    fill_colorline(&g->colorline, extend, numStops, curStop, stopIterP, readVar);
    return 1;
}

UT_API int ut_colr_get_colorstop(FT_Face face, uint32_t* numStops, uint32_t* curStop,
        void** iterP, int* readVar, int* stopOffset, uint16_t* colorIndex, int* alpha) {
    if (stopOffset) *stopOffset=0; if (colorIndex) *colorIndex=0; if (alpha) *alpha=0;
    if (!face) return 0;
    FT_ColorStopIterator it;
    it.num_color_stops = numStops ? *numStops : 0;
    it.current_color_stop = curStop ? *curStop : 0;
    it.p = iterP ? (FT_Byte*)*iterP : NULL;
    it.read_variable = readVar ? (FT_Bool)*readVar : 0;
    FT_ColorStop stop;
    FT_Bool r = FT_Get_Colorline_Stops(face, &stop, &it);
    if (numStops) *numStops = it.num_color_stops;
    if (curStop) *curStop = it.current_color_stop;
    if (iterP) *iterP = it.p;
    if (readVar) *readVar = it.read_variable;
    if (!r) return 0;
    if (stopOffset) *stopOffset = (int)stop.stop_offset;      /* F2DOT14 */
    if (colorIndex) *colorIndex = stop.color.palette_index;
    if (alpha) *alpha = (int)stop.color.alpha;                /* F2DOT14 */
    return 1;
}

UT_API int ut_colr_get_clipbox(FT_Face face, uint32_t base_glyph,
        int* blX,int* blY,int* tlX,int* tlY,int* trX,int* trY,int* brX,int* brY) {
    if (blX)*blX=0;if(blY)*blY=0;if(tlX)*tlX=0;if(tlY)*tlY=0;
    if (trX)*trX=0;if(trY)*trY=0;if(brX)*brX=0;if(brY)*brY=0;
    if (!face) return 0;
    FT_ClipBox cb;
    if (!FT_Get_Color_Glyph_ClipBox(face, base_glyph, &cb)) return 0;
    if (blX)*blX=(int)cb.bottom_left.x;  if(blY)*blY=(int)cb.bottom_left.y;
    if (tlX)*tlX=(int)cb.top_left.x;     if(tlY)*tlY=(int)cb.top_left.y;
    if (trX)*trX=(int)cb.top_right.x;    if(trY)*trY=(int)cb.top_right.y;
    if (brX)*brX=(int)cb.bottom_right.x; if(brY)*brY=(int)cb.bottom_right.y;
    return 1;
}
