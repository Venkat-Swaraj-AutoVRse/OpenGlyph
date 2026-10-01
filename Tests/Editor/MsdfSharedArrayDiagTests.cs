using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>R2 diagnostic: inspect the shared RGBA32 MSDF slice content vs the legacy RGB24 page.</summary>
    public class MsdfSharedArrayDiagTests
    {
        [Test]
        public void MsdfPage_PublishedToSharedArray_MedianMatchesLegacy()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans not found.");
            SharedGlyphAtlas.Clear();
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48, 0.25f, UniTextRenderMode.Msdf);
            try
            {
                if (font == null || font.AtlasRenderMode != UniTextRenderMode.Msdf) Assert.Ignore("MSDF unavailable.");
                uint g = Shaper.GetGlyphIndex(font, 'A');
                font.TryAddGlyphsBatch(new System.Collections.Generic.List<uint> { g });
                Assert.IsTrue(font.TryGetGlyph(g, VariationKey.None, out var glyph));
                var legacyPage = font.AtlasTextures[glyph.atlasIndex];
                Assert.AreEqual(TextureFormat.RGB24, legacyPage.format, "legacy MSDF page should be RGB24");

                // Publish the WHOLE PAGE into the shared array (this is the path the unified renderer
                // uses: UnifiedRenderBuilder.Build -> GlyphAtlasArray.AddPage), then upload.
                var arr = SharedGlyphAtlas.Get(TextureFormat.RGBA32, font.AtlasSize);
                Assert.IsTrue(arr.AddPage(legacyPage.GetInstanceID(), legacyPage, out int slice), "AddPage must accept the RGB24 MSDF page");
                arr.Apply(false);
                var cellSlice = slice;
                Assert.IsNotNull(arr.Texture);

                // Sample a grid of texels inside the glyph rect from BOTH sources; compare median3.
                var rect = glyph.glyphRect;
                var legacyRaw = legacyPage.GetRawTextureData<byte>();
                int lw = legacyPage.width;
                float Med(float r, float gg, float b) => Mathf.Max(Mathf.Min(r, gg), Mathf.Min(Mathf.Max(r, gg), b));

                int sampled = 0; float maxMedianDelta = 0; float legMedCenter = -1, shaMedCenter = -1;
                int cx = rect.x + rect.width / 2, cy = rect.y + rect.height / 2;
                for (int yy = 0; yy < rect.height; yy += Mathf.Max(1, rect.height / 6))
                for (int xx = 0; xx < rect.width; xx += Mathf.Max(1, rect.width / 6))
                {
                    int px = rect.x + xx, py = rect.y + yy;
                    int li = (py * lw + px) * 3;
                    float lr = legacyRaw[li] / 255f, lg = legacyRaw[li + 1] / 255f, lb = legacyRaw[li + 2] / 255f;
                    float sr = arr.ReadCpuByte(cellSlice, px, py, 0) / 255f;
                    float sg = arr.ReadCpuByte(cellSlice, px, py, 1) / 255f;
                    float sb = arr.ReadCpuByte(cellSlice, px, py, 2) / 255f;
                    float lmed = Med(lr, lg, lb), smed = Med(sr, sg, sb);
                    maxMedianDelta = Mathf.Max(maxMedianDelta, Mathf.Abs(lmed - smed));
                    if (px == cx && py == cy) { legMedCenter = lmed; shaMedCenter = smed; }
                    sampled++;
                }
                Debug.Log($"[MSDF DIAG] rect={rect} sampled={sampled} maxMedianDelta(copy)={maxMedianDelta:F4} centerMedian legacy={legMedCenter:F3} shared={shaMedCenter:F3}");

                // The COPY must preserve the field: shared median == legacy median at every texel.
                Assert.LessOrEqual(maxMedianDelta, 1.5f / 255f, "RGB24->RGBA32 copy must preserve the MSDF field (median)");
            }
            finally { if (font != null) Object.DestroyImmediate(font); SharedGlyphAtlas.Clear(); }
        }
    }
}
