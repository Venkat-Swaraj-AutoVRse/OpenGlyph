using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>The unified renderer is the project default; ForceOff still opts a component out.</summary>
    public class UnifiedRendererDefaultTests
    {
        [Test]
        public void RuntimeDefaultInstance_UsesUnifiedRenderer()
        {
            var def = ScriptableObject.CreateInstance<UniTextSettings>();
            var saved = UniTextSettings.IsNull ? null : UniTextSettings.Instance;
            try
            {
                UniTextSettings.SetInstance(def);
                Assert.IsTrue(UniTextSettings.UseUnifiedRenderer, "a freshly created settings instance must default to the unified renderer.");
            }
            finally
            {
                UniTextSettings.SetInstance(saved);
                Object.DestroyImmediate(def);
            }
        }

        [Test]
        public void ShippedSettingsAsset_UsesUnifiedRenderer()
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniTextSettings>("Packages/com.openglyph.text/Defaults/UniTextSettings.asset");
            Assert.IsNotNull(asset, "shipped Defaults/UniTextSettings.asset must load.");
            var so = new SerializedObject(asset);
            var p = so.FindProperty("useUnifiedRenderer");
            Assert.IsNotNull(p);
            Assert.IsTrue(p.boolValue, "shipped asset must serialize useUnifiedRenderer = true.");
        }

        [Test]
        public void Component_ForceOff_OptsOut_EvenWhenProjectDefaultIsOn()
        {
            var def = ScriptableObject.CreateInstance<UniTextSettings>();
            var saved = UniTextSettings.IsNull ? null : UniTextSettings.Instance;
            var go = new GameObject("t", typeof(RectTransform));
            try
            {
                UniTextSettings.SetInstance(def);
                var t = go.AddComponent<UniText>();
                Assert.IsTrue(t.UseUnifiedRenderer, "UseProjectSetting follows the (now ON) default.");
                t.UnifiedRenderer = UniText.UnifiedRendererMode.ForceOff;
                Assert.IsFalse(t.UseUnifiedRenderer);
            }
            finally
            {
                Object.DestroyImmediate(go);
                UniTextSettings.SetInstance(saved);
                Object.DestroyImmediate(def);
            }
        }
    }
}
