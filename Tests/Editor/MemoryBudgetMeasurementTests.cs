using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace LightSide.Tests
{
    /// <summary>
    /// MEASURE-FIRST harness for the memory-budget work (Documentation/Design/MemoryBudgets.md).
    /// Drives the REAL text pipeline (TextProcessor + UniTextFontProvider + UniTextMeshGenerator,
    /// the same objects <see cref="UniText"/> builds) over the benchmark-shaped workload (N objects
    /// of a long mixed Latin/Arabic/Hebrew string) and logs (prefix <c>[MemMeasure]</c>):
    ///   - transient managed allocation per build pass (GC.GetAllocatedBytesForCurrentThread delta),
    ///   - retained mesh-buffer bytes (48 B/vertex: Vector3 + 2x Vector4 + Color32; + 4 B/index),
    ///   - resident shared-atlas texture bytes + page count,
    ///   - raw font-byte footprint held per font.
    /// Not a pass/fail gate on absolute MB (hardware-dependent); asserts only that work happened.
    /// </summary>
    [TestFixture]
    internal sealed class MemoryBudgetMeasurementTests
    {
        private const int ObjectCount = 100;
        private const float FontSize = 36f;

        private static string BuildWorkloadText()
        {
            const string unit =
                "The quick brown fox jumps over the lazy dog while typesetting mixed scripts here. " +
                "\u0627\u0644\u0646\u0635 \u0627\u0644\u0639\u0631\u0628\u064a \u064a\u062e\u062a\u0628\u0631 \u0627\u0644\u062a\u0634\u0643\u064a\u0644. " +
                "\u05d8\u05e7\u05e1\u05d8 \u05e2\u05d1\u05e8\u05d9 \u05dc\u05d1\u05d3\u05d9\u05e7\u05ea \u05db\u05d9\u05d5\u05d5\u05df. ";
            var sb = new StringBuilder(2600);
            while (sb.Length < 2405) sb.Append(unit);
            return sb.ToString(0, 2405);
        }

        [Test]
        public void Measure_CreationAllocation_And_RetainedFootprint()
        {
            string fontPath = MsdfTestUtil.FindNotoSansPath();
            if (fontPath == null) { Assert.Ignore("NotoSans fixture missing — measurement skipped."); return; }

            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 90, 0.10f, UniTextRenderMode.SDF, 1024);
            if (font == null) { Assert.Ignore("Font backend unavailable."); return; }

            long rawFontBytes = font.HasFontData ? font.FontData.LongLength : 0;
            string text = BuildWorkloadText();

            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font);
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();

            try
            {
                // Warm up: first build rasterizes glyphs + JITs the pipeline so neither is counted below.
                RunOneBuild(stack, appearance, text, warmRaster: true, out _, out _);

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

                int gc0Before = GC.CollectionCount(0);
                long monoBefore = Profiler.GetMonoUsedSizeLong();
                long totalMemBefore = GC.GetTotalMemory(false);

                long totalVerts = 0, totalTris = 0, retainedMeshBytes = 0;
                for (int i = 0; i < ObjectCount; i++)
                {
                    RunOneBuild(stack, appearance, text, warmRaster: false, out int verts, out int tris);
                    totalVerts += verts; totalTris += tris;
                    retainedMeshBytes += (long)verts * 48 + (long)tris * 4;
                }

                long monoGrowth = Profiler.GetMonoUsedSizeLong() - monoBefore;
                long totalMemGrowth = GC.GetTotalMemory(false) - totalMemBefore;
                int gc0 = GC.CollectionCount(0) - gc0Before;

                long atlasBytes = 0; int atlasPages = 0;
                var atlases = font.AtlasTextures;
                if (atlases != null)
                {
                    foreach (var t in atlases)
                        if (t != null) { atlasPages++; atlasBytes += (long)t.width * t.height; } // Alpha8 = 1 B/px
                }

                double MB(long b) => b / (1024.0 * 1024.0);
                Debug.Log($"[MemMeasure] objects={ObjectCount} chars/obj={text.Length} totalChars={(long)ObjectCount * text.Length}");
                Debug.Log($"[MemMeasure] mono-heap growth over {ObjectCount} builds (net, after returns) = {MB(monoGrowth):F1} MB; GC.GetTotalMemory net = {MB(totalMemGrowth):F1} MB; gc0 during = {gc0}");
                Debug.Log($"[MemMeasure] total verts={totalVerts:N0} tris={totalTris:N0}; retained mesh-buffer bytes if all live = {MB(retainedMeshBytes):F1} MB ({MB(retainedMeshBytes / Math.Max(1, ObjectCount)):F3} MB/object)");
                Debug.Log($"[MemMeasure] per-font atlas: pages={atlasPages} resident = {MB(atlasBytes):F2} MB (page {font.AtlasSize}x{font.AtlasSize} Alpha8)");
                Debug.Log($"[MemMeasure] raw font bytes held (one font, NotoSans) = {rawFontBytes:N0} B ({MB(rawFontBytes):F3} MB)");

                Assert.Greater(totalVerts, 0, "Measurement must have generated vertices (workload actually ran).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stack);
                UnityEngine.Object.DestroyImmediate(font);
            }
        }

        private static void RunOneBuild(UniTextFontStack stack, UniTextAppearance appearance, string text,
            bool warmRaster, out int vertexCount, out int triangleCount)
        {
            vertexCount = 0; triangleCount = 0;
            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(256);
            var tp = new TextProcessor(buffers);
            var fontProvider = new UniTextFontProvider(stack, appearance);
            tp.SetFontProvider(fontProvider);

            var settings = new TextProcessSettings
            {
                fontSize = FontSize,
                baseDirection = TextDirection.Auto,
                MaxWidth = 1200f,
                MaxHeight = TextProcessSettings.FloatMax,
                enableWordWrap = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            try
            {
                tp.EnsureFirstPass(text, settings);
                tp.EnsureLines(1200f, FontSize, wordWrap: true);
                tp.EnsurePositions(settings);

                if (warmRaster)
                {
                    var glyphSet = new List<uint>();
                    var seen = new HashSet<uint>();
                    var sg = tp.buf.shapedGlyphs.Span;
                    for (int i = 0; i < sg.Length; i++)
                    {
                        var gi = (uint)sg[i].glyphId;
                        if (gi != 0 && seen.Add(gi)) glyphSet.Add(gi);
                    }
                    if (glyphSet.Count > 0) stack.fonts[0].TryAddGlyphsBatch(glyphSet);
                }

                var gen = new UniTextMeshGenerator(fontProvider, buffers);
                gen.FontSize = FontSize;
                gen.defaultColor = new Color32(255, 255, 255, 255);
                gen.SetCanvasParametersCached(1f, false);
                gen.SetRectOffset(new Rect(0, 0, 1200, 100000));
                gen.SetHorizontalAlignment(HorizontalAlignment.Left);
                gen.GenerateMeshDataOnly(tp.PositionedGlyphs);
                vertexCount = gen.vertexCount;
                triangleCount = gen.triangleCount;
                gen.ReturnInstanceBuffers();
            }
            finally
            {
                buffers.EnsureReturnBuffers();
            }
        }
    }
}
