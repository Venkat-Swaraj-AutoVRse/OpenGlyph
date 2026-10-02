using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 1: the serializable component-authored <see cref="UniTextStyle"/>.
    /// Verifies it mirrors a <see cref="GlyphStyle"/> (a <see cref="StyleTable"/> row) field-for-field,
    /// round-trips losslessly, and that a <see cref="UniText"/> component with Override Style ON feeds
    /// its own style into the style table instead of the shim.
    /// </summary>
    public class UniTextStyleTests
    {
        [Test]
        public void Default_EqualsGlyphStyleDefault()
        {
            Assert.AreEqual(GlyphStyle.Default, UniTextStyle.Default.ToGlyphStyle());
        }

        [Test]
        public void RoundTrip_IsLossless()
        {
            var g = new GlyphStyle
            {
                faceColor = new Color(0.1f, 0.2f, 0.3f, 1f),
                faceDilate = 0.2f,
                softness = 0.05f,
                outlineColor = new Color(1f, 0f, 0f, 0.8f),
                outlineWidth = 0.3f,
                outlineDilate = -0.1f,
                underlayColor = new Color(0f, 0f, 0f, 0.5f),
                underlayOffsetX = 0.01f,
                underlayOffsetY = -0.02f,
                underlayDilate = 0.03f,
                underlaySoftness = 0.4f,
                glowColor = new Color(0f, 1f, 1f, 0.6f),
                glowOffset = 0.1f, glowOuter = 0.2f, glowInner = 0.3f, glowPower = 1.5f,
            };

            var authored = UniTextStyle.FromGlyphStyle(g);
            var back = authored.ToGlyphStyle();

            Assert.AreEqual(g, back, "GlyphStyle -> UniTextStyle -> GlyphStyle is lossless");
        }

        [Test]
        public void HasSameFieldCount_AsGlyphStyle()
        {
            // Both must expose the same style fields so the component style packs into a StyleTable row 1:1.
            var gFields = typeof(GlyphStyle).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var uFields = typeof(UniTextStyle).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            Assert.AreEqual(gFields.Length, uFields.Length,
                "UniTextStyle must have the same instance field count as a StyleTable row (GlyphStyle)");
        }

        [Test]
        public void StyleTable_DedupsComponentStyle()
        {
            using var table = new StyleTable();
            var authored = UniTextStyle.Default;
            authored.outlineColor = Color.red;
            authored.outlineWidth = 0.2f;

            int a = table.GetOrAdd(authored.ToGlyphStyle());
            int b = table.GetOrAdd(authored.ToGlyphStyle());
            Assert.AreEqual(a, b, "identical component style collapses to one row");
            Assert.AreEqual(1, table.Count);
        }

        [Test]
        public void Component_OverrideStyle_UsesComponentStyle()
        {
            var go = new GameObject("ut", typeof(RectTransform));
            try
            {
                var ut = go.AddComponent<UniText>();
                Assert.IsFalse(ut.OverrideStyle, "defaults to shim fallback (false)");

                var s = UniTextStyle.Default;
                s.faceColor = new Color(0.3f, 0.6f, 0.9f, 1f);
                ut.Style = s;
                ut.OverrideStyle = true;

                Assert.IsTrue(ut.OverrideStyle);
                Assert.AreEqual(new Color(0.3f, 0.6f, 0.9f, 1f), ut.Style.faceColor);
                Assert.AreEqual(s.ToGlyphStyle(), ut.Style.ToGlyphStyle(),
                    "component round-trips its own style to the GlyphStyle the builder consumes");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
