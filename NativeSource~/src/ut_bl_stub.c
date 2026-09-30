/* ut_bl_stub.c — Blend2D wrappers, STUB used when OPENGLYPH_ENABLE_BLEND2D is OFF.
 * The REAL implementation is src/ut_bl.cpp (built when Blend2D is enabled). This stub
 * keeps the full ABI exported so the DLL links on hosts where the Blend2D subproject
 * cannot build (e.g. the CMake 4.2.2 recursion bug — see PARITY_RESULTS_M2.md).
 * Handles return NULL, void functions no-op, ut_ft_outline_to_blpath returns 0. */
#include "ut_native.h"

typedef void* BLPtr;

UT_API BLPtr ut_blImageCreate(int w, int h, uint32_t fmt){(void)w;(void)h;(void)fmt;return 0;}
UT_API void  ut_blImageDestroy(BLPtr img){(void)img;}
UT_API BLPtr ut_blImageGetData(BLPtr img, int* stride){(void)img; if(stride)*stride=0; return 0;}

UT_API BLPtr ut_blContextCreate(BLPtr img){(void)img;return 0;}
UT_API void ut_blContextDestroy(BLPtr c){(void)c;}
UT_API void ut_blContextEnd(BLPtr c){(void)c;}
UT_API void ut_blContextSetFillStyleRgba32(BLPtr c, uint32_t v){(void)c;(void)v;}
UT_API void ut_blContextFillAll(BLPtr c){(void)c;}
UT_API void ut_blContextFillRect(BLPtr c,double x,double y,double w,double h){(void)c;(void)x;(void)y;(void)w;(void)h;}
UT_API void ut_blContextFillPath(BLPtr c, BLPtr p){(void)c;(void)p;}
UT_API void ut_blContextSetFillStyleGradient(BLPtr c, BLPtr g){(void)c;(void)g;}
UT_API void ut_blContextSetFillRule(BLPtr c, uint32_t r){(void)c;(void)r;}
UT_API void ut_blContextSave(BLPtr c){(void)c;}
UT_API void ut_blContextRestore(BLPtr c){(void)c;}
UT_API void ut_blContextTranslate(BLPtr c,double x,double y){(void)c;(void)x;(void)y;}
UT_API void ut_blContextScale(BLPtr c,double x,double y){(void)c;(void)x;(void)y;}
UT_API void ut_blContextRotate(BLPtr c,double a){(void)c;(void)a;}
UT_API void ut_blContextTransform(BLPtr c,double a,double b,double d,double e,double f,double g){(void)c;(void)a;(void)b;(void)d;(void)e;(void)f;(void)g;}
UT_API void ut_blContextResetMatrix(BLPtr c){(void)c;}
UT_API void ut_blContextSetCompOp(BLPtr c, uint32_t op){(void)c;(void)op;}
UT_API void ut_blContextClipToRect(BLPtr c,double x,double y,double w,double h){(void)c;(void)x;(void)y;(void)w;(void)h;}
UT_API void ut_blContextRestoreClipping(BLPtr c){(void)c;}
UT_API void ut_blContextBlitImage(BLPtr c, BLPtr img, double x, double y){(void)c;(void)img;(void)x;(void)y;}

UT_API BLPtr ut_blPathCreate(void){return 0;}
UT_API void ut_blPathDestroy(BLPtr p){(void)p;}
UT_API void ut_blPathClear(BLPtr p){(void)p;}
UT_API void ut_blPathMoveTo(BLPtr p,double x,double y){(void)p;(void)x;(void)y;}
UT_API void ut_blPathLineTo(BLPtr p,double x,double y){(void)p;(void)x;(void)y;}
UT_API void ut_blPathQuadTo(BLPtr p,double x1,double y1,double x2,double y2){(void)p;(void)x1;(void)y1;(void)x2;(void)y2;}
UT_API void ut_blPathCubicTo(BLPtr p,double x1,double y1,double x2,double y2,double x3,double y3){(void)p;(void)x1;(void)y1;(void)x2;(void)y2;(void)x3;(void)y3;}
UT_API void ut_blPathClose(BLPtr p){(void)p;}
UT_API void ut_blPathTransform(BLPtr p,double a,double b,double c,double d,double e,double f){(void)p;(void)a;(void)b;(void)c;(void)d;(void)e;(void)f;}

UT_API BLPtr ut_blGradientCreateLinear(double x0,double y0,double x1,double y1){(void)x0;(void)y0;(void)x1;(void)y1;return 0;}
UT_API BLPtr ut_blGradientCreateRadial(double cx,double cy,double fx,double fy,double r){(void)cx;(void)cy;(void)fx;(void)fy;(void)r;return 0;}
UT_API BLPtr ut_blGradientCreateConic(double cx,double cy,double a){(void)cx;(void)cy;(void)a;return 0;}
UT_API void ut_blGradientDestroy(BLPtr g){(void)g;}
UT_API void ut_blGradientAddStop(BLPtr g,double off,uint32_t rgba){(void)g;(void)off;(void)rgba;}
UT_API void ut_blGradientResetStops(BLPtr g){(void)g;}
UT_API void ut_blGradientApplyTransform(BLPtr g,double a,double b,double c,double d,double e,double f){(void)g;(void)a;(void)b;(void)c;(void)d;(void)e;(void)f;}

UT_API int ut_ft_outline_to_blpath(void* face, void* blPath){(void)face;(void)blPath;return 0;}
