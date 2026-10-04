# Web (WebGL 2 and WebGPU)

OpenGlyph works in Unity Web players with either web graphics API:

| Graphics API | Unity | Status |
|---|---|---|
| WebGL 2 (`OpenGLES3`) | 2021.3+ | supported |
| WebGPU (`WebGPU`) | 6000.0+ | supported; Unity's WebGPU backend itself is experimental |

Both use the same player and the same native library, `Plugins/WebGL/libunitext_native.a` (a static
wasm archive with FreeType, HarfBuzz, libpng and zlib built in). Verified with Unity 6000.3.19f1 in
Chrome: a WebGPU-only build and a WebGL 2-only build.

## Setting up a WebGPU build

1. *Edit > Project Settings > Player > Web > Other Settings*: turn off **Auto Graphics API** and put
   **WebGPU** in the list. Keep **WebGL 2** after it if you want a fallback for browsers without WebGPU.
2. Build as usual (*File > Build Profiles > Web*).

OpenGlyph leaves the Web graphics API list as you set it. On Unity 2023.1 and later its build processor
changes nothing (it used to turn off Auto Graphics API to remove WebGL 1, which no longer exists).

All package shaders compile for WebGPU and WebGL 2: the unified `UniText/Uber` (Texture2DArray atlas and
style-table fetches), `UniText/World/Uber`, and the legacy SDF, MSDF and bitmap shaders. The
`WebPlatformTests` editor tests compile every pass and keyword for both platforms.

## Differences from desktop and mobile

- **No system fonts.** A browser exposes no installed font files, so the system font fallback is off
  in Web players (`SystemFontFallback.IsSupportedOnThisPlatform == false`, as on UWP; one warning per
  session the first time a code point is uncovered). Add a font for each script
  you ship (for example Noto Sans Devanagari, a CJK font) to your `UniTextFontStack`. Characters with
  no font in the stack render as missing glyphs.
- **Emoji** come from the browser's own emoji font, drawn through a Canvas 2D (`BrowserEmoji.jslib`),
  not from a system emoji font file.
- **Single-threaded.** Web players have no worker threads: shaping, layout and glyph rendering run on
  the main thread (`UniTextWorkerPool.IsParallelSupported` is false). Output is the same as the parallel
  path.
- **No Blend2D.** COLRv1 vector emoji are not rasterised natively on the web; browser emoji are used.

## Native library notes

Unity's Web player links its own FreeType, libpng and zlib. To avoid duplicate symbols, every symbol in
`libunitext_native.a` other than the public `ut_*` entry points is renamed to `__ut_<name>`
(`Tools~/wasm_prefix_symbols.py`, run by the `webgl` job of `.github/workflows/native.yml`). The archive
is built with emscripten 3.1.39, the version Unity 6 links with; objects from a newer emscripten
reference `__wasm_setjmp`, which Unity's runtime does not provide. `WebNativeArchiveTests` checks both.
