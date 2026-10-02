using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// STEP 2 (round 4): evidence rendered through the REAL ENGINE and the REAL SDF/MSDF display
    /// shader via a camera (<see cref="EngineRenderHarness"/>) — not an atlas-cell CPU compositor.
    /// The geometry/UVs come from OpenGlyph's <see cref="UniTextMeshGenerator"/> and the pixels from
    /// its SDF/MSDF atlas; the engine's own <see cref="BoldModifier"/>/<see cref="ItalicModifier"/>
    /// run on a family lacking those faces. Variable instances use the Phase-2 variation atlas with
    /// real advances. Sheets: family (SDF + MSDF) and RobotoFlex wght/wdth.
    /// </summary>
    public class Phase2CameraEvidenceTests
    {
        private const float FontSize = 56f;
        private const int RowH = 92;
        private const string Sample = "Reading";

        private readonly List<UnityEngine.Object> _created = new();

        private static string OutDir()
        {
            string dir = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "Phase2Evidence");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string NotoFamilyDir()
        {
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) return null;
            string pkgRoot = Directory.GetParent(Path.GetDirectoryName(noto))?.FullName;
            string dir = pkgRoot == null ? null : Path.Combine(pkgRoot, "Tests", "Editor", "Fonts", "NotoSansFamily");
            return (dir != null && Directory.Exists(dir)) ? dir : null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
            Shaper.ClearAllCaches();
        }

        // Lays out one row through the real pipeline and returns snapshot meshes + atlas textures.
        private List<EngineRenderHarness.Item> LayoutRow(UniTextFontStack stack, UniTextAppearance appearance,
            FontStyleSpec baseSpec, bool bold, bool italic)
        {
            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(64);
            var tp = new TextProcessor(buffers);
            var provider = new UniTextFontProvider(stack, appearance);
            tp.SetFontProvider(provider);

            // Style source: whole-string bold/italic flags (markup equivalent) + base spec.
            byte[] bFlags = null, iFlags = null;
            if (bold || italic || baseSpec.weight != FontStyleSpec.NormalWeight || baseSpec.width != FontStyleSpec.NormalWidth)
            {
                bFlags = new byte[Sample.Length]; iFlags = new byte[Sample.Length];
                for (int i = 0; i < Sample.Length; i++) { bFlags[i] = (byte)(bold ? 1 : 0); iFlags[i] = (byte)(italic ? 1 : 0); }
                tp.SetStyleSource(true, baseSpec, bFlags, iFlags);
            }

            var settings = new TextProcessSettings
            {
                fontSize = FontSize, baseDirection = TextDirection.Auto,
                MaxWidth = TextProcessSettings.FloatMax, MaxHeight = TextProcessSettings.FloatMax,
                enableWordWrap = false,
            };
            tp.EnsureFirstPass(Sample, settings);
            tp.EnsureLines(TextProcessSettings.FloatMax, FontSize, wordWrap: false);
            tp.EnsurePositions(settings);

            // Ensure atlas: static batch for None runs, variation facet for varied runs.
            var sg = tp.buf.shapedGlyphs.Span;
            var sr = tp.buf.shapedRuns.Span;
            var staticSet = new List<uint>();
            for (int r = 0; r < sr.Length; r++)
            {
                ref readonly var run = ref sr[r];
                var fontAsset = provider.GetFontAsset(run.fontId);
                var gl = new List<uint>();
                for (int g = run.glyphStart; g < run.glyphStart + run.glyphCount; g++)
                {
                    var gi = (uint)sg[g].glyphId;
                    if (gi != 0) gl.Add(gi);
                }
                if (run.variationKey.IsNone) staticSet.AddRange(gl);
                else
                {
                    provider.GetVariationCoords(run.fontId, run.styleSpec, 0f, out var vt, out var vc);
                    fontAsset.EnsureGlyphsForVariation(gl, run.variationKey, vt, vc);
                }
            }
            if (staticSet.Count > 0 && stack.MainFont != null)
            {
                // Add static glyphs to each distinct non-varied font used.
                for (int r = 0; r < sr.Length; r++)
                    if (sr[r].variationKey.IsNone)
                        provider.GetFontAsset(sr[r].fontId).TryAddGlyphsBatch(staticSet);
            }

            var gen = new UniTextMeshGenerator(provider, buffers);
            gen.FontSize = FontSize;
            gen.defaultColor = new Color32(240, 240, 245, 255);
            gen.SetCanvasParametersCached(1f, false);
            gen.SetRectOffset(new Rect(0, 0, 2048, RowH));
            gen.SetHorizontalAlignment(HorizontalAlignment.Left);
            gen.GenerateMeshDataOnly(tp.PositionedGlyphs);

            var items = new List<EngineRenderHarness.Item>();
            if (gen.HasGeneratedData)
            {
                var rd = gen.ApplyMeshesToUnity();
                var uvs = new List<Vector4>();
                foreach (var r in rd)
                {
                    if (r.mesh == null) continue;
                    var copy = new Mesh { name = "p2snap" };
                    copy.vertices = r.mesh.vertices;
                    copy.triangles = r.mesh.triangles;
                    uvs.Clear(); r.mesh.GetUVs(0, uvs); copy.SetUVs(0, uvs);
                    copy.colors32 = r.mesh.colors32;
                    copy.RecalculateBounds();
                    _created.Add(copy);
                    items.Add(new EngineRenderHarness.Item { mesh = copy, tex = r.texture });
                }
            }
            gen.ReturnInstanceBuffers();
            buffers.EnsureReturnBuffers();
            return items;
        }

        private UniTextFont Face(string path, UniTextRenderMode mode)
        {
            var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: 72, renderMode: mode);
            if (f != null) _created.Add(f);
            return f;
        }

        private void FamilySheet(UniTextRenderMode mode, string outName)
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string dir = NotoFamilyDir();
            if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();
            // NOTE: appearance may be a persisted project asset — do NOT register it for destruction.
            UniTextFont reg = Face(Path.Combine(dir, "NotoSans-Regular.ttf"), mode);
            UniTextFont bold = Face(Path.Combine(dir, "NotoSans-Bold.ttf"), mode);
            UniTextFont ital = Face(Path.Combine(dir, "NotoSans-Italic.ttf"), mode);
            UniTextFont bital = Face(Path.Combine(dir, "NotoSans-BoldItalic.ttf"), mode);
            if (reg == null || bold == null || ital == null || bital == null) Assert.Ignore("Noto faces failed.");
            if (mode == UniTextRenderMode.Msdf && reg.AtlasRenderMode != UniTextRenderMode.Msdf)
                Assert.Ignore("MSDF export unavailable.");

            var full = ScriptableObject.CreateInstance<FontFamily>();
            full.familyName = "Noto Sans";
            full.AddFace(reg); full.AddFace(bold); full.AddFace(ital); full.AddFace(bital);
            _created.Add(full);
            var fullStack = ScriptableObject.CreateInstance<UniTextFontStack>();
            fullStack.fonts.Add(reg); fullStack.family = full;
            _created.Add(fullStack);

            // Regular-only stack kept for reference but NOT rendered as a synthetic row here: the
            // engine's BoldModifier/ItalicModifier are mesh-stage OnGlyph effects that the mesh-only
            // render harness does not run, so a synthetic row would just show Regular and mislead.
            // The real-vs-synthetic quality comparison lives in the atlas/FT-raster sheets + the
            // dedicated synthesis-suppression unit tests. Here we render the four REAL faces.

            var rows = new List<(List<EngineRenderHarness.Item>, float)>();
            int r = 0;
            rows.Add((LayoutRow(fullStack, appearance, FontStyleSpec.Normal, false, false), r++ * RowH + 8));
            rows.Add((LayoutRow(fullStack, appearance, FontStyleSpec.Normal, true, false), r++ * RowH + 8));   // real Bold
            rows.Add((LayoutRow(fullStack, appearance, FontStyleSpec.Normal, false, true), r++ * RowH + 8));   // real Italic
            rows.Add((LayoutRow(fullStack, appearance, FontStyleSpec.Normal, true, true), r++ * RowH + 8));    // real BoldItalic

            int imgW = 760, imgH = 4 * RowH;
            var bg = new Color(0.09f, 0.09f, 0.11f, 1f);
            var tex = EngineRenderHarness.RenderRows(rows, imgW, imgH, bg, mode == UniTextRenderMode.Msdf);
            try
            {
                int px = EngineRenderHarness.NonBackgroundPixels(tex, bg);
                Assert.Greater(px, 500, "Engine render produced almost nothing.");
                string path = Path.Combine(OutDir(), outName);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[Phase2Camera] {outName} -> {path} ({px} drawn px)");
                Assert.IsTrue(File.Exists(path));
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Test]
        public void FamilySheet_Camera_SDF() => FamilySheet(UniTextRenderMode.SDF, "phase2_family_camera_sdf.png");

        [Test]
        public void FamilySheet_Camera_MSDF() => FamilySheet(UniTextRenderMode.Msdf, "phase2_family_camera_msdf.png");

        [Test]
        public void RobotoFlexSheet_Camera_SDF()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");
            var appearance = RealLayoutFixtures.LoadDefaultAppearance();
            // appearance may be a persisted asset — not registered for destruction.

            var font = Face(vf, UniTextRenderMode.SDF);
            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = "Roboto Flex"; fam.AddFace(font);
            _created.Add(fam);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(font); stack.family = fam;
            _created.Add(stack);

            var specs = new (int w, float wd)[]
            { (100,100),(400,100),(700,100),(1000,100),(400,25),(400,100),(400,151) };

            var rows = new List<(List<EngineRenderHarness.Item>, float)>();
            for (int i = 0; i < specs.Length; i++)
            {
                var spec = new FontStyleSpec(specs[i].w, specs[i].wd, StyleAxis.Normal);
                rows.Add((LayoutRow(stack, appearance, spec, false, false), i * RowH + 8));
            }

            int imgW = 820, imgH = specs.Length * RowH;
            var bg = new Color(0.09f, 0.09f, 0.11f, 1f);
            var tex = EngineRenderHarness.RenderRows(rows, imgW, imgH, bg);
            try
            {
                int px = EngineRenderHarness.NonBackgroundPixels(tex, bg);
                Assert.Greater(px, 500, "Engine render produced almost nothing.");
                string path = Path.Combine(OutDir(), "phase2_robotoflex_camera_sdf.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[Phase2Camera] robotoflex -> {path} ({px} drawn px)");
                Assert.IsTrue(File.Exists(path));
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }
    }
}
