# Platforms

OpenGlyph's native layer (FreeType + HarfBuzz + libpng + zlib, plus Blend2D for COLRv1 color glyphs)
ships prebuilt for every platform below in [`Plugins/`](../Plugins/). Every runtime library exports the
same 118 `ut_*` functions; provenance, sizes and SHA-256 hashes are in
[`NativeSource~/tests/BINARIES.md`](../NativeSource~/tests/BINARIES.md).

| Platform (Unity target) | Plugin | Architectures | Notes |
|---|---|---|---|
| Windows (`StandaloneWindows64`, Editor) | `Windows/x86_64/`, `Windows/ARM64/` `unitext_native.dll` | x64, ARM64 | Static CRT (no VC++ redistributable needed). Editor tooling DLL on x64. |
| Universal Windows Platform (`WSAPlayer`) | `WSA/x64/`, `WSA/ARM64/` `unitext_native.dll` | x64, ARM64 (HoloLens 2, Windows on ARM) | IL2CPP. App-container build. No system font fallback and no system emoji font: bundle fonts. |
| macOS (`StandaloneOSX`, Editor) | `macOS/libunitext_native.dylib` | x64 + Apple Silicon (universal) | |
| Linux (`StandaloneLinux64`, Editor) | `Linux/x86_64/`, `Linux/ARM64/` | x64, ARM64 | |
| Android (incl. Meta Quest) | `Android/<abi>/libunitext_native.so` | ARMv7, ARM64, x86, x64 | Quest 3S verified on device. |
| iOS | `iOS/libunitext_native.xcframework` | ARM64 device, ARM64/x64 simulator | Static, linked as `__Internal`. Blend2D off. |
| tvOS | `tvOS/libunitext_native.a` | ARM64 | Static, `__Internal`. Blend2D off. |
| WebGL | `WebGL/libunitext_native.a` | wasm | Static, `__Internal`. Single-threaded; emoji drawn by the browser. |

## Universal Windows Platform (UWP)

**Binaries.** `Plugins/WSA/x64/unitext_native.dll` and `Plugins/WSA/ARM64/unitext_native.dll`, built by
the `uwp` job of `.github/workflows/native.yml` with `CMAKE_SYSTEM_NAME=WindowsStore`
(`WINAPI_FAMILY_APP`, `/APPCONTAINER`, linked against `WindowsApp.lib` only, so any API outside the
UWP API set fails the link). The C# side loads them as `unitext_native` (a DLL, like desktop Windows).

- **Import settings.** Each WSA DLL is enabled for `WSAPlayer` only, with its CPU (`X64` / `ARM64`),
  and is disabled for the Editor and every other platform. The desktop `Plugins/Windows/` DLLs are not
  enabled for `WSAPlayer`, so a UWP build gets exactly one `unitext_native.dll` per CPU. Editor tests:
  `UwpPluginImportTests`.
- **C runtime.** The UWP DLLs use the dynamic app CRT (`VCRUNTIME140_APP.dll` and the UCRT
  `api-ms-win-crt-*` API sets). MSVC has no static CRT for the app partition; the CRT comes from the
  `Microsoft.VCLibs.140.00` framework package, which Unity's generated UWP project already depends on.
  (Desktop Windows DLLs keep the static CRT.)
- **App Certification Kit.** CI checks that each DLL is marked App Container, imports only the app CRT
  and `api-ms-win-*` API sets, and that every imported OS function is in the Windows App Certification
  Kit's supported-API list (`SupportedAPIs-x64.xml`). FreeType uses its app-container file API
  (`CreateFile2`, `CreateFileMappingFromApp`), HarfBuzz its own UWP paths. Blend2D is built without
  its JIT (executable memory needs APIs outside the app set and the `codeGeneration` capability) and
  without its futex probe (`GetModuleHandleA`), so COLRv1 color glyphs use Blend2D's portable
  pipelines.
- **No system font fallback.** UWP apps run in an app container and cannot rely on reading installed
  font files, so `SystemFontFallback` is off (`SystemFontFallback.IsSupportedOnThisPlatform == false`).
  Code points no font in the stack covers render as missing glyphs, with one warning per session.
  **Bundle a font for every script the app shows** (the default stack covers Latin, Greek, Cyrillic,
  Arabic and Hebrew). There is no system emoji font either; bundle a color emoji font if you show emoji.
- **Threads, input.** The worker pool (`UniText.UseParallel`) uses ordinary managed threads, which
  IL2CPP supports on UWP. The input field uses `GUIUtility.systemCopyBuffer` for the clipboard and
  Unity's `TouchScreenKeyboard` for the on-screen keyboard where the device has one.
- **Building.** Switch to Universal Windows Platform (scripting backend IL2CPP), pick the architecture
  (x64 or ARM64) and build. Unity writes a Visual Studio solution; compiling, packaging and deploying it
  needs Visual Studio with the **Universal Windows Platform development** workload (and its C++ UWP
  tools). Verified so far: the Unity player build (IL2CPP code generation, solution generation, plugin
  selection) for x64 and ARM64. Not yet run on a device.
