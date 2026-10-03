using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Runtime fallback to the operating system CJK fonts, used when no font in the component
    /// <see cref="UniTextFontStack"/> covers a Chinese / Japanese / Korean code point (the bundled
    /// default stack has no CJK font). Nothing is bundled: the system font file is read lazily, once
    /// per process, the first time such a code point is requested.
    /// </summary>
    /// <remarks>
    /// <para>Threading: file IO and font asset creation run on the MAIN thread only (in the prepare
    /// step before first pass, or on the calling thread when that is the main thread). A worker thread
    /// only uses fonts that are already loaded.</para>
    /// <para>Disable with <c>UniTextSettings.useSystemFontFallback = false</c>. See Documentation/SystemFontFallback.md.</para>
    /// </remarks>
    public static class SystemFontFallback
    {
        private sealed class Slot
        {
            public string[] paths;
            public bool attempted;
            public UniTextFont font;
            public string loadedPath;
        }

        private static readonly object gate = new();
        private static Slot[] slots;
        private static bool warned;

        /// <summary>
        /// Preferred language for picking a face inside a multi-language CJK collection
        /// (Noto Sans CJK .ttc): "ja", "ko", "zh-Hant"/"zh-TW"/"zh-HK", anything else = Simplified
        /// Chinese. Null (default) derives it from the current UI culture.
        /// </summary>
        public static string PreferredLanguage { get; set; }

        /// <summary>Paths of the system fonts that were actually loaded (diagnostics / tests).</summary>
        public static IReadOnlyList<string> LoadedPaths
        {
            get
            {
                var r = new List<string>();
                lock (gate)
                    if (slots != null)
                        foreach (var s in slots)
                            if (s.font != null) r.Add(s.loadedPath);
                return r;
            }
        }

        /// <summary>True for code points handled by the system CJK fallback.</summary>
        public static bool IsCjkCodepoint(uint cp)
        {
            return (cp >= 0x2E80 && cp <= 0x9FFF) ||      // radicals, CJK symbols, kana, bopomofo, enclosed, Han
                   (cp >= 0xAC00 && cp <= 0xD7AF) ||      // Hangul syllables
                   (cp >= 0x1100 && cp <= 0x11FF) ||      // Hangul Jamo
                   (cp >= 0xF900 && cp <= 0xFAFF) ||      // CJK compatibility ideographs
                   (cp >= 0xFE30 && cp <= 0xFE4F) ||      // CJK compatibility forms
                   (cp >= 0xFF00 && cp <= 0xFFEF) ||      // half/full-width forms
                   (cp >= 0x20000 && cp <= 0x3FFFF);      // Han extensions
        }

        private static bool IsHangul(uint cp) =>
            (cp >= 0xAC00 && cp <= 0xD7AF) || (cp >= 0x1100 && cp <= 0x11FF) || (cp >= 0x3130 && cp <= 0x318F);

        /// <summary>
        /// Returns the system font that covers <paramref name="cp"/>, loading it on first use when
        /// <paramref name="allowLoad"/> is true AND the caller is on the main thread. Null when the
        /// fallback is disabled, no system font exists, or none covers it.
        /// </summary>
        public static UniTextFont Resolve(uint cp, bool allowLoad = true)
        {
            if (!UniTextSettings.UseSystemFontFallback) return null;
            if (!IsCjkCodepoint(cp)) return null;
            if (allowLoad && !UniTextThreadGuard.IsMainThread) allowLoad = false;

            lock (gate)
            {
                slots ??= BuildSlots();
                var hangul = IsHangul(cp);
                for (var pass = 0; pass < slots.Length; pass++)
                {
                    // General first; Hangul puts Korean first.
                    var idx = hangul ? (pass == 0 ? 2 : pass == 1 ? 0 : 1) : pass;
                    var slot = slots[idx];
                    if (slot.font == null)
                    {
                        if (slot.attempted || !allowLoad) continue;
                        TryLoad(slot);
                        if (slot.font == null) { WarnIfNothingAvailable(); continue; }
                    }
                    if (Shaper.GetGlyphIndex(slot.font, cp) != 0) return slot.font;
                }
            }
            return null;
        }

        /// <summary>
        /// MAIN-THREAD ONLY. Called from the prepare step with a component text so a worker never needs
        /// to load a font: loads the system font if the text has a CJK code point the stack does not cover.
        /// </summary>
        internal static void PrepareForText(ReadOnlySpan<char> text, UniTextFontStack stack)
        {
            if (!UniTextSettings.UseSystemFontFallback || text.IsEmpty) return;
            for (var i = 0; i < text.Length; i++)
            {
                uint cp = text[i];
                if (cp < 0x1100) continue;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    cp = (uint)char.ConvertToUtf32(text[i], text[i + 1]);
                if (!IsCjkCodepoint(cp)) continue;
                if (stack != null && stack.FindFontForCodepoint(cp) != null) continue;
                Resolve(cp, true);
                if (AllAttempted()) return;
            }
        }

        private static bool AllAttempted()
        {
            lock (gate)
            {
                if (slots == null) return false;
                foreach (var s in slots) if (!s.attempted) return false;
                return true;
            }
        }

        private static void WarnIfNothingAvailable()
        {
            if (warned) return;
            foreach (var s in slots) if (!s.attempted || s.font != null) return;
            warned = true;
            Debug.LogWarning("[OpenGlyph] System CJK font fallback: no usable system CJK font was found (or it could not be read); " +
                             "CJK text will render as missing-glyph boxes. Bundle a CJK font in your font stack, or see " +
                             "Documentation/SystemFontFallback.md. Disable this fallback with UniTextSettings.useSystemFontFallback.");
        }

        private static void TryLoad(Slot slot)
        {
            slot.attempted = true;
            foreach (var path in slot.paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var bytes = File.ReadAllBytes(path);
                    var face = PickFace(path);
                    var font = UniTextFont.CreateFontAsset(bytes, faceIndex: face);
                    if (font == null && face != 0)
                        font = UniTextFont.CreateFontAsset(bytes);
                    if (font == null) continue;
                    font.name = "SystemCJK:" + Path.GetFileName(path);
                    font.hideFlags = HideFlags.HideAndDontSave;
                    slot.font = font;
                    slot.loadedPath = path;
                    return;
                }
                catch (Exception)
                {
                    // IO / permission failure: try the next candidate; summarised by the single warning.
                }
            }
        }

        // Noto Sans CJK collections order faces JP, KR, SC, TC, HK. Other collections: face 0.
        private static int PickFace(string path)
        {
            var name = Path.GetFileName(path);
            if (name == null || !name.StartsWith("NotoSansCJK", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                return 0;
            var lang = (PreferredLanguage ?? CultureInfo.CurrentUICulture.Name).ToLowerInvariant();
            if (lang.StartsWith("ja")) return 0;
            if (lang.StartsWith("ko")) return 1;
            if (lang.Contains("tw") || lang.Contains("hant") || lang.Contains("mo")) return 3;
            if (lang.Contains("hk")) return 4;
            return 2; // Simplified Chinese
        }

        private static Slot[] BuildSlots()
        {
            return new[]
            {
                new Slot { paths = GeneralPaths() },
                new Slot { paths = JapanesePaths() },
                new Slot { paths = KoreanPaths() },
            };
        }

        private static string FontsDir()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.Fonts); }
            catch { return @"C:\Windows\Fonts"; }
        }

        private static string[] GeneralPaths()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return new[] { Path.Combine(FontsDir(), "msyh.ttc"), @"C:\Windows\Fonts\msyh.ttc", @"C:\Windows\Fonts\simsun.ttc" };
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            return new[] { "/System/Library/Fonts/PingFang.ttc", "/System/Library/Fonts/Hiragino Sans GB.ttc",
                           "/System/Library/Fonts/Core/PingFang.ttc", "/System/Library/Fonts/Supplemental/Songti.ttc" };
#elif UNITY_ANDROID
            return new[] { "/system/fonts/NotoSansCJK-Regular.ttc", "/system/fonts/NotoSansCJKsc-Regular.otf",
                           "/system/fonts/DroidSansFallback.ttf", "/system/fonts/DroidSansFallbackFull.ttf" };
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            return new[] { "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
                           "/usr/share/fonts/google-noto-cjk/NotoSansCJK-Regular.ttc", "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
                           "/usr/share/fonts/OTF/NotoSansCJK-Regular.ttc", "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc" };
#else
            return Array.Empty<string>();
#endif
        }

        private static string[] JapanesePaths()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return new[] { Path.Combine(FontsDir(), "YuGothM.ttc"), @"C:\Windows\Fonts\YuGothM.ttc", @"C:\Windows\Fonts\msgothic.ttc", @"C:\Windows\Fonts\meiryo.ttc" };
#else
            return Array.Empty<string>();
#endif
        }

        private static string[] KoreanPaths()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return new[] { Path.Combine(FontsDir(), "malgun.ttf"), @"C:\Windows\Fonts\malgun.ttf", @"C:\Windows\Fonts\gulim.ttc" };
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            return new[] { "/System/Library/Fonts/AppleSDGothicNeo.ttc", "/System/Library/Fonts/Supplemental/AppleGothic.ttf" };
#else
            return Array.Empty<string>();
#endif
        }

        /// <summary>TEST ONLY: forget loaded fonts so a test can observe a fresh lazy load.</summary>
        internal static void ResetForTests()
        {
            lock (gate)
            {
                if (slots != null)
                    foreach (var s in slots)
                        if (s.font != null) UnityEngine.Object.DestroyImmediate(s.font);
                slots = null;
                warned = false;
            }
        }

        /// <summary>TEST ONLY: true when any system font has been loaded.</summary>
        internal static bool AnyLoaded
        {
            get { lock (gate) { if (slots == null) return false; foreach (var s in slots) if (s.font != null) return true; return false; } }
        }
    }
}
