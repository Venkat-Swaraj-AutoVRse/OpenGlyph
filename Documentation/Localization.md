# Unity Localization integration

`OpenGlyph.Localization` is an optional assembly that connects a `UniText` (or `GlyphMeshProUGUI`) to
the [Unity Localization](https://docs.unity3d.com/Packages/com.unity.localization@latest) package.

It **compiles only when `com.unity.localization` (1.0.0 or newer) is installed**: its asmdef defines
`OPENGLYPH_LOCALIZATION` through `versionDefines` and requires it in `defineConstraints`, so without the
package the assembly is skipped and adds nothing to your build.

## `LocalizeUniText`

Add **Component > OpenGlyph > Localize UniText** next to the UniText and pick the string (table + entry)
in **String Reference**.

- Whenever the string resolves — on enable and on every locale change — the UniText's `Text` is replaced.
  The string may contain markup (`<b>`, `<color>`, `<lang>` …) and Smart String arguments (set them on
  `StringReference` as with `LocalizeStringEvent`).
- With **Set Language** on (default), `UniText.Language` is set from the selected locale's code
  (`ja-JP`, `zh-Hant`, `sr-Cyrl` …), so the text gets that language's glyph forms and the matching CJK
  system font ([LanguageShaping.md](LanguageShaping.md)).

```csharp
using OpenGlyph.Localization;
using UnityEngine.Localization;

var loc = label.gameObject.AddComponent<LocalizeUniText>();
loc.StringReference = new LocalizedString("UI", "greeting");
loc.SetLanguage = true;
```

`ApplyString(string)` and `ApplyLocale(Locale)` are public, so you can also call them yourself (for example
from a `LocalizeStringEvent`'s *Update String* event). `LocalizeUniText.LanguageOf(locale)` returns the
tag used for a locale.

## Notes

- `LocalizeUniText` subscribes to `LocalizationSettings.SelectedLocaleChanged` only when a Localization
  Settings asset exists, so it never creates one.
- It is `[ExecuteAlways]`: it also updates the text in Edit Mode when the string resolves.

## Tests

`Tests/Editor/Localization/LocalizeUniTextTests.cs` (assembly `OpenGlyph.Localization.Tests.Editor`, compiled
only with the package). Run in the test project with `com.unity.localization` 1.5.13:

- `LocaleChange_UpdatesTextAndLanguage_ThroughLocalizationSettings`: an in-memory `LocalizationSettings` with
  locales `en`/`ja` and one string table (served by a custom `ITableProvider`); the component shows "Hello"
  with `Language = "en"`, then "こんにちは" with `Language = "ja"` after the locale changes.
- `ApplyString_SetsText_AndApplyLocale_SetsLanguage`, `LanguageOf_UsesTheLocaleCode`.

Without the package the main test assembly does not reference this one, and both Localization assemblies
are skipped by the compiler.
