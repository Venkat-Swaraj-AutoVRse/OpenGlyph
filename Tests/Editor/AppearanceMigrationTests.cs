using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 3: the editor migration tool. A legacy outline material on
    /// an appearance converts to a component <see cref="UniTextStyle"/> whose values equal the material
    /// values (via the shim mapping); a second run changes nothing (idempotent); Undo restores; and a
    /// temp PREFAB round-trips through the asset-walking <see cref="AppearanceMigration.Migrate"/>.
    /// </summary>
    public class AppearanceMigrationTests
    {
        private const string TempDir = "Assets/__UniTextMigrationTests";
        private Material _outlineMat;
        private UniTextAppearance _app;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempDir))
                AssetDatabase.CreateFolder("Assets", "__UniTextMigrationTests");

            var sh = Shader.Find("UniText/SDF SSD") ?? Shader.Find("UniText/SDF") ?? Shader.Find("UI/Default");
            _outlineMat = new Material(sh) { name = "LegacyOutline" };
            if (_outlineMat.HasProperty("_OutlineColor")) _outlineMat.SetColor("_OutlineColor", new Color(1f, 0f, 0f, 1f));
            if (_outlineMat.HasProperty("_OutlineWidth")) _outlineMat.SetFloat("_OutlineWidth", 0.3f);
            if (_outlineMat.HasProperty("_FaceColor")) _outlineMat.SetColor("_FaceColor", new Color(0f, 0.5f, 1f, 1f));

            _app = ScriptableObject.CreateInstance<UniTextAppearance>();
            _app.SetDefaultMaterials(_outlineMat);
        }

        [TearDown]
        public void TearDown()
        {
            if (_outlineMat != null) Object.DestroyImmediate(_outlineMat);
            if (_app != null) Object.DestroyImmediate(_app);
            if (AssetDatabase.IsValidFolder(TempDir))
            {
                AssetDatabase.DeleteAsset(TempDir);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void MigrateComponent_StyleEqualsMaterialValues()
        {
            var go = new GameObject("ut", typeof(RectTransform));
            try
            {
                var t = go.AddComponent<UniText>();
                t.Appearance = _app;

                // Expected = exactly what the shim synthesises from the same material (the faithful map).
                var expected = AppearanceStyleShim.StyleFromMaterials(new[] { _outlineMat });

                Assert.IsTrue(AppearanceMigration.TryMigrateComponent(t, AppearanceMigration.Options.Default, out _));
                Assert.IsTrue(t.OverrideStyle, "component now uses its own style");
                Assert.AreEqual(UniText.UnifiedRendererMode.ForceOn, t.UnifiedRenderer, "switched to unified");

                Assert.AreEqual(expected, t.Style.ToGlyphStyle(), "migrated style equals the material values");
                if (_outlineMat.HasProperty("_OutlineColor"))
                    Assert.AreEqual(new Color(1f, 0f, 0f, 1f), t.Style.outlineColor, "outline colour carried");
                if (_outlineMat.HasProperty("_OutlineWidth"))
                    Assert.AreEqual(0.3f, t.Style.outlineWidth, 1e-5f, "outline width carried");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void SecondRun_IsNoOp_Idempotent()
        {
            var go = new GameObject("ut", typeof(RectTransform));
            try
            {
                var t = go.AddComponent<UniText>();
                t.Appearance = _app;

                Assert.IsTrue(AppearanceMigration.TryMigrateComponent(t, AppearanceMigration.Options.Default, out _));
                var afterFirst = t.Style.ToGlyphStyle();

                // Second run must change nothing and report "already migrated".
                Assert.IsFalse(AppearanceMigration.TryMigrateComponent(t, AppearanceMigration.Options.Default, out var reason));
                Assert.AreEqual("already migrated", reason);
                Assert.AreEqual(afterFirst, t.Style.ToGlyphStyle(), "idempotent: style unchanged on a second run");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Undo_RestoresPreMigrationState()
        {
            var go = new GameObject("ut", typeof(RectTransform));
            try
            {
                var t = go.AddComponent<UniText>();
                t.Appearance = _app;
                Assert.IsFalse(t.OverrideStyle);

                Undo.RecordObject(t, "Migrate UniText Appearance to Style");
                AppearanceMigration.TryMigrateComponent(t, AppearanceMigration.Options.Default, out _);
                Assert.IsTrue(t.OverrideStyle);

                Undo.PerformUndo();
                Assert.IsFalse(t.OverrideStyle, "Undo restores the pre-migration (shim-fallback) state");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Prefab_RoundTrips_AndSecondMigrateIsNoOp()
        {
            // Build a prefab with a UniText referencing the legacy outline appearance.
            var src = new GameObject("MigGO", typeof(RectTransform));
            var t = src.AddComponent<UniText>();
            t.Appearance = _app;
            string path = TempDir + "/Mig.prefab";
            PrefabUtility.SaveAsPrefabAsset(src, path);
            Object.DestroyImmediate(src);

            // Apply migration across prefabs (scenes off).
            var report1 = AppearanceMigration.Migrate(AppearanceMigration.Options.Default, includeOpenScenes: false);
            Assert.GreaterOrEqual(report1.migrated, 1, "at least our prefab's component migrated");

            // Reload the prefab and confirm the component is now on a style + unified.
            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var rt = reloaded.GetComponentInChildren<UniText>(true);
            Assert.IsTrue(rt.OverrideStyle, "prefab component persisted the migrated style");
            Assert.AreEqual(UniText.UnifiedRendererMode.ForceOn, rt.UnifiedRenderer);

            // Second migrate pass is a no-op for this prefab (idempotent at the asset level).
            var before = rt.Style.ToGlyphStyle();
            var report2 = AppearanceMigration.Migrate(AppearanceMigration.Options.Default, includeOpenScenes: false);
            var reloaded2 = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var rt2 = reloaded2.GetComponentInChildren<UniText>(true);
            Assert.IsTrue(rt2.OverrideStyle);
            Assert.AreEqual(before, rt2.Style.ToGlyphStyle(), "second run leaves the already-migrated prefab unchanged");
        }

        [Test]
        public void DryRun_DoesNotMutate()
        {
            var go = new GameObject("ut", typeof(RectTransform));
            try
            {
                var t = go.AddComponent<UniText>();
                t.Appearance = _app;
                var report = AppearanceMigration.DryRun(includeOpenScenes: false);
                // Dry run must not have changed the component.
                Assert.IsFalse(t.OverrideStyle, "dry run never mutates components");
                Assert.NotNull(report);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
