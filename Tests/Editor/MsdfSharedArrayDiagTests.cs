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

                // Now render the glyph through the REAL uber material (mode=1 MSDF) over the shared
                // array and read back: with correct data, median3 must produce a GLYPH, not a solid
                // block. A single quad covering the glyph rect's UV, UV1=(_, slice, 1, 0).
                var uber = new Material(Shader.Find("UniText/Uber"));
                uber.SetTexture("_MainTexArray", arr.Texture);
                // Minimal style table (1 row, white face) so StyleTexel(0,*) is valid.
                var st2 = new StyleTable(); st2.GetOrAdd(GlyphStyle.Default); var sTex = st2.Apply();
                uber.SetTexture("_StyleTex", sTex); uber.SetFloat("_StyleTexWidth", st2.Width); uber.SetFloat("_StyleTexHeight", 1);

                int QW = 64, QH = 64;
                var rtq = new RenderTexture(QW, QH, 0, RenderTextureFormat.ARGB32); rtq.Create();
                var mesh = new Mesh();
                float u0 = (float)rect.x / font.AtlasSize, v0 = (float)rect.y / font.AtlasSize;
                float u1 = (float)(rect.x + rect.width) / font.AtlasSize, v1 = (float)(rect.y + rect.height) / font.AtlasSize;
                mesh.vertices = new[] { new Vector3(0,0,0), new Vector3(QW,0,0), new Vector3(QW,QH,0), new Vector3(0,QH,0) };
                mesh.SetUVs(0, new System.Collections.Generic.List<Vector4> { new(u0,v0,0,0), new(u1,v0,0,0), new(u1,v1,0,0), new(u0,v1,0,0) });
                mesh.SetUVs(1, new System.Collections.Generic.List<Vector4> { new(0,cellSlice,1,0), new(0,cellSlice,1,0), new(0,cellSlice,1,0), new(0,cellSlice,1,0) });
                mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                mesh.triangles = new[] { 0,1,2, 0,2,3 };
                var cb = new UnityEngine.Rendering.CommandBuffer();
                cb.SetRenderTarget(rtq); cb.ClearRenderTarget(true, true, Color.black);
                cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0, QW, 0, QH, -1, 1));
                cb.SetGlobalFloat("unity_GUIZTestMode", (float)UnityEngine.Rendering.CompareFunction.Always);
                cb.DrawMesh(mesh, Matrix4x4.identity, uber, 0, -1);
                Graphics.ExecuteCommandBuffer(cb); cb.Release();
                var prevRT = RenderTexture.active; RenderTexture.active = rtq;
                var shot = new Texture2D(QW, QH, TextureFormat.RGBA32, false); shot.ReadPixels(new Rect(0,0,QW,QH),0,0); shot.Apply();
                RenderTexture.active = prevRT;
                var sp = shot.GetPixels32(); int lit = 0, full = 0;
                foreach (var p in sp) { if (p.r>8||p.g>8||p.b>8) lit++; if (p.r>240&&p.g>240&&p.b>240) full++; }
                Debug.Log($"[MSDF UBER] quad={QW*QH} lit={lit} fullWhite={full} (a glyph lights SOME, not all; a solid block lights ~all)");
                RenderTexture.active = null; rtq.Release(); Object.DestroyImmediate(rtq); Object.DestroyImmediate(shot); Object.DestroyImmediate(mesh); Object.DestroyImmediate(uber); st2.Dispose();

                // A correctly reconstructed glyph lights a MINORITY of the quad; a solid block lights most.
                Assert.Less(lit, QW * QH * 0.9f, $"uber MSDF must render a glyph, not a solid block (lit {lit}/{QW*QH})");
            }
            finally { if (font != null) Object.DestroyImmediate(font); SharedGlyphAtlas.Clear(); }
        }
    }
}
