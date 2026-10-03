# Phase 0 shipped native binaries — provenance

Every binary under `Plugins/` was built by OpenGlyph CI and committed as the
accepted baseline. Architecture and exported/defined `ut_*` symbol counts are
quoted from the per-platform **Verify arch + ut_\* symbol count** steps of the
source run.

- **Source CI run:** [36830158490](https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph/actions/runs/36830158490) (branch `openglyph/phase0-native`)
- **Windows static-CRT rebuild:** the three `Windows/` DLLs were rebuilt with the MSVC CRT linked statically (`CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded…`, CMP0091 NEW) by run [36878829990](https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph/actions/runs/36878829990) (branch `openglyph/static-crt`) and recommitted. They now import **no VC++ runtime** — `unitext_native.dll` imports only `KERNEL32.dll`; `unitext_native_editor.dll` only `KERNEL32.dll` + `COMDLG32.dll` (OS file-dialog) — so a Unity player loads them without the VC++ redistributable installed. CI gates this with `dumpbin /dependents` (fails on any `VCRUNTIME*`/`MSVCP*`/`api-ms-win-crt-*` import). The static-CRT change is build-only: the Windows parity gate (fresh vs committed), the msdfref gate and the xplat_parity gate (Linux/macOS vs the Windows dump) all stayed green, i.e. the rendered output is byte-identical to the previous Windows binary. Only `Size` and `SHA-256` change below (the CRT objects are now linked in).
- **Wave 1 rebuild (language-aware shaping):** every runtime library was rebuilt by run [37148984360](https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph/actions/runs/37148984360) (branch `openglyph/wave1-native`) to add one export, `ut_hb_buffer_set_language` (`hb_buffer_set_language` + `hb_language_from_string`), and recommitted from that run's `plugins-tree` artifact. All jobs passed, including the Windows parity gate, the msdfref gate and both xplat_parity gates (Linux/macOS vs the Windows dump), so glyph output is unchanged. The editor libraries and `iOS/.../Info.plist` were not touched (no change in them).
- **Symbol contract (verified in CI):** runtime libraries export **118** `ut_*` symbols (117 + `ut_hb_buffer_set_language`); editor libraries export **8**.
- Static archives (iOS device, iOS simulator, tvOS, WebGL) are **self-contained**: FreeType + HarfBuzz + libpng + zlib are merged into the archive (Apple via `libtool -static`, WebGL via `emar`), and each passes a CI link smoke test (compile a C file referencing `ut_ft_init`, `ut_hb_buffer_create`, `ut_ft_get_outline_data` and link it against ONLY the bundled archive).

| Path (under `Plugins/`) | Size (bytes) | SHA-256 | Architecture | `ut_*` count | Kind |
|---|---:|---|---|---:|---|
| `Windows/x86_64/unitext_native.dll` | 3709440 | `d51e18f36f1642e2fd185ffd36666ea19645594b92cdff235d27b09f2c0a563b` | x64 (8664 machine) | 118 | runtime |
| `Windows/x86_64/unitext_native_editor.dll` | 2864128 | `030c46ef37bc60be7742e6cfa50e1a493e335648d3b4d9c64d7de1bb79c4dfc2` | x64 (8664 machine) | 8 | editor |
| `Windows/ARM64/unitext_native.dll` | 2704896 | `89534d52b705416140bb9d94787cbeb25db9793368082004de05be6a1d15c756` | ARM64 (AA64 machine) | 118 | runtime |
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
