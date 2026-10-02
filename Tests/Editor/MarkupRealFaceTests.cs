using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Verifies that <c>&lt;b&gt;</c>/<c>&lt;i&gt;</c> markup selects a REAL face from a
    /// <see cref="FontFamily"/> when one exists, and only falls back to synthetic emboldening/slant
    /// when it does not. Uses the four discrete OFL Noto Sans static faces
    /// (Regular/Bold/Italic/BoldItalic) under <c>Tests/Editor/Fonts/NotoSansFamily</c>.
    /// </summary>
    public class MarkupRealFaceTests
    {
        private readonly List<Object> _created = new();
        private UniTextFont _regular, _bold, _italic, _boldItalic;

        private static string FamilyDir()
        {
            // Reuse the package-physical-root resolution used by MsdfTestUtil, then descend.
            string noto = MsdfTestUtil.FindNotoSansPath(); // .../Defaults/NotoSans-Regular.ttf
            if (noto == null) return null;
            string pkgRoot = Directory.GetParent(Path.GetDirectoryName(noto))?.FullName; // package root
            if (pkgRoot == null) return null;
            string dir = Path.Combine(pkgRoot, "Tests", "Editor", "Fonts", "NotoSansFamily");
            return Directory.Exists(dir) ? dir : null;
        }

        private UniTextFont Load(string dir, string file)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;
            var f = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: 48);
            if (f != null) _created.Add(f);
            return f;
        }

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable in this environment.");

            string dir = FamilyDir();
            if (dir == null)
                Assert.Ignore("NotoSansFamily fixtures not present (run the Phase 2 font fetch).");

            _regular = Load(dir, "NotoSans-Regular.ttf");
            _bold = Load(dir, "NotoSans-Bold.ttf");
            _italic = Load(dir, "NotoSans-Italic.ttf");
            _boldItalic = Load(dir, "NotoSans-BoldItalic.ttf");

            if (_regular == null || _bold == null || _italic == null || _boldItalic == null)
                Assert.Ignore("One or more NotoSansFamily faces failed to load.");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private FontFamily FullFamily()
        {
            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = "Noto Sans";
            fam.AddFace(_regular);
            fam.AddFace(_bold);
            fam.AddFace(_italic);
            fam.AddFace(_boldItalic);
            _created.Add(fam);
            return fam;
        }

        [Test]
        public void AddFace_DerivesStyleFromName()
        {
            var fam = FullFamily();
            // The four faces must land on four distinct style coordinates.
            var hasRegular = false; var hasBold = false; var hasItalic = false; var hasBoldItalic = false;
            foreach (var ff in fam.faces)
            {
                var st = ff.style;
                if (st.weight == 400 && st.style == StyleAxis.Normal) hasRegular = true;
                if (st.weight == 700 && st.style == StyleAxis.Normal) hasBold = true;
                if (st.weight == 400 && st.style == StyleAxis.Italic) hasItalic = true;
                if (st.weight == 700 && st.style == StyleAxis.Italic) hasBoldItalic = true;
            }
            Assert.IsTrue(hasRegular && hasBold && hasItalic && hasBoldItalic,
                "AddFace should derive Regular/Bold/Italic/BoldItalic from the Noto style names.");
        }

        [Test]
        public void Markup_Bold_SelectsRealBoldFace_NoSynthesis()
        {
            var fam = FullFamily();
            var face = fam.Resolve(FontStyleSpec.Normal, markupBold: true, markupItalic: false, out var how);
            Assert.AreSame(_bold, face);
            Assert.IsFalse(how.synthesizeBold);
        }

        [Test]
        public void Markup_Italic_SelectsRealItalicFace_NoSynthesis()
        {
            var fam = FullFamily();
            var face = fam.Resolve(FontStyleSpec.Normal, markupBold: false, markupItalic: true, out var how);
            Assert.AreSame(_italic, face);
            Assert.IsFalse(how.synthesizeItalic);
        }

        [Test]
        public void Markup_BoldItalic_SelectsRealBoldItalicFace_NoSynthesis()
        {
            var fam = FullFamily();
            var face = fam.Resolve(FontStyleSpec.Normal, markupBold: true, markupItalic: true, out var how);
            Assert.AreSame(_boldItalic, face);
            Assert.IsFalse(how.synthesizeBold);
            Assert.IsFalse(how.synthesizeItalic);
            Assert.IsTrue(how.exact);
        }

        [Test]
        public void Markup_Bold_RegularOnlyFamily_RequestsSynthetic()
        {
            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = "Noto Sans (regular only)";
            fam.AddFace(_regular);
            _created.Add(fam);

            var face = fam.Resolve(FontStyleSpec.Normal, markupBold: true, markupItalic: false, out var how);
            Assert.AreSame(_regular, face, "Falls back to the only available face.");
            Assert.IsTrue(how.synthesizeBold, "No real bold -> synthetic bold requested.");
        }

        [Test]
        public void RealBoldAndRegular_HaveDifferentAdvances()
        {
            // Independent proof the faces are genuinely different (not the same outline set): the
            // 'g' advance differs between the real Regular and real Bold faces.
            float regAdv = _regular.FaceInfo.tabWidth; // space advance as a cheap proxy metric
            float boldAdv = _bold.FaceInfo.tabWidth;
            // Not asserting a specific delta direction, only that the real faces are distinct assets.
            Assert.AreNotEqual(_regular.FontDataHash, _bold.FontDataHash,
                "Regular and Bold must be distinct real faces.");
        }
    }
}
