/* ut_version.c — combined version string of the underlying libraries. */
#include "ut_native.h"
#include <stdio.h>

#include <ft2build.h>
#include FT_FREETYPE_H
#include <hb.h>

static char g_version[160];

UT_API const char* ut_version(void) {
    snprintf(g_version, sizeof(g_version),
             "openglyph-native; FreeType %d.%d.%d; HarfBuzz %s; Blend2D %s",
             FREETYPE_MAJOR, FREETYPE_MINOR, FREETYPE_PATCH,
             HB_VERSION_STRING,
#ifdef OPENGLYPH_HAVE_BLEND2D
             "linked"
#else
             "stub(M2)"
#endif
    );
    return g_version;
}
