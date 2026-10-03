using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>Wave 1: Language-aware shaping: Language and the &lt;lang&gt; tag (locl, CJK system face).</summary>
    [TestFixture]
    public class Wave1LanguageTests : Wave1TestBase
    {
        // ================================================================== 2. Language

        // Cyrillic letters with Bulgarian / Serbian / Macedonian localized forms in Noto Sans (locl).
        private static readonly (string lang, string text)[] LoclCandidates =
        {
            ("sr", "\u0431"), ("mk", "\u0431"), ("bg", "\u0434"), ("bg", "\u0432"), ("bg", "\u0436"), ("bg", "\u043B"),
            ("ro", "\u015F"), ("pl", "\u00F3"),
        };

        [Test]
        public void Language_SelectsLoclForms_ComponentAndSpan()
        {
            Assert.IsTrue((bool)W1.StaticProp(typeof(HB), "SupportsLanguage"), "the native library must export ut_hb_buffer_set_language");

            foreach (var (l, s) in LoclCandidates)
            {
                var p = Glyphs(Make(s, name: "plain"))[0].glyphId;
                var q = Glyphs(Make(s, name: "local", setup: c => W1.Set(c, "Language", l)))[0].glyphId;
                Debug.Log($"[W1] locl probe {l} U+{(int)s[0]:X4}: default glyph {p}, {l} glyph {q}");
            }

            // Noto Sans (test fixture) has Serbian/Macedonian italic-style be (б) and Romanian s-comma for ş in locl.
            const string lang = "sr", text = "\u0431";
            var plainId = Glyphs(Make(text, name: "plain"))[0].glyphId;
            var localId = Glyphs(Make(text, name: "local", setup: c => W1.Set(c, "Language", lang)))[0].glyphId;
            Assert.AreNotEqual(plainId, localId, "sr: locl must select the Serbian form of U+0431");
            var roPlain = Glyphs(Make("\u015F", name: "roPlain"))[0].glyphId;
            var ro = Glyphs(Make("\u015F", name: "ro", setup: c => W1.Set(c, "Language", "ro")))[0].glyphId;
            Assert.AreNotEqual(roPlain, ro, "ro: locl must map s-cedilla to the s-comma form");

            // A <lang> span gives the span text the same form; text outside keeps the default form.
            var span = Make($"{text}<lang={lang}>{text}</lang>{text}", setup: AddLangTag);
            var sg = Glyphs(span);
            Assert.AreEqual(new[] { plainId, localId, plainId }, sg.Select(g => g.glyphId).ToArray(), $"<lang={lang}> span");

            // A span overrides the component language (back to a language without the form).
            var over = Make($"{text}<lang=en>{text}</lang>", setup: c => { AddLangTag(c); W1.Set(c, "Language", lang); });
            Assert.AreEqual(new[] { localId, plainId }, Glyphs(over).Select(g => g.glyphId).ToArray(), "<lang=en> inside a component language");

            // Unset language = previous behaviour; and setting it after render reshapes.
            var live = Make(text);
            Assert.AreEqual(plainId, Glyphs(live)[0].glyphId);
            W1.Set(live, "Language", lang);
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(localId, Glyphs(live)[0].glyphId, "Language setter must reshape");
            W1.Set(live, "Language", "");
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(plainId, Glyphs(live)[0].glyphId);
        }

        private static string WinFonts => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        private static string FontFileOf(UniText t, int glyphIndex = 0)
        {
            var g = Glyphs(t)[glyphIndex];
            var font = t.FontProvider.GetFontAsset(g.fontId);
            var path = (string)W1.CallStatic(typeof(SystemFontFallback), "PathOf", font);
            return path == null ? "(stack:" + font?.name + ")" : Path.GetFileName(path).ToLowerInvariant();
        }

        [Test]
        public void Language_PicksCjkSystemFace_PerComponentAndPerSpan()
        {
#if !(UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
            Assert.Ignore("Windows system font paths only.");
#endif
            foreach (var f in new[] { "msyh.ttc", "YuGothM.ttc", "msjh.ttc", "malgun.ttf" })
                if (!File.Exists(Path.Combine(WinFonts, f))) Assert.Ignore($"Windows font {f} not installed.");
            UniTextSettings.SetUseSystemFontFallbackForTests(true);

            const string han = "\u76F4"; // 直: different forms in Japanese / Chinese
            var def = Make(han, name: "def");
            var ja = Make(han, name: "ja", setup: c => W1.Set(c, "Language", "ja"));
            var hans = Make(han, name: "hans", setup: c => W1.Set(c, "Language", "zh-Hans"));
            var hant = Make(han, name: "hant", setup: c => W1.Set(c, "Language", "zh-Hant"));
            var ko = Make(han, name: "ko", setup: c => W1.Set(c, "Language", "ko-KR"));
            Debug.Log($"[W1] CJK faces: default={FontFileOf(def)} ja={FontFileOf(ja)} zh-Hans={FontFileOf(hans)} zh-Hant={FontFileOf(hant)} ko={FontFileOf(ko)}");

            Assert.AreEqual("msyh.ttc", FontFileOf(def), "unset language: the previous default (general face first)");
            Assert.AreEqual("yugothm.ttc", FontFileOf(ja), "ja: Japanese face");
            Assert.AreEqual("msyh.ttc", FontFileOf(hans), "zh-Hans: Simplified Chinese face");
            Assert.AreEqual("msjh.ttc", FontFileOf(hant), "zh-Hant: Traditional Chinese face");
            Assert.AreEqual("malgun.ttf", FontFileOf(ko), "ko: Korean face");
            Assert.AreNotEqual(Glyphs(ja)[0].fontId, Glyphs(hans)[0].fontId);

            // Per span, in one component whose language is zh-Hans.
            var mixed = Make($"{han}<lang=ja>{han}</lang><lang=zh-TW>{han}</lang>", name: "mixed",
                setup: c => { AddLangTag(c); W1.Set(c, "Language", "zh-Hans"); });
            Assert.AreEqual("msyh.ttc", FontFileOf(mixed, 0));
            Assert.AreEqual("yugothm.ttc", FontFileOf(mixed, 1), "<lang=ja> span");
            Assert.AreEqual("msjh.ttc", FontFileOf(mixed, 2), "<lang=zh-TW> span");

            // Two components with different languages do not share a cached face.
            def.Text = han + han;
            ja.Text = han + han;
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual("msyh.ttc", FontFileOf(def, 1));
            Assert.AreEqual("yugothm.ttc", FontFileOf(ja, 1));
        }

        [Test]
        public void Language_PanCjkFont_ShapesJapaneseAndChineseForms()
        {
            Assert.IsTrue((bool)W1.StaticProp(typeof(HB), "SupportsLanguage"), "the native library must export ut_hb_buffer_set_language");
            var path = FindPanCjkFont();
            var candidates = new List<(string path, bool pan)>();
            if (path != null) candidates.Add((path, true));
            // System CJK faces present on Windows. They are single-language designs, so they may not carry
            // locl forms for other languages; they are used only when no pan-CJK font is installed.
            foreach (var f in new[] { "msyh.ttc", "YuGothM.ttc", "msjh.ttc" })
            {
                var p = Path.Combine(WinFonts, f);
                if (File.Exists(p)) candidates.Add((p, false));
            }
            if (candidates.Count == 0) Assert.Ignore("No CJK font installed.");

            var differed = new List<string>();
            foreach (var (fontPath, pan) in candidates)
            {
                var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(fontPath), 48);
                if (font == null) continue;
                _extraFonts.Add(font);
                var stack = ScriptableObject.CreateInstance<UniTextFontStack>();
                stack.fonts.Add(font);
                _extraStacks.Add(stack);

                var all = true;
                foreach (var cp in new[] { "\u76F4", "\u9AA8", "\u4ECA" })
                {
                    var ja = Glyphs(Make(cp, stack: stack, setup: c => W1.Set(c, "Language", "ja")))[0].glyphId;
                    var sc = Glyphs(Make(cp, stack: stack, setup: c => W1.Set(c, "Language", "zh-Hans")))[0].glyphId;
                    Debug.Log($"[W1] {Path.GetFileName(fontPath)} U+{(int)cp[0]:X4}: ja={ja} zh-Hans={sc}");
                    if (pan) Assert.AreNotEqual(ja, sc, $"{Path.GetFileName(fontPath)} U+{(int)cp[0]:X4} must use different forms for ja and zh-Hans");
                    if (ja == sc) all = false;
                }
                if (all) differed.Add(Path.GetFileName(fontPath));
            }
            if (path == null && differed.Count == 0)
                Assert.Ignore("No pan-CJK font (Noto Sans CJK / Source Han Sans) installed, and the installed system CJK fonts " +
                              "have no ja / zh-Hans locl forms for U+76F4 U+9AA8 U+4ECA (see the [W1] log).");
        }

        private static string FindPanCjkFont()
        {
            // A machine without one installed can point the test at a downloaded copy.
            var overridePath = Environment.GetEnvironmentVariable("OPENGLYPH_PANCJK_FONT");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;
            var dirs = new List<string> { WinFonts };
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local)) dirs.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
            dirs.AddRange(new[] { "/usr/share/fonts/opentype/noto", "/usr/share/fonts/noto-cjk", "/System/Library/Fonts" });
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d))
                {
                    var n = Path.GetFileName(f);
                    if ((n.StartsWith("NotoSansCJK", StringComparison.OrdinalIgnoreCase) || n.StartsWith("SourceHanSans", StringComparison.OrdinalIgnoreCase)) &&
                        (n.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
                        return f;
                }
            }
            return null;
        }

        [Test]
        public void LanguageTags_ClassifyCjk()
        {
            var t = W1.Type("LightSide.LanguageTags");
            var m = t.GetMethod("ClassifyCjk", BindingFlags.Public | BindingFlags.Static);
            string C(string s) => m.Invoke(null, new object[] { s }).ToString();
            Assert.AreEqual("Japanese", C("ja-JP"));
            Assert.AreEqual("Korean", C("ko"));
            Assert.AreEqual("SimplifiedChinese", C("zh"));
            Assert.AreEqual("SimplifiedChinese", C("zh-Hans-HK"));
            Assert.AreEqual("TraditionalChinese", C("zh_TW"));
            Assert.AreEqual("TraditionalChinese", C("zh-Hant"));
            Assert.AreEqual("HongKongChinese", C("zh-HK"));
            Assert.AreEqual("HongKongChinese", C("yue"));
            Assert.AreEqual("Default", C("sr-Cyrl"));
            Assert.AreEqual("Default", C(""));
        }
    }
}
