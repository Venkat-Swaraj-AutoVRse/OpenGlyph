using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Live-pipeline wiring: a <see cref="TextProcessor"/> whose font provider carries a Noto
    /// <see cref="FontFamily"/> itemizes a bold span into a run resolved to the REAL Bold face, with
    /// the run carrying the expected <see cref="FontStyleSpec"/>. The bold span is supplied through
    /// <see cref="TextProcessor.SetStyleSource"/> (the hook the component fills from parsed markup).
    /// </summary>
    public class LivePipelineStyleTests
    {
        private readonly List<UnityEngine.Object> _created = new();
        private UniTextFont _regular, _bold;

        private static string FamilyDir()
        {
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) return null;
            string pkgRoot = Directory.GetParent(Path.GetDirectoryName(noto))?.FullName;
            string dir = pkgRoot == null ? null : Path.Combine(pkgRoot, "Tests", "Editor", "Fonts", "NotoSansFamily");
            return (dir != null && Directory.Exists(dir)) ? dir : null;
        }

        private UniTextFont Load(string dir, string f)
        {
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(Path.Combine(dir, f)), 48);
            if (font != null) _created.Add(font);
            return font;
        }

        private UniTextFontProvider _provider;
        private int _boldId, _regularId;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string dir = FamilyDir();
            if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");
            _regular = Load(dir, "NotoSans-Regular.ttf");
            _bold = Load(dir, "NotoSans-Bold.ttf");
            if (_regular == null || _bold == null) Assert.Ignore("Noto faces failed to load.");

            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = "Noto Sans";
            fam.AddFace(_regular); fam.AddFace(_bold);
            _created.Add(fam);

            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(_regular);   // main for codepoint fallback
            stack.family = fam;
            _created.Add(stack);

            _provider = new UniTextFontProvider(stack, null, 48f);
            _regularId = UniTextFontProvider.GetFontId(_regular);
            _boldId = UniTextFontProvider.GetFontId(_bold);
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        [Test]
        public void Provider_ResolvesBoldMarkup_ToBoldFaceId()
        {
            var id = _provider.ResolveStyledFontId(FontStyleSpec.Normal, markupBold: true, markupItalic: false,
                out bool rb, out bool ri);
            Assert.AreEqual(_boldId, id, "Bold markup must resolve to the Bold face id.");
            Assert.IsTrue(rb, "A real bold face exists -> realBold true.");
            Assert.IsFalse(ri);
        }

        [Test]
        public void Provider_NoMarkup_ResolvesRegularId()
        {
            var id = _provider.ResolveStyledFontId(FontStyleSpec.Normal, false, false, out bool rb, out bool ri);
            Assert.AreEqual(_regularId, id);
            Assert.IsFalse(rb); Assert.IsFalse(ri);
        }

        [Test]
        public void TextProcessor_BoldSpan_ProducesBoldFaceRun()
        {
            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(64);
            var proc = new TextProcessor(buffers);
            proc.SetFontProvider(_provider);

            // "RbR": middle char bold. Supply per-codepoint bold flags.
            string text = "RbR";
            var bold = new byte[] { 0, 1, 0 };
            proc.SetStyleSource(active: true, FontStyleSpec.Normal, bold, italic: null);

            var settings = new TextProcessSettings { fontSize = 48f, baseDirection = TextDirection.Auto };
            proc.EnsureFirstPass(text, settings);

            // Expect a run whose fontId is the Bold face (the middle 'b' span) and runs for Regular.
            bool sawBold = false, sawRegular = false, boldRunRealBold = false;
            for (int i = 0; i < proc.buf.shapedRuns.count; i++)
            {
                var run = proc.buf.shapedRuns[i];
                if (run.fontId == _boldId) { sawBold = true; boldRunRealBold = run.realBold; }
                if (run.fontId == _regularId) sawRegular = true;
            }
            buffers.EnsureReturnBuffers();
            Assert.IsTrue(sawBold, "A run must be resolved to the Bold face for the <b> span.");
            Assert.IsTrue(sawRegular, "The non-bold spans must stay on the Regular face.");
            Assert.IsTrue(boldRunRealBold, "The bold run must be flagged realBold (synthesis suppressed).");
        }

        [Test]
        public void TextProcessor_VariableFont_Weight400_vs_700_DifferentRunWidth()
        {
            string vfPath = MsdfTestUtil.FindRobotoFlexPath();
            if (vfPath == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");
            var vf = UniTextFont.CreateFontAsset(File.ReadAllBytes(vfPath), 48);
            _created.Add(vf);

            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = "Roboto Flex";
            fam.AddFace(vf);
            _created.Add(fam);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(vf);
            stack.family = fam;
            _created.Add(stack);
            var provider = new UniTextFontProvider(stack, null, 48f);

            float Width(int weight)
            {
                var buffers = new UniTextBuffers();
                buffers.EnsureRentBuffers(32);
                var proc = new TextProcessor(buffers);
                proc.SetFontProvider(provider);
                var spec = new FontStyleSpec(weight, 100, StyleAxis.Normal);
                proc.SetStyleSource(true, spec, null, null);
                var settings = new TextProcessSettings { fontSize = 48f, baseDirection = TextDirection.Auto };
                proc.EnsureFirstPass("Reading", settings);
                float w = 0f;
                for (int i = 0; i < proc.buf.shapedRuns.count; i++) w += proc.buf.shapedRuns[i].width;
                buffers.EnsureReturnBuffers();
                return w;
            }

            Shaper.ClearAllCaches();
            float w400 = Width(400);
            float w700 = Width(700);
            Assert.AreNotEqual(w400, w700,
                $"Same text through TextProcessor must differ in width between wght 400 ({w400}) and 700 ({w700}).");
        }

        [Test]
        public void PositionedGlyphs_BoldSpan_CarryRealBold_ForSynthesisSuppression()
        {
            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = "Noto Sans";
            fam.AddFace(_regular); fam.AddFace(_bold);
            _created.Add(fam);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(_regular); stack.family = fam;
            _created.Add(stack);
            var provider = new UniTextFontProvider(stack, null, 48f);

            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(32);
            var proc = new TextProcessor(buffers);
            proc.SetFontProvider(provider);
            // "RbR": middle bold.
            proc.SetStyleSource(true, FontStyleSpec.Normal, new byte[] { 0, 1, 0 }, null);
            var settings = new TextProcessSettings { fontSize = 48f, baseDirection = TextDirection.Auto,
                MaxWidth = TextProcessSettings.FloatMax, MaxHeight = TextProcessSettings.FloatMax };
            proc.EnsureFirstPass("RbR", settings);
            proc.EnsureLines(TextProcessSettings.FloatMax, 48f, wordWrap: false);
            proc.EnsurePositions(settings);

            int boldId = UniTextFontProvider.GetFontId(_bold);
            bool anyRealBold = false, anyRegularNotBold = false;
            var pg = proc.PositionedGlyphs;
            for (int i = 0; i < pg.Length; i++)
            {
                if (pg[i].fontId == boldId) { if (pg[i].realBold) anyRealBold = true; }
                else if (!pg[i].realBold) anyRegularNotBold = true;
            }
            buffers.EnsureReturnBuffers();
            Assert.IsTrue(anyRealBold, "The bold-face glyph must carry realBold=true (suppresses faux bold).");
            Assert.IsTrue(anyRegularNotBold, "Regular glyphs must carry realBold=false.");
        }
    }
}
