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
        public void GlobalByteBudget_IsDisabled_NoEvictionEvenWhenBudgetSet()
        {
            // Budgets are DISABLED in the runtime (SharedGlyphAtlas reads every budget as 0,
            // regardless of the serialized UniTextSettings values). Setting a tiny global byte budget
            // and enforcing it must therefore be a NO-OP: nothing is evicted and resident bytes are
            // unchanged. This FAILS on main, where SafeGlobalByteBudget() returned the settings value
            // and enforcement evicted across arrays. (The eviction MECHANISM itself is still covered
            // directly at the GlyphAtlasArray level by the ByteBudget_* tests above.)
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
                int cellsBefore = alpha.CellCount + rgba.CellCount;
                Assert.Greater(before, 0);

                // Set a tiny global budget directly on the settings instance (one Alpha8 page). On main
                // this would force eviction across both arrays; with budgets disabled it is ignored.
                long budget = (long)Size * Size * 1;
                typeof(UniTextSettings).GetField("sharedAtlasByteBudgetGlobal",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(settings, budget);

                // A non-zero budget is configured, but SharedGlyphAtlas reports it as 0 (ignored).
                // (The once-per-session "NOT ACTIVE" warning is not asserted here: it is a static,
                // fire-once flag and any earlier test in the suite may have already consumed it, so
                // asserting it would be test-order dependent.)
                int evicted = SharedGlyphAtlas.EnforceGlobalByteBudget();
                Assert.AreEqual(0, evicted, "budgets disabled => global enforcement evicts nothing even with a budget set");
                Assert.AreEqual(cellsBefore, alpha.CellCount + rgba.CellCount, "no cells evicted from either array");
                Assert.AreEqual(before, SharedGlyphAtlas.TotalResidentBytes, "resident bytes unchanged (no eviction)");
                Assert.Greater(SharedGlyphAtlas.TotalResidentBytes, budget,
                    "resident bytes stay ABOVE the configured budget — proof the budget is not enforced");
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

        /// <summary>
        /// REGRESSION GUARD for the disabled shared-atlas budgets (PR "disable-budgets").
        ///
        /// With a NON-ZERO per-array byte budget set in <see cref="UniTextSettings"/>, driving the
        /// RUNTIME path (<see cref="SharedGlyphAtlas.Get"/> → <c>AddGlyph</c>) with many distinct
        /// glyphs must NEVER evict: because the runtime never calls Acquire/Release or BeginFrame,
        /// every glyph has refcount 0 and the LRU clock is frozen, so any enforced budget would evict
        /// glyphs that are on screen and corrupt visible text. The fix makes SharedGlyphAtlas read the
        /// budget as 0, so the array created for the runtime is unbounded.
        ///
        /// Assertions: (1) every added glyph is still resident (no eviction); (2) the pages GREW
        /// rather than being capped; (3) every cell returned when the glyph was added still reads back
        /// its ORIGINAL pixel value — i.e. its atlas space was never reused for a different glyph.
        ///
        /// This FAILS ON MAIN: there <c>SafeByteBudget()</c> returned the settings value, so
        /// <see cref="SharedGlyphAtlas.Get"/> built a budgeted array, <c>AddGlyph</c> evicted
        /// unreferenced (refcount-0) cells to stay under budget, CellCount was capped and the recorded
        /// cells' pixels were overwritten by later glyphs.
        /// </summary>
        [Test]
        public void SharedAtlas_RuntimePath_WithByteBudgetSet_NeverEvicts_AndCellsKeepTheirPixels()
        {
            SharedGlyphAtlas.Clear();
            try
            {
                // A real settings instance carrying a TINY per-array byte budget: one Alpha8 page.
                // On main this caps each array at a single page and forces eviction; with budgets
                // disabled it is ignored and the array grows unbounded.
                var settings = ScriptableObject.CreateInstance<UniTextSettings>();
                long onePage = (long)Size * Size * 1; // Alpha8 = 1 B/px
                typeof(UniTextSettings).GetField("sharedAtlasByteBudgetPerArray",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(settings, onePage);
                typeof(UniTextSettings).GetField("sharedAtlasPageBudget",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(settings, 1);
                UniTextSettings.SetInstance(settings);

                // Drive the RUNTIME path: Get() reads the budget from settings at array creation.
                var atlas = SharedGlyphAtlas.Get(TextureFormat.Alpha8, Size);

                // Add many distinct glyphs through the runtime path. Each gets a DIFFERENT, non-zero
                // pixel value so a reused cell is detectable. Record the (key, cell, pixel) of each.
                const int glyphW = 10, glyphH = 10;
                const int count = 300; // far more than fit in one page => main would evict heavily
                var keys = new Key[count];
                var cells = new GlyphAtlasArray.GlyphCell[count];
                var pixelOf = new byte[count];

                for (int i = 0; i < count; i++)
                {
                    byte v = (byte)(1 + (i % 254)); // 1..254, never 0 (0 would be an empty cell)
                    var key = K(1, (uint)(1000 + i));
                    Assert.IsTrue(atlas.AddGlyph(key, Alpha(glyphW, glyphH, v), glyphW, glyphH, 1, out var cell),
                        $"AddGlyph #{i} should succeed");
                    keys[i] = key;
                    cells[i] = cell;
                    pixelOf[i] = v;
                }

                // (1) No eviction: every glyph is still resident and CellCount == count.
                Assert.AreEqual(count, atlas.CellCount,
                    "budgets disabled => the runtime atlas retained every glyph (no eviction)");
                for (int i = 0; i < count; i++)
                    Assert.IsTrue(atlas.IsResident(keys[i]),
                        $"glyph #{i} must still be resident (never evicted)");

                // (2) It GREW pages rather than being capped at the (ignored) one-page budget.
                Assert.Greater(atlas.PageCount, 1,
                    "the array grew past one page — proof the one-page byte budget was NOT enforced");

                // (3) Every originally-returned cell still holds ITS glyph's pixels: the atlas space
                //     was never reused for a different glyph. Read back one pixel from each cell and
                //     also confirm the live cell position is unchanged.
                for (int i = 0; i < count; i++)
                {
                    Assert.IsTrue(atlas.TryGetCell(keys[i], out var now), $"cell #{i} still looked up");
                    Assert.AreEqual(cells[i].slice, now.slice, $"cell #{i} slice unchanged (space not reused)");
                    Assert.AreEqual(cells[i].pixelRect.x, now.pixelRect.x, $"cell #{i} x unchanged");
                    Assert.AreEqual(cells[i].pixelRect.y, now.pixelRect.y, $"cell #{i} y unchanged");
                    byte readBack = atlas.ReadCpuByte(cells[i].slice, cells[i].pixelRect.x, cells[i].pixelRect.y);
                    Assert.AreEqual(pixelOf[i], readBack,
                        $"cell #{i} still holds its original glyph's pixel ({pixelOf[i]}) — space never reused");
                }
            }
            finally
            {
                SharedGlyphAtlas.Clear();
            }
        }
    }
}
