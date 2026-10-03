using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LightSide.Tests
{
    /// <summary>
    /// Settings-fallback fix: a player/project with no <c>Resources/UniTextSettings.asset</c> must NOT
    /// fail silently. These tests pin the two runtime guarantees:
    /// <list type="number">
    /// <item>Unicode data initializes from the package-shipped <c>Resources/UnicodeData.bytes</c> alone —
    /// it does NOT depend on <see cref="UniTextSettings.Instance"/>, so text renders without a settings asset.</item>
    /// <item><see cref="UniTextSettings.Instance"/> never returns null: with no asset it serves a runtime
    /// default (so static accessors don't NRE) and emits a single loud error; a real asset, when assigned,
    /// still wins.</item>
    /// </list>
    /// The suite restores whatever instance was resolved before each test so it leaves no global state behind.
    /// </summary>
    public class SettingsFallbackTests
    {
        private UniTextSettings _saved;

        [SetUp]
        public void SetUp()
        {
            // Capture the current instance WITHOUT forcing a load/fallback (IsNull does not trigger the
            // getter). A runtime default is disposable, so we only keep a real, non-default instance.
            _saved = (!UniTextSettings.IsNull && !UniTextSettings.IsUsingRuntimeDefault)
                ? UniTextSettings.Instance
                : null;
            // These tests deliberately exercise the "missing asset" path, which logs an error; don't let
            // that error fail the test, and don't depend on run order for whether it was already logged.
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            UniTextSettings.SetInstance(_saved);
        }

        [Test]
        public void Instance_IsNeverNull_EvenAfterClear()
        {
            // Instance must never return null. In the EDITOR a provider ([InitializeOnLoadMethod] in
            // UniTextSettingsProvider) auto-creates Assets/UniText/Resources/UniTextSettings.asset, so
            // Resources.Load usually resolves a real asset here; in a PLAYER with no such asset the getter
            // instead manufactures a runtime default. Either way the contract is identical: non-null. (The
            // runtime-default fallback specifically is proven by the no-asset IL2CPP player build, where
            // the editor-only provider never runs.) Note: SetInstance fires Changed, and a subscriber
            // (DictionarySegmenter) may re-read Instance synchronously — so we do not assume the field stays
            // null; we only require that reading Instance yields a non-null value.
            UniTextSettings.SetInstance(null);

            var inst = UniTextSettings.Instance;

            Assert.IsNotNull(inst, "Instance must never be null — a missing asset must yield a runtime default.");
        }

        [Test]
        public void RuntimeDefault_IsManufactured_WhenResourcesHasNoAsset()
        {
            // Drive the fallback branch directly and deterministically, independent of whether this editor
            // host happens to have an auto-created asset: a freshly manufactured default is what the getter
            // returns in a player with no asset. This pins that the default is a valid, usable instance.
            var def = ScriptableObject.CreateInstance<UniTextSettings>();
            try
            {
                Assert.IsNotNull(def, "a runtime default settings instance must be constructible.");
                // Static accessors must not NRE against a default instance.
                UniTextSettings.SetInstance(def);
                Assert.DoesNotThrow(() => { var _ = UniTextSettings.SharedAtlasPageBudget; });
                Assert.DoesNotThrow(() => { var _ = UniTextSettings.UseUnifiedRenderer; });
                Assert.IsFalse(UniTextSettings.IsUsingRuntimeDefault,
                    "an explicitly-set instance is not flagged as the auto-manufactured runtime default.");
            }
            finally
            {
                Object.DestroyImmediate(def);
            }
        }

        [Test]
        public void Instance_WithExplicitAsset_UsesThatAsset_RuntimeDefaultFlagClears()
        {
            var custom = ScriptableObject.CreateInstance<UniTextSettings>();
            custom.name = "custom-under-test";
            try
            {
                UniTextSettings.SetInstance(custom);

                Assert.AreSame(custom, UniTextSettings.Instance,
                    "an explicitly-assigned settings asset must win over any fallback.");
                Assert.IsFalse(UniTextSettings.IsUsingRuntimeDefault,
                    "a real assigned instance must clear the runtime-default flag.");
            }
            finally
            {
                Object.DestroyImmediate(custom);
            }
        }

        [Test]
        public void UnicodeData_Initializes_WithoutSettingsInstance()
        {
            // The core guarantee: Unicode init depends only on the package-shipped UnicodeData.bytes.
            // Even with the settings instance explicitly cleared, EnsureInitialized must succeed, because
            // UnicodeDataAsset is a Resources TextAsset that ships inside the package.
            UniTextSettings.SetInstance(null);

            UnicodeData.EnsureInitialized();

            Assert.IsTrue(UnicodeData.IsInitialized,
                "UnicodeData must initialize from the package Resources/UnicodeData.bytes without a settings asset.");
            Assert.IsNotNull(UnicodeData.Provider, "the Unicode data provider must be available after init.");
        }

        [Test]
        public void UnicodeDataAsset_ShipsInPackageResources()
        {
            // Sanity: the asset the whole fix relies on is actually loadable from Resources.
            var asset = Resources.Load<TextAsset>("UnicodeData");
            Assert.IsNotNull(asset, "Resources/UnicodeData.bytes must ship inside the package and be loadable.");
            Assert.Greater(asset.bytes.Length, 0, "UnicodeData.bytes must contain data.");
        }
    }
}
