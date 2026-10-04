# Phase 0 shipped native binaries — provenance

Every binary under `Plugins/` was built by OpenGlyph CI and committed as the
accepted baseline. Architecture and exported/defined `ut_*` symbol counts are
quoted from the per-platform **Verify arch + ut_\* symbol count** steps of the
source run.

- **Source CI run:** [36830158490](https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph/actions/runs/36830158490) (branch `openglyph/phase0-native`)
- **Windows static-CRT rebuild:** the three `Windows/` DLLs were rebuilt with the MSVC CRT linked statically (`CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded…`, CMP0091 NEW) by run [36878829990](https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph/actions/runs/36878829990) (branch `openglyph/static-crt`) and recommitted. They now import **no VC++ runtime** — `unitext_native.dll` imports only `KERNEL32.dll`; `unitext_native_editor.dll` only `KERNEL32.dll` + `COMDLG32.dll` (OS file-dialog) — so a Unity player loads them without the VC++ redistributable installed. CI gates this with `dumpbin /dependents` (fails on any `VCRUNTIME*`/`MSVCP*`/`api-ms-win-crt-*` import). The static-CRT change is build-only: the Windows parity gate (fresh vs committed), the msdfref gate and the xplat_parity gate (Linux/macOS vs the Windows dump) all stayed green, i.e. the rendered output is byte-identical to the previous Windows binary. Only `Size` and `SHA-256` change below (the CRT objects are now linked in).
- **Wave 1 rebuild (language-aware shaping):** every runtime library was rebuilt by run [37148984360](https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph/actions/runs/37148984360) (branch `openglyph/wave1-native`) to add one export, `ut_hb_buffer_set_language` (`hb_buffer_set_language` + `hb_language_from_string`), and recommitted from that run's `plugins-tree` artifact. All jobs passed, including the Windows parity gate, the msdfref gate and both xplat_parity gates (Linux/macOS vs the Windows dump), so glyph output is unchanged. The editor libraries and `iOS/.../Info.plist` were not touched (no change in them).
- **UWP (Universal Windows Platform, Unity `WSAPlayer`):** `WSA/x64/unitext_native.dll` and `WSA/ARM64/unitext_native.dll` were added from the `native-uwp-x64` / `native-uwp-ARM64` artifacts of run [37217510431](https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph/actions/runs/37217510431) (branch `openglyph/uwp`, commit `483d8b5`). Built with `CMAKE_SYSTEM_NAME=WindowsStore`, `CMAKE_SYSTEM_VERSION=10.0` (`WINAPI_FAMILY_APP`, `/APPCONTAINER`, linked against `WindowsApp.lib` only), Blend2D on without JIT and futex probe, no editor DLL.
  - **CRT:** dynamic app CRT (`/MD`): `VCRUNTIME140_APP.dll` (+ `VCRUNTIME140_1_APP.dll` on x64) and the UCRT `api-ms-win-crt-*` API sets. MSVC ships no static CRT for the app partition (`lib/<arch>/store` has only import libraries); the `_APP` CRT comes from the `Microsoft.VCLibs.140.00` framework package that Unity's generated UWP project declares. This is what the App Certification Kit accepts; the desktop static-CRT rule above does not apply to `WSA/`.
  - **Gates in the `uwp` job (all passed):** arch; DLL characteristics include `App Container`; 118 `ut_*` exports; dependents limited to `api-ms-win-*` / `ext-ms-win-*` / `vcruntime140(_1)_app` / `msvcp140*_app`; every OS import found in the App Certification Kit's `SupportedAPIs-x64.xml` (14,015 entries; the kit ships no ARM64 list, the API names are the same): **x64 85 / 85, ARM64 81 / 81** (plus 19 VCLibs framework imports each, not looked up); and the parity harness on the UWP x64 DLL vs the committed desktop x64 DLL: **90 passed, 0 failed** (SDF / hinted bitmap / variable-font / Blend2D raster byte-identical).
  - **Local re-check** (`dumpbin` 14.44, same kit list): identical results, and the 118 exported names are exactly those of `Windows/x86_64/unitext_native.dll`.
- **Symbol contract (verified in CI):** runtime libraries export **118** `ut_*` symbols (117 + `ut_hb_buffer_set_language`); editor libraries export **8**.
- Static archives (iOS device, iOS simulator, tvOS, WebGL) are **self-contained**: FreeType + HarfBuzz + libpng + zlib are merged into the archive (Apple via `libtool -static`, WebGL via `emar`), and each passes a CI link smoke test (compile a C file referencing `ut_ft_init`, `ut_hb_buffer_create`, `ut_ft_get_outline_data` and link it against ONLY the bundled archive).

| Path (under `Plugins/`) | Size (bytes) | SHA-256 | Architecture | `ut_*` count | Kind |
|---|---:|---|---|---:|---|
| `Windows/x86_64/unitext_native.dll` | 3709440 | `d51e18f36f1642e2fd185ffd36666ea19645594b92cdff235d27b09f2c0a563b` | x64 (8664 machine) | 118 | runtime |
| `Windows/x86_64/unitext_native_editor.dll` | 2864128 | `030c46ef37bc60be7742e6cfa50e1a493e335648d3b4d9c64d7de1bb79c4dfc2` | x64 (8664 machine) | 8 | editor |
| `Windows/ARM64/unitext_native.dll` | 2704896 | `89534d52b705416140bb9d94787cbeb25db9793368082004de05be6a1d15c756` | ARM64 (AA64 machine) | 118 | runtime |
| `WSA/x64/unitext_native.dll` | 2895360 | `db35c2a157e9fe2faf91cb21adcbe60c861cd2f5e6e160ef2c046f34d1029201` | x64 (8664 machine), App Container | 118 | runtime (UWP, dynamic app CRT) |
| `WSA/ARM64/unitext_native.dll` | 2540032 | `faace4d6c93dd41a9b24dca361bda84764f2b5dd73b6864b60d708019ce16a5b` | ARM64 (AA64 machine), App Container | 118 | runtime (UWP, dynamic app CRT) |
| `Linux/x86_64/libunitext_native.so` | 5534280 | `f3c139092d0beddb846b17b994fc58ee90c66c447831f89bce91412a1c465605` | ELF64 x86-64 (i386:x86-64) | 118 | runtime |
| `Linux/x86_64/libunitext_native_editor.so` | 4200880 | `b5668e2524603afa41eedcc963c96686c7429617e59880fc15c176053aaf1c8d` | ELF64 x86-64 (i386:x86-64) | 8 | editor |
| `Linux/ARM64/libunitext_native.so` | 4839872 | `7fd509c98149400b24f34105d53c7280111c5f6acf78443e1551a581b5d71db5` | ELF64 aarch64 | 118 | runtime |
| `macOS/libunitext_native.dylib` | 9319736 | `a6b3be7c95a187b81f35713c4c36cbd6f56d3471032533f9e2b28c48b3d50ba7` | Mach-O universal arm64 + x86_64 | 118 | runtime |
| `macOS/libunitext_native_editor.dylib` | 7395536 | `90533bc243f6ab5acd8513d9d49d92c54a0cb41fc13f8d332f513f734d266254` | Mach-O universal arm64 + x86_64 | 8 | editor |
| `Android/arm64-v8a/libunitext_native.so` | 38483768 | `20dcbe36ac62af5ffe678fd146349de34ebd575de4e6e741e1b2645ea4dc8aea` | ELF64 aarch64 (elf64-littleaarch64) | 118 | runtime |
| `Android/armeabi-v7a/libunitext_native.so` | 27887520 | `455c7e7df52f63c9ab6b81477bec46d077c39dbff8d4ccbc8923286e71f05434` | ELF32 arm (elf32-littlearm) | 118 | runtime |
| `Android/x86/libunitext_native.so` | 34608428 | `32606acbe603c8c6fa78fa714a93e79032f0c7831b26ad5100bb073da22f4713` | ELF32 i386 (elf32-i386) | 118 | runtime |
| `Android/x86_64/libunitext_native.so` | 39672504 | `e85227fbd48485f54c9ad88e9f88bdfd0ea097d19280fcf223f2699d4ec335a8` | ELF64 x86-64 (elf64-x86-64) | 118 | runtime |
| `iOS/libunitext_native.xcframework/ios-arm64/libunitext_native_device.a` | 3044568 | `810af77a7cb55d35c587daceeb935262757d042544024e15d68a44282edf8454` | Mach-O static archive, arm64 (iOS device) | 118 | runtime (self-contained: FreeType+HarfBuzz+libpng+zlib) |
| `iOS/libunitext_native.xcframework/ios-arm64_x86_64-simulator/libunitext_native_sim.a` | 6365512 | `3798b9789d75567cf8726518f0d3adfb73146107935d97132b6f170c693d19b5` | Mach-O static archive, fat arm64 + x86_64 (iOS simulator) | 118 | runtime (self-contained) |
| `tvOS/libunitext_native.a` | 3044568 | `cc27c18df525ab418d1c475ca05df0903434181424b549d5eacef35c633a6e2b` | Mach-O static archive, arm64 (tvOS) | 118 | runtime (self-contained) |
| `WebGL/libunitext_native.a` | 3177622 | `614cfde7c39927149eed8b12cb19b50b58ac3c37f6a8bba17f44e6b533076675` | WebAssembly (wasm) static archive, \0asm members | 118 | runtime (self-contained) |

## Verification evidence (quoted from the source run)
```
windows (x64):   arch: 8664 machine (x64)   exported runtime symbols: 117 / editor symbols: 8
windows (ARM64): arch: AA64 machine (ARM64) exported runtime symbols: 117
linux (x64):     architecture: i386:x86-64  exported symbols: 117 (runtime) / 8 (editor)
linux (ARM64):   architecture: aarch64      exported symbols: 117
macos:           Mach-O universal [x86_64][arm64]  exported symbols: 117 (runtime) / 8 (editor)
android arm64-v8a / armeabi-v7a / x86 / x86_64: exported ut_* symbols: 117 (each)
apple ios-device (arm64):            defined ut_* symbols: 117
apple ios-sim    (fat arm64 x86_64): defined ut_* symbols: 117
apple tvos       (arm64):            defined ut_* symbols: 117
webgl: member magic 0061736d (\0asm) — WebAssembly; link smoke test OK
```

UWP, run 37217510431:
```
uwp (x64):   arch: 8664 machine (x64)   OK: DLL characteristics include 'App Container'   exported runtime symbols: 118
             OK: only app CRT + api-ms-win-* apiset imports   imported functions: 104
             framework (VCLibs) imports, not looked up: 19   OK: all 85 OS imports are in the WACK supported-API list (SupportedAPIs-x64.xml)
             parity (UWP x64 DLL vs committed desktop x64 DLL): ===== RESULT: 90 passed, 0 failed =====
uwp (ARM64): arch: AA64 machine (ARM64) OK: DLL characteristics include 'App Container'   exported runtime symbols: 118
             OK: only app CRT + api-ms-win-* apiset imports   imported functions: 100
             framework (VCLibs) imports, not looked up: 19   OK: all 81 OS imports are in the WACK supported-API list (SupportedAPIs-x64.xml)
```
