using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Verifies <see cref="FontFamily.Match"/> against the CSS Fonts font-matching algorithm:
    /// the width→style→weight stage order and the exact font-weight fallback ladder.
    /// These tests are pure (no font bytes): faces are distinct <see cref="UniTextFont"/> marker
    /// instances tagged by name, selection reads only the attached <see cref="FaceStyle"/>.
    /// </summary>
    public class FontFamilyMatchTests
    {
        private readonly List<Object> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created)
                if (o != null) Object.DestroyImmediate(o);
            created.Clear();
        }

        private UniTextFont Face(string tag)
        {
            var f = ScriptableObject.CreateInstance<UniTextFont>();
            f.name = tag;
            created.Add(f);
            return f;
        }

        private FontFamily Family(params (string tag, int weight, float width, StyleAxis style)[] faces)
        {
            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = "Test";
            foreach (var (tag, weight, width, style) in faces)
                fam.faces.Add(new FontFamily.FamilyFace(Face(tag), new FaceStyle(weight, width, style)));
            created.Add(fam);
            return fam;
        }

        private static FontStyleSpec Spec(int weight, float width = 100f, StyleAxis style = StyleAxis.Normal)
            => new FontStyleSpec(weight, width, style);

        // ---- Weight ladder ------------------------------------------------------

        [Test]
        public void Weight_Exact_Wins()
        {
            var fam = Family(("r", 400, 100, StyleAxis.Normal), ("b", 700, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(700), out var how);
            Assert.AreEqual("b", r.name);
            Assert.IsTrue(how.exact);
            Assert.IsFalse(how.synthesizeBold);
        }

        [Test]
        public void Weight_600_Missing_PicksNearestHeavier_700()
        {
            // >500 prefers heavier ascending: 600 -> 700 (not 400).
            var fam = Family(("r", 400, 100, StyleAxis.Normal), ("b", 700, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(600), out var how);
            Assert.AreEqual("b", r.name);
            Assert.IsFalse(how.exact);
        }

        [Test]
        public void Weight_300_Missing_PicksLighter_200_then_100()
        {
            // <400 prefers lighter descending: 300 -> 200 (not 100, not 400).
            var fam = Family(
                ("thin", 100, 100, StyleAxis.Normal),
                ("extralight", 200, 100, StyleAxis.Normal),
                ("regular", 400, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(300), out _);
            Assert.AreEqual("extralight", r.name);
        }

        [Test]
        public void Weight_300_OnlyHeavierAvailable_FallsUp()
        {
            // <400 with nothing lighter: ascend.
            var fam = Family(("regular", 400, 100, StyleAxis.Normal), ("bold", 700, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(300), out _);
            Assert.AreEqual("regular", r.name);
        }

        [Test]
        public void Weight_400_PrefersFiveHundred_BeforeDescending()
        {
            // CSS 400 special band: 400 -> 500 before going below.
            var fam = Family(
                ("300", 300, 100, StyleAxis.Normal),
                ("500", 500, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(400), out _);
            Assert.AreEqual("500", r.name);
        }

        [Test]
        public void Weight_500_PrefersFourHundred_BeforeDescending()
        {
            var fam = Family(
                ("300", 300, 100, StyleAxis.Normal),
                ("400", 400, 100, StyleAxis.Normal),
                ("600", 600, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(500), out _);
            Assert.AreEqual("400", r.name);
        }

        [Test]
        public void Weight_900_PicksHeaviestAvailable()
        {
            var fam = Family(
                ("400", 400, 100, StyleAxis.Normal),
                ("700", 700, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(900), out var how);
            Assert.AreEqual("700", r.name);
            // 700 already satisfies the bold threshold, so no synthetic emboldening is applied
            // even though 900 was requested (we do not faux-bold beyond a real bold face).
            Assert.IsFalse(how.synthesizeBold);
        }

        // ---- Synthesis flags ----------------------------------------------------

        [Test]
        public void Bold_NoRealBold_RequestsSyntheticBold()
        {
            var fam = Family(("regular", 400, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(700), out var how);
            Assert.AreEqual("regular", r.name);
            Assert.IsTrue(how.synthesizeBold);
        }

        [Test]
        public void Bold_RealBoldExists_NoSynthetic()
        {
            var fam = Family(("regular", 400, 100, StyleAxis.Normal), ("bold", 700, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(700), out var how);
            Assert.AreEqual("bold", r.name);
            Assert.IsFalse(how.synthesizeBold);
        }

        [Test]
        public void Italic_NoRealItalic_RequestsSyntheticItalic()
        {
            var fam = Family(("regular", 400, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(400, 100, StyleAxis.Italic), out var how);
            Assert.AreEqual("regular", r.name);
            Assert.IsTrue(how.synthesizeItalic);
        }

        [Test]
        public void Italic_RealItalicExists_NoSynthetic()
        {
            var fam = Family(
                ("regular", 400, 100, StyleAxis.Normal),
                ("italic", 400, 100, StyleAxis.Italic));
            var r = fam.Match(Spec(400, 100, StyleAxis.Italic), out var how);
            Assert.AreEqual("italic", r.name);
            Assert.IsFalse(how.synthesizeItalic);
        }

        [Test]
        public void BoldItalic_RealFaceExists_NoSynthetic()
        {
            var fam = Family(
                ("regular", 400, 100, StyleAxis.Normal),
                ("bold", 700, 100, StyleAxis.Normal),
                ("italic", 400, 100, StyleAxis.Italic),
                ("bolditalic", 700, 100, StyleAxis.Italic));
            var r = fam.Match(Spec(700, 100, StyleAxis.Italic), out var how);
            Assert.AreEqual("bolditalic", r.name);
            Assert.IsTrue(how.exact);
            Assert.IsFalse(how.synthesizeBold);
            Assert.IsFalse(how.synthesizeItalic);
        }

        // ---- Style substitution -------------------------------------------------

        [Test]
        public void Style_Italic_SubstitutesOblique_BeforeNormal()
        {
            var fam = Family(
                ("regular", 400, 100, StyleAxis.Normal),
                ("oblique", 400, 100, StyleAxis.Oblique));
            var r = fam.Match(Spec(400, 100, StyleAxis.Italic), out var how);
            Assert.AreEqual("oblique", r.name);
            // A real oblique satisfies the slant, so no synthetic italic.
            Assert.IsFalse(how.synthesizeItalic);
        }

        // ---- Width stage --------------------------------------------------------

        [Test]
        public void Width_Condensed_PrefersNarrower_WhenBelowNormal()
        {
            // desired 75% (<=100): prefer narrower (<=75) then wider.
            var fam = Family(
                ("normal", 400, 100, StyleAxis.Normal),
                ("semicond", 400, 87.5f, StyleAxis.Normal),
                ("cond", 400, 75, StyleAxis.Normal));
            var r = fam.Match(Spec(400, 75), out var how);
            Assert.AreEqual("cond", r.name);
            Assert.IsTrue(how.exact);
        }

        [Test]
        public void Width_Expanded_PrefersWider_WhenAboveNormal()
        {
            // desired 125% (>100): prefer wider (>=125) then narrower; here nearest wider is 150.
            var fam = Family(
                ("normal", 400, 100, StyleAxis.Normal),
                ("expanded", 400, 150, StyleAxis.Normal));
            var r = fam.Match(Spec(400, 125), out _);
            Assert.AreEqual("expanded", r.name);
        }

        [Test]
        public void Width_NarrowerFiltered_ThenWeightApplies()
        {
            // Width stage reduces to the 75% group; weight ladder then picks bold within it.
            var fam = Family(
                ("cond-reg", 400, 75, StyleAxis.Normal),
                ("cond-bold", 700, 75, StyleAxis.Normal),
                ("normal-bold", 700, 100, StyleAxis.Normal));
            var r = fam.Match(Spec(700, 75), out var how);
            Assert.AreEqual("cond-bold", r.name);
            Assert.IsTrue(how.exact);
        }

        [Test]
        public void EmptyFamily_ReturnsNull()
        {
            var fam = ScriptableObject.CreateInstance<FontFamily>();
            created.Add(fam);
            Assert.IsNull(fam.Match(FontStyleSpec.Normal, out _));
        }
    }
}
