# OpenGlyph native ABI inventory (Phase 0, Task 1)

Ground truth for the clean-room rebuild of `unitext_native` / `unitext_native_editor`.
Generated from: the C# P/Invoke surface in this repo, cross-checked against the real
exports of the shipped Windows x64 DLLs via `dumpbin /exports`.

## Method

- `dumpbin /exports Plugins/Windows/x86_64/unitext_native.dll`
- `dumpbin /exports Plugins/Windows/x86_64/unitext_native_editor.dll`
- Extracted every `[DllImport]` in `Runtime/Native/{FT,HB,BL}.cs`,
  `Runtime/EmojiCore/NativeFontReader.cs`, `Runtime/EmojiCore/WebGL/WebGLEmoji.cs`,
  `Editor/FontSubsetter.cs`, `Editor/NativeFileDialog.cs`.
- Compared the two sets programmatically.

## Result summary

| Library | Exports (real DLL) | C# P/Invoke references | Load-failure mismatches |
|---|---|---|---|
| `unitext_native.dll` (runtime) | **110** `ut_*` | 108 distinct `ut_*` | 0 |
| `unitext_native_editor.dll` (editor) | **8** | 8 | 0 |

The task brief estimated "~71 runtime exports"; the true count is **110**. Every C# P/Invoke
resolves to a real export (0 missing). Three runtime exports have no C# caller today but
MUST be reimplemented for a drop-in ABI (see "Exported but currently unreferenced").

The original C source file was named `unitext_native.cpp` (per a comment in `FT.cs:138`
referencing `ut_sdf_glyph_result`). That source is NOT published — this rebuild is clean-room
from the C# usage + upstream FreeType/HarfBuzz/Blend2D public APIs only.

Platform note: on iOS/tvOS/WebGL the C# side uses `__Internal` (static link) instead of the
`unitext_native` dynamic name; on those targets the symbols are the same `ut_*` set linked into
the app binary. iOS additionally has Objective-C `UniText_*` entry points (Core Text emoji,
`Plugins/iOS/SystemFontReader.mm`) and WebGL has `JS_BrowserEmoji_*` JS-lib entry points — both
OUT OF SCOPE for the Windows rebuild but recorded here for completeness.

## Convention

All runtime exports are `cdecl` (C# declares `CallingConvention = Cdecl`). Editor exports are
also `cdecl`. Integer return of most `ut_ft_*` / `ut_colr_*` is a FreeType-style error code
(0 == success) EXCEPT the COLR predicate-style wrappers where C# treats non-zero as "true"
(e.g. `ut_colr_get_glyph_paint`, `ut_ft_get_color_glyph_layer`, all `ut_colr_get_paint_*`,
`ut_ft_outline_to_blpath`, `ut_ft_get_outline_info`). Semantics inferred per-call from the C#
wrapper that consumes the return value — see the per-function notes below.

---

## Runtime DLL — 110 exports

### FreeType core (ut_ft_*) — 28

| Export | C signature (inferred) | Return semantics |
|---|---|---|
| `ut_ft_init` | `int(FT_Library* out)` | 0=ok |
| `ut_ft_done` | `int(FT_Library)` | 0=ok |
| `ut_ft_new_memory_face` | `int(FT_Library, const FT_Byte* base, FT_Long size, FT_Long faceIndex, FT_Face* out)` | 0=ok |
| `ut_ft_done_face` | `int(FT_Face)` | 0=ok |
| `ut_ft_get_char_index` | `FT_UInt(FT_Face, FT_ULong charcode)` | glyph index (0=missing) |
| `ut_ft_set_pixel_sizes` | `int(FT_Face, FT_UInt w, FT_UInt h)` | 0=ok |
| `ut_ft_select_size` | `int(FT_Face, int strikeIndex)` | 0=ok (FT error code) |
| `ut_ft_load_glyph` | `int(FT_Face, FT_UInt gid, FT_Int32 loadFlags)` | 0=ok |
| `ut_ft_render_glyph` | `int(FT_GlyphSlot, int renderMode)` | 0=ok |
| `ut_ft_get_glyph_slot` | `FT_GlyphSlot(FT_Face)` | slot ptr |
| `ut_ft_get_face_info` | `void(FT_Face, long* faceFlags, int* numGlyphs, int* unitsPerEm, int* numFixedSizes, int* numFaces, int* faceIndex, short* asc, short* desc, short* height)` | out params |
| `ut_ft_get_extended_face_info` | `int(FT_Face, short* capHeight, short* xHeight, short* superYOff, short* superYSize, short* subYOff, short* subYSize, short* strikeoutPos, short* strikeoutSize, short* underlinePos, short* underlineThick, const char** familyName, const char** styleName)` | non-zero if OS/2 present |
| `ut_ft_get_fixed_size` | `int(FT_Face, int index)` | ppem of strike |
| `ut_ft_get_glyph_metrics` | `void(FT_Face, int* w, int* h, int* bearingX, int* bearingY, int* advX, int* advY)` | out params (26.6 or design units per prior scale call) |
| `ut_ft_get_bitmap_info` | `void(FT_Face, int* w, int* h, int* pitch, int* pixelMode, void** buffer)` | out params |
| `ut_ft_get_bitmap_left` | `int(FT_Face)` | slot->bitmap_left |
| `ut_ft_get_bitmap_top` | `int(FT_Face)` | slot->bitmap_top |
| `ut_ft_set_sdf_spread` | `int(FT_Library, int spread)` | 0=ok (sets sdf+bsdf module `spread`) |
| `ut_ft_render_sdf_glyph` | `int(FT_Face, FT_UInt gid, int loadFlags, int spread, ut_sdf_glyph_result* out)` | 0=ok; malloc's out->bmpBuffer (see struct) |
| `ut_ft_free_sdf_buffer` | `void(void* buffer)` | frees malloc'd SDF buffer |
| `ut_ft_get_outline_info` | `int(FT_Face, int* numContours, int* numPoints)` | non-zero=has outline |
| `ut_ft_outline_to_blpath` | `int(FT_Face, BLPathCore* blPath)` | non-zero=ok; appends slot outline to a Blend2D path |
| `ut_ft_outline_decompose` | `int(FT_Face, ...callbacks/out...)` | **unreferenced by C#** — implement per FT_Outline_Decompose |
| `ut_ft_palette_data_get` | `int(FT_Face, FT_Palette_Data* out)` | 0=ok |
| `ut_ft_palette_select` | `int(FT_Face, FT_UShort paletteIndex, FT_Color** out)` | 0=ok |
| `ut_ft_get_color_glyph_clipbox` | `int(FT_Face, FT_UInt baseGlyph, FT_ClipBox* out)` | 0=ok |
| `ut_ft_get_color_glyph_layer` | `int(FT_Face, FT_UInt baseGlyph, FT_UInt* gid, FT_UInt* colorIndex, FT_LayerIterator* iter)` | non-zero while layers remain (COLRv0) |
| `ut_debug_sbix_graphic_type` | `int(FT_Face, char out[5], int* numStrikes)` | non-zero=sbix parsed |

### COLRv1 paint tree (ut_colr_*) — 20

All take `(FT_Face, FT_Byte* paintP, int paintInsert, ...)` = a decomposed `FT_OpaquePaint`
`{p, insert_root_transform}`, and return child paints as `(FT_Byte** childP, int* childInsert)`.
Non-zero return == success/"has value". Fixed-point outputs are 16.16 (`int`) unless noted.

| Export | Purpose |
|---|---|
| `ut_colr_get_glyph_paint` | root paint for a base glyph -> (paintP, paintInsert) |
| `ut_colr_debug_glyph_paint` | out: hasColr, hasCpal, ftResult |
| `ut_colr_get_paint_format` | returns FT_PaintFormat int |
| `ut_colr_get_paint_solid` | out: colorIndex(u16), alpha(F2DOT14 as int) |
| `ut_colr_get_paint_layers` | out: numLayers, layer, iterP (layer iterator) |
| `ut_colr_get_next_layer` | advance layer iterator -> child paint |
| `ut_colr_get_paint_glyph` | out: glyphId, child paint |
| `ut_colr_get_paint_colr_glyph` | out: glyphId |
| `ut_colr_get_paint_translate` | out: dx,dy (16.16), child |
| `ut_colr_get_paint_scale` | out: scaleX,scaleY,centerX,centerY, child |
| `ut_colr_get_paint_rotate` | out: angle,centerX,centerY, child |
| `ut_colr_get_paint_skew` | out: xSkew,ySkew,centerX,centerY, child |
| `ut_colr_get_paint_transform` | out: affine xx,xy,dx,yx,yy,dy, child |
| `ut_colr_get_paint_composite` | out: mode, backdrop paint, source paint |
| `ut_colr_get_paint_linear_gradient` | out: p0/p1/p2 (x,y), extend, colorstop iterator |
| `ut_colr_get_paint_radial_gradient` | out: c0(x,y),r0,c1(x,y),r1, extend, colorstop iterator |
| `ut_colr_get_paint_sweep_gradient` | out: center(x,y), startAngle,endAngle, extend, colorstop iterator |
| `ut_colr_get_colorstop` | advance colorstop iterator -> stopOffset(F2DOT14),colorIndex(u16),alpha(F2DOT14) |
| `ut_colr_get_clipbox` | out: 4 corners (26.6 int) |
| `ut_colr_get_paint_layers` (listed above) | |

### HarfBuzz (ut_hb_*) — 24

| Export | C signature |
|---|---|
| `ut_hb_blob_create` | `hb_blob_t*(const char* data, unsigned length, hb_memory_mode_t, void* userData, hb_destroy_func_t)` |
| `ut_hb_blob_destroy` | `void(hb_blob_t*)` |
| `ut_hb_face_create` | `hb_face_t*(hb_blob_t*, unsigned index)` |
| `ut_hb_face_destroy` | `void(hb_face_t*)` |
| `ut_hb_face_get_upem` | `unsigned(hb_face_t*)` |
| `ut_hb_font_create` | `hb_font_t*(hb_face_t*)` |
| `ut_hb_font_destroy` | `void(hb_font_t*)` |
| `ut_hb_font_get_face` | `hb_face_t*(hb_font_t*)` |
| `ut_hb_ot_font_set_funcs` | `void(hb_font_t*)` |
| `ut_hb_font_get_glyph` | `hb_bool_t(hb_font_t*, hb_codepoint_t unicode, hb_codepoint_t vs, hb_codepoint_t* glyph)` |
| `ut_hb_font_get_glyph_h_advance` | `hb_position_t(hb_font_t*, hb_codepoint_t glyph)` |
| `ut_hb_buffer_create` | `hb_buffer_t*()` |
| `ut_hb_buffer_destroy` | `void(hb_buffer_t*)` |
| `ut_hb_buffer_clear_contents` | `void(hb_buffer_t*)` |
| `ut_hb_buffer_set_direction` | `void(hb_buffer_t*, hb_direction_t)` |
| `ut_hb_buffer_set_script` | `void(hb_buffer_t*, hb_script_t)` |
| `ut_hb_buffer_set_content_type` | `void(hb_buffer_t*, hb_buffer_content_type_t)` |
| `ut_hb_buffer_set_flags` | `void(hb_buffer_t*, hb_buffer_flags_t)` |
| `ut_hb_buffer_set_language` | `void(hb_buffer_t*, const char* bcp47, int len)` — added in wave 1 (language-aware shaping); NULL/empty clears |
| `ut_hb_buffer_add_codepoints` | `void(hb_buffer_t*, const uint32_t* text, int textLen, unsigned itemOffset, int itemLen)` |
| `ut_hb_buffer_get_length` | `unsigned(hb_buffer_t*)` |
| `ut_hb_buffer_get_glyph_infos` | `hb_glyph_info_t*(hb_buffer_t*, unsigned* len)` (20-byte struct: codepoint,mask,cluster,var1,var2) |
| `ut_hb_buffer_get_glyph_positions` | `hb_glyph_position_t*(hb_buffer_t*, unsigned* len)` (20-byte struct: x_adv,y_adv,x_off,y_off,var) |
| `ut_hb_shape` | `void(hb_font_t*, hb_buffer_t*, const hb_feature_t*, unsigned numFeatures)` |
| `ut_version` | `const char*()` — **unreferenced by C#**; return combined FT/HB/BL versions |

### Blend2D (ut_bl*) — 38

Image (3): `ut_blImageCreate(int w,int h,uint format)->BLImage*`, `ut_blImageDestroy`,
`ut_blImageGetData(img,int* outStride)->void*`.
Context (20): `ut_blContextCreate(img)->ctx`, `Destroy`, `End`, `SetFillStyleRgba32(ctx,uint)`,
`FillAll`, `FillRect(ctx,double x,y,w,h)`, `FillPath(ctx,path)`, `SetFillStyleGradient(ctx,grad)`,
`Save`, `Restore`, `Translate`, `Scale`, `Rotate(ctx,double angle)`,
`Transform(ctx,double m00,m01,m10,m11,m20,m21)`, `ResetMatrix`, `SetCompOp(ctx,uint)`,
`ClipToRect(ctx,double x,y,w,h)`, `RestoreClipping`, `BlitImage(ctx,img,double x,y)`,
`SetFillRule(ctx,...)` — **unreferenced by C#**.
Path (9): `ut_blPathCreate()->path`, `Destroy`, `Clear`, `MoveTo`, `LineTo`,
`QuadTo(x1,y1,x2,y2)`, `CubicTo(x1,y1,x2,y2,x3,y3)`, `Close`, `Transform(6 doubles)`.
Gradient (7): `ut_blGradientCreateLinear(x0,y0,x1,y1)`, `CreateRadial(cx,cy,fx,fy,r)`,
`CreateConic(cx,cy,angle)`, `Destroy`, `AddStop(grad,double offset,uint rgba32)`,
`ResetStops`, `ApplyTransform(6 doubles)`.

### Exported but currently unreferenced by C# (still required for drop-in ABI) — 3
`ut_version`, `ut_ft_outline_decompose`, `ut_blContextSetFillRule`.

**`ut_ft_outline_decompose` — determined signature/semantics.** No C# caller exists, so the
signature is inferred from FreeType's public `FT_Outline_Decompose` (the only plausible thing a
`ut_ft_outline_decompose` wraps) plus this repo's usage pattern (all other outline access is
pull-style — `ut_ft_get_outline_info`, `ut_ft_outline_to_blpath`). Most likely original C:
`int ut_ft_outline_decompose(FT_Face face, const FT_Outline_Funcs* funcs, void* user)` — a thin
pass-through of the loaded glyph slot's `outline` to `FT_Outline_Decompose(&slot->outline, funcs, user)`,
returning the FreeType error code (0=ok). It walks the outline emitting move/line/conic/cubic
callbacks in 26.6 fixed point. Because it takes native function pointers it is awkward from C#,
which is why the codebase prefers `ut_ft_outline_to_blpath` and the new pull-style
`ut_ft_get_outline_data` below. Reimplement it as the pass-through above for ABI completeness;
this is a best-inference and should be re-confirmed against a decompose-based caller if one turns up.

### NEW additive export (Phase 0) — raw outline data — 1
Requested by the MSDF worker. Brings the planned runtime export count to **111** (110 original + 1).

| Export | C signature | Return |
|---|---|---|
| `ut_ft_get_outline_data` | `int(FT_Face, int32_t* xy /*2*nPts 26.6*/, uint8_t* tags /*nPts*/, int16_t* contourEnds /*nCtr*/, int pointCap, int contourCap, int* outNumPoints, int* outNumContours, int* outFlags)` | 1=ok, 0=no outline or capacity too small (out counts still filled) |

Pull-style companion to `ut_ft_get_outline_info` (sizing) — copies the loaded glyph slot's
`FT_Outline` into caller buffers: `points[i].x/y` -> `xy[2i]/xy[2i+1]` (26.6), `tags[i]` (FT_CURVE_TAG),
`contours[i]` -> `contourEnds[i]`, and `outline.flags` -> `outFlags`. C# side: `FT.TryGetOutlineData`
in `Runtime/Native/FT.Outline.cs`, which catches `EntryPointNotFoundException` so it degrades to
false on binaries predating this export.

---

## Editor DLL — 8 exports (`unitext_native_editor`)

Backed by HarfBuzz-subset (font ops) + OS-native file dialog. `cdecl`.

| Export | C signature | Notes |
|---|---|---|
| `subset_font` | `uint(const void* fontData, uint size, const uint32_t* codepoints, uint count, void* outData, uint outCapacity)` | keep only codepoints; returns bytes written (or needed size when out too small) |
| `subset_font_remove_codepoints` | same shape | remove codepoints |
| `subset_font_remove_glyphs` | `uint(..., const uint32_t* glyphIds, uint count, ...)` | remove by glyph id |
| `get_glyph_count` | `uint(const void* fontData, uint size)` | face num glyphs |
| `shape_text` | `uint(const void* fontData, uint size, const uint32_t* codepoints, uint count, uint32_t* outGlyphIds, uint outCap)` | returns glyph count |
| `get_font_codepoints` | `uint(const void* fontData, uint size, uint32_t* outCodepoints, uint outCap)` | enumerates cmap; returns count |
| `unitext_open_files_dialog` | `char*(const char* titleUtf8, const char* filtersUtf8, const char* initialDirUtf8)` | Win32 GetOpenFileNameW; returns newline/`\0`-joined UTF-8 paths |
| `unitext_free_dialog_result` | `void(char*)` | frees the dialog result |

---

## Struct layouts the C ABI MUST match (from C# [StructLayout])

- `ut_sdf_glyph_result` (LayoutKind.Sequential, 11×int32 + IntPtr):
  `success, metricWidth, metricHeight, metricBearingX, metricBearingY, metricAdvanceX,
   bmpWidth, bmpHeight, bmpPitch, bitmapLeft, bitmapTop, void* bmpBuffer`.
- `FT_Palette_Data`: `u16 num_palettes; void* palette_name_ids; void* palette_flags;
   u16 num_palette_entries; void* palette_entry_name_ids;` (natural x64 alignment).
- `FT_Color`: `u8 blue,green,red,alpha` (BGRA).
- `FT_OpaquePaint`: `void* p; u8 insert_root_transform;` (Pack=8 -> 16 bytes).
- `FT_LayerIterator`: `u32 num_layers; u32 layer; void* p;` (16 bytes, Pack=8).
- `FT_ColorStopIterator`: `u32 num_color_stops; u32 current_color_stop; void* p; u8 read_variable;`
  (24 bytes, Pack=8).
- `hb_glyph_info_t`: `u32 codepoint, mask, cluster, var1, var2` (20 bytes) — matches upstream.
- `hb_glyph_position_t`: `i32 x_advance, y_advance, x_offset, y_offset; u32 var` (20 bytes) — matches upstream.

These match upstream FreeType/HarfBuzz headers, so implementing the wrappers as thin
pass-throughs over the real upstream structs preserves layout automatically. The only
bespoke struct is `ut_sdf_glyph_result`, defined in the wrapper C.
