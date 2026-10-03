# System font fallback (CJK)

The default font stack (Noto Sans, Arabic, Hebrew) has no Chinese, Japanese or Korean font, so CJK
text would render as missing-glyph boxes. Rather than bundle a 15-20 MB font, OpenGlyph falls back at
runtime to the CJK font that ships with the operating system.

## Behaviour

- Applies only when **no font in the component's font stack covers the code point** and the code point
  is CJK (Han, kana, Hangul, CJK punctuation/symbols, full-width forms, Han extensions). Fonts in your
  stack always win.
- **Lazy:** nothing is read until the first such code point is needed. Then the font file is read and
  turned into a font asset **on the main thread** (the prepare step before the first pass), and cached
  for the whole process. Worker threads only ever use already-loaded fonts.
- **Graceful failure:** no file, or no permission, means missing-glyph boxes plus a single warning log.
- **Memory:** the font file bytes stay resident (e.g. about 20 MB for msyh.ttc). Disable the fallback or
  bundle a smaller subset font if that matters.
- Fonts are looked up in up to three groups (general, Japanese, Korean). Hangul tries Korean first; a
  group is read only when an earlier group does not cover the code point.

## Paths tried (first existing file per group)

| Platform | General | Japanese | Korean |
|---|---|---|---|
| Windows | `Fonts\msyh.ttc`, `simsun.ttc` | `YuGothM.ttc`, `msgothic.ttc`, `meiryo.ttc` | `malgun.ttf`, `gulim.ttc` |
| macOS / iOS | `/System/Library/Fonts/PingFang.ttc`, `Hiragino Sans GB.ttc` | (general) | `AppleSDGothicNeo.ttc`, `AppleGothic.ttf` |
| Android / Quest | `/system/fonts/NotoSansCJK-Regular.ttc`, `NotoSansCJKsc-Regular.otf`, `DroidSansFallback.ttf` | (general) | (general) |
| Linux | `/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc` and the other usual Noto CJK / WenQuanYi locations | | |

WebGL has no file system and is not supported by this fallback.

## TTC collections and language

For `NotoSansCJK*.ttc` (face order JP, KR, SC, TC, HK) the face is picked from
`SystemFontFallback.PreferredLanguage` (`"ja"`, `"ko"`, `"zh-Hant"`/`"zh-TW"`, `"zh-HK"`), defaulting to the
current UI culture, and **Simplified Chinese (face 2)** otherwise. Other collections use face 0. Han
characters are shared between languages, so only the glyph shape variant differs.

## Disable

Untick **Use System Font Fallback** on the `UniTextSettings` asset, or in code:
`UniTextSettings.Instance` field `useSystemFontFallback = false`. With it off, CJK is missing-glyph boxes
unless your own stack covers it.

## Bundle Noto CJK instead

For identical output on every device: import Noto Sans CJK (or a subset), create a font asset
(`Create > UniText > Font` / `UniTextFont.CreateFontAsset(bytes, faceIndex: n)` for a TTC), and add it to
your `UniTextFontStack`. A stack font always takes precedence, so the system fallback is never used for
code points it covers.
