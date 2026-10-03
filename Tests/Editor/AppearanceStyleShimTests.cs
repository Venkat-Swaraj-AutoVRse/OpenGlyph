using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 3: the compatibility shim's property mapping. A material
    /// carrying the legacy TMP-style face/outline/underlay/glow properties must synthesise the
    /// equivalent <see cref="GlyphStyle"/> (design §3.7), so an existing appearance renders the same
    /// through the new uber-shader path. (The end-to-end pixel-equivalence-through-a-camera check is a
    /// separate play-mode/graphics test; this verifies the data mapping it depends on.)
    /// </summary>
    public class AppearanceStyleShimTests
    {
        private Material _m;

        [TearDown]
        public void TearDown() { if (_m != null) Object.DestroyImmediate(_m); }

        private static Material MakeSdfMaterial()
        {
            var sh = Shader.Find("UniText/SDF") ?? Shader.Find("UniText/SDF SSD") ?? Shader.Find("UI/Default");
            return new Material(sh);
        }

        [Test]
        public void NullAppearance_MapsToDefaultStyle()
        {
            Assert.AreEqual(GlyphStyle.Default, AppearanceStyleShim.StyleFor(null, null));
            Assert.AreEqual(GlyphStyle.Default, AppearanceStyleShim.StyleFromMaterials(null));
            Assert.AreEqual(GlyphStyle.Default, AppearanceStyleShim.StyleFromMaterials(new Material[0]));
        }

        [Test]
        public void MaterialProperties_MapToStyleFields()
        {
            _m = MakeSdfMaterial();
            // Only set properties the SDF shader actually declares (others fall back to Default).
            void TrySet(string p, float v) { if (_m.HasProperty(p)) _m.SetFloat(p, v); }
            void TrySetC(string p, Color c) { if (_m.HasProperty(p)) _m.SetColor(p, c); }

            TrySetC("_FaceColor", new Color(0.2f, 0.4f, 0.6f, 1f));
            TrySet("_FaceDilate", 0.1f);
            TrySet("_OutlineSoftness", 0.05f);
            TrySetC("_OutlineColor", new Color(1f, 0f, 0f, 1f));
            TrySet("_OutlineWidth", 0.25f);

            var s = AppearanceStyleShim.StyleFromMaterials(new[] { _m });

            // Tolerance, not exact Color equality: in Linear colour space Material.Set/GetColor round
            // trips through a gamma conversion and drifts by a few ULPs.
            if (_m.HasProperty("_FaceColor"))
                AssertColor(new Color(0.2f, 0.4f, 0.6f, 1f), s.faceColor, "face colour mapped");
            if (_m.HasProperty("_OutlineColor"))
                AssertColor(new Color(1f, 0f, 0f, 1f), s.outlineColor, "outline colour mapped");
            if (_m.HasProperty("_OutlineWidth"))
                Assert.AreEqual(0.25f, s.outlineWidth, 1e-5f, "outline width mapped");
            if (_m.HasProperty("_FaceDilate"))
                Assert.AreEqual(0.1f, s.faceDilate, 1e-5f, "face dilate mapped");
        }

        [Test]
        public void TwoPassPair_FaceFromSecond_OutlineFromFirst()
        {
            var outlineMat = MakeSdfMaterial();
            var faceMat = MakeSdfMaterial();
            try
            {
                if (outlineMat.HasProperty("_OutlineColor")) outlineMat.SetColor("_OutlineColor", Color.green);
                if (outlineMat.HasProperty("_OutlineWidth")) outlineMat.SetFloat("_OutlineWidth", 0.4f);
                if (faceMat.HasProperty("_FaceColor")) faceMat.SetColor("_FaceColor", Color.blue);

                var s = AppearanceStyleShim.StyleFromMaterials(new[] { outlineMat, faceMat });

                if (faceMat.HasProperty("_FaceColor"))
                    Assert.AreEqual(Color.blue, s.faceColor, "face colour comes from the 2nd (face) material");
                if (outlineMat.HasProperty("_OutlineColor"))
                    Assert.AreEqual(Color.green, s.outlineColor, "outline colour comes from the 1st (outline) material");
            }
            finally { Object.DestroyImmediate(outlineMat); Object.DestroyImmediate(faceMat); }
        }

        private static void AssertColor(Color expected, Color actual, string message)
        {
            Assert.AreEqual(expected.r, actual.r, 1e-4f, message + " (r)");
            Assert.AreEqual(expected.g, actual.g, 1e-4f, message + " (g)");
            Assert.AreEqual(expected.b, actual.b, 1e-4f, message + " (b)");
            Assert.AreEqual(expected.a, actual.a, 1e-4f, message + " (a)");
        }
    }
}
