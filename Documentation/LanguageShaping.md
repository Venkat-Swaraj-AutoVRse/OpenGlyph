# Language-aware shaping

The same character can have different correct shapes in different languages: Serbian and Macedonian
write the Cyrillic б differently from Russian, Romanian uses a comma below s (ș) where Turkish uses a
cedilla, and Japanese, Simplified Chinese, Traditional Chinese and Korean draw many shared Han
characters differently. Fonts carry these forms as OpenType *language systems* (mostly the `locl`
feature). OpenGlyph passes the text's language to HarfBuzz so the font picks them, and uses it to pick
the right CJK system font.

![Language-aware shaping: Cyrillic б in Russian vs Serbian, ş in Turkish vs Romanian, a lang span, and Han characters with no language (Microsoft YaHei) vs ja (Yu Gothic) and zh-Hant (Microsoft JhengHei)](../.github/assets/features/language-shaping.png)

## Component: `Language`

A BCP 47 tag for the whole text (Inspector: **Typography > Language (BCP 47)**). Empty = unset, which
is exactly the previous behaviour.

```csharp
title.Language = "sr";       // Serbian forms in a font that has them
label.Language = "ja";       // Japanese forms; Japanese system font for CJK the stack does not cover
label.Language = "zh-Hant";  // Traditional Chinese (Taiwan)
label.Language = "";         // unset
```

Setting it reshapes the text on the next rebuild. `-` or `_` separators are accepted.

## Span tag: `<lang=…>`

Register **`LanguageParseRule` + `LanguageModifier`**:

```csharp
uniText.RegisterModifier(new ModRegister { Rule = new LanguageParseRule(), Modifier = new LanguageModifier() });
uniText.Text = "Русский б, <lang=sr>српски б</lang>; 直 <lang=ja>直</lang>";
```

A span overrides the component language on its range (`<lang=en>` inside a `ja` component gives the
language-less forms back). Nested spans: the innermost wins. A language change splits the text into
separate shaping runs.

## What the language changes

1. **HarfBuzz shaping.** The language is set on the HarfBuzz buffer (`hb_buffer_set_language`), so the
   font's language system for that language is used: `locl` and any other language-specific lookups.
2. **CJK system font face.** When no font in the component's stack covers a CJK character, the system
   fallback ([SystemFontFallback.md](SystemFontFallback.md)) picks the face for the language, per
   component and per span:

   | Language | Windows | Noto Sans CJK collection (Android, Linux) |
   |---|---|---|
   | `ja` | Yu Gothic (`YuGothM.ttc`), then MS Gothic, Meiryo | face 0 (JP) |
   | `ko` | Malgun Gothic (`malgun.ttf`), Gulim | face 1 (KR) |
   | `zh`, `zh-Hans`, `zh-CN`, `zh-SG` | Microsoft YaHei (`msyh.ttc`), SimSun | face 2 (SC) |
   | `zh-Hant`, `zh-TW`, `zh-MO` | Microsoft JhengHei (`msjh.ttc`), MingLiU | face 3 (TC) |
   | `zh-HK`, `yue` | Microsoft JhengHei (no Hong Kong face ships with Windows) | face 4 (HK) |
   | unset or non-CJK | previous order: `msyh.ttc`, then Japanese, then Korean | `SystemFontFallback.PreferredLanguage`, else the UI culture |

   If the chosen face lacks a character, the other CJK faces are tried. Fonts in your stack always win.
   Each component keeps its own answer, so a Japanese label and a Chinese label in the same scene get
   different faces.

## Notes and limitations

- **Fonts without the forms:** a single-language font (Yu Gothic, Microsoft YaHei) has no other
  language's forms, so `Language` changes nothing in it — only the face choice above does. Pan-CJK
  fonts (Noto Sans CJK / Source Han Sans) do have them.
- The script still comes from the text (Unicode script property); the language only selects the
  language system inside that script.
- **Native library:** language shaping needs the `ut_hb_buffer_set_language` export, which ships with this
  version for every platform. With an older native library the language is ignored for shaping (the
  CJK face choice still works); `Shaper.SupportsLanguage` reports which.
- Unity Localization users can drive `Language` from the selected locale with `LocalizeUniText`
  ([Localization.md](Localization.md)).

## Tests

`Tests/Editor/Wave1LanguageTests.cs` (and `Wave1FeatureTests` for the parallel path):

- `Language_SelectsLoclForms_ComponentAndSpan`: Noto Sans `б` (U+0431) is glyph 457 by default and 2092 with
  `sr` (also `mk`); `ş` (U+015F) is glyph 288 by default and 329 with `ro`. A `<lang=sr>` span changes only
  its own character, `<lang=en>` inside an `sr` component restores the default form, and the setter
  reshapes.
- `Language_PicksCjkSystemFace_PerComponentAndPerSpan` (Windows): 直 with no language → `msyh.ttc`, `ja` →
  `YuGothM.ttc`, `zh-Hans` → `msyh.ttc`, `zh-Hant` → `msjh.ttc`, `ko-KR` → `malgun.ttf`; spans in one
  component pick per span; two components with different languages never share a cached face.
- `Language_PanCjkFont_ShapesJapaneseAndChineseForms`: in Noto Sans CJK JP (`NotoSansCJKjp-Regular.otf`) 直 骨 今 shape to glyphs 27873 / 45132 / 9770 with `ja` and 27874 / 45133 / 9771 with `zh-Hans`. The test uses an installed Noto Sans CJK / Source Han Sans, or the file in `OPENGLYPH_PANCJK_FONT`; it is skipped without one. The Windows CJK fonts (YaHei, Yu Gothic, JhengHei) give the same glyph for both languages (no locl forms), which the test logs.
- `LanguageTags_ClassifyCjk`, `FeaturesAndLanguage_ParallelPath_MatchesSerial`.
