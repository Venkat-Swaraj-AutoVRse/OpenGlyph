using System.Collections.Generic;
using LightSide;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace OpenGlyph.Localization.Tests
{
    /// <summary>
    /// LocalizeUniText: a LocalizedString drives UniText.Text, and the selected locale drives
    /// UniText.Language. Compiled only when com.unity.localization is installed.
    /// </summary>
    [TestFixture]
    public class LocalizeUniTextTests
    {
        private GameObject _canvas;
        private LocalizationSettings _savedSettings;
        private LocalizationSettings _settings;
        private readonly List<Object> _owned = new();

        [SetUp]
        public void SetUp()
        {
            _canvas = new GameObject("Canvas", typeof(Canvas));
            _savedSettings = LocalizationSettings.HasSettings ? LocalizationSettings.Instance : null;
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvas != null) Object.DestroyImmediate(_canvas);
            if (_settings != null)
            {
                LocalizationSettings.Instance = _savedSettings;
                _settings = null;
            }
            foreach (var o in _owned) if (o != null) Object.DestroyImmediate(o);
            _owned.Clear();
        }

        private (UniText text, LocalizeUniText loc) Make(bool active)
        {
            var go = new GameObject("t", typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(_canvas.transform, false);
            var t = go.AddComponent<UniText>();
            var l = go.AddComponent<LocalizeUniText>();
            if (active) go.SetActive(true);
            return (t, l);
        }

        [Test]
        public void LanguageOf_UsesTheLocaleCode()
        {
            Assert.AreEqual("ja-JP", LocalizeUniText.LanguageOf(Locale.CreateLocale("ja-JP")));
            Assert.AreEqual("zh-Hant", LocalizeUniText.LanguageOf(new LocaleIdentifier("zh-Hant")));
            Assert.AreEqual("", LocalizeUniText.LanguageOf((Locale)null));
        }

        [Test]
        public void ApplyString_SetsText_AndApplyLocale_SetsLanguage()
        {
            var (t, l) = Make(false);
            Assert.AreSame(t, l.Target, "defaults to the UniText on the same GameObject");

            l.ApplyString("こんにちは <b>世界</b>");
            Assert.AreEqual("こんにちは <b>世界</b>", t.Text);

            l.ApplyLocale(Locale.CreateLocale("ja-JP"));
            Assert.AreEqual("ja-JP", t.Language);

            l.SetLanguage = false;
            l.ApplyLocale(Locale.CreateLocale("ko-KR"));
            Assert.AreEqual("ja-JP", t.Language, "SetLanguage off: the language is left alone");
        }

        [Test]
        public void LocaleChange_UpdatesTextAndLanguage_ThroughLocalizationSettings()
        {
            // In-memory LocalizationSettings: two locales, one string table served by a table provider
            // (no Addressables content needed).
            _settings = ScriptableObject.CreateInstance<LocalizationSettings>();
            _owned.Add(_settings);
            var en = Locale.CreateLocale("en");
            var ja = Locale.CreateLocale("ja");
            _owned.Add(en);
            _owned.Add(ja);
            var locales = new LocalesProvider();
            locales.AddLocale(en);
            locales.AddLocale(ja);
            _settings.SetAvailableLocales(locales);

            var shared = ScriptableObject.CreateInstance<SharedTableData>();
            shared.TableCollectionName = TestTableProvider.Collection;
            _owned.Add(shared);
            var provider = new TestTableProvider();
            provider.Add(en, shared, "Greeting", "Hello", _owned);
            provider.Add(ja, shared, "Greeting", "こんにちは", _owned);
            var db = new LocalizedStringDatabase { TableProvider = provider };
            _settings.SetStringDatabase(db);
            LocalizationSettings.Instance = _settings;
            LocalizationSettings.SelectedLocale = en;

            var (t, l) = Make(false);
            l.StringReference = new LocalizedString(TestTableProvider.Collection, "Greeting");
            t.gameObject.SetActive(true); // OnEnable subscribes: the string resolves
            var op = l.StringReference.GetLocalizedStringAsync();
            op.WaitForCompletion();

            Assert.AreEqual("Hello", t.Text);
            Assert.AreEqual("en", t.Language);

            LocalizationSettings.SelectedLocale = ja;
            l.StringReference.GetLocalizedStringAsync().WaitForCompletion();
            l.StringReference.RefreshString();
            Assert.AreEqual("こんにちは", t.Text);
            Assert.AreEqual("ja", t.Language);
        }
    }

    /// <summary>Serves in-memory string tables (Localization's custom table provider hook).</summary>
    internal sealed class TestTableProvider : ITableProvider
    {
        public const string Collection = "W1Table";
        private readonly Dictionary<string, StringTable> tables = new();

        public void Add(Locale locale, SharedTableData shared, string key, string value, List<Object> owned)
        {
            if (!tables.TryGetValue(locale.Identifier.Code, out var table))
            {
                table = ScriptableObject.CreateInstance<StringTable>();
                table.SharedData = shared;
                table.LocaleIdentifier = locale.Identifier;
                tables[locale.Identifier.Code] = table;
                owned.Add(table);
            }
            table.AddEntry(key, value);
        }

        public AsyncOperationHandle<TTable> ProvideTableAsync<TTable>(string tableCollectionName, Locale locale)
            where TTable : LocalizationTable
        {
            tables.TryGetValue(locale.Identifier.Code, out var table);
            return Addressables.ResourceManager.CreateCompletedOperation(table as TTable, null);
        }
    }
}
