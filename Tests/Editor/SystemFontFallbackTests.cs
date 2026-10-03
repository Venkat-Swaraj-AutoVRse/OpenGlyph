using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LightSide.Tests
{
    /// <summary>
    /// System font fallback: code points no stack font covers use an installed OS font (any script),
    /// with one warning per script per session; disabled = tofu; covered text never touches system fonts.
    /// </summary>
    /// <remarks>
    /// New test hooks (<c>SystemFontFinder.DirectoriesOverride</c>, <c>SystemFontFallback.AttemptedScriptCount</c>)
    /// are reached by reflection so this file also compiles against a build without them.
    /// </remarks>
    [TestFixture]
    public class SystemFontFallbackTests
    {
        private const string Cjk = "你好世界 こんにちは 안녕하세요";
        private const string Tamil = "தமிழ்";       // U+0BA4 ...
        private const string Georgian = "ქართული";   // U+10E5 ...
        private const string Ethiopic = "ሰላም";      // U+1230 ...

        private UniTextFontStack _stack;
        private UniTextFont _font;
        private GameObject _canvasGo;
        private bool _savedSetting;
        private bool _savedParallel;
        private string _emptyDir;

        private static string WinFonts => System.Environment.GetFolderPath(System.Environment.SpecialFolder.Fonts);

        private static bool WindowsFontsPresent()
        {
            var d = WinFonts;
            return File.Exists(Path.Combine(d, "msyh.ttc")) && File.Exists(Path.Combine(d, "malgun.ttf")) &&
                   (File.Exists(Path.Combine(d, "YuGothM.ttc")) || File.Exists(Path.Combine(d, "msgothic.ttc")));
        }

        private static void RequireWindowsFonts(params string[] files)
        {
#if !(UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
            Assert.Ignore("Windows system font paths only.");
#endif
            foreach (var f in files)
                if (!File.Exists(Path.Combine(WinFonts, f)))
                    Assert.Ignore($"Windows font {f} not installed.");
        }

        private static void SetDirectoriesOverride(string[] dirs)
        {
            var t = typeof(SystemFontFallback).Assembly.GetType("LightSide.SystemFontFinder");
            t?.GetField("DirectoriesOverride", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.SetValue(null, dirs);
        }

        /// <summary>-1 when the hook does not exist.</summary>
        private static int AttemptedScriptCount()
        {
            var p = typeof(SystemFontFallback).GetProperty("AttemptedScriptCount", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            return p == null ? -1 : (int)p.GetValue(null);
        }

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            var noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            SegHelper.EnsureUnicode();
            SharedGlyphAtlas.Clear();
            _savedSetting = UniTextSettings.UseSystemFontFallback;
            _savedParallel = UniText.UseParallel;
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);
            _canvasGo = new GameObject("Canvas", typeof(Canvas));
            _canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            SystemFontFallback.ResetForTests();
            SetDirectoriesOverride(null);
            SharedFontCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(_savedSetting);
            UniText.UseParallel = _savedParallel;
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
            Object.DestroyImmediate(_stack);
            Object.DestroyImmediate(_font);
            SetDirectoriesOverride(null);
            SystemFontFallback.ResetForTests();
            SharedFontCache.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
            if (_emptyDir != null) { try { Directory.Delete(_emptyDir, true); } catch { } _emptyDir = null; }
        }

        private UniText Make(string text, string name = "t", bool update = true)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_canvasGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(1400, 400);
            var t = go.AddComponent<UniText>();
            t.FontStack = _stack;
            t.FontSize = 24;
            t.Text = text;
            if (update) Canvas.ForceUpdateCanvases();
            return t;
        }

        private static int CountNotdef(UniText t)
        {
            var n = 0;
            foreach (var g in t.ResultGlyphs) if (g.glyphId == 0) n++;
            return n;
        }

        private static bool Loaded(string fileName)
        {
            foreach (var p in SystemFontFallback.LoadedPaths)
                if (string.Equals(Path.GetFileName(p), fileName, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static Regex UsingWarning(string script, string cp, string component, string file) => new(
            $@"^\[OpenGlyph\] No font in the font stack covers {script} \(U\+{cp} in ""{component}""\); using system font ""{Regex.Escape(file)}""\. " +
            "Add a font for this script to the font stack for consistent results across devices\\.$",
            RegexOptions.IgnoreCase);

        // ---------------------------------------------------------------- CJK (existing behaviour)

        [Test]
        public void Cjk_UsesSystemFont_NoNotdef()
        {
#if !(UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
            Assert.Ignore("Windows system font paths only.");
#endif
            if (!WindowsFontsPresent()) Assert.Ignore("Windows CJK fonts (msyh/malgun/YuGoth) not present.");
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "must be lazy: nothing loaded before first CJK text.");

            var t = Make(Cjk);

            Assert.IsTrue(SystemFontFallback.AnyLoaded, "system font loads on first CJK code point.");
            Assert.Greater(t.ResultGlyphs.Length, 0);
            Assert.AreEqual(0, CountNotdef(t), "CJK must shape to real glyphs (no .notdef).");
        }

        [Test]
        public void SettingOff_StaysTofu()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(false);
            var t = Make(Cjk);
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "nothing may load when disabled.");
            Assert.Greater(CountNotdef(t), 0, "with the fallback off, CJK stays .notdef (tofu).");
        }

        [Test]
        public void NonCjkText_NeverLoadsSystemFont()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            Make("Hello world");
            Assert.IsFalse(SystemFontFallback.AnyLoaded);
        }

        [Test]
        public void AndroidFontsXml_MapsScriptTagsToFiles()
        {
            var t = typeof(SystemFontFallback).Assembly.GetType("LightSide.SystemFontFinder");
            var m = t?.GetMethod("ParseFontsXml", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(m, "SystemFontFinder.ParseFontsXml missing (no general system font fallback).");
            const string xml = @"<familyset version=""23"">
  <family name=""sans-serif""><font weight=""400"" style=""normal"">Roboto-Regular.ttf</font></family>
  <family lang=""und-Taml"" variant=""elegant"">
    <font weight=""400"" style=""normal"">NotoSansTamil-VF.ttf
      <axis tag=""wght"" stylevalue=""400""/></font>
  </family>
  <family lang=""und-Ethi""><font weight=""400"" style=""normal"" postScriptName=""NotoSansEthiopic"">NotoSansEthiopic-VF.ttf</font></family>
  <family lang=""ja""><font weight=""400"" style=""normal"" index=""0"">NotoSansCJK-Regular.ttc</font></family>
  <family lang=""und-Geor,und-Geok""><font weight=""400"" style=""normal"">NotoSansGeorgian-VF.ttf</font></family>
</familyset>";
            var map = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);
            m.Invoke(null, new object[] { xml, "/system/fonts", map });
            CollectionAssert.AreEqual(new[] { "/system/fonts/NotoSansTamil-VF.ttf" }, map["Taml"]);
            CollectionAssert.AreEqual(new[] { "/system/fonts/NotoSansEthiopic-VF.ttf" }, map["Ethi"]);
            CollectionAssert.AreEqual(new[] { "/system/fonts/NotoSansGeorgian-VF.ttf" }, map["Geor"]);
            Assert.IsFalse(map.ContainsKey("ja"), "language-only tags carry no script");
        }

        // ---------------------------------------------------------------- any script

        [Test]
        public void UncoveredScripts_UseSystemFont_NoNotdef_OneWarningPerScript()
        {
            RequireWindowsFonts("Nirmala.ttc", "sylfaen.ttf", "ebrima.ttf");
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            foreach (var s in new[] { Tamil, Georgian, Ethiopic })
                Assert.IsNull(_stack.FindFontForCodepoint((uint)char.ConvertToUtf32(s, 0)),
                    "precondition: the test stack (Noto Sans) must not cover " + s);
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "lazy: nothing loaded before uncovered text.");

            LogAssert.Expect(LogType.Warning, UsingWarning("Tamil", "0BA4", "Tamil1", "Nirmala.ttc"));
            LogAssert.Expect(LogType.Warning, UsingWarning("Georgian", "10E5", "Georgian1", "sylfaen.ttf"));
            LogAssert.Expect(LogType.Warning, UsingWarning("Ethiopic", "1230", "Ethiopic1", "ebrima.ttf"));

            var tamil = Make(Tamil, "Tamil1");
            var georgian = Make(Georgian, "Georgian1");
            var ethiopic = Make(Ethiopic, "Ethiopic1");

            Assert.IsTrue(Loaded("Nirmala.ttc"), "Tamil must use Nirmala UI. Loaded: " + string.Join(", ", SystemFontFallback.LoadedPaths));
            Assert.IsTrue(Loaded("sylfaen.ttf"), "Georgian must use Sylfaen. Loaded: " + string.Join(", ", SystemFontFallback.LoadedPaths));
            Assert.IsTrue(Loaded("ebrima.ttf"), "Ethiopic must use Ebrima. Loaded: " + string.Join(", ", SystemFontFallback.LoadedPaths));
            foreach (var t in new[] { tamil, georgian, ethiopic })
            {
                Assert.Greater(t.ResultGlyphs.Length, 0, t.name + ": no glyphs");
                Assert.AreEqual(0, CountNotdef(t), t.name + ": must shape to real glyphs (no .notdef).");
            }

            // More components and rebuilds, mixed scripts: no further warnings.
            var mixed = Make("Hello " + Tamil + " " + Georgian + " " + Ethiopic, "Mixed");
            Assert.AreEqual(0, CountNotdef(mixed), "mixed text: no .notdef");
            tamil.Text = Tamil + " " + Tamil;
            georgian.Text = Georgian + "!";
            Canvas.ForceUpdateCanvases();
            Make(Tamil, "Tamil2");
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(0, CountNotdef(tamil), "rebuild: no .notdef");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void UncoveredScript_SettingOff_TofuAndNoSystemFont()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(false);
            var t = Make(Tamil + " " + Georgian);
            Assert.Greater(CountNotdef(t), 0, "with the fallback off, uncovered scripts stay .notdef.");
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "nothing may load when disabled.");
            var attempted = AttemptedScriptCount();
            if (attempted >= 0) Assert.AreEqual(0, attempted, "no system font lookup may run when disabled.");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void CoveredLatinText_NoSystemFont_NoWarning()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            var t = Make("Hello world, café naïve — “quoted” 123.");
            t.Text = "Another rebuild, ÀÉÎÕÜ ß.";
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(0, CountNotdef(t));
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "fully covered text must not load a system font.");
            var attempted = AttemptedScriptCount();
            if (attempted >= 0) Assert.AreEqual(0, attempted, "fully covered text must not scan for system fonts.");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ScriptWithNoSystemFont_WarnsOnceAsMissing_CachesNegative()
        {
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            _emptyDir = Path.Combine(Path.GetTempPath(), "og_sysfont_empty_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_emptyDir);
            SetDirectoriesOverride(new[] { _emptyDir }); // no installed fonts at all

            LogAssert.Expect(LogType.Warning, new Regex(
                @"^\[OpenGlyph\] No font in the font stack covers Tamil \(U\+0BA4 in ""Missing1""\) and no installed system font " +
                @"covers it either; it will render as missing glyphs\. Add a font for this script to the font stack\.$"));

            var t = Make(Tamil, "Missing1");
            Assert.Greater(CountNotdef(t), 0, "no font anywhere: missing glyphs");
            Assert.IsFalse(SystemFontFallback.AnyLoaded);

            t.Text = Tamil + " " + Tamil;
            Canvas.ForceUpdateCanvases();
            Make(Tamil, "Missing2");
            Canvas.ForceUpdateCanvases();

            Assert.AreEqual(1, AttemptedScriptCount(), "the negative result is cached: one lookup for the script, not one per rebuild.");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ParallelPath_MatchesSerial()
        {
            RequireWindowsFonts("Nirmala.ttc", "sylfaen.ttf", "ebrima.ttf");
            if (!UniTextWorkerPool.IsParallelSupported) Assert.Ignore("Single-core host: parallel path not supported.");
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
            UniTextThreadGuard.CaptureMainThread();

            UniText.UseParallel = true;
            UniTextThreadGuard.ResetViolationCount();
            var parallelComps = BuildBatch();
            var tookParallel = typeof(UniText).GetField("useParallel", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (tookParallel is bool b0) Assert.IsTrue(b0, "precondition: the batch must take the parallel (worker) path");
            var parallel = Snapshot(parallelComps);
            var parallelNotdef = new List<int>();
            foreach (var t in parallelComps) parallelNotdef.Add(CountNotdef(t));
            Assert.AreEqual(0L, Interlocked.Read(ref UniTextThreadGuard.ViolationCount), "parallel pass touched main-thread-only APIs");
            var parallelLoaded = new List<string>(SystemFontFallback.LoadedPaths);

            // Fresh state, serial.
            Object.DestroyImmediate(_canvasGo);
            _canvasGo = new GameObject("Canvas", typeof(Canvas));
            _canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            SystemFontFallback.ResetForTests();
            SharedFontCache.Clear();
            UniText.UseParallel = false;
            var serial = Snapshot(BuildBatch());

            CollectionAssert.AreEquivalent(new List<string>(SystemFontFallback.LoadedPaths), parallelLoaded, "same system fonts");
            Assert.AreEqual(serial.Count, parallel.Count);
            for (var i = 0; i < serial.Count; i++)
            {
                Assert.Greater(parallel[i].Length, 0, $"parallel component {i}: no glyphs");
                Assert.AreEqual(0, parallelNotdef[i], $"parallel component {i}: no .notdef");
                var a = serial[i];
                var b = parallel[i];
                Assert.AreEqual(a.Length, b.Length, $"component {i}: glyph count");
                for (var g = 0; g < a.Length; g++)
                {
                    Assert.AreEqual(a[g].glyphId, b[g].glyphId, $"component {i} glyph {g}: id");
                    Assert.AreEqual(a[g].cluster, b[g].cluster, $"component {i} glyph {g}: cluster");
                    Assert.AreEqual(a[g].x, b[g].x, 1e-3f, $"component {i} glyph {g}: x");
                    Assert.AreEqual(a[g].y, b[g].y, 1e-3f, $"component {i} glyph {g}: y");
                }
            }
        }

        private static List<PositionedGlyph[]> Snapshot(List<UniText> comps)
        {
            var r = new List<PositionedGlyph[]>();
            foreach (var t in comps) r.Add(t.ResultGlyphs.ToArray());
            return r;
        }

        // 6 components x ~150 chars: crosses ParallelCharacterThreshold (500) and MinComponentsForParallel (3).
        private List<UniText> BuildBatch()
        {
            var list = new List<UniText>();
            var total = 0;
            for (var i = 0; i < 6; i++)
            {
                var s = $"Item {i}: {Tamil} {Georgian} {Ethiopic} the quick brown fox jumps over the lazy dog. " +
                        $"{Tamil} {Tamil} {Georgian} {Ethiopic} more text to pad the line out nicely {i}{i}{i}.";
                total += s.Length;
                list.Add(Make(s, "P" + i, update: false));
            }
            Assert.Greater(total, 500, "precondition: batch must cross the parallel threshold");
            Canvas.ForceUpdateCanvases();
            return list;
        }
    }
}
