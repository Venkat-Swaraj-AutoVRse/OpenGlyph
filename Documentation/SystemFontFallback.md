# System font fallback (any script)

The default font stack (Noto Sans, Arabic, Hebrew) covers Latin, Greek, Cyrillic, Arabic and Hebrew.
Text in any other script would render as missing-glyph boxes. Rather than bundle large fonts, OpenGlyph
falls back at runtime to a font installed on the operating system. This covers CJK, Tamil, Bengali,
Gujarati, Sinhala, Georgian, Armenian, Ethiopic, Tibetan and any other script.

**For consistent results across devices, bundle a font for each script you ship** and add it to your
`UniTextFontStack`. Installed fonts differ between devices and OS versions, and some devices have no
font for a given script. The fallback is a safety net, not a replacement for bundled fonts.

## Behaviour

- Applies only when **no font in the component's font stack (or its fallback stacks) covers the code
  point**. Fonts in your stack always win.
- **Warns once per script per session** (not per glyph or frame), naming the component and the font used:

  ```
  [OpenGlyph] No font in the font stack covers Tamil (U+0BA4 in "Title"); using system font "Nirmala.ttc". Add a font for this script to the font stack for consistent results across devices.
  ```

  If no installed font covers the script either, it warns once that it will render as missing glyphs:

  ```
  [OpenGlyph] No font in the font stack covers Tamil (U+0BA4 in "Title") and no installed system font covers it either; it will render as missing glyphs. Add a font for this script to the font stack.
  ```
- **Lazy:** text the stack fully covers never reads or scans anything. The first time a script is
  missing, the font is found and loaded **on the main thread** (in the prepare step before the first
  pass), then cached for the whole process. Worker threads in the parallel pipeline only read cached
  results.
- **Negative results are cached per script**, so a script with no system font is searched once, not
  every frame.
- Punctuation, digits and combining marks (Unicode script Common / Inherited) never start a search of
  their own. They use a fallback font that is already loaded if it has the glyph.
- **Memory:** the bytes of each loaded font file stay resident (for example about 20 MB for `msyh.ttc`
  and about 5 MB for `Nirmala.ttc`).

## Finding a font (non-CJK scripts)

The script of the code point comes from the package's UAX #24 data. Candidates are tried in this order,
and each one is checked by reading only its `cmap` table (every face of a `.ttc`) before it is loaded:

1. **Android / Quest:** families tagged with the script in `/system/etc/font_fallback.xml` and
   `/system/etc/fonts.xml` (for example `lang="und-Taml"`).
2. **Per-platform file-name hints:**

   | Platform | Examples |
   |---|---|
   | Windows (`%WINDIR%\Fonts`, per-user fonts) | Nirmala UI (`Nirmala.ttc`: Indic scripts, Sinhala, Ol Chiki), Ebrima (Ethiopic, N'Ko, Tifinagh, Vai, Adlam), Sylfaen (Georgian, Armenian), Microsoft Himalaya (Tibetan), Leelawadee UI (Thai, Lao, Khmer, Buginese), Myanmar Text, Gadugi (Cherokee, Canadian syllabics, Osage), Mongolian Baiti, Javanese Text, Microsoft Yi Baiti, Segoe UI Historic / Symbol |
   | macOS / iOS (`/System/Library/Fonts`, `Supplemental/`, `/Library/Fonts`) | `Tamil Sangam MN`, `Kohinoor*`, `Bangla Sangam MN`, `Gujarati Sangam MN`, `Sinhala Sangam MN`, `Kefa`, `Kailasa`, `Mshtakan`, `Thonburi`, `Arial Unicode` |
   | Android / Quest (`/system/fonts`, `/product/fonts`) | `fonts.xml` (above), then `NotoSans<Script>*` |
   | Linux (`/usr/share/fonts/**`, `~/.local/share/fonts`) | `NotoSans<Script>*`, `DejaVuSans` |

3. Files named `NotoSans<Script>*` / `NotoSerif<Script>*` in the font directories (regular weights first).
4. All other font files in those directories, up to 400 probed files per script. Only the table
   directory and `cmap` of each file are read.

WebGL has no file system and is not supported by this fallback.

## CJK

CJK code points keep their dedicated lookup in up to three groups (general, Japanese, Korean). Hangul
tries Korean first, and a group is read only when an earlier group does not cover the code point.

| Platform | General | Japanese | Korean |
|---|---|---|---|
| Windows | `Fonts\msyh.ttc`, `simsun.ttc` | `YuGothM.ttc`, `msgothic.ttc`, `meiryo.ttc` | `malgun.ttf`, `gulim.ttc` |
| macOS / iOS | `/System/Library/Fonts/PingFang.ttc`, `Hiragino Sans GB.ttc` | (general) | `AppleSDGothicNeo.ttc`, `AppleGothic.ttf` |
| Android / Quest | `/system/fonts/NotoSansCJK-Regular.ttc`, `NotoSansCJKsc-Regular.otf`, `DroidSansFallback.ttf` | (general) | (general) |
| Linux | `/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc` and the other usual Noto CJK / WenQuanYi locations | | |

For `NotoSansCJK*.ttc` (face order JP, KR, SC, TC, HK) the face is picked from
`SystemFontFallback.PreferredLanguage` (`"ja"`, `"ko"`, `"zh-Hant"`/`"zh-TW"`, `"zh-HK"`), defaulting to the
current UI culture, and **Simplified Chinese (face 2)** otherwise. Other collections use face 0. Han
characters are shared between languages, so only the glyph shape variant differs. CJK text logs the
same once-per-script warning (Han, Hiragana, Katakana, Hangul, ...).

## Disable

Untick **Use System Font Fallback** on the `UniTextSettings` asset, or in code:
`UniTextSettings.Instance` field `useSystemFontFallback = false`. With it off, no system font is read and
uncovered scripts render as missing-glyph boxes unless your own stack covers them.

## Bundle fonts instead

For identical output on every device: import a font for each script (for example Noto Sans Tamil, or
Noto Sans CJK or a subset), create a font asset (`Create > UniText > Font`, or
`UniTextFont.CreateFontAsset(bytes, faceIndex: n)` for a TTC), and add it to your `UniTextFontStack`.
A stack font always takes precedence, so the system fallback and its warning never fire for code points
it covers.
