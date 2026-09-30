/* ut_colr_stub.c — COLRv1 paint-tree exports, STUBBED for milestone 1.
 * Every function returns 0 (== "no value"/failure per the C# predicate convention),
 * so the ABI is complete and the DLL links. Real COLRv1 walking lands in milestone 2.
 * TODO(M2): implement over FreeType FT_COLOR_H FT_Get_Paint / FT_Get_Color_Glyph_Paint. */
#include "ut_native.h"

typedef void* FTFace;

UT_API int ut_colr_get_glyph_paint(FTFace face, uint32_t base_glyph, int root_transform,
        void** paintP, int* paintInsert) {
    (void)face;(void)base_glyph;(void)root_transform;
    if (paintP) *paintP = 0; if (paintInsert) *paintInsert = 0; return 0;
}
UT_API int ut_colr_debug_glyph_paint(FTFace face, uint32_t base_glyph,
        int* hasColr, int* hasCpal, int* ftResult) {
    (void)face;(void)base_glyph;
    if (hasColr) *hasColr = 0; if (hasCpal) *hasCpal = 0; if (ftResult) *ftResult = 0; return 0;
}
UT_API int ut_colr_get_paint_format(FTFace face, void* p, int insert) {
    (void)face;(void)p;(void)insert; return -1;
}
UT_API int ut_colr_get_paint_solid(FTFace face, void* p, int insert, uint16_t* ci, int* alpha) {
    (void)face;(void)p;(void)insert; if(ci)*ci=0; if(alpha)*alpha=0; return 0;
}
UT_API int ut_colr_get_paint_layers(FTFace face, void* p, int insert,
        uint32_t* numLayers, uint32_t* layer, void** iterP) {
    (void)face;(void)p;(void)insert;
    if(numLayers)*numLayers=0; if(layer)*layer=0; if(iterP)*iterP=0; return 0;
}
UT_API int ut_colr_get_next_layer(FTFace face, uint32_t* numLayers, uint32_t* layer,
        void** iterP, void** childP, int* childInsert) {
    (void)face;(void)numLayers;(void)layer;(void)iterP;
    if(childP)*childP=0; if(childInsert)*childInsert=0; return 0;
}
UT_API int ut_colr_get_paint_glyph(FTFace face, void* p, int insert,
        uint32_t* gid, void** childP, int* childInsert) {
    (void)face;(void)p;(void)insert;
    if(gid)*gid=0; if(childP)*childP=0; if(childInsert)*childInsert=0; return 0;
}
UT_API int ut_colr_get_paint_colr_glyph(FTFace face, void* p, int insert, uint32_t* gid) {
    (void)face;(void)p;(void)insert; if(gid)*gid=0; return 0;
}
UT_API int ut_colr_get_paint_translate(FTFace face, void* p, int insert,
        int* dx, int* dy, void** childP, int* childInsert) {
    (void)face;(void)p;(void)insert;
    if(dx)*dx=0; if(dy)*dy=0; if(childP)*childP=0; if(childInsert)*childInsert=0; return 0;
}
UT_API int ut_colr_get_paint_scale(FTFace face, void* p, int insert,
        int* sx, int* sy, int* cx, int* cy, void** childP, int* childInsert) {
    (void)face;(void)p;(void)insert;
    if(sx)*sx=0; if(sy)*sy=0; if(cx)*cx=0; if(cy)*cy=0;
    if(childP)*childP=0; if(childInsert)*childInsert=0; return 0;
}
UT_API int ut_colr_get_paint_rotate(FTFace face, void* p, int insert,
        int* angle, int* cx, int* cy, void** childP, int* childInsert) {
    (void)face;(void)p;(void)insert;
    if(angle)*angle=0; if(cx)*cx=0; if(cy)*cy=0;
    if(childP)*childP=0; if(childInsert)*childInsert=0; return 0;
}
UT_API int ut_colr_get_paint_skew(FTFace face, void* p, int insert,
        int* xs, int* ys, int* cx, int* cy, void** childP, int* childInsert) {
    (void)face;(void)p;(void)insert;
    if(xs)*xs=0; if(ys)*ys=0; if(cx)*cx=0; if(cy)*cy=0;
    if(childP)*childP=0; if(childInsert)*childInsert=0; return 0;
}
UT_API int ut_colr_get_paint_transform(FTFace face, void* p, int insert,
        int* xx, int* xy, int* dx, int* yx, int* yy, int* dy, void** childP, int* childInsert) {
    (void)face;(void)p;(void)insert;
    if(xx)*xx=0; if(xy)*xy=0; if(dx)*dx=0; if(yx)*yx=0; if(yy)*yy=0; if(dy)*dy=0;
    if(childP)*childP=0; if(childInsert)*childInsert=0; return 0;
}
UT_API int ut_colr_get_paint_composite(FTFace face, void* p, int insert,
        int* mode, void** backdropP, int* backdropInsert, void** sourceP, int* sourceInsert) {
    (void)face;(void)p;(void)insert;
    if(mode)*mode=0; if(backdropP)*backdropP=0; if(backdropInsert)*backdropInsert=0;
    if(sourceP)*sourceP=0; if(sourceInsert)*sourceInsert=0; return 0;
}
UT_API int ut_colr_get_paint_linear_gradient(FTFace face, void* p, int insert,
        int* p0x,int* p0y,int* p1x,int* p1y,int* p2x,int* p2y,int* extend,
        uint32_t* numStops, uint32_t* curStop, void** stopIterP, int* readVar) {
    (void)face;(void)p;(void)insert;
    if(p0x)*p0x=0;if(p0y)*p0y=0;if(p1x)*p1x=0;if(p1y)*p1y=0;if(p2x)*p2x=0;if(p2y)*p2y=0;
    if(extend)*extend=0;if(numStops)*numStops=0;if(curStop)*curStop=0;
    if(stopIterP)*stopIterP=0;if(readVar)*readVar=0; return 0;
}
UT_API int ut_colr_get_paint_radial_gradient(FTFace face, void* p, int insert,
        int* c0x,int* c0y,int* r0,int* c1x,int* c1y,int* r1,int* extend,
        uint32_t* numStops, uint32_t* curStop, void** stopIterP, int* readVar) {
    (void)face;(void)p;(void)insert;
    if(c0x)*c0x=0;if(c0y)*c0y=0;if(r0)*r0=0;if(c1x)*c1x=0;if(c1y)*c1y=0;if(r1)*r1=0;
    if(extend)*extend=0;if(numStops)*numStops=0;if(curStop)*curStop=0;
    if(stopIterP)*stopIterP=0;if(readVar)*readVar=0; return 0;
}
UT_API int ut_colr_get_paint_sweep_gradient(FTFace face, void* p, int insert,
        int* cx,int* cy,int* startAngle,int* endAngle,int* extend,
        uint32_t* numStops, uint32_t* curStop, void** stopIterP, int* readVar) {
    (void)face;(void)p;(void)insert;
    if(cx)*cx=0;if(cy)*cy=0;if(startAngle)*startAngle=0;if(endAngle)*endAngle=0;
    if(extend)*extend=0;if(numStops)*numStops=0;if(curStop)*curStop=0;
    if(stopIterP)*stopIterP=0;if(readVar)*readVar=0; return 0;
}
UT_API int ut_colr_get_colorstop(FTFace face, uint32_t* numStops, uint32_t* curStop,
        void** iterP, int* readVar, int* stopOffset, uint16_t* colorIndex, int* alpha) {
    (void)face;(void)numStops;(void)curStop;(void)iterP;(void)readVar;
    if(stopOffset)*stopOffset=0; if(colorIndex)*colorIndex=0; if(alpha)*alpha=0; return 0;
}
UT_API int ut_colr_get_clipbox(FTFace face, uint32_t base_glyph,
        int* blX,int* blY,int* tlX,int* tlY,int* trX,int* trY,int* brX,int* brY) {
    (void)face;(void)base_glyph;
    if(blX)*blX=0;if(blY)*blY=0;if(tlX)*tlX=0;if(tlY)*tlY=0;
    if(trX)*trX=0;if(trY)*trY=0;if(brX)*brX=0;if(brY)*brY=0; return 0;
}

/* ut_ft_outline_to_blpath depends on Blend2D — stubbed with ut_bl* in M2. */
