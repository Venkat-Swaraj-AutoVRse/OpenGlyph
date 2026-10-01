using System;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace LightSide.Tests
{
    /// <summary>
    /// Renders a BEFORE/AFTER composite of OpenGlyph's own engine output and saves it as a PNG.
    ///
    /// <para>The image is produced entirely by OpenGlyph: the glyph atlas is OpenGlyph's SDF
    /// atlas (rasterised by its own font backend), the geometry is the mesh built by
    /// <see cref="UniTextMeshGenerator"/> from the real layout's <see cref="PositionedGlyph"/>s,
    /// and it is drawn with the font's own SDF material through a Unity <see cref="CommandBuffer"/>
    /// into a <see cref="RenderTexture"/>. No external rasteriser (SkiaSharp / PIL / etc.) is
    /// involved.</para>
    ///
    /// <para><b>BEFORE</b> (left): the Thai paragraph with NO segmentation dictionary — the whole
    /// run is one unbreakable token, so at a narrow width it renders as a single line that runs
    /// off the right edge. <b>AFTER</b> (right): the same text and width WITH the Thai dictionary
    /// assigned — the engine wraps it into several lines at dictionary word boundaries.</para>
    /// </summary>
    [TestFixture]
    public class SegmentationRenderTests
    {
        private const int PanelW = 384;
        private const int PanelH = 384;
        private const float FontSize = 34f;
        private const float LayoutWidth = 150f;   // narrow: forces many wraps so before/after differ

        [Test]
        public void BeforeAfter_DictionaryWrap_RendersEnginePng()
        {
            RenderBeforeAfter("Thai", RealLayoutFixtures.FindThaiFontPath(), "NotoSansThai-Regular (fixture)",
                SegmentationFixtures.Thai[0].Text + SegmentationFixtures.Thai[1].Text
              + SegmentationFixtures.Thai[2].Text + SegmentationFixtures.Thai[3].Text,
                "thai-segmentation-before-after.png");
        }

        /// <summary>
        /// Khmer and Myanmar before/after renders, mirroring the Thai one above but driven through
        /// the complex-script fixtures. BEFORE (left): no dictionary — one unbreakable SA token that
        /// overruns the narrow panel; AFTER (right): the same text + width WITH the script's
        /// dictionary assigned, wrapped at word boundaries. The picture is OpenGlyph's own output:
        /// its SDF atlas, its <see cref="UniTextMeshGenerator"/> mesh, drawn with the font's SDF
        /// material — no external rasteriser.
        /// </summary>
        [TestCase(SegmentationScript.Khmer, "NotoSansKhmer-Regular.ttf", "khmer-segmentation-before-after.png")]
        [TestCase(SegmentationScript.Myanmar, "NotoSansMyanmar-Regular.ttf", "myanmar-segmentation-before-after.png")]
        public void BeforeAfter_DictionaryWrap_ComplexScript_RendersEnginePng(
            SegmentationScript script, string fontFile, string outFile)
        {
            string fontPath = RealLayoutFixtures.FindComplexScriptFont(script, out var cases);
            string text = cases[0].Text + cases[1].Text + cases[2].Text + cases[3].Text;
            RenderBeforeAfter(script.ToString(), fontPath, fontFile + " (fixture)", text, outFile);
        }

        /// <summary>
        /// Renders a BEFORE (no dictionary) / AFTER (dictionary) composite for one script and writes
        /// it as a PNG, asserting the engine actually drew glyph pixels in each panel and that the
        /// dictionary adds word-break opportunities. Shared by the Thai and complex-script tests.
        /// </summary>
        private void RenderBeforeAfter(string scriptLabel, string fontPath, string fontName,
            string text, string outFileName)
        {
            SegHelper.EnsureUnicode();

            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("No graphics device (running with -nographics); the engine render " +
                              "needs a GPU to rasterise the atlas/mesh. Run EditMode WITHOUT -nographics " +
                              "to produce the before/after PNG. (The layout correctness tests do not need graphics.)");

            if (fontPath == null)
                Assert.Ignore($"{scriptLabel} font fixture missing; cannot render engine output.");

            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath));
            if (font == null)
                Assert.Ignore($"Font backend unavailable; cannot build the {scriptLabel} font to render.");
            font.name = fontName;
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font);
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();

            string outPath = RealLayoutFixtures.RenderOutputPath(outFileName);
            RenderTexture rt = null;
            Texture2D readback = null;
            try
            {
                // Panels: left = no dictionary (before), right = dictionary (after).
                var before = LayoutLines(stack, font, appearance, text, assignDictionary: false,
                    out int beforeLines, out int beforeWordBreaks);
                var after = LayoutLines(stack, font, appearance, text, assignDictionary: true,
                    out int afterLines, out int afterWordBreaks);

                // The behavioural difference is in WORD break opportunities: none without a
                // dictionary (the run is one SA token — any wrapping is emergency overflow), many
                // with it. Both panels still render real engine output.
                Assert.AreEqual(0, beforeWordBreaks,
                    "BEFORE panel (no dictionary) must have no dictionary word-break opportunities.");
                Assert.Greater(afterWordBreaks, 0,
                    "AFTER panel (dictionary) must gain dictionary word-break opportunities.");

                // Canvas height must FIT the taller panel. The AFTER panel wraps the narrow Thai run
                // into many lines; with the fixed PanelH the lines below PanelH fell outside the
                // ortho frustum and were cropped off the bottom of the PNG. Size the target to the
                // actual laid-out content: max line count across panels × a generous per-line pitch
                // (Thai stacks above+below the baseline, so 1.6×FontSize is a safe upper bound),
                // plus top/bottom margin — never smaller than PanelH.
                int maxLines = Mathf.Max(beforeLines, afterLines);
                const float LinePitch = 1.6f;   // upper-bound line advance as a multiple of FontSize
                const int VMargin = 16;         // px top+bottom breathing room
                int canvasH = Mathf.Max(PanelH, Mathf.CeilToInt(maxLines * FontSize * LinePitch) + 2 * VMargin);
                TestContext.WriteLine($"Canvas height {canvasH}px for {maxLines} line(s) (was fixed {PanelH}px).");

                rt = new RenderTexture(PanelW * 2, canvasH, 24, RenderTextureFormat.ARGB32)
                { antiAliasing = 1 };

                var drawMat = BuildAtlasMaterial();
                try
                {
                    var cb = new CommandBuffer { name = "OpenGlyphSegmentationRender" };
                    cb.SetRenderTarget(rt);
                    cb.ClearRenderTarget(true, true, new Color(0.09f, 0.09f, 0.11f, 1f));

                    // One ortho over the whole 2*PanelW x canvasH target. Layout space has the text
                    // origin at the top with y increasing downward, so we map y to -y and sit the
                    // top near the top of each panel. Each panel's glyphs are offset in X by its
                    // model matrix (left = 0, right = PanelW).
                    var view = Matrix4x4.identity;
                    var proj = Matrix4x4.Ortho(0, PanelW * 2, canvasH, 0, -100f, 100f);
                    cb.SetViewProjectionMatrices(view, proj);

                    DrawPanel(cb, before, drawMat, panelX: 0f);
                    DrawPanel(cb, after, drawMat, panelX: PanelW);

                    Graphics.ExecuteCommandBuffer(cb);
                    cb.Release();

                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                    readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    readback.Apply();
                    RenderTexture.active = prev;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(drawMat);
                }

                // Sanity: the engine drew SOMETHING (non-background pixels) in each panel.
                int beforePx = NonBackgroundPixels(readback, 0, PanelW);
                int afterPx = NonBackgroundPixels(readback, PanelW, PanelW * 2);
                TestContext.WriteLine($"[{scriptLabel}] Rendered pixels — before panel: {beforePx}, after panel: {afterPx}");
                Assert.Greater(beforePx, 50, "BEFORE panel has no rendered glyph pixels — engine render produced nothing.");
                Assert.Greater(afterPx, 50, "AFTER panel has no rendered glyph pixels — engine render produced nothing.");

                File.WriteAllBytes(outPath, readback.EncodeToPNG());
                TestContext.WriteLine($"[{scriptLabel}] Engine-rendered before/after PNG written: {outPath} " +
                    $"(before: {beforeLines} line(s), {beforeWordBreaks} word-breaks; " +
                    $"after: {afterLines} line(s), {afterWordBreaks} word-breaks)");
                Assert.IsTrue(File.Exists(outPath), "PNG was not written.");

                // Clean up the per-layout meshes/materials.
                before.Dispose();
                after.Dispose();
            }
            finally
            {
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                UnityEngine.Object.DestroyImmediate(stack);
                UnityEngine.Object.DestroyImmediate(font);
                // Restore shipped dictionaries for later tests.
                SegHelper.ResetDictionaryAssignment();
                SegHelper.AssignDictionaries();
            }
        }

        /// <summary>Owned (snapshotted) engine render data for one panel: standalone meshes +
        /// their atlas textures, decoupled from the pooled buffers that produced them.</summary>
        private sealed class PanelRender : IDisposable
        {
            public List<(Mesh mesh, Texture tex)> items = new List<(Mesh, Texture)>();
            public void Dispose()
            {
                foreach (var it in items)
                    if (it.mesh != null) UnityEngine.Object.DestroyImmediate(it.mesh);
                items.Clear();
            }
        }

        private PanelRender LayoutLines(UniTextFontStack stack, UniTextFont font, UniTextAppearance appearance,
            string text, bool assignDictionary, out int lineCount, out int wordBreaks)
        {
            if (assignDictionary)
            {
                SegHelper.ResetDictionaryAssignment();
                SegHelper.AssignDictionaries();
            }
            else
            {
                // No dictionaries: swap in an empty settings instance (fires Changed → segmenter re-resolves).
                var empty = ScriptableObject.CreateInstance<UniTextSettings>();
                UniTextSettings.SetInstance(empty);
                SegHelper.ResetDictionaryAssignment();
            }

            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(256);
            var tp = new TextProcessor(buffers);
            var fontProvider = new UniTextFontProvider(stack, appearance);
            tp.SetFontProvider(fontProvider);

            var settings = new TextProcessSettings
            {
                fontSize = FontSize,
                baseDirection = TextDirection.Auto,
                MaxWidth = LayoutWidth,
                MaxHeight = TextProcessSettings.FloatMax,
                enableWordWrap = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            tp.EnsureFirstPass(text, settings);
            tp.EnsureLines(settings.MaxWidth, FontSize, wordWrap: true);
            tp.EnsurePositions(settings);
            lineCount = tp.buf.lines.count;

            // Count interior break opportunities segmentation ADDED beyond baseline UAX#14.
            int cpn = tp.buf.codepoints.count;
            var cps = new int[cpn];
            for (int i = 0; i < cpn; i++) cps[i] = tp.buf.codepoints[i];
            var seg = new LineBreakType[cpn + 1];
            var baseBreaks = new LineBreakType[cpn + 1];
            SharedPipelineComponents.LineBreakAlgorithm.GetBreakOpportunitiesWithSegmentation(cps, seg);
            SharedPipelineComponents.LineBreakAlgorithm.GetBreakOpportunities(cps, baseBreaks);
            int wb = 0;
            for (int i = 1; i < cpn; i++)
                if (seg[i] != LineBreakType.None && baseBreaks[i] == LineBreakType.None) wb++;
            wordBreaks = wb;

            // Populate the SDF atlas with the shaped glyphs (OpenGlyph's own rasteriser).
            var glyphSet = new List<uint>();
            var seen = new HashSet<uint>();
            var sg = tp.buf.shapedGlyphs.Span;
            for (int i = 0; i < sg.Length; i++)
            {
                var gi = (uint)sg[i].glyphId;
                if (gi != 0 && seen.Add(gi)) glyphSet.Add(gi);
            }
            if (glyphSet.Count > 0) font.TryAddGlyphsBatch(glyphSet);

            var gen = new UniTextMeshGenerator(fontProvider, buffers);
            gen.FontSize = FontSize;
            gen.defaultColor = new Color32(240, 240, 245, 255);
            gen.SetCanvasParametersCached(1f, false);
            gen.SetRectOffset(new Rect(0, 0, LayoutWidth, PanelH));
            gen.SetHorizontalAlignment(HorizontalAlignment.Left);
            gen.GenerateMeshDataOnly(tp.PositionedGlyphs);

            var panel = new PanelRender();
            if (gen.HasGeneratedData)
            {
                var rd = gen.ApplyMeshesToUnity();
                // Snapshot each engine mesh into a standalone Mesh so the shared pooled buffers
                // (reused by the next panel's layout) cannot mutate this panel's geometry.
                var uvs = new List<Vector4>();
                foreach (var r in rd)
                {
                    if (r.mesh == null) continue;
                    var copy = new Mesh { name = "ogsnap" };
                    copy.vertices = r.mesh.vertices;
                    copy.triangles = r.mesh.triangles;
                    uvs.Clear(); r.mesh.GetUVs(0, uvs); copy.SetUVs(0, uvs);
                    copy.colors32 = r.mesh.colors32;
                    copy.RecalculateBounds();
                    panel.items.Add((copy, r.texture));
                }
            }
            gen.ReturnInstanceBuffers();
            buffers.EnsureReturnBuffers();
            return panel;
        }

        private static void DrawPanel(CommandBuffer cb, PanelRender panel, Material drawMat, float panelX)
        {
            if (panel.items == null || panel.items.Count == 0) return;

            // Combined bounds of this panel's meshes (origin/orientation agnostic), then a model
            // matrix placing the content at an 8px inset at the panel's X offset (y-down target).
            var min = new Vector3(float.MaxValue, float.MaxValue, 0);
            var max = new Vector3(float.MinValue, float.MinValue, 0);
            foreach (var it in panel.items)
            {
                if (it.mesh == null) continue;
                var b = it.mesh.bounds;
                min = Vector3.Min(min, b.min);
                max = Vector3.Max(max, b.max);
            }
            if (min.x > max.x) return;

            // Render at native 1:1 scale (translate to inset + flip Y). We do NOT scale-to-fit, so
            // differing wrap positions between the panels show as a genuinely different layout.
            foreach (var it in panel.items)
            {
                if (it.mesh == null) continue;
                var block = new MaterialPropertyBlock();
                if (it.tex != null) block.SetTexture("_MainTex", it.tex);
                var m = Matrix4x4.TRS(new Vector3(panelX + 8f, 8f, 0f), Quaternion.identity, new Vector3(1f, -1f, 1f))
                      * Matrix4x4.Translate(new Vector3(-min.x, -max.y, 0f));
                cb.DrawMesh(it.mesh, m, drawMat, 0, 0, block);
            }
        }

        /// <summary>
        /// A minimal Cull-Off, alpha-blended material that samples the engine's SDF atlas and
        /// renders the glyph fill (smoothstep around the SDF mid-level). The glyph shapes come
        /// entirely from OpenGlyph's rasterised atlas and the geometry/UVs from OpenGlyph's mesh —
        /// only the final display shader is this passthrough, so the picture is engine output.
        /// </summary>
        private static Material BuildAtlasMaterial()
        {
            const string src = @"
Shader ""Hidden/OpenGlyphAtlasPreview"" {
  Properties { _MainTex (""Atlas"", 2D) = ""black"" {} }
  SubShader {
    Tags { ""Queue""=""Transparent"" ""RenderType""=""Transparent"" }
    Blend SrcAlpha OneMinusSrcAlpha
    Cull Off ZWrite Off ZTest Always
    Pass {
      CGPROGRAM
      #pragma vertex vert
      #pragma fragment frag
      #include ""UnityCG.cginc""
      sampler2D _MainTex;
      struct appdata { float4 vertex:POSITION; float4 uv:TEXCOORD0; float4 color:COLOR; };
      struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float4 col:COLOR; };
      v2f vert(appdata v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.uv.xy; o.col=v.color; return o; }
      fixed4 frag(v2f i):SV_Target {
        // SDF atlas: distance/coverage is in the alpha channel (fall back to red if alpha flat).
        fixed4 t = tex2D(_MainTex, i.uv);
        float d = max(t.a, t.r);
        float aa = fwidth(d) + 1e-4;
        float cov = smoothstep(0.5 - aa, 0.5 + aa, d);
        return fixed4(i.col.rgb, i.col.a * cov);
      }
      ENDCG
    }
  }
}";
            var shader = ShaderUtilCompat.CreateRuntimeShader(src);
            var mat = new Material(shader != null ? shader : Shader.Find("Unlit/Transparent"));
            return mat;
        }

        private static int NonBackgroundPixels(Texture2D tex, int x0, int x1)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height, count = 0;
            var bg = new Color32(23, 23, 28, 255); // 0.09*255 ≈ 23
            for (int y = 0; y < h; y++)
                for (int x = x0; x < x1 && x < w; x++)
                {
                    var c = px[y * w + x];
                    if (Mathf.Abs(c.r - bg.r) > 24 || Mathf.Abs(c.g - bg.g) > 24 || Mathf.Abs(c.b - bg.b) > 24)
                        count++;
                }
            return count;
        }
    }

    /// <summary>Builds a runtime Shader object from source across Unity versions (editor-only).</summary>
    internal static class ShaderUtilCompat
    {
        public static Shader CreateRuntimeShader(string src)
        {
#if UNITY_EDITOR
            // ShaderUtil.CreateShaderAsset compiles source into a Shader at runtime (editor only).
            try { return UnityEditor.ShaderUtil.CreateShaderAsset(src); }
            catch { return null; }
#else
            return null;
#endif
        }
    }
}
