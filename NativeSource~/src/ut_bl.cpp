/* ut_bl.cpp — Blend2D wrappers (ut_bl*), Milestone 2.
 * Clean-room over the Blend2D C++ API, matching Runtime/Native/BL.cs. Handles are
 * heap-allocated Blend2D objects returned as opaque pointers; RGBA32 packing matches
 * BL.cs (0xAARRGGBB). Also implements ut_ft_outline_to_blpath (FreeType outline -> BLPath).
 */
#include "ut_native.h"

#include <blend2d.h>

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_OUTLINE_H

extern "C" {

/* ---- Image ---------------------------------------------------------------- */
UT_API void* ut_blImageCreate(int w, int h, uint32_t format) {
    if (w <= 0 || h <= 0) return nullptr;
    BLImage* img = new BLImage();
    if (img->create(w, h, (BLFormat)format) != BL_SUCCESS) { delete img; return nullptr; }
    return img;
}
UT_API void ut_blImageDestroy(void* img) { delete static_cast<BLImage*>(img); }
UT_API void* ut_blImageGetData(void* img, int* outStride) {
    if (outStride) *outStride = 0;
    if (!img) return nullptr;
    BLImageData d;
    if (static_cast<BLImage*>(img)->getData(&d) != BL_SUCCESS) return nullptr;
    if (outStride) *outStride = (int)d.stride;
    return d.pixelData;
}

/* ---- Context -------------------------------------------------------------- */
UT_API void* ut_blContextCreate(void* img) {
    if (!img) return nullptr;
    BLContext* ctx = new BLContext();
    if (ctx->begin(*static_cast<BLImage*>(img)) != BL_SUCCESS) { delete ctx; return nullptr; }
    return ctx;
}
UT_API void ut_blContextDestroy(void* ctx) { delete static_cast<BLContext*>(ctx); }
UT_API void ut_blContextEnd(void* ctx) { if (ctx) static_cast<BLContext*>(ctx)->end(); }
UT_API void ut_blContextSetFillStyleRgba32(void* ctx, uint32_t rgba32) {
    if (ctx) static_cast<BLContext*>(ctx)->setFillStyle(BLRgba32(rgba32));
}
UT_API void ut_blContextFillAll(void* ctx) { if (ctx) static_cast<BLContext*>(ctx)->fillAll(); }
UT_API void ut_blContextFillRect(void* ctx, double x, double y, double w, double h) {
    if (ctx) static_cast<BLContext*>(ctx)->fillRect(BLRect(x, y, w, h));
}
UT_API void ut_blContextFillPath(void* ctx, void* path) {
    if (ctx && path) static_cast<BLContext*>(ctx)->fillPath(*static_cast<BLPath*>(path));
}
UT_API void ut_blContextSetFillStyleGradient(void* ctx, void* grad) {
    if (ctx && grad) static_cast<BLContext*>(ctx)->setFillStyle(*static_cast<BLGradient*>(grad));
}
UT_API void ut_blContextSetFillRule(void* ctx, uint32_t rule) {
    if (ctx) static_cast<BLContext*>(ctx)->setFillRule((BLFillRule)rule);
}
UT_API void ut_blContextSave(void* ctx) { if (ctx) static_cast<BLContext*>(ctx)->save(); }
UT_API void ut_blContextRestore(void* ctx) { if (ctx) static_cast<BLContext*>(ctx)->restore(); }
UT_API void ut_blContextTranslate(void* ctx, double x, double y) {
    if (ctx) static_cast<BLContext*>(ctx)->translate(x, y);
}
UT_API void ut_blContextScale(void* ctx, double x, double y) {
    if (ctx) static_cast<BLContext*>(ctx)->scale(x, y);
}
UT_API void ut_blContextRotate(void* ctx, double angle) {
    if (ctx) static_cast<BLContext*>(ctx)->rotate(angle);
}
UT_API void ut_blContextTransform(void* ctx, double m00, double m01, double m10,
                                  double m11, double m20, double m21) {
    if (ctx) static_cast<BLContext*>(ctx)->applyTransform(BLMatrix2D(m00, m01, m10, m11, m20, m21));
}
UT_API void ut_blContextResetMatrix(void* ctx) {
    if (ctx) static_cast<BLContext*>(ctx)->resetTransform();
}
UT_API void ut_blContextSetCompOp(void* ctx, uint32_t compOp) {
    if (ctx) static_cast<BLContext*>(ctx)->setCompOp((BLCompOp)compOp);
}
UT_API void ut_blContextClipToRect(void* ctx, double x, double y, double w, double h) {
    if (ctx) static_cast<BLContext*>(ctx)->clipToRect(BLRect(x, y, w, h));
}
UT_API void ut_blContextRestoreClipping(void* ctx) {
    if (ctx) static_cast<BLContext*>(ctx)->restoreClipping();
}
UT_API void ut_blContextBlitImage(void* ctx, void* img, double x, double y) {
    if (ctx && img) static_cast<BLContext*>(ctx)->blitImage(BLPoint(x, y), *static_cast<BLImage*>(img));
}

/* ---- Path ----------------------------------------------------------------- */
UT_API void* ut_blPathCreate() { return new BLPath(); }
UT_API void ut_blPathDestroy(void* p) { delete static_cast<BLPath*>(p); }
UT_API void ut_blPathClear(void* p) { if (p) static_cast<BLPath*>(p)->clear(); }
UT_API void ut_blPathMoveTo(void* p, double x, double y) { if (p) static_cast<BLPath*>(p)->moveTo(x, y); }
UT_API void ut_blPathLineTo(void* p, double x, double y) { if (p) static_cast<BLPath*>(p)->lineTo(x, y); }
UT_API void ut_blPathQuadTo(void* p, double x1, double y1, double x2, double y2) {
    if (p) static_cast<BLPath*>(p)->quadTo(x1, y1, x2, y2);
}
UT_API void ut_blPathCubicTo(void* p, double x1, double y1, double x2, double y2, double x3, double y3) {
    if (p) static_cast<BLPath*>(p)->cubicTo(x1, y1, x2, y2, x3, y3);
}
UT_API void ut_blPathClose(void* p) { if (p) static_cast<BLPath*>(p)->close(); }
UT_API void ut_blPathTransform(void* p, double m00, double m01, double m10,
                               double m11, double m20, double m21) {
    if (p) static_cast<BLPath*>(p)->transform(BLMatrix2D(m00, m01, m10, m11, m20, m21));
}

/* ---- Gradient ------------------------------------------------------------- */
UT_API void* ut_blGradientCreateLinear(double x0, double y0, double x1, double y1) {
    BLGradient* g = new BLGradient(BLLinearGradientValues(x0, y0, x1, y1));
    return g;
}
UT_API void* ut_blGradientCreateRadial(double cx, double cy, double fx, double fy, double r) {
    BLGradient* g = new BLGradient(BLRadialGradientValues(cx, cy, fx, fy, r));
    return g;
}
UT_API void* ut_blGradientCreateConic(double cx, double cy, double angle) {
    BLGradient* g = new BLGradient(BLConicGradientValues(cx, cy, angle));
    return g;
}
UT_API void ut_blGradientDestroy(void* g) { delete static_cast<BLGradient*>(g); }
UT_API void ut_blGradientAddStop(void* g, double offset, uint32_t rgba32) {
    if (g) static_cast<BLGradient*>(g)->addStop(offset, BLRgba32(rgba32));
}
UT_API void ut_blGradientResetStops(void* g) { if (g) static_cast<BLGradient*>(g)->resetStops(); }
UT_API void ut_blGradientApplyTransform(void* g, double m00, double m01, double m10,
                                        double m11, double m20, double m21) {
    if (g) static_cast<BLGradient*>(g)->applyTransform(BLMatrix2D(m00, m01, m10, m11, m20, m21));
}

/* ---- FreeType outline -> BLPath ------------------------------------------- */
struct DecomposeCtx { BLPath* path; };
static int mv(const FT_Vector* to, void* u) {
    ((DecomposeCtx*)u)->path->moveTo(to->x / 64.0, to->y / 64.0); return 0;
}
static int ln(const FT_Vector* to, void* u) {
    ((DecomposeCtx*)u)->path->lineTo(to->x / 64.0, to->y / 64.0); return 0;
}
static int cn(const FT_Vector* c, const FT_Vector* to, void* u) {
    ((DecomposeCtx*)u)->path->quadTo(c->x / 64.0, c->y / 64.0, to->x / 64.0, to->y / 64.0); return 0;
}
static int cu(const FT_Vector* c1, const FT_Vector* c2, const FT_Vector* to, void* u) {
    ((DecomposeCtx*)u)->path->cubicTo(c1->x/64.0, c1->y/64.0, c2->x/64.0, c2->y/64.0, to->x/64.0, to->y/64.0); return 0;
}
UT_API int ut_ft_outline_to_blpath(void* face_, void* blPath) {
    FT_Face face = (FT_Face)face_;
    if (!face || !face->glyph || !blPath) return 0;
    if (face->glyph->format != FT_GLYPH_FORMAT_OUTLINE) return 0;
    FT_Outline_Funcs funcs; funcs.move_to = mv; funcs.line_to = ln;
    funcs.conic_to = cn; funcs.cubic_to = cu; funcs.shift = 0; funcs.delta = 0;
    DecomposeCtx ctx; ctx.path = static_cast<BLPath*>(blPath);
    return FT_Outline_Decompose(&face->glyph->outline, &funcs, &ctx) == 0 ? 1 : 0;
}

} // extern "C"
