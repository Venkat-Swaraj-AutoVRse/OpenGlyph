/* ut_ft_variations.c — FreeType Multiple Master / GX-var wrappers (Task 3).
 * Clean-room from FT.Variations.cs + FreeType FT_MULTIPLE_MASTERS_H API. */
#include "ut_native.h"

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_MULTIPLE_MASTERS_H

#include <stdlib.h>

UT_API int ut_ft_get_mm_var(FT_Face face, int* numAxis, int* numNamedInstances) {
    if (numAxis) *numAxis = 0;
    if (numNamedInstances) *numNamedInstances = 0;
    if (!face) return 1;
    FT_MM_Var* mm = NULL;
    FT_Error err = FT_Get_MM_Var(face, &mm);
    if (err || !mm) return err ? (int)err : 1;
    if (numAxis) *numAxis = (int)mm->num_axis;
    if (numNamedInstances) *numNamedInstances = (int)mm->num_namedstyles;
    FT_Done_MM_Var(face->glyph ? face->glyph->library : NULL, mm);
    return 0;
}

UT_API int ut_ft_get_var_axis(FT_Face face, int axisIndex,
        uint32_t* tag, int* min1616, int* def1616, int* max1616, uint32_t* nameId) {
    if (!face) return 1;
    FT_MM_Var* mm = NULL;
    if (FT_Get_MM_Var(face, &mm) || !mm) return 1;
    int rc = 1;
    if (axisIndex >= 0 && axisIndex < (int)mm->num_axis) {
        FT_Var_Axis* a = &mm->axis[axisIndex];
        if (tag)     *tag     = (uint32_t)a->tag;
        if (min1616) *min1616 = (int)a->minimum; /* FT_Fixed 16.16 */
        if (def1616) *def1616 = (int)a->def;
        if (max1616) *max1616 = (int)a->maximum;
        if (nameId)  *nameId  = (uint32_t)a->strid;
        rc = 0;
    }
    FT_Done_MM_Var(face->glyph ? face->glyph->library : NULL, mm);
    return rc;
}

UT_API int ut_ft_get_var_named_instance(FT_Face face, int instanceIndex,
        uint32_t* nameId, int32_t* outCoords1616, int coordCapacity, int* coordCount) {
    if (coordCount) *coordCount = 0;
    if (!face) return 1;
    FT_MM_Var* mm = NULL;
    if (FT_Get_MM_Var(face, &mm) || !mm) return 1;
    int rc = 1;
    if (instanceIndex >= 0 && instanceIndex < (int)mm->num_namedstyles) {
        FT_Var_Named_Style* ns = &mm->namedstyle[instanceIndex];
        if (nameId) *nameId = (uint32_t)ns->strid;
        int n = (int)mm->num_axis;
        if (coordCount) *coordCount = n;
        if (outCoords1616 && coordCapacity >= n) {
            for (int i = 0; i < n; ++i) outCoords1616[i] = (int32_t)ns->coords[i];
            rc = 0;
        } else {
            rc = 0; /* counts filled; treat as success for count query */
        }
    }
    FT_Done_MM_Var(face->glyph ? face->glyph->library : NULL, mm);
    return rc;
}

UT_API int ut_ft_set_var_design_coordinates(FT_Face face, int32_t* coords1616, int coordCount) {
    if (!face || !coords1616 || coordCount <= 0) return 1;
    FT_Fixed stackbuf[32];
    FT_Fixed* c = (coordCount <= 32) ? stackbuf : (FT_Fixed*)malloc(sizeof(FT_Fixed)*coordCount);
    if (!c) return 1;
    for (int i = 0; i < coordCount; ++i) c[i] = (FT_Fixed)coords1616[i];
    FT_Error err = FT_Set_Var_Design_Coordinates(face, coordCount, c);
    if (c != stackbuf) free(c);
    return (int)err;
}

UT_API int ut_ft_set_named_instance(FT_Face face, int instanceIndex) {
    if (!face) return 1;
    return (int)FT_Set_Named_Instance(face, (FT_UInt)instanceIndex);
}
