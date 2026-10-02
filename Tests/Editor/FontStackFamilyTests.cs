using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Verifies the stack-level styled-font contract that the live component relies on: a
    /// <see cref="UniTextFontStack"/> carrying a <see cref="FontFamily"/> resolves <c>&lt;b&gt;</c>
    /// to the REAL Bold face, the resolved face's HarfBuzz advances equal the Bold face's own
    /// advances (and differ from Regular), and synthetic bold is NOT requested when a real bold face
    /// exists. Uses the four real OFL Noto static faces.
    /// </summary>
    public class FontStackFamilyTests
    {
        private readonly List<Object> _created = new();
        private UniTextFont _regular, _bold, _italic, _boldItalic;

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

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string dir = FamilyDir();
            if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");
            _regular = Load(dir, "NotoSans-Regular.ttf");
            _bold = Load(dir, "NotoSans-Bold.ttf");
            _italic = Load(dir, "NotoSans-Italic.ttf");
            _boldItalic = Load(dir, "NotoSans-BoldItalic.ttf");
            if (_regular == null || _bold == null || _italic == null || _boldItalic == null)
                Assert.Ignore("Noto faces failed to load.");
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private UniTextFontStack StackWithFamily()
        {
            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = "Noto Sans";
            fam.AddFace(_regular); fam.AddFace(_bold); fam.AddFace(_italic); fam.AddFace(_boldItalic);
            _created.Add(fam);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.family = fam;
            _created.Add(stack);
            return stack;
        }

        [Test]
        public void Stack_WithFamily_BoldMarkup_SelectsRealBoldFace()
        {
            var stack = StackWithFamily();
            var face = stack.ResolveStyledFont(FontStyleSpec.Normal, markupBold: true, markupItalic: false, out var how);
            Assert.AreSame(_bold, face, "<b> must resolve to the real Bold face via the family.");
            Assert.IsFalse(how.synthesizeBold, "A real bold face exists -> no synthetic bold (CSS font-synthesis).");
        }

        [Test]
        public void ResolvedBoldFace_HarfBuzzAdvance_EqualsBoldFace_AndDiffersFromRegular()
        {
            var stack = StackWithFamily();
            var boldFace = stack.ResolveStyledFont(FontStyleSpec.Normal, true, false, out _);

            // Advance of 'R' shaped through the RESOLVED face must equal the Bold face's own advance.
            Assert.IsTrue(Shaper.TryGetGlyphInfo(boldFace, 'R', 48f, out var giResolved, out var advResolved));
            Assert.IsTrue(Shaper.TryGetGlyphInfo(_bold, 'R', 48f, out var giBold, out var advBold));
            Assert.AreEqual(giBold, giResolved);
            Assert.AreEqual(advBold, advResolved, 1e-4f, "Resolved-bold advance must equal the Bold face advance.");

            // And it must differ from Regular (proof it is really the bold face, not synthetic).
            Assert.IsTrue(Shaper.TryGetGlyphInfo(_regular, 'R', 48f, out _, out var advRegular));
            Assert.AreNotEqual(advRegular, advBold, "Bold 'R' advance must differ from Regular 'R'.");
        }

        [Test]
        public void Stack_NoFamily_RequestsSyntheticForMarkup()
        {
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(_regular);
            _created.Add(stack);
            var face = stack.ResolveStyledFont(FontStyleSpec.Normal, markupBold: true, markupItalic: false, out var how);
            Assert.AreSame(_regular, face, "No family -> the single main face.");
            Assert.IsTrue(how.synthesizeBold, "No real bold face -> synthetic bold requested (legacy behaviour).");
        }

        [Test]
        public void Stack_WithFamily_BoldItalic_SelectsRealBoldItalic_NoSynthesis()
        {
            var stack = StackWithFamily();
            var face = stack.ResolveStyledFont(FontStyleSpec.Normal, true, true, out var how);
            Assert.AreSame(_boldItalic, face);
            Assert.IsFalse(how.synthesizeBold);
            Assert.IsFalse(how.synthesizeItalic);
        }
    }
}
