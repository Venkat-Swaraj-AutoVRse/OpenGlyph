// Optional Unity Localization integration. This assembly only compiles when the com.unity.localization
// package (1.0.0 or newer) is installed: OpenGlyph.Localization.asmdef sets OPENGLYPH_LOCALIZATION through
// versionDefines and lists it as a define constraint, so without the package it compiles to nothing.

using System;
using LightSide;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

namespace OpenGlyph.Localization
{
    /// <summary>
    /// Drives a <see cref="UniText"/> from a <see cref="LocalizedString"/>: whenever the string resolves
    /// (on enable and on every locale change) the component's <see cref="UniText.Text"/> is replaced, and,
    /// when <see cref="SetLanguage"/> is on, <see cref="UniText.Language"/> is set from the selected locale's
    /// code (e.g. <c>ja-JP</c>, <c>zh-Hant</c>), so the text gets language-specific shaping and the right
    /// CJK system font face. The text may contain markup; smart-string arguments work as in
    /// <c>LocalizeStringEvent</c> (set them on <see cref="StringReference"/>).
    /// </summary>
    [AddComponentMenu("OpenGlyph/Localize UniText")]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class LocalizeUniText : MonoBehaviour
    {
        [SerializeField, Tooltip("The localized string shown by the target UniText.")]
        private LocalizedString stringReference = new();

        [SerializeField, Tooltip("The UniText to update. Defaults to the UniText on this GameObject.")]
        private UniText target;

        [SerializeField, Tooltip("Set UniText.Language from the selected locale's code on every locale change.")]
        private bool setLanguage = true;

        private bool subscribedLocale;

        /// <summary>The localized string shown by <see cref="Target"/>.</summary>
        public LocalizedString StringReference
        {
            get => stringReference;
            set
            {
                if (ReferenceEquals(stringReference, value)) return;
                if (isActiveAndEnabled) Unsubscribe();
                stringReference = value;
                if (isActiveAndEnabled) Subscribe();
            }
        }

        /// <summary>The UniText to update; defaults to the one on this GameObject.</summary>
        public UniText Target
        {
            get => target != null ? target : (target = GetComponent<UniText>());
            set => target = value;
        }

        /// <summary>When true, <see cref="UniText.Language"/> follows the selected locale.</summary>
        public bool SetLanguage
        {
            get => setLanguage;
            set => setLanguage = value;
        }

        private void OnEnable() => Subscribe();

        private void OnDisable() => Unsubscribe();

        private void Subscribe()
        {
            if (stringReference != null) stringReference.StringChanged += ApplyString;
            if (!subscribedLocale && LocalizationSettings.HasSettings)
            {
                LocalizationSettings.SelectedLocaleChanged += ApplyLocale;
                subscribedLocale = true;
            }
        }

        private void Unsubscribe()
        {
            if (stringReference != null) stringReference.StringChanged -= ApplyString;
            if (subscribedLocale)
            {
                LocalizationSettings.SelectedLocaleChanged -= ApplyLocale;
                subscribedLocale = false;
            }
        }

        /// <summary>
        /// Shows <paramref name="value"/> on the target and, when <see cref="SetLanguage"/> is on, applies the
        /// currently selected locale's language. Called by the <see cref="LocalizedString"/>.
        /// </summary>
        public void ApplyString(string value)
        {
            var t = Target;
            if (t == null) return;
            if (setLanguage && LocalizationSettings.HasSettings)
            {
                var locale = SelectedLocaleOrNull();
                if (locale != null) t.Language = LanguageOf(locale);
            }
            t.Text = value ?? string.Empty;
        }

        /// <summary>Sets the target's language from <paramref name="locale"/> (when <see cref="SetLanguage"/> is on).</summary>
        public void ApplyLocale(Locale locale)
        {
            if (!setLanguage || locale == null) return;
            var t = Target;
            if (t != null) t.Language = LanguageOf(locale);
        }

        /// <summary>The BCP 47 tag used for a locale: its identifier code (e.g. "ja-JP", "zh-Hant", "sr-Cyrl").</summary>
        public static string LanguageOf(Locale locale) => locale == null ? string.Empty : LanguageOf(locale.Identifier);

        /// <summary>The BCP 47 tag used for a locale identifier (its code, '_' mapped to '-').</summary>
        public static string LanguageOf(LocaleIdentifier identifier) =>
            LanguageTags.Normalize(identifier.Code) ?? string.Empty;

        private static Locale SelectedLocaleOrNull()
        {
            try
            {
                var op = LocalizationSettings.SelectedLocaleAsync;
                return op.IsDone ? op.Result : null;
            }
            catch (Exception)
            {
                return null; // no settings / not initialised yet: keep the current language
            }
        }
    }
}
