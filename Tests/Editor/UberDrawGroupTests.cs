using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using U = LightSide.UberDrawGroup;

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2: the draw-group plan. Verifies the per-glyph mode
    /// mapping and the decision §5.1 guarantee that any mix of fonts/modes/spans collapses to AT MOST
    /// two draw groups (Alpha8 + RGBA32) — the "n draw calls -> 1 or 2" property the benchmark measures.
    /// </summary>
    public class UberDrawGroupTests
    {
        [Test]
        public void ModeMapping_MatchesDesign()
        {
            Assert.AreEqual(U.GlyphMode.Sdf, U.ModeFor(UniTextRenderMode.SDF, false));
            Assert.AreEqual(U.GlyphMode.Msdf, U.ModeFor(UniTextRenderMode.Msdf, false));
            Assert.AreEqual(U.GlyphMode.Bitmap, U.ModeFor(UniTextRenderMode.Smooth, false));
            Assert.AreEqual(U.GlyphMode.Bitmap, U.ModeFor(UniTextRenderMode.Mono, false));
            Assert.AreEqual(U.GlyphMode.Colr, U.ModeFor(UniTextRenderMode.Smooth, true), "color font -> COLR regardless of mode");
        }

        [Test]
        public void FormatMapping_TwoArraysOnly()
        {
            Assert.AreEqual(TextureFormat.Alpha8, U.FormatFor(U.GlyphMode.Sdf));
            Assert.AreEqual(TextureFormat.Alpha8, U.FormatFor(U.GlyphMode.Bitmap));
            Assert.AreEqual(TextureFormat.RGBA32, U.FormatFor(U.GlyphMode.Msdf));
            Assert.AreEqual(TextureFormat.RGBA32, U.FormatFor(U.GlyphMode.Colr));
        }

        [Test]
        public void TextOnly_ThreeSdfFonts_OneDrawGroup()
        {
            var usages = new List<U.Usage>
            {
                new(1, UniTextRenderMode.SDF, false),
                new(2, UniTextRenderMode.SDF, false),
                new(3, UniTextRenderMode.SDF, false),
            };
            Assert.AreEqual(1, U.DrawGroupCount(usages), "three SDF fonts collapse to ONE draw group");
        }

        [Test]
        public void MixedScenario_ThreeSdf_Msdf_Emoji_TwoDrawGroups()
        {
            // The sub-task-4 benchmark scenario: 3 SDF fonts + 1 MSDF + emoji (+ per-span outline,
            // which is a style index, not a group). Decision §5.1: exactly TWO groups.
            var usages = new List<U.Usage>
            {
                new(1, UniTextRenderMode.SDF, false),
                new(2, UniTextRenderMode.SDF, false),
                new(3, UniTextRenderMode.SDF, false),
                new(4, UniTextRenderMode.Msdf, false), // -> RGBA32
                new(5, UniTextRenderMode.Smooth, true), // emoji COLR -> RGBA32
            };
            var groups = U.PlanGroups(usages);
            Assert.AreEqual(2, groups.Count, "mixed SDF + MSDF + emoji => exactly two draw groups");
            CollectionAssert.Contains(groups, TextureFormat.Alpha8);
            CollectionAssert.Contains(groups, TextureFormat.RGBA32);
        }

        [Test]
        public void NeverExceedsTwo_ForAnyMix()
        {
            var usages = new List<U.Usage>();
            for (int i = 0; i < 50; i++)
            {
                usages.Add(new U.Usage(i, UniTextRenderMode.SDF, false));
                usages.Add(new U.Usage(i + 1000, UniTextRenderMode.Msdf, false));
                usages.Add(new U.Usage(i + 2000, UniTextRenderMode.Mono, false));
                usages.Add(new U.Usage(i + 3000, UniTextRenderMode.Smooth, true));
            }
            Assert.LessOrEqual(U.DrawGroupCount(usages), 2, "no mix of fonts/modes ever exceeds two draw groups");
        }

        [Test]
        public void Empty_ZeroGroups()
        {
            Assert.AreEqual(0, U.DrawGroupCount(new List<U.Usage>()));
        }
    }
}
