<div align="center">

<img src=".github/assets/logo/openglyph-mark.svg" alt="OpenGlyph logo" width="120">

# OpenGlyph

**HarfBuzz-shaped Unicode text for Unity — UI, world space and VR**

[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE.md)
[![Unity 2021.3+](https://img.shields.io/badge/Unity-2021.3%2B-black?logo=unity)](package.json)
[![Unicode 17.0](https://img.shields.io/badge/Unicode-17.0-blue)](Conformance~/README.md)
[![Conformance](https://img.shields.io/badge/conformance-1%2C042%2C587%20passed-brightgreen)](#unicode-conformance)

<img src=".github/assets/intro/openglyph_intro.gif" alt="OpenGlyph intro animation: multilingual text, rich-text styles and layout rendered in Unity" width="960">

[▶ Full-quality video (MP4)](.github/assets/intro/openglyph_intro.mp4)

</div>

![OpenGlyph rendering English, Vietnamese, Russian, Greek, Arabic, Hebrew, Hindi, Thai, Khmer, Myanmar, mixed bidirectional text and emoji](.github/assets/showcase/hero.png)

OpenGlyph is an open-source (MIT) text engine for Unity, forked from UniText 1.0. Text is shaped by
[HarfBuzz](https://harfbuzz.github.io/), laid out with Unicode-conformant BiDi and line breaking,
and rendered as SDF/MSDF through Unity's Canvas — including in VR.

## Key features

| | Feature | Details |
|---|---|---|
| 🌐 | **Every script via HarfBuzz** | Arabic, Hebrew, Devanagari and other complex scripts are shaped by HarfBuzz. Thai, Lao, Khmer and Myanmar get dictionary word breaking (ICU-matched). Any script your font stack does not cover (CJK, Tamil, Bengali, Georgian, Ethiopic, Tibetan, ...) falls back to an installed system font at runtime, with one warning per script; bundle fonts for consistent results across devices ([details](Documentation/SystemFontFallback.md)). |
| 🔄 | **Full UAX #9 BiDi** | Mixed LTR/RTL text with numbers and punctuation; 100 % of the Unicode 17.0 BiDi conformance data (861,948 cases). |
| 😀 | **Color emoji** | COLRv1 color glyphs; ZWJ sequences (e.g. 👨‍👩‍👧‍👦) are a single grapheme cluster. |
| 🏷️ | **Rich text & span styles** | `<b> <i> <u> <s> <color> <size> <gradient> <link> <cspace> <sup> <sub>` and more; per-span outline/underlay styles. |
| ✂️ | **Overflow modes** | Overflow, Truncate, Ellipsis (grapheme- and RTL-aware) and Clip; works with Mask and RectMask2D. |
| 🧱 | **Unified renderer + legacy** | Default unified path: one Uber shader with a shared atlas array — SDF text is one draw group per component, never more than two when MSDF/emoji are mixed. The legacy per-material path remains selectable. |
| 🥽 | **VR-ready** | All shaders declare single-pass-instanced stereo macros; verified on a Meta Quest 3S (OpenXR, Vulkan) in both eyes. |
| 🔁 | **GlyphMeshProUGUI** | A TextMesh Pro–style API on top of the OpenGlyph engine ([below](#drop-in-textmesh-pro-api--glyphmeshprougui)). |
| 🔍 | **MSDF** | Multi-channel SDF glyphs for sharp corners at large sizes. |
| 👾 | **Pixel fonts** | Pixel-grid detection and pixel-perfect rendering at integer scales. |
| ⚡ | **Parallel processing** | Shaping and layout run across worker threads, byte-identical to the serial path. |
| 🗜️ | **Font compression** | Font bytes stored compressed (Deflate by default) and decompressed transparently; byte-exact round trip. |

## Drop-in TextMesh Pro API — GlyphMeshProUGUI

![GlyphMeshProUGUI: TextMesh Pro-style C# code and the text it renders](.github/assets/showcase/glyphmeshpro.png)

`OpenGlyph.GlyphMeshProUGUI` is a Canvas text component whose members are named after
TextMesh Pro's, so existing TMP code usually migrates by changing the `using` and the type name:

```diff
- using TMPro;
+ using OpenGlyph;

- var label = gameObject.AddComponent<TextMeshProUGUI>();
+ var label = gameObject.AddComponent<GlyphMeshProUGUI>();
  label.text = "<b>Hello</b> world";
  label.fontSize = 48;
  label.fontStyle = FontStyles.Italic | FontStyles.Underline;
  label.alignment = TextAlignmentOptions.Center;
  label.color = Color.white;
  label.overflowMode = TextOverflowModes.Ellipsis;
```

Create one from **GameObject > UI > OpenGlyph > GlyphMeshPro - Text**.

**Implemented and applied by the engine** (verified in `Runtime/GlyphMeshPro/GlyphMeshProUGUI*.cs`):

- `text`, `SetText(string)`, `SetText(StringBuilder)`, `SetText(string, float…)` with `{0}`–`{7}` placeholders
- `font` (a `UniTextFont`), `fontSize`, `enableAutoSizing`, `fontSizeMin`, `fontSizeMax`, `fontWeight`
- `fontStyle`: Bold, Italic, Underline, Strikethrough, UpperCase, LowerCase, Superscript, Subscript
- `color` (inherited from `Graphic`), `alignment` (incl. Justified and Flush), `textWrappingMode`, `enableWordWrapping`
- `overflowMode`: Overflow, Ellipsis, Truncate, Masking
- `isRightToLeftText`, `preferredWidth`/`preferredHeight`, `GetPreferredValues(…)`, `GetRenderedValues()`, `ForceMeshUpdate()`, `textInfo` (character/line subset)
- Rich-text tags registered automatically: `<b> <i> <u> <s> <color> <size> <cspace> <line-height> <link> <uppercase> <lowercase> <sup> <sub> <voffset>`

**Not full parity.** These TMP members exist so code compiles, but their values are only stored
today: `characterSpacing`, `wordSpacing`, `lineSpacing`, `paragraphSpacing`, `margin`, `richText`,
`enableVertexGradient`/`colorGradient`, and `maxVisibleCharacters/Words/Lines` (which only affect
`textInfo` visibility). Not yet supported: `fontStyle` SmallCaps and Highlight; overflow ScrollRect,
Page and Linked (fall back to Overflow with a warning); tags such as `<mark> <nobr> <font> <align>
<indent> <sprite>`; and the world-space `GlyphMeshPro` (MeshRenderer) component, which is a stub.
TMP font assets are not used — fonts are `UniTextFont` assets. Full mapping:
[GlyphMeshPro-Parity.md](Documentation/GlyphMeshPro-Parity.md).

## Showcase

| Rich text | Layout |
|---|---|
| ![Gradients, outline, drop shadow, bold/italic/underline/strike, color, size, superscript, subscript, link and character spacing](.github/assets/showcase/styles.png) | ![Justified word wrap, auto-size, Truncate/Ellipsis/Clip overflow and right-aligned Arabic and Hebrew paragraphs](.github/assets/showcase/layout.png) |
| **SDF sharpness (OpenGlyph vs TextMesh Pro)** | **On device: Meta Quest 3S, both eyes** |
| ![The word Crisp at 12 to 200 px in OpenGlyph and TextMesh Pro](.github/assets/showcase/sharpness.png) | ![Stereo capture from a Quest 3S showing Latin, Vietnamese, Cyrillic, Greek, Arabic, Hebrew, Devanagari, Thai, CJK, emoji, Khmer and Myanmar rows](.github/assets/quest3s_stereo_scripts.png) |

In the Quest capture the left column is TextMesh Pro with its default font (no fallback assets), so
non-Latin rows show missing-glyph boxes; the other columns are OpenGlyph (legacy, unified) and
GlyphMeshProUGUI. CJK comes from the device's system font. More captures:
[`Documentation/Design/evidence/quest/`](Documentation/Design/evidence/quest/).

## Benchmarks

**3–9× faster than TextMesh Pro on Quest 3S.**

Meta Quest 3S, VR (OpenXR, single-pass instanced, Vulkan, IL2CPP ARM64), Unity 6000.3.19f1.
Workload: 100 objects × 2,405 characters (Latin + Arabic + Hebrew + mixed), 10 iterations after
3 warm-ups; each value is the median of 3 runs on main `2c71ef6`. Times in ms, lower is better.

| Operation | OpenGlyph parallel | OpenGlyph 1-thread | TextMesh Pro | UI Toolkit* | × faster than TMP |
|---|--:|--:|--:|--:|--:|
| Object creation | 168.2 | 307.4 | 610.0 | 952.1 | 3.63× |
| Full rebuild | 142.9 | 247.6 | 447.8 | 893.7 | 3.13× |
| Layout (wrap + auto-size) | 139.5 | 147.9 | 1289.3 | 270.6 | 9.24× |
| Mesh rebuild (colour) | 111.6 | 118.7 | 534.8 | 164.0 | 4.79× |

**Memory & GC**

| Creation | OpenGlyph parallel | OpenGlyph 1-thread | TextMesh Pro | UI Toolkit* |
|---|--:|--:|--:|--:|
| GC collections during creation | 1 | 2 | 30 | 0 |
| Allocated during creation (MB) | 427 | 427 | 552 | 67 |

Where OpenGlyph is not ahead: destruction takes 31.9 ms vs TextMesh Pro's 29.5 ms (0.93×) and UI
Toolkit's 2.1 ms, and UI Toolkit allocates far less during creation (67 MB vs 427 MB).

\* The harness validates that OpenGlyph and TextMesh Pro actually did the work each iteration;
it cannot verify this for UI Toolkit, so treat those numbers with caution.
Summary and raw data: [`Documentation/Benchmarks/quest3s/`](Documentation/Benchmarks/quest3s/SUMMARY_main_2c71ef6.md)
(`ua_91a39f0_vr_run{1,2,3}.json`; `91a39f0` is the PR head with package content identical to main `2c71ef6`).

**Font compression** (byte-exact round trip; [`font_compression.csv`](Documentation/Benchmarks/quest3s/font_compression.csv)):

| Font | Original (bytes) | Deflate (bytes) | Saved |
|---|--:|--:|--:|
| NotoSans | 629,024 | 296,411 | −52.9 % (Brotli: 229,001) |
| NotoSansArabic | 194,176 | 88,621 | −54.4 % |
| NotoSansHebrew | 48,036 | 27,650 | −42.4 % |

## Unicode conformance

Unicode 17.0.0 test data, 100 % pass, 0 exclusions. Runners: [`Tests/Editor/Conformance/`](Tests/Editor/Conformance/);
data and results: [`Conformance~/`](Conformance~/README.md).

| Standard | Name | Cases | Pass |
|---|---|--:|--:|
| UAX #9 | Bidirectional Algorithm (`BidiTest` 770,241 + `BidiCharacterTest` 91,707) | 861,948 | 100 % |
| UAX #14 | Line Breaking | 19,338 | 100 % |
| UAX #24 | Script property (`Scripts.txt`) | 159,866 | 100 % |
| UAX #24 | Script_Extensions | 669 | 100 % |
| UAX #29 | Grapheme Clusters | 766 | 100 % |
| | **Total** | **1,042,587** | **100 %** |

## Comparison

| Feature | OpenGlyph | TextMesh Pro | UI Toolkit |
|---|---|---|---|
| BiDi (UAX #9) | ✅ Full support | ❌ Requires plugin | 🟡 Backend-dependent |
| Arabic / Hebrew shaping | ✅ Full support | ❌ Requires plugin | 🟡 Backend-dependent |
| Devanagari conjuncts | ✅ Full support | ❌ Not supported | 🟡 Backend-dependent |
| Thai / Lao / Khmer / Myanmar word breaking | ✅ Dictionary-based | ❌ Not supported | ❌ Not supported |
| CJK system-font fallback | ✅ Automatic | 🟡 Manual fallback assets | — Not verified |
| Color emoji | ✅ COLRv1 | 🟡 Sprite sheets | ❌ Not supported |
| Per-span styling | ✅ Full support | ✅ Full support | 🟡 Limited |
| Overflow (Truncate / Ellipsis / Clip) | ✅ Full support | ✅ Full support | ✅ Via USS |
| VR single-pass instanced | ✅ Verified on Quest 3S | ✅ Observed on Quest 3S | — Not verified |
| Parallel processing | ✅ Multi-threaded | ❌ Main thread | ❌ Main thread |
| MSDF | ✅ Full support | ❌ Not supported | ❌ Not supported |
| Steady-state allocation | 🟡 Low, not zero | 🟡 Low (GC on full rebuild) | 🟡 Very low* |

Every OpenGlyph ✅ cites a committed test in [FeatureComparison.md](Documentation/FeatureComparison.md),
along with the sources for the TextMesh Pro and UI Toolkit cells.

## Installation

1. Open **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter:
   ```
   https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph.git
   ```

The package name is `com.openglyph.text` (version 1.0.0 in `package.json`). For reproducible builds,
pin a commit (or a tag, once published): `https://github.com/Venkat-Swaraj-AutoVRse/OpenGlyph.git#<commit-sha>`.

## Quick start

**UniText** (the core component, namespace `LightSide`). Without an explicit font stack it uses the
project defaults from `UniTextSettings`.

```csharp
using LightSide;
using UnityEngine;

public class Hello : MonoBehaviour
{
    void Start()
    {
        var t = gameObject.AddComponent<UniText>();   // needs a RectTransform under a Canvas
        t.FontSize = 36;
        t.Text = "Mixed: Hello עולם World — مرحبا 123";
        t.Overflow = TextOverflow.Ellipsis;            // Overflow | Truncate | Ellipsis | Clip
        t.UnifiedRenderer = UniText.UnifiedRendererMode.UseProjectSetting; // or ForceOn / ForceOff
    }
}
```

**GlyphMeshProUGUI** (TMP-style API, namespace `OpenGlyph`):

```csharp
using OpenGlyph;
using UnityEngine;

public class HelloTmpStyle : MonoBehaviour
{
    void Start()
    {
        var t = gameObject.AddComponent<GlyphMeshProUGUI>();
        t.text = "<color=#38BDF8>Same</color> API, <b>new</b> engine";
        t.fontSize = 72;
        t.fontStyle = FontStyles.Bold;
        t.alignment = TextAlignmentOptions.Center;
    }
}
```

Parallel processing is on by default (`UniText.UseParallel`).

## Supported platforms

Native plugins shipped in [`Plugins/`](Plugins/):

| Platform | Architectures |
|---|---|
| Windows | x64, ARM64 |
| macOS | x64, Apple Silicon (universal) |
| Linux | x64, ARM64 |
| Android (incl. Meta Quest) | ARMv7, ARM64, x86, x64 |
| iOS | ARM64 (device), ARM64/x64 simulator |
| tvOS | static library |
| WebGL | static library |

Meta Quest 3S (Android ARM64, IL2CPP, Vulkan, OpenXR) is verified on device. Other platforms ship
binaries but were not tested on device for this release.

## Documentation

- [Getting Started](Documentation/GettingStarted.md)
- [Feature comparison (with test citations)](Documentation/FeatureComparison.md)
- [GlyphMeshPro ↔ TextMesh Pro parity](Documentation/GlyphMeshPro-Parity.md)
- [Unified vs legacy render path](Documentation/RenderPathComparison.md)
- [System font fallback (any script)](Documentation/SystemFontFallback.md)
- [Render architecture](Documentation/Design/RenderArchitecture.md) · [Memory budgets](Documentation/Design/MemoryBudgets.md) · [Font families](Documentation/Design/Phase2-FontFamilies.md)
- [Changelog](CHANGELOG.md)

## Credits & License

OpenGlyph is released under the [MIT License](LICENSE.md).

OpenGlyph is a fork of UniText 1.0 by Light Side (MIT). It is not affiliated with or endorsed by
Light Side LLC; see [NOTICE.md](NOTICE.md).

<details>
<summary><b>Third-Party Software</b></summary>
<br>

See [Third-Party Notices.txt](Third-Party%20Notices.txt) for full license texts.

| Component | License |
|---|---|
| **HarfBuzz** | Old MIT License |
| **FreeType** | FreeType License (FTL) |
| **Blend2D** | Zlib License |
| **Zstandard** | BSD-3-Clause |
| **zlib** | Zlib License |
| **libpng** | PNG Reference Library License v2 |
| **msdfgen** (algorithms ported to C#) | MIT License |
| **ICU word segmentation dictionaries** (Thai, Lao, Khmer, Myanmar) | Unicode License V3 |

Fonts (Noto Sans, Noto Sans Arabic, Noto Sans Hebrew and the test fixtures) are licensed under the
[SIL Open Font License 1.1](https://openfontlicense.org). Unicode data and conformance test files
are licensed under the [Unicode License V3](https://www.unicode.org/license.txt).

</details>
