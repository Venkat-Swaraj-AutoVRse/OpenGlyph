using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>Wave 1: Padding.</summary>
    [TestFixture]
    public class Wave1PaddingTests : Wave1TestBase
    {
        // ================================================================== 5. Padding

        [Test]
        public void Padding_InsetsLayoutPreferredSizeClipAndHitTest()
        {
            var pad = new Vector4(30, 10, 20, 5);
            // Generated vertices live in shared buffers: read each component's right after its build.
            var plain = Make("Hello padding", 400, 200, name: "plain");
            var vp = plain.GetGeneratedVerticesForEditorTests();
            var padded = Make("Hello padding", 400, 200, c => W1.Set(c, "Padding", pad), name: "padded");
            var vq = padded.GetGeneratedVerticesForEditorTests();

            Assert.AreEqual(PreferredWidth(plain) + 50f, PreferredWidth(padded), 0.001f, "preferredWidth + left + right");
            Assert.AreEqual(PreferredHeight(plain) + 15f, PreferredHeight(padded), 0.001f, "preferredHeight + top + bottom");

            Assert.AreEqual(vp.Min(v => v.x) + 30f, vq.Min(v => v.x), 0.01f, "mesh moves right by the left padding");
            Assert.AreEqual(vp.Max(v => v.y) - 10f, vq.Max(v => v.y), 0.01f, "mesh moves down by the top padding");

            // Wrapping uses the padded width.
            var text = "Wrapping inside the padded box uses the inner width only, not the rect";
            var wrapPad = Make(text, 260, 400, c => W1.Set(c, "Padding", new Vector4(60, 0, 0, 0)), name: "wrapPad");
            var wrapInner = Make(text, 200, 400, name: "wrapInner");
            Assert.AreEqual(wrapInner.TextProcessor.LayoutLineCount, wrapPad.TextProcessor.LayoutLineCount);
            CollectionAssert.AreEqual(Glyphs(wrapInner).Select(g => (g.x, g.y)), Glyphs(wrapPad).Select(g => (g.x, g.y)),
                "same line breaks and layout-space positions as an unpadded box of the inner width");

            // Hit testing: the glyph is found where it is drawn (shifted by the padding), not in the padding.
            var g0 = padded.ResultGlyphs[0];
            var r = padded.rectTransform.rect;
            var cx = (g0.left + g0.right) * 0.5f;
            var cy = (g0.top + g0.bottom) * 0.5f;
            var hit = padded.HitTest(new Vector2(r.xMin + 30f + cx, r.yMax - 10f - cy), 0f);
            Assert.IsTrue(hit.hit, "glyph under the pointer, inside the padded box");
            Assert.AreEqual(g0.cluster, hit.cluster);
            Assert.IsFalse(padded.HitTest(new Vector2(r.xMin + 2f, r.yMax - 2f), 0f).hit, "the padding itself is not text");

            // Clip: the padded content box.
            padded.Overflow = TextOverflow.Clip;
            Canvas.ForceUpdateCanvases();
            var clip = (Rect)typeof(UniText).GetField("lastAppliedClip", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(padded);
            Assert.AreEqual(400f - 50f, clip.width, 0.01f, "clip width = rect - left - right padding");
            Assert.AreEqual(200f - 15f, clip.height, 0.01f, "clip height = rect - top - bottom padding");

            // Negative values clamp to 0.
            W1.Set(padded, "Padding", new Vector4(-5, 1, 2, 3));
            Assert.AreEqual(new Vector4(0, 1, 2, 3), (Vector4)W1.Get(padded, "Padding"));
        }
    }
}
