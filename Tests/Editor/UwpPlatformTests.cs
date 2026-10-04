using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace LightSide.Tests
{
    /// <summary>
    /// Universal Windows Platform (Unity WSAPlayer) support: the UWP native plugins are imported for
    /// WSAPlayer only, with the right CPU, and no other native plugin of the package is enabled for
    /// WSAPlayer (a duplicate unitext_native.dll would fail the player build).
    /// </summary>
    [TestFixture]
    public class UwpPluginImportTests
    {
        private const string Root = "Packages/com.openglyph.text/Plugins/";

        private static readonly BuildTarget[] OtherTargets =
        {
            BuildTarget.StandaloneWindows, BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64,
            BuildTarget.StandaloneOSX, BuildTarget.Android, BuildTarget.iOS, BuildTarget.tvOS, BuildTarget.WebGL,
        };

        private static List<PluginImporter> PackagePlugins()
        {
            var r = new List<PluginImporter>();
            foreach (var imp in PluginImporter.GetAllImporters())
                if (imp != null && imp.assetPath.StartsWith(Root, System.StringComparison.Ordinal)) r.Add(imp);
            return r;
        }

        [TestCase("WSA/x64/unitext_native.dll", "X64")]
        [TestCase("WSA/ARM64/unitext_native.dll", "ARM64")]
        public void WsaRuntimeDll_EnabledOnlyForWSAPlayer_WithItsCpu(string relPath, string cpu)
        {
            var imp = AssetImporter.GetAtPath(Root + relPath) as PluginImporter;
            Assert.IsNotNull(imp, $"no PluginImporter for {Root + relPath}");
            Assert.IsFalse(imp.GetCompatibleWithAnyPlatform(), "must not be 'Any Platform'");
            Assert.IsTrue(imp.GetCompatibleWithPlatform(BuildTarget.WSAPlayer), "must be enabled for WSAPlayer");
            Assert.AreEqual(cpu, imp.GetPlatformData(BuildTarget.WSAPlayer, "CPU"), "WSAPlayer CPU");
            Assert.IsFalse(imp.GetCompatibleWithEditor(), "a UWP (app container) DLL must not load in the Editor");
            foreach (var t in OtherTargets)
                Assert.IsFalse(imp.GetCompatibleWithPlatform(t), $"must not be enabled for {t}");
        }

        [Test]
        public void NonWsaPlugins_AreNotEnabledForWSAPlayer()
        {
            var checkedDesktopDlls = 0;
            foreach (var imp in PackagePlugins())
            {
                if (imp.assetPath.StartsWith(Root + "WSA/", System.StringComparison.Ordinal)) continue;
                Assert.IsFalse(imp.GetCompatibleWithPlatform(BuildTarget.WSAPlayer),
                    $"{imp.assetPath} is enabled for WSAPlayer (desktop/other-platform binary in a UWP build)");
                if (imp.assetPath.StartsWith(Root + "Windows/", System.StringComparison.Ordinal)) checkedDesktopDlls++;
            }
            Assert.AreEqual(3, checkedDesktopDlls, "expected the 3 desktop Windows DLLs (x86_64 runtime + editor, ARM64 runtime)");
        }

        [TestCase("X64")]
        [TestCase("ARM64")]
        public void ExactlyOneRuntimeLibrary_PerWsaCpu(string cpu)
        {
            var matches = new List<string>();
            foreach (var imp in PackagePlugins())
            {
                if (!imp.GetCompatibleWithPlatform(BuildTarget.WSAPlayer)) continue;
                if (!Path.GetFileName(imp.assetPath).StartsWith("unitext_native", System.StringComparison.OrdinalIgnoreCase)) continue;
                var c = imp.GetPlatformData(BuildTarget.WSAPlayer, "CPU");
                if (c == cpu || c == "AnyCPU" || string.IsNullOrEmpty(c)) matches.Add(imp.assetPath);
            }
            Assert.AreEqual(1, matches.Count, $"WSAPlayer/{cpu} native runtime libraries: {string.Join(", ", matches)}");
        }
    }

    /// <summary>
    /// System font fallback where the platform cannot read installed fonts (UWP players): Resolve returns
    /// null, nothing is loaded or scanned, one warning per session, and a component renders missing
    /// glyphs without throwing. The platform switch is forced through the
    /// <c>SystemFontFallback.PlatformSupportedOverride</c> test seam (reached by reflection so this file
    /// also compiles against a build without it).
    /// </summary>
    [TestFixture]
    public class SystemFontFallbackUnsupportedPlatformTests
    {
        private const string Tamil = "தமிழ்"; // U+0BA4 ...
        private const string Cjk = "你好";

        private UniTextFontStack _stack;
        private UniTextFont _font;
        private GameObject _canvasGo;
        private bool _savedSetting;
        private bool _savedParallel;

        private static FieldInfo OverrideField => typeof(SystemFontFallback).GetField("PlatformSupportedOverride",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        private static void SetPlatformSupported(bool? value)
        {
            var f = OverrideField;
            if (f == null) Assert.Fail("SystemFontFallback.PlatformSupportedOverride is missing (no platform switch).");
            f.SetValue(null, value);
        }

        private static string WarningText()
        {
            var f = typeof(SystemFontFallback).GetField("UnsupportedPlatformWarning",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (f == null) Assert.Fail("SystemFontFallback.UnsupportedPlatformWarning is missing.");
            return (string)f.GetValue(null);
        }

        private static Regex UnsupportedWarning(string cp) =>
            new("^" + Regex.Escape(WarningText()) + @" \(first uncovered: U\+" + cp + @"\)$");

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
            SharedFontCache.Clear();
            UniTextSettings.SetUseSystemFontFallbackForTests(true);
        }

        [TearDown]
        public void TearDown()
        {
            OverrideField?.SetValue(null, null);
            UniTextSettings.SetUseSystemFontFallbackForTests(_savedSetting);
            UniText.UseParallel = _savedParallel;
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
            Object.DestroyImmediate(_stack);
            Object.DestroyImmediate(_font);
            SystemFontFallback.ResetForTests();
            SharedFontCache.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        [Test]
        public void EditorDefault_IsSupported()
        {
            SetPlatformSupported(null);
            var p = typeof(SystemFontFallback).GetProperty("IsSupportedOnThisPlatform", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(p, "SystemFontFallback.IsSupportedOnThisPlatform is missing.");
            Assert.IsTrue((bool)p.GetValue(null), "the Editor (and every non-UWP player) supports the fallback");
        }

        [Test]
        public void Resolve_ReturnsNull_WarnsOnce_LoadsNothing()
        {
            SetPlatformSupported(false);
            LogAssert.Expect(LogType.Warning, UnsupportedWarning("0BA4"));
            Assert.IsNull(SystemFontFallback.Resolve(0x0BA4u));                       // Tamil
            Assert.IsNull(SystemFontFallback.Resolve(0x4F60u));                       // CJK: no second warning
            Assert.IsNull(SystemFontFallback.Resolve(0x10D0u, CjkLanguage.Japanese)); // Georgian
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "no system font may be loaded");
            Assert.AreEqual(0, SystemFontFallback.LoadedPaths.Count);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void PrepareForText_DoesNoWork()
        {
            SetPlatformSupported(false);
            var before = SystemFontFallback.PrepareForTextCalls;
            SystemFontFallback.PrepareForText(new System.ReadOnlySpan<char>((Tamil + Cjk).ToCharArray()), _stack);
            Assert.AreEqual(before, SystemFontFallback.PrepareForTextCalls, "prepare must return before scanning");
            Assert.IsFalse(SystemFontFallback.AnyLoaded);
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Component_UncoveredScripts_RenderMissingGlyphs_OneWarning_NoThrow(bool parallel)
        {
            SetPlatformSupported(false);
            UniText.UseParallel = parallel;
            LogAssert.Expect(LogType.Warning, new Regex("^" + Regex.Escape(WarningText()) + @" \(first uncovered: U\+[0-9A-F]{4}\)$"));
            var go = new GameObject("uwp", typeof(RectTransform));
            go.transform.SetParent(_canvasGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(1400, 400);
            var t = go.AddComponent<UniText>();
            t.FontStack = _stack;
            t.FontSize = 24;
            t.Text = "Hello " + Tamil + " " + Cjk;
            Assert.DoesNotThrow(() => Canvas.ForceUpdateCanvases());
            var notdef = 0;
            foreach (var g in t.ResultGlyphs) if (g.glyphId == 0) notdef++;
            Assert.Greater(notdef, 0, "uncovered scripts render as missing glyphs");
            Assert.IsFalse(SystemFontFallback.AnyLoaded, "no system font may be loaded");
            t.Text = Cjk + " " + Tamil + " again";
            Canvas.ForceUpdateCanvases();
            LogAssert.NoUnexpectedReceived();
        }
    }
}
