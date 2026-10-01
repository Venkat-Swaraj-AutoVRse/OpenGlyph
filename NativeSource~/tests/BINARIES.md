# Phase 0 shipped native binaries — provenance

Every binary under `Plugins/` was built by OpenGlyph CI and committed as the
accepted baseline. Architecture and exported/defined `ut_*` symbol counts are
quoted from the per-platform **Verify arch + ut_\* symbol count** steps of the
source run.

- **Source CI run:** [36830158490](https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph/actions/runs/36830158490) (branch `openglyph/phase0-native`)
- **Symbol contract (verified in CI):** runtime libraries export **117** `ut_*` symbols; editor libraries export **8**.
- Static archives (iOS device, iOS simulator, tvOS, WebGL) are **self-contained**: FreeType + HarfBuzz + libpng + zlib are merged into the archive (Apple via `libtool -static`, WebGL via `emar`), and each passes a CI link smoke test (compile a C file referencing `ut_ft_init`, `ut_hb_buffer_create`, `ut_ft_get_outline_data` and link it against ONLY the bundled archive).

| Path (under `Plugins/`) | Size (bytes) | SHA-256 | Architecture | `ut_*` count | Kind |
|---|---:|---|---|---:|---|
| `Windows/x86_64/unitext_native.dll` | 3499520 | `1468ea5ff69eb6d3aabf721df1a708e825cbeba0cd5cdf36d1bacd0964ff066f` | x64 (8664 machine) | 117 | runtime |
| `Windows/x86_64/unitext_native_editor.dll` | 2674688 | `72544fb3e556c9942565297783820367262bd51d4b4b5110cc45accee2a7c45a` | x64 (8664 machine) | 8 | editor |
| `Windows/ARM64/unitext_native.dll` | 2523648 | `d2a6fd708308f1de5d5599ebcd38d479cb38518b99612213adf487d866f1b68e` | ARM64 (AA64 machine) | 117 | runtime |
| `Linux/x86_64/libunitext_native.so` | 5534248 | `72cef2d54e96309e5193cc2e3dcb712ac8a25e63af1a7023b00b99fe8faad3d0` | ELF64 x86-64 (i386:x86-64) | 117 | runtime |
| `Linux/x86_64/libunitext_native_editor.so` | 4200880 | `b5668e2524603afa41eedcc963c96686c7429617e59880fc15c176053aaf1c8d` | ELF64 x86-64 (i386:x86-64) | 8 | editor |
| `Linux/ARM64/libunitext_native.so` | 4852200 | `aec34a9a8e4bb9878b1fc52d13e7048802b4b72ebfe18d10a057e2db8ecebb4b` | ELF64 aarch64 | 117 | runtime |
| `macOS/libunitext_native.dylib` | 9319672 | `589820fc90d111049d2edb53941e951c66ac5a7ad0209b236e278cf94ed3dd62` | Mach-O universal arm64 + x86_64 | 117 | runtime |
| `macOS/libunitext_native_editor.dylib` | 7395536 | `90533bc243f6ab5acd8513d9d49d92c54a0cb41fc13f8d332f513f734d266254` | Mach-O universal arm64 + x86_64 | 8 | editor |
| `Android/arm64-v8a/libunitext_native.so` | 38482336 | `3b3cfdbd5e42f17aea213499dd98c20c59ebee144b7014a56b5e05de502fd764` | ELF64 aarch64 (elf64-littleaarch64) | 117 | runtime |
| `Android/armeabi-v7a/libunitext_native.so` | 27886644 | `df58d7026b5dd1a8f54be6fabf8ffd58a984b836489dc552d1b63e847c5aa3ef` | ELF32 arm (elf32-littlearm) | 117 | runtime |
| `Android/x86/libunitext_native.so` | 34607448 | `43e2b17372fb628e9cc7b9efd45cb3b1bcd3b22bdce48bbf075c5a9b974ebbaf` | ELF32 i386 (elf32-i386) | 117 | runtime |
| `Android/x86_64/libunitext_native.so` | 39671264 | `89c8f0f1f63750b6be943c6d56ebf8fc078bacf59dfbfd371c287a2e307dbb2b` | ELF64 x86-64 (elf64-x86-64) | 117 | runtime |
| `iOS/libunitext_native.xcframework/ios-arm64/libunitext_native_device.a` | 3044264 | `5dd9ae621c250dd6b479dbfcdf755df1d694a1c66933cc19b58955d1de5734e0` | Mach-O static archive, arm64 (iOS device) | 117 | runtime (self-contained: FreeType+HarfBuzz+libpng+zlib) |
| `iOS/libunitext_native.xcframework/ios-arm64_x86_64-simulator/libunitext_native_sim.a` | 12729544 | `0e5cb6d78d93eb08c728f8c42d646fcf5915b780e6a82da09738d5f6fba80145` | Mach-O static archive, fat arm64 + x86_64 (iOS simulator) | 117 | runtime (self-contained) |
| `tvOS/libunitext_native.a` | 3044264 | `c1d4b892fba92e250f02e2c910e1f7c5c77e2b7f8e7769ada71c461de19d12ae` | Mach-O static archive, arm64 (tvOS) | 117 | runtime (self-contained) |
| `WebGL/libunitext_native.a` | 3177428 | `0c4659db7ab9adf6678904adead4603b515ddbf78b44f1ae72aac020b7a32558` | WebAssembly (wasm) static archive, \0asm members | 117 | runtime (self-contained) |

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
