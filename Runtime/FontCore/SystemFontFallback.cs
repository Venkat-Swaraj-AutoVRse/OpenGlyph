using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Runtime fallback to installed operating-system fonts, used when no font in the component
    /// <see cref="UniTextFontStack"/> covers a code point. Works for any script: CJK (with
    /// language-aware face picking), and every other script (Tamil, Bengali, Georgian, Ethiopic,
    /// Tibetan, ...) through <see cref="SystemFontFinder"/>. Nothing is bundled: a system font file is
    /// read lazily, once per process, the first time an uncovered code point of its script is seen.
    /// </summary>
    /// <remarks>
    /// <para>Each uncovered script logs ONE warning per session: either which system font is used,
    /// or that no installed font covers it and it renders as missing glyphs. Bundle a font for the
    /// script in the font stack for consistent results across devices.</para>
    /// <para>Threading: file IO and font asset creation run on the MAIN thread only (in the prepare
    /// step before the first pass, or on the calling thread when that is the main thread). A worker
    /// thread only reads cached results (loaded fonts and cached negative results).</para>
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

        /// <summary>Lookup state of one non-CJK script (positive or negative, cached for the session).</summary>
        private sealed class ScriptEntry
        {
            public bool attempted;
            public UniTextFont font;
            public string loadedPath;
        }

        private const int CjkWarnKey = -1;

        private static readonly object gate = new();
        private static Slot[] slots;
        private static readonly Dictionary<int, ScriptEntry> scriptEntries = new();
        private static readonly Dictionary<string, UniTextFont> fontsByFile = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<UniTextFont, string> pathByFont = new();
        private static readonly List<UniTextFont> loadedFonts = new(); // every loaded fallback font, load order
        private static readonly HashSet<int> warnedScripts = new();
        private static readonly HashSet<int> prepareSearched = new();
        private static readonly HashSet<ulong> stackCovered = new();
        private const int MaxStackCoveredEntries = 1 << 16;
        private static readonly List<(string message, UnityEngine.Object context)> pendingWarnings = new();
        private static volatile int loadedCount;

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
                    foreach (var f in loadedFonts)
                        if (pathByFont.TryGetValue(f, out var p)) r.Add(p);
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

        private static UnicodeScript ScriptOf(uint cp) =>
            UnicodeData.IsInitialized ? UnicodeData.Provider.GetScript((int)cp) : UnicodeScript.Unknown;

        /// <summary>Scripts that get their own system-font lookup (Common / Inherited / Unknown do not).</summary>
        private static bool IsRealScript(UnicodeScript s) =>
            s != UnicodeScript.Unknown && s != UnicodeScript.Common && s != UnicodeScript.Inherited;

        /// <summary>Warning key: the script, or <see cref="CjkWarnKey"/> for CJK code points of Common script.</summary>
        private static int WarnKey(uint cp, UnicodeScript script, bool cjk)
        {
            if (!cjk) return (int)script;
            return script is UnicodeScript.Han or UnicodeScript.Hiragana or UnicodeScript.Katakana
                or UnicodeScript.Hangul or UnicodeScript.Bopomofo ? (int)script : CjkWarnKey;
        }

        /// <summary>
        /// Returns the system font that covers <paramref name="cp"/>, loading it on first use when
        /// <paramref name="allowLoad"/> is true AND the caller is on the main thread. Null when the
        /// fallback is disabled, no system font exists, or none covers it.
        /// </summary>
        public static UniTextFont Resolve(uint cp, bool allowLoad = true) => Resolve(cp, allowLoad, null);

        internal static UniTextFont Resolve(uint cp, bool allowLoad, UnityEngine.Object context)
        {
            if (!UniTextSettings.UseSystemFontFallback) return null;
            if (allowLoad && !UniTextThreadGuard.IsMainThread) allowLoad = false;

            var cjk = IsCjkCodepoint(cp);
            var script = ScriptOf(cp);
            if (!cjk && !IsRealScript(script))
            {
                // Punctuation, digits, combining marks: never trigger a lookup of their own, but may
                // use a fallback font already loaded for a real script (e.g. a danda after Devanagari).
                if (loadedCount == 0) return null;
                lock (gate) return FromLoadedLocked(cp);
            }

            UniTextFont result;
            lock (gate)
            {
                result = cjk ? ResolveCjkLocked(cp, allowLoad) : ResolveScriptLocked(script, cp, allowLoad);
                if (result == null && loadedCount > 0) result = FromLoadedLocked(cp);
                if (allowLoad) ReportLocked(WarnKey(cp, script, cjk), script, cjk, cp, result, context);
            }
            FlushWarnings();
            return result;
        }

        private static UniTextFont FromLoadedLocked(uint cp)
        {
            foreach (var f in loadedFonts)
                if (Shaper.GetGlyphIndex(f, cp) != 0) return f;
            return null;
        }

        private static UniTextFont ResolveCjkLocked(uint cp, bool allowLoad)
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
                    if (slot.font == null) continue;
                }
                if (Shaper.GetGlyphIndex(slot.font, cp) != 0) return slot.font;
            }
            return null;
        }

        private static UniTextFont ResolveScriptLocked(UnicodeScript script, uint cp, bool allowLoad)
        {
            if (!scriptEntries.TryGetValue((int)script, out var e))
            {
                if (!allowLoad) return null;
                scriptEntries[(int)script] = e = new ScriptEntry();
            }
            if (!e.attempted)
            {
                if (!allowLoad) return null;
                e.attempted = true;
                LoadForScript(e, script, cp);
            }
            return e.font != null && Shaper.GetGlyphIndex(e.font, cp) != 0 ? e.font : null;
        }

        /// <summary>
        /// MAIN-THREAD ONLY. Called from the prepare step with a component text so a worker never needs
        /// to load a font: for every code point the stack does not cover, resolves (and if needed loads)
        /// the system font for its script and logs the once-per-script warning. Once a script has been
        /// resolved (positively or negatively) its code points are skipped without any stack lookup.
        /// </summary>
        internal static void PrepareForText(ReadOnlySpan<char> text, UniTextFontStack stack, UnityEngine.Object context = null)
        {
            if (!UniTextSettings.UseSystemFontFallback || text.IsEmpty) return;
            for (var i = 0; i < text.Length; i++)
            {
                uint cp = text[i];
                if (cp < 0x80) continue;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = (uint)char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                var cjk = IsCjkCodepoint(cp);
                var script = ScriptOf(cp);
                if (!cjk && !IsRealScript(script)) continue;

                lock (gate)
                {
                    // Already resolved for the session: workers read the cached result.
                    if (warnedScripts.Contains(WarnKey(cp, script, cjk)) && (!cjk || AllAttemptedLocked())) continue;
                }

                if (stack != null)
                {
                    // Main-thread-only cache of (stack, code point) pairs the stack covers, so covered
                    // non-ASCII text (Cyrillic, Arabic, accented Latin) is not re-searched every rebuild.
                    var key = ((ulong)(uint)stack.GetInstanceID() << 32) | cp;
                    if (stackCovered.Contains(key)) continue;
                    prepareSearched.Clear();
                    if (stack.FindFontForCodepoint(cp, prepareSearched) != null)
                    {
                        if (stackCovered.Count >= MaxStackCoveredEntries) stackCovered.Clear();
                        stackCovered.Add(key);
                        continue;
                    }
                }
                Resolve(cp, true, context);
            }
        }

        private static bool AllAttemptedLocked()
        {
            if (slots == null) return false;
            foreach (var s in slots) if (!s.attempted) return false;
            return true;
        }

        private static void ReportLocked(int key, UnicodeScript script, bool cjk, uint cp, UniTextFont font, UnityEngine.Object context)
        {
            if (!warnedScripts.Add(key)) return;
            var name = key == CjkWarnKey ? "CJK symbols" : SystemFontFinder.DisplayName(script);
            string where = "";
            if (context != null)
            {
                try { where = $" in \"{context.name}\""; } catch (Exception) { /* destroyed */ }
            }
            string msg;
            if (font != null)
            {
                pathByFont.TryGetValue(font, out var path);
                msg = $"[OpenGlyph] No font in the font stack covers {name} (U+{cp:X4}{where}); using system font " +
                      $"\"{Path.GetFileName(path)}\". Add a font for this script to the font stack for consistent results across devices.";
            }
            else
            {
                msg = $"[OpenGlyph] No font in the font stack covers {name} (U+{cp:X4}{where}) and no installed system font " +
                      "covers it either; it will render as missing glyphs. Add a font for this script to the font stack.";
            }
            pendingWarnings.Add((msg, context));
        }

        private static void FlushWarnings()
        {
            List<(string, UnityEngine.Object)> copy = null;
            lock (gate)
            {
                if (pendingWarnings.Count == 0) return;
                copy = new List<(string, UnityEngine.Object)>(pendingWarnings);
                pendingWarnings.Clear();
            }
            foreach (var (msg, ctx) in copy) Debug.LogWarning(msg, ctx);
        }

        #region Loading

        private static UniTextFont LoadFile(string path, int face, string label)
        {
            UniTextThreadGuard.AssertMainThread("SystemFontFallback font load");
            var key = path + "#" + face;
            if (fontsByFile.TryGetValue(key, out var existing)) return existing;
            var bytes = File.ReadAllBytes(path);
            var font = UniTextFont.CreateFontAsset(bytes, faceIndex: face);
            if (font == null && face != 0)
                font = UniTextFont.CreateFontAsset(bytes);
            if (font == null) return null;
            font.name = label + Path.GetFileName(path);
            font.hideFlags = HideFlags.HideAndDontSave;
            fontsByFile[key] = font;
            pathByFont[font] = path;
            loadedFonts.Add(font);
            loadedCount = loadedFonts.Count;
            return font;
        }

        private static void LoadForScript(ScriptEntry e, UnicodeScript script, uint cp)
        {
            var probes = 0;
            foreach (var path in SystemFontFinder.Candidates(script))
            {
                if (++probes > SystemFontFinder.MaxProbesPerScript) break;
                if (!SystemFontFinder.TryFindFace(path, cp, out var face)) continue;
                try
                {
                    var font = LoadFile(path, face, "System:");
                    if (font == null || Shaper.GetGlyphIndex(font, cp) == 0) continue;
                    e.font = font;
                    e.loadedPath = path;
                    return;
                }
                catch (Exception)
                {
                    // IO / permission failure: try the next candidate; summarised by the single warning.
                }
            }
        }

        private static void TryLoad(Slot slot)
        {
            slot.attempted = true;
            foreach (var path in slot.paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var font = LoadFile(path, PickFace(path), "SystemCJK:");
                    if (font == null) continue;
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

        #endregion

        #region CJK paths

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

        #endregion

        #region Test hooks

        /// <summary>TEST ONLY: forget loaded fonts, cached results and warnings so a test can observe a fresh lazy load.</summary>
        internal static void ResetForTests()
        {
            lock (gate)
            {
                foreach (var f in loadedFonts)
                    if (f != null) UnityEngine.Object.DestroyImmediate(f);
                loadedFonts.Clear();
                loadedCount = 0;
                fontsByFile.Clear();
                pathByFont.Clear();
                scriptEntries.Clear();
                warnedScripts.Clear();
                pendingWarnings.Clear();
                stackCovered.Clear();
                slots = null;
                SystemFontFinder.Reset();
            }
        }

        /// <summary>TEST ONLY: true when any system font has been loaded.</summary>
        internal static bool AnyLoaded => loadedCount > 0;

        /// <summary>TEST ONLY: number of non-CJK scripts whose lookup ran (positive or negative).</summary>
        internal static int AttemptedScriptCount
        {
            get { lock (gate) { var n = 0; foreach (var e in scriptEntries.Values) if (e.attempted) n++; return n; } }
        }

        #endregion
    }
}
