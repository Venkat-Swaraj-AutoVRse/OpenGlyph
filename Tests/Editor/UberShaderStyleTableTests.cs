using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2: the style storage + uber-shader. Verifies the
    /// float-texture <see cref="StyleTable"/> packs/dedups style records correctly, and that the
    /// single uber-shader <c>UniText/Uber</c> compiles and is supported (so it binds a
    /// Texture2DArray + float style table through a CanvasRenderer rather than silently failing).
    /// </summary>
    public class UberShaderStyleTableTests
    {
        [Test]
        public void StyleTable_Dedups_IdenticalStyles()
        {
            using var t = new StyleTable();
            var a = GlyphStyle.Default;
            var b = GlyphStyle.Default; // identical
            int ia = t.GetOrAdd(a);
            int ib = t.GetOrAdd(b);
            Assert.AreEqual(ia, ib, "identical styles collapse to one row");
            Assert.AreEqual(1, t.Count);

            var c = GlyphStyle.Default; c.outlineColor = Color.red; c.outlineWidth = 0.3f;
            int ic = t.GetOrAdd(c);
            Assert.AreNotEqual(ia, ic, "a distinct style gets its own row");
            Assert.AreEqual(2, t.Count);
        }

        [Test]
        public void StyleTable_PacksRecord_IntoExpectedColumns()
        {
            using var t = new StyleTable();
            var s = GlyphStyle.Default;
            s.faceColor = new Color(0.1f, 0.2f, 0.3f, 0.4f);
            s.outlineColor = new Color(0.5f, 0.6f, 0.7f, 0.8f);
            s.underlayColor = new Color(0.11f, 0.22f, 0.33f, 0.44f);
            s.glowColor = new Color(0.9f, 0.8f, 0.7f, 0.6f);
            s.faceDilate = 0.12f; s.softness = 0.34f; s.outlineWidth = 0.56f; s.outlineDilate = -0.2f;
            s.underlayOffsetX = 0.7f; s.underlayOffsetY = -0.3f; s.underlayDilate = 0.1f; s.underlaySoftness = 0.25f;
            s.glowOffset = 0.15f; s.glowOuter = 0.4f; s.glowInner = 0.2f; s.glowPower = 0.9f;
            int row = t.GetOrAdd(s);

            Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 0.4f), t.ReadPacked(row, 0), "col0 = faceColor");
            Assert.AreEqual(new Color(0.5f, 0.6f, 0.7f, 0.8f), t.ReadPacked(row, 1), "col1 = outlineColor");
            Assert.AreEqual(new Color(0.11f, 0.22f, 0.33f, 0.44f), t.ReadPacked(row, 2), "col2 = underlayColor");
            Assert.AreEqual(new Color(0.9f, 0.8f, 0.7f, 0.6f), t.ReadPacked(row, 3), "col3 = glowColor");
            Assert.AreEqual(new Color(0.12f, 0.34f, 0.56f, -0.2f), t.ReadPacked(row, 4), "col4 = faceDilate,softness,outlineWidth,outlineDilate");
            Assert.AreEqual(new Color(0.7f, -0.3f, 0.1f, 0.25f), t.ReadPacked(row, 5), "col5 = underlay offsets/dilate/softness");
            Assert.AreEqual(new Color(0.15f, 0.4f, 0.2f, 0.9f), t.ReadPacked(row, 6), "col6 = glow offset/outer/inner/power");
        }

        [Test]
        public void StyleTable_Apply_BuildsFloatTexture_RowPerStyle()
        {
            using var t = new StyleTable();
            t.GetOrAdd(GlyphStyle.Default);
            var s2 = GlyphStyle.Default; s2.faceColor = Color.green;
            t.GetOrAdd(s2);
            var tex = t.Apply();
            Assert.IsNotNull(tex);
            Assert.AreEqual(TextureFormat.RGBAFloat, tex.format);
            Assert.AreEqual(StyleTable.ColumnsPerStyle, tex.width);
            Assert.AreEqual(2, tex.height, "one texture ROW per distinct style");
            Assert.AreEqual(FilterMode.Point, tex.filterMode, "point-sampled so styleIdx rows are exact");
        }

        [Test]
        public void UberShader_Compiles_AndIsSupported()
        {
            var shader = Shader.Find("UniText/Uber");
            Assert.IsNotNull(shader, "UniText/Uber shader must exist");
            if (ShaderUtil.ShaderHasError(shader))
            {
                var sb = new System.Text.StringBuilder();
                foreach (var m in ShaderUtil.GetShaderMessages(shader))
                    if (m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                        sb.Append($"\n  [{m.platform}] {m.message} ({m.file}:{m.line})");
                Assert.Fail("UniText/Uber has compile errors:" + sb);
            }
            Assert.IsTrue(shader.isSupported, "UniText/Uber must be supported on this platform");
        }
    }
}
