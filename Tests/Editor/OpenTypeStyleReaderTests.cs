using System.IO;
using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Verifies <see cref="OpenTypeStyleReader"/> parses weight/width/style from OS/2 + head.macStyle
    /// of real fonts, matching the known coordinates of the Noto Sans static faces.
    /// </summary>
    public class OpenTypeStyleReaderTests
    {
        private static string FamilyDir()
        {
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) return null;
            string pkgRoot = Directory.GetParent(Path.GetDirectoryName(noto))?.FullName;
            string dir = pkgRoot == null ? null : Path.Combine(pkgRoot, "Tests", "Editor", "Fonts", "NotoSansFamily");
            return (dir != null && Directory.Exists(dir)) ? dir : null;
        }

        private static FaceStyle Read(string dir, string file)
        {
            Assert.IsTrue(OpenTypeStyleReader.TryRead(File.ReadAllBytes(Path.Combine(dir, file)), out var s),
                $"OS/2 parse failed for {file}");
            return s;
        }

        [Test]
        public void Noto_Regular_Is400_Normal_100()
        {
            string dir = FamilyDir(); if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");
            var s = Read(dir, "NotoSans-Regular.ttf");
            Assert.AreEqual(400, s.weight);
            Assert.AreEqual(StyleAxis.Normal, s.style);
            Assert.AreEqual(100f, s.width, 0.01f);
        }

        [Test]
        public void Noto_Bold_Is700_Normal()
        {
            string dir = FamilyDir(); if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");
            var s = Read(dir, "NotoSans-Bold.ttf");
            Assert.AreEqual(700, s.weight);
            Assert.AreEqual(StyleAxis.Normal, s.style);
        }

        [Test]
        public void Noto_Italic_Is400_Italic()
        {
            string dir = FamilyDir(); if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");
            var s = Read(dir, "NotoSans-Italic.ttf");
            Assert.AreEqual(400, s.weight);
            Assert.AreEqual(StyleAxis.Italic, s.style);
        }

        [Test]
        public void Noto_BoldItalic_Is700_Italic()
        {
            string dir = FamilyDir(); if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");
            var s = Read(dir, "NotoSans-BoldItalic.ttf");
            Assert.AreEqual(700, s.weight);
            Assert.AreEqual(StyleAxis.Italic, s.style);
        }

        [Test]
        public void RobotoFlex_DefaultInstance_Parses()
        {
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched.");
            Assert.IsTrue(OpenTypeStyleReader.TryRead(File.ReadAllBytes(vf), out var s));
            // Default instance is upright regular-ish; just assert sane ranges.
            Assert.GreaterOrEqual(s.weight, 1);
            Assert.LessOrEqual(s.weight, 1000);
            Assert.AreEqual(StyleAxis.Normal, s.style);
        }

        [Test]
        public void GarbageBytes_FailGracefully()
        {
            Assert.IsFalse(OpenTypeStyleReader.TryRead(new byte[] { 1, 2, 3, 4, 5 }, out _));
            Assert.IsFalse(OpenTypeStyleReader.TryRead(null, out _));
        }
    }
}
