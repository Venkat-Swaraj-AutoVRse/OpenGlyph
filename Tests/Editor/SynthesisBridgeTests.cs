using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Pins the fix for the round-4 gap: setting weight/style through the component property
    /// (<see cref="TextProcessor.SetStyleSource"/>) on a family that LACKS the face must trigger
    /// UniText's synthetic <see cref="BoldModifier"/>/<see cref="ItalicModifier"/> exactly as
    /// <c>&lt;b&gt;</c>/<c>&lt;i&gt;</c> markup does — by populating the SAME AttributeKeys.Bold/Italic
    /// buffers (single source of truth) — while a family WITH a real face resolves it and the
    /// suppression (realBold/realItalic) stops synthetic double-styling.
    /// </summary>
    public class SynthesisBridgeTests
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

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string dir = FamilyDir();
            if (dir == null) Assert.Ignore("NotoSansFamily fixtures absent.");
            _regular = Load(dir, "NotoSans-Regular.ttf");
            _bold = Load(dir, "NotoSans-Bold.ttf");
            if (_regular == null || _bold == null) Assert.Ignore("Noto faces failed.");
            Shaper.ClearAllCaches();
        }

        [TearDown]
        public void TearDown()
        {
            Shaper.ClearAllCaches();
            foreach (var o in _created) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        private UniTextFontStack Stack(bool withBold)
        {
            var fam = ScriptableObject.CreateInstance<FontFamily>();
            fam.familyName = withBold ? "Full" : "RegularOnly";
            fam.AddFace(_regular);
            if (withBold) fam.AddFace(_bold);
            _created.Add(fam);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            stack.fonts.Add(_regular);
            stack.family = fam;
            _created.Add(stack);
            return stack;
        }

        // Lays out "Rg" with a weight-700 base spec (the component-property path) and returns the
        // Bold attribute buffer + whether the bold run was resolved to a real face.
        private (byte[] boldBuf, bool anyRealBold, int boldFontId) RunProperty(UniTextFontStack stack, bool markup)
        {
            var buffers = new UniTextBuffers();
            buffers.EnsureRentBuffers(32);
            var proc = new TextProcessor(buffers);
            var provider = new UniTextFontProvider(stack, null, 48f);
            proc.SetFontProvider(provider);

            if (markup)
            {
                // Markup path: whole-string <b> flags, base spec Normal.
                var flags = new byte[] { 1, 1 };
                proc.SetStyleSource(true, FontStyleSpec.Normal, flags, null);
            }
            else
            {
                // Property path: base spec weight 700, no per-cp flags.
                proc.SetStyleSource(true, new FontStyleSpec(700, 100, StyleAxis.Normal), null, null);
            }

            var settings = new TextProcessSettings { fontSize = 48f, baseDirection = TextDirection.Auto };
            proc.EnsureFirstPass("Rg", settings);

            bool anyRealBold = false; int boldId = 0;
            for (int i = 0; i < proc.buf.shapedRuns.count; i++)
            {
                var r = proc.buf.shapedRuns[i];
                if (r.realBold) { anyRealBold = true; boldId = r.fontId; }
            }
            var attr = buffers.GetAttributeData<PooledArrayAttribute<byte>>(AttributeKeys.Bold);
            byte[] copy = null;
            if (attr != null && attr.buffer.data != null)
            {
                copy = new byte[proc.buf.codepoints.count];
                for (int i = 0; i < copy.Length && i < attr.buffer.data.Length; i++) copy[i] = attr.buffer.data[i];
            }
            buffers.EnsureReturnBuffers();
            return (copy, anyRealBold, boldId);
        }

        private static bool AnyFlag(byte[] b) { if (b == null) return false; foreach (var v in b) if (v != 0) return true; return false; }

        [Test]
        public void Property_Weight700_RegularOnly_FlagsBoldForSyntheticModifier()
        {
            var (boldBuf, anyRealBold, _) = RunProperty(Stack(withBold: false), markup: false);
            Assert.IsTrue(AnyFlag(boldBuf), "Property weight 700 on a Regular-only family must set the Bold flag so synthetic BoldModifier fires.");
            Assert.IsFalse(anyRealBold, "No real bold face -> run is not realBold -> modifier is NOT suppressed.");
        }

        [Test]
        public void Property_Weight700_FamilyWithBold_ResolvesRealFace_NoSyntheticFlagEffect()
        {
            var (_, anyRealBold, boldId) = RunProperty(Stack(withBold: true), markup: false);
            Assert.IsTrue(anyRealBold, "A real bold face exists -> run is realBold -> synthetic bold is suppressed.");
            Assert.AreEqual(UniTextFontProvider.GetFontId(_bold), boldId, "The run must use the real Bold face.");
        }

        [Test]
        public void MarkupAndProperty_GiveIdenticalBoldFlagging_OnRegularOnly()
        {
            var markup = RunProperty(Stack(withBold: false), markup: true);
            var property = RunProperty(Stack(withBold: false), markup: false);
            Assert.AreEqual(AnyFlag(markup.boldBuf), AnyFlag(property.boldBuf),
                "Markup <b> and the weight property must flag bold identically on a Regular-only family.");
            Assert.AreEqual(markup.anyRealBold, property.anyRealBold,
                "Both paths resolve to the same (non-real-bold) face on a Regular-only family.");
        }
    }
}
