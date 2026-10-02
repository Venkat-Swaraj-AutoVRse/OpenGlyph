using NUnit.Framework;
using UnityEngine;
using Key = LightSide.GlyphAtlasArray.GlyphCellKey;

namespace LightSide.Tests
{
    /// <summary>
    /// Memory-budget tests for the glyph atlas: the BYTE budget (per-array), the combined
    /// effective-page-budget (page ∧ byte, whichever is lower), and the process-wide GLOBAL byte
    /// budget enforced across arrays by <see cref="SharedGlyphAtlas"/>. Complements
    /// <see cref="GlyphAtlasArrayTests"/> (which covers the page budget). All assert the task's
    /// eviction contract: unreferenced LRU glyphs are evicted, pinned glyphs never are, and an
    /// evicted glyph re-rasterizes identically on demand.
    /// </summary>
    public class GlyphAtlasBudgetTests
    {
        private const int Size = 64;

        // The whole suite shares one process-wide UniTextSettings instance + shared-atlas registry.
        // The global-budget tests swap the settings instance and populate the shared atlas, so we
        // save the original in SetUp and restore it in TearDown — otherwise downstream tests
        // (segmentation, modifier-config, render) inherit our empty settings and fail.
        private UniTextSettings _savedSettings;

        [SetUp]
        public void SetUp()
        {
            _savedSettings = UniTextSettings.IsNull ? null : UniTextSettings.Instance;
        }

        [TearDown]
        public void TearDown()
        {
            SharedGlyphAtlas.Clear();
            if (_savedSettings != null)
                UniTextSettings.SetInstance(_savedSettings);
            // Force the segmenter to re-resolve the shipped dictionaries against the restored settings.
            SegHelper.ResetDictionaryAssignment();
        }

        private static byte[] Alpha(int w, int h, byte v)
        {
            var b = new byte[w * h];
            for (int i = 0; i < b.Length; i++) b[i] = v;
            return b;
        }

        private static Key K(int font, uint glyph) => new(font, glyph, VariationKey.None);

        [Test]
        public void ByteBudget_One_Page_Worth_BehavesLikePageBudgetOne()
        {
            // A byte budget equal to exactly one page's bytes must cap the array at one page, i.e.
            // force eviction rather than growth — identical to pageBudget:1.
            long onePage = (long)Size * Size * 1; // Alpha8 = 1 B/px
            using var atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8, pageBudget: 0, byteBudget: onePage);

            Assert.AreEqual(1, atlas.EffectivePageBudget, "byte budget of one page => effective page budget 1");

            bool evictionHappened = false;
            for (int i = 0; i < 1000; i++)
            {
                int before = atlas.CellCount;
                Assert.IsTrue(atlas.AddGlyph(K(1, (uint)(100 + i)), Alpha(14, 14, (byte)(1 + i % 254)), 14, 14, 1, out _));
                if (atlas.CellCount == before) { evictionHappened = true; break; }
            }
            Assert.IsTrue(evictionHappened, "one-page byte budget must force eviction");
            Assert.AreEqual(1, atlas.PageCount, "never grew past one page");
            Assert.LessOrEqual(atlas.ResidentBytes, onePage, "resident bytes stay within the byte budget");
        }

        [Test]
        public void EffectivePageBudget_TakesTheLowerOfPageAndByte()
        {
            long twoPages = (long)Size * Size * 2;
            // page budget 5, byte budget 2 pages => effective 2.
            using var a1 = new GlyphAtlasArray(Size, TextureFormat.Alpha8, pageBudget: 5, byteBudget: twoPages);
            Assert.AreEqual(2, a1.EffectivePageBudget);
            // page budget 1, byte budget 2 pages => effective 1.
            using var a2 = new GlyphAtlasArray(Size, TextureFormat.Alpha8, pageBudget: 1, byteBudget: twoPages);
            Assert.AreEqual(1, a2.EffectivePageBudget);
            // both zero => unbounded.
            using var a3 = new GlyphAtlasArray(Size, TextureFormat.Alpha8);
            Assert.LessOrEqual(a3.EffectivePageBudget, 0);
        }

        [Test]
        public void ByteBudget_Default_Zero_IsUnbounded_NeverEvicts()
        {
            using var atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8); // page 0, byte 0
            for (int i = 0; i < 60; i++)
                Assert.IsTrue(atlas.AddGlyph(K(1, (uint)i), Alpha(14, 14, (byte)(1 + i % 254)), 14, 14, 1, out _));
            Assert.AreEqual(60, atlas.CellCount, "unbounded => every glyph retained");
            Assert.Greater(atlas.PageCount, 1, "grew pages rather than evicting");
        }

        [Test]
        public void ByteBudget_PinnedGlyph_SurvivesEviction()
        {
            long onePage = (long)Size * Size * 1;
            using var atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8, byteBudget: onePage);

            var pinned = K(1, 1);
            Assert.IsTrue(atlas.AddGlyph(pinned, Alpha(14, 14, 70), 14, 14, 1, out _));
            atlas.Acquire(pinned);

            for (int i = 0; i < 1000; i++)
                Assert.IsTrue(atlas.AddGlyph(K(1, (uint)(100 + i)), Alpha(14, 14, (byte)(1 + i % 254)), 14, 14, 1, out _));

            Assert.IsTrue(atlas.IsResident(pinned), "pinned (refcount>0) glyph is never evicted under a byte budget");
        }

        [Test]
        public void ByteBudget_EvictedGlyph_ReRasterizesIdentically()
        {
            long onePage = (long)Size * Size * 1;
            using var atlas = new GlyphAtlasArray(Size, TextureFormat.Alpha8, byteBudget: onePage);

            var g = K(3, 9);
            Assert.IsTrue(atlas.AddGlyph(g, Alpha(10, 10, 123), 10, 10, 1, out _));
            Assert.IsTrue(atlas.Evict(g), "unpinned glyph evicts");
            Assert.IsFalse(atlas.TryGetCell(g, out _), "lookup misses after eviction");

            // Re-add with the same bitmap: cell is valid again and pixels match the re-rasterized value.
            Assert.IsTrue(atlas.AddGlyph(g, Alpha(10, 10, 123), 10, 10, 1, out var second));
            Assert.IsTrue(atlas.TryGetCell(g, out var got));
            Assert.IsTrue(got.IsValid);
            Assert.AreEqual((byte)123, atlas.ReadCpuByte(second.slice, second.pixelRect.x, second.pixelRect.y),
                "re-rasterized glyph bitmap is written back identically");
        }

        [Test]
        public void GlobalByteBudget_EvictsAcrossArrays_UntilUnderBudget()
        {
            // Two independent arrays (two formats) share one global ceiling. Fill both unbounded,
            // then set a tiny global budget and enforce: eviction must drop unreferenced glyphs
            // (globally LRU) across BOTH arrays until total resident bytes <= budget.
            SharedGlyphAtlas.Clear();
            try
            {
                var settings = ScriptableObject.CreateInstance<UniTextSettings>();
                UniTextSettings.SetInstance(settings);

                var alpha = SharedGlyphAtlas.Get(TextureFormat.Alpha8, Size);
                var rgba = SharedGlyphAtlas.Get(TextureFormat.RGBA32, Size);

                SharedGlyphAtlas.BeginFrame();
                for (int i = 0; i < 40; i++)
                    alpha.AddGlyph(K(1, (uint)i), Alpha(20, 20, (byte)(1 + i % 254)), 20, 20, 1, out _);
                SharedGlyphAtlas.BeginFrame();
                var rgbaPix = new byte[20 * 20 * 4];
                for (int i = 0; i < 40; i++)
                    rgba.AddGlyph(K(2, (uint)i), rgbaPix, 20, 20, 4, out _);

                long before = SharedGlyphAtlas.TotalResidentBytes;
                Assert.Greater(before, 0);

                // Budget = one Alpha8 page (whichever pages survive, total must come under this).
                long budget = (long)Size * Size * 1;
                typeof(UniTextSettings).GetField("sharedAtlasByteBudgetGlobal",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(settings, budget);

                int evicted = SharedGlyphAtlas.EnforceGlobalByteBudget();
                Assert.Greater(evicted, 0, "global enforcement evicted at least one unreferenced glyph");
                Assert.LessOrEqual(SharedGlyphAtlas.TotalResidentBytes, budget,
                    "after enforcement, combined resident bytes are within the global budget");
            }
            finally
            {
                SharedGlyphAtlas.Clear();
            }
        }

        [Test]
        public void GlobalByteBudget_Zero_IsNoOp()
        {
            SharedGlyphAtlas.Clear();
            try
            {
                var settings = ScriptableObject.CreateInstance<UniTextSettings>();
                UniTextSettings.SetInstance(settings); // global budget defaults to 0
                var alpha = SharedGlyphAtlas.Get(TextureFormat.Alpha8, Size);
                for (int i = 0; i < 40; i++)
                    alpha.AddGlyph(K(1, (uint)i), Alpha(20, 20, (byte)(1 + i % 254)), 20, 20, 1, out _);
                int before = alpha.CellCount;
                Assert.AreEqual(0, SharedGlyphAtlas.EnforceGlobalByteBudget(), "zero global budget evicts nothing");
                Assert.AreEqual(before, alpha.CellCount, "no glyphs evicted when global budget is unbounded");
            }
            finally
            {
                SharedGlyphAtlas.Clear();
            }
        }
    }
}
