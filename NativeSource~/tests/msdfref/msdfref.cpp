// msdfref — upstream-msdfgen reference field generator for the OpenGlyph MSDF parity test.
//
// Uses msdfgen v1.12 (85e8b3d, MIT, Viktor Chlumsky) CORE as a pure oracle: it never reads a
// font. The glyphref .NET harness exports our real FreeType outline — framed and coloured by
// the managed Runtime/FontCore/Msdf code — as an msdfgen shape-description text file. This
// driver reads that identical shape and generates msdfgen's own MSDF at the identical
// projection/range, so any per-texel difference is purely algorithmic (our port vs upstream),
// not a difference of outline, framing, or colouring.
//
// USAGE:
//   msdfref <shape.txt> <w> <h> <range> <scaleX> <scaleY> <translateX> <translateY> <outPrefix> [recolorAngle]
//
// Framing matches MsdfBuilder: scale=(1,1), translate=(spread-gx0, spread-gy0), range=2*spread,
// inverseYAxis=false (bottom-up field, y=0 is the bottom row — same as MsdfGenerator).
//
// If [recolorAngle] (radians) is given, the shape is RE-coloured here with msdfgen's own
// edgeColoringSimple(shape, angle, 0) IGNORING any colours in the file, and the resulting
// per-edge colours are written to <outPrefix>.msdfgen_colors.txt. Otherwise the colours
// already present in the file (OpenGlyph's own colouring) are used verbatim and echoed to
// <outPrefix>.used_colors.txt. The harness runs BOTH: file colours (parity of fields) and a
// recolor pass (independent proof that the two colourings agree per edge).
//
// OUTPUT (little-endian):
//   <outPrefix>.raw.f32        : int32 w, int32 h, int32 N(=3), then w*h*3 float32 (NO error correction)
//   <outPrefix>.corrected.f32  : same header + field WITH msdfgen error correction (EDGE_PRIORITY default)
//   <outPrefix>.<used|msdfgen>_colors.txt : one line per edge "contour edge color(0..7)"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "msdfgen.h"

using namespace msdfgen;

static std::string slurp(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "msdfref: cannot open shape file %s\n", path); exit(2); }
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    std::string s; s.resize(n > 0 ? (size_t) n : 0);
    if (n > 0) { size_t rd = fread(&s[0], 1, (size_t) n, f); s.resize(rd); }
    fclose(f);
    return s;
}

static void dumpField(const char *path, const float *px, int w, int h, int N) {
    FILE *f = fopen(path, "wb");
    if (!f) { fprintf(stderr, "msdfref: cannot write %s\n", path); exit(3); }
    int32_t hdr[3] = { w, h, N };
    fwrite(hdr, sizeof(int32_t), 3, f);
    fwrite(px, sizeof(float), (size_t) w * h * N, f);
    fclose(f);
}

static void dumpColors(const char *path, const Shape &shape) {
    FILE *f = fopen(path, "wb");
    if (!f) { fprintf(stderr, "msdfref: cannot write %s\n", path); exit(3); }
    int ci = 0;
    for (std::vector<Contour>::const_iterator c = shape.contours.begin(); c != shape.contours.end(); ++c, ++ci) {
        int ei = 0;
        for (std::vector<EdgeHolder>::const_iterator e = c->edges.begin(); e != c->edges.end(); ++e, ++ei)
            fprintf(f, "%d %d %d\n", ci, ei, (int) (*e)->color);
    }
    fclose(f);
}

int main(int argc, char **argv) {
    if (argc < 10) {
        fprintf(stderr, "usage: msdfref <shape.txt> <w> <h> <range> <sx> <sy> <tx> <ty> <outPrefix> [recolorAngle]\n");
        return 2;
    }
    const char *shapePath = argv[1];
    int   w     = atoi(argv[2]);
    int   h     = atoi(argv[3]);
    double range= atof(argv[4]);
    double sx   = atof(argv[5]);
    double sy   = atof(argv[6]);
    double tx   = atof(argv[7]);
    double ty   = atof(argv[8]);
    const char *outPrefix = argv[9];
    bool  recolor = (argc > 10);
    double recolorAngle = recolor ? atof(argv[10]) : 3.0;

    std::string text = slurp(shapePath);

    Shape shape;
    bool colorsSpecified = false;
    if (!readShapeDescription(text.c_str(), shape, &colorsSpecified)) {
        fprintf(stderr, "msdfref: failed to parse shape description %s\n", shapePath);
        return 4;
    }
    // The harness has already oriented contours before serializing; do NOT re-run orientContours
    // (it could flip a contour and change the field). normalize() only splits fully-smooth
    // single-edge contours into thirds, which is exactly what msdfgen does internally before
    // colouring, so it is safe and matches the pipeline. We skip it to keep geometry byte-identical
    // to what glyphref serialized; colouring token semantics are preserved.

    if (recolor) {
        // Independent colouring: ignore file colours, colour with msdfgen's own algorithm.
        edgeColoringSimple(shape, recolorAngle, 0);
        std::string cpath = std::string(outPrefix) + ".msdfgen_colors.txt";
        dumpColors(cpath.c_str(), shape);
    } else {
        if (!colorsSpecified)
            fprintf(stderr, "msdfref: WARNING shape has no per-edge colours; medians will be plain SDF\n");
        std::string cpath = std::string(outPrefix) + ".used_colors.txt";
        dumpColors(cpath.c_str(), shape);
    }

    Projection projection(Vector2(sx, sy), Vector2(tx, ty));
    Range distRange(range); // symmetrical width => [-range/2, +range/2]

    // RAW: error correction DISABLED.
    {
        std::vector<float> buf((size_t) w * h * 3, 0.f);
        BitmapRef<float, 3> ref(&buf[0], w, h);
        MSDFGeneratorConfig cfg;
        cfg.overlapSupport = true;
        cfg.errorCorrection.mode = ErrorCorrectionConfig::DISABLED;
        generateMSDF(ref, shape, projection, distRange, cfg);
        std::string p = std::string(outPrefix) + ".raw.f32";
        dumpField(p.c_str(), &buf[0], w, h, 3);
    }

    // CORRECTED: msdfgen default EDGE_PRIORITY + CHECK_DISTANCE_AT_EDGE.
    {
        std::vector<float> buf((size_t) w * h * 3, 0.f);
        BitmapRef<float, 3> ref(&buf[0], w, h);
        MSDFGeneratorConfig cfg;
        cfg.overlapSupport = true;
        cfg.errorCorrection.mode = ErrorCorrectionConfig::EDGE_PRIORITY;
        cfg.errorCorrection.distanceCheckMode = ErrorCorrectionConfig::CHECK_DISTANCE_AT_EDGE;
        generateMSDF(ref, shape, projection, distRange, cfg);
        std::string p = std::string(outPrefix) + ".corrected.f32";
        dumpField(p.c_str(), &buf[0], w, h, 3);
    }

    printf("msdfref: wrote %s.{raw,corrected}.f32 (%dx%d range=%g scale=%g,%g translate=%g,%g)\n",
           outPrefix, w, h, range, sx, sy, tx, ty);
    return 0;
}
