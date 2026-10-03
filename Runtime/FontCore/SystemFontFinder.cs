using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LightSide
{
    /// <summary>
    /// Finds installed operating-system font files that may cover a given Unicode script, and checks
    /// a font file's <c>cmap</c> for a code point WITHOUT reading the whole file. Used by
    /// <see cref="SystemFontFallback"/> for non-CJK scripts.
    /// </summary>
    /// <remarks>
    /// <para>Candidate order for a script:</para>
    /// <list type="number">
    /// <item>Android / Quest: families tagged with the script in <c>/system/etc/fonts.xml</c> /
    /// <c>font_fallback.xml</c> (e.g. <c>lang="und-Taml"</c>).</item>
    /// <item>Per-platform file-name hints (Windows: Nirmala UI, Ebrima, Sylfaen, ...; macOS: Sangam MN, Kohinoor, ...).</item>
    /// <item>Files named <c>NotoSans&lt;Script&gt;*</c> / <c>NotoSerif&lt;Script&gt;*</c> in the font directories.</item>
    /// <item>Every other font file in those directories (the caller bounds how many are probed).</item>
    /// </list>
    /// <para>Main thread only (file IO). The directory listing and fonts.xml map are built once, lazily.</para>
    /// </remarks>
    internal static class SystemFontFinder
    {
        /// <summary>Upper bound on font files whose cmap is probed for one script.</summary>
        internal const int MaxProbesPerScript = 400;

        private const int MaxListedFiles = 6000;
        private const int MaxDirDepth = 5;

        private static string[] listing;
        private static Dictionary<string, List<string>> androidScriptFiles; // ISO 15924 code -> file paths

        /// <summary>TEST ONLY: replaces the platform font directories (and disables fonts.xml / hints outside them).</summary>
        internal static string[] DirectoriesOverride;

        internal static void Reset()
        {
            listing = null;
            androidScriptFiles = null;
        }

        #region Candidates

        /// <summary>Candidate font file paths for <paramref name="script"/>, best first, without duplicates.</summary>
        internal static IEnumerable<string> Candidates(UnicodeScript script)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = Listing();

            // 1. Android fonts.xml (most reliable source on Android / Quest).
            var iso = IsoCode(script);
            if (iso != null && DirectoriesOverride == null)
            {
                var map = AndroidScriptFiles();
                if (map.TryGetValue(iso, out var list))
                    foreach (var p in list)
                        if (seen.Add(p)) yield return p;
            }

            // 2. Platform file-name hints, resolved against the listing (case-insensitive).
            var hints = HintFileNames(script);
            if (hints.Length > 0)
            {
                foreach (var hint in hints)
                    foreach (var f in files)
                        if (string.Equals(Path.GetFileName(f), hint, StringComparison.OrdinalIgnoreCase) && seen.Add(f))
                            yield return f;
            }

            // 3. Noto naming convention (Android, Linux, macOS Supplemental, user installs).
            var scriptName = script.ToString();
            var noto = new List<string>();
            foreach (var f in files)
            {
                var n = Path.GetFileName(f);
                if (MatchesNoto(n, "NotoSans", scriptName) || MatchesNoto(n, "NotoSerif", scriptName))
                    noto.Add(f);
            }
            noto.Sort(CompareByStyle);
            foreach (var f in noto)
                if (seen.Add(f)) yield return f;

            // 4. Everything else, regular-looking faces first (bounded by the caller).
            var rest = new List<string>(files.Length);
            foreach (var f in files)
                if (!seen.Contains(f)) rest.Add(f);
            rest.Sort(CompareByStyle);
            foreach (var f in rest)
                if (seen.Add(f)) yield return f;
        }

        private static bool MatchesNoto(string fileName, string prefix, string scriptName)
        {
            var head = prefix + scriptName;
            if (!fileName.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Length == head.Length) return true;
            // Next char must end the script name ("NotoSansTai" must not match "NotoSansTaiTham"),
            // but "UI" is an accepted suffix ("NotoSansTamilUI-Regular.ttf").
            var c = fileName[head.Length];
            if (c == '-' || c == '_' || c == '.' || c == '[') return true;
            return string.Compare(fileName, head.Length, "UI", 0, 2, StringComparison.Ordinal) == 0 &&
                   (fileName.Length == head.Length + 2 || !char.IsLower(fileName[head.Length + 2]));
        }

        private static readonly string[] NonRegularWords =
            { "bold", "italic", "light", "thin", "black", "medium", "semi", "condensed", "heavy", "extra", "oblique", "mono" };

        private static int StyleRank(string path)
        {
            var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var rank = 0;
            foreach (var w in NonRegularWords) if (n.Contains(w)) rank++;
            if (n.Contains("regular") || n.EndsWith("-vf") || n.Contains("[")) rank--;
            return rank;
        }

        private static int CompareByStyle(string a, string b)
        {
            var c = StyleRank(a).CompareTo(StyleRank(b));
            return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Directories

        private static string[] Listing()
        {
            if (listing != null) return listing;
            var result = new List<string>();
            var dirs = DirectoriesOverride ?? PlatformDirectories();
            foreach (var d in dirs) Collect(d, 0, result);
            return listing = result.ToArray();
        }

        private static void Collect(string dir, int depth, List<string> into)
        {
            if (string.IsNullOrEmpty(dir) || into.Count >= MaxListedFiles) return;
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (into.Count >= MaxListedFiles) return;
                    if (IsFontFile(f)) into.Add(f);
                }
                if (depth >= MaxDirDepth) return;
                foreach (var sub in Directory.GetDirectories(dir))
                    Collect(sub, depth + 1, into);
            }
            catch (Exception)
            {
                // Permission / IO error on one directory: skip it.
            }
        }

        private static bool IsFontFile(string f)
        {
            var e = Path.GetExtension(f);
            return e.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || e.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                   e.Equals(".ttc", StringComparison.OrdinalIgnoreCase) || e.Equals(".otc", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] PlatformDirectories()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string sys;
            try { sys = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); }
            catch { sys = null; }
            if (string.IsNullOrEmpty(sys)) sys = @"C:\Windows\Fonts";
            string user = null;
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local)) user = Path.Combine(local, "Microsoft", "Windows", "Fonts");
            }
            catch { }
            return new[] { sys, user };
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            return new[] { "/System/Library/Fonts", "/Library/Fonts" }; // Supplemental/ is a subfolder of the first
#elif UNITY_ANDROID
            return new[] { "/system/fonts", "/product/fonts" };
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            string home = null;
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
            return new[] { "/usr/share/fonts", "/usr/local/share/fonts",
                           home != null ? Path.Combine(home, ".local/share/fonts") : null,
                           home != null ? Path.Combine(home, ".fonts") : null };
#else
            return Array.Empty<string>();
#endif
        }

        #endregion

        #region Android fonts.xml

        private static readonly Regex FamilyRx = new(@"<family\b([^>]*)>(.*?)</family>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex LangRx = new(@"\blang\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant);
        private static readonly Regex FontRx = new(@"<font\b[^>]*>\s*([^<\s]+)", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static Dictionary<string, List<string>> AndroidScriptFiles()
        {
            if (androidScriptFiles != null) return androidScriptFiles;
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
#if UNITY_ANDROID
            foreach (var xml in new[] { "/system/etc/font_fallback.xml", "/system/etc/fonts.xml" })
            {
                try { if (File.Exists(xml)) ParseFontsXml(File.ReadAllText(xml), "/system/fonts", map); }
                catch (Exception) { /* unreadable: fall back to name hints */ }
            }
#endif
            return androidScriptFiles = map;
        }

        /// <summary>Parses an Android fonts.xml into ISO 15924 script code -> font file paths (document order).</summary>
        internal static void ParseFontsXml(string xml, string fontDir, Dictionary<string, List<string>> map)
        {
            foreach (Match fam in FamilyRx.Matches(xml))
            {
                var lang = LangRx.Match(fam.Groups[1].Value);
                if (!lang.Success) continue;
                var scripts = new List<string>();
                foreach (var tag in lang.Groups[1].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    foreach (var sub in tag.Split('-'))
                        if (sub.Length == 4 && char.IsUpper(sub[0]) && !scripts.Contains(sub))
                            scripts.Add(sub);
                if (scripts.Count == 0) continue;
                foreach (Match font in FontRx.Matches(fam.Groups[2].Value))
                {
                    var path = Path.Combine(fontDir, font.Groups[1].Value.Trim()).Replace('\\', '/');
                    foreach (var s in scripts)
                    {
                        if (!map.TryGetValue(s, out var list)) map[s] = list = new List<string>();
                        if (!list.Contains(path)) list.Add(path);
                    }
                }
            }
        }

        #endregion

        #region Script tables

        /// <summary>Human-readable script name ("OlChiki" -> "Ol Chiki").</summary>
        internal static string DisplayName(UnicodeScript script)
        {
            var s = script.ToString();
            var sb = new System.Text.StringBuilder(s.Length + 4);
            for (var i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        /// <summary>ISO 15924 code used by Android fonts.xml <c>lang</c> tags, or null.</summary>
        internal static string IsoCode(UnicodeScript s) => s switch
        {
            UnicodeScript.Latin => "Latn", UnicodeScript.Greek => "Grek", UnicodeScript.Cyrillic => "Cyrl",
            UnicodeScript.Armenian => "Armn", UnicodeScript.Hebrew => "Hebr", UnicodeScript.Arabic => "Arab",
            UnicodeScript.Syriac => "Syrc", UnicodeScript.Thaana => "Thaa", UnicodeScript.Devanagari => "Deva",
            UnicodeScript.Bengali => "Beng", UnicodeScript.Gurmukhi => "Guru", UnicodeScript.Gujarati => "Gujr",
            UnicodeScript.Oriya => "Orya", UnicodeScript.Tamil => "Taml", UnicodeScript.Telugu => "Telu",
            UnicodeScript.Kannada => "Knda", UnicodeScript.Malayalam => "Mlym", UnicodeScript.Sinhala => "Sinh",
            UnicodeScript.Thai => "Thai", UnicodeScript.Lao => "Laoo", UnicodeScript.Tibetan => "Tibt",
            UnicodeScript.Myanmar => "Mymr", UnicodeScript.Georgian => "Geor", UnicodeScript.Hangul => "Hang",
            UnicodeScript.Ethiopic => "Ethi", UnicodeScript.Cherokee => "Cher", UnicodeScript.CanadianAboriginal => "Cans",
            UnicodeScript.Ogham => "Ogam", UnicodeScript.Runic => "Runr", UnicodeScript.Khmer => "Khmr",
            UnicodeScript.Mongolian => "Mong", UnicodeScript.Yi => "Yiii", UnicodeScript.Tagalog => "Tglg",
            UnicodeScript.Hanunoo => "Hano", UnicodeScript.Buhid => "Buhd", UnicodeScript.Tagbanwa => "Tagb",
            UnicodeScript.Limbu => "Limb", UnicodeScript.TaiLe => "Tale", UnicodeScript.Buginese => "Bugi",
            UnicodeScript.Coptic => "Copt", UnicodeScript.NewTaiLue => "Talu", UnicodeScript.Glagolitic => "Glag",
            UnicodeScript.Tifinagh => "Tfng", UnicodeScript.SylotiNagri => "Sylo", UnicodeScript.Balinese => "Bali",
            UnicodeScript.PhagsPa => "Phag", UnicodeScript.Nko => "Nkoo", UnicodeScript.Sundanese => "Sund",
            UnicodeScript.Lepcha => "Lepc", UnicodeScript.OlChiki => "Olck", UnicodeScript.Vai => "Vaii",
            UnicodeScript.Saurashtra => "Saur", UnicodeScript.KayahLi => "Kali", UnicodeScript.Rejang => "Rjng",
            UnicodeScript.Cham => "Cham", UnicodeScript.TaiTham => "Lana", UnicodeScript.TaiViet => "Tavt",
            UnicodeScript.Samaritan => "Samr", UnicodeScript.Lisu => "Lisu", UnicodeScript.Bamum => "Bamu",
            UnicodeScript.Javanese => "Java", UnicodeScript.MeeteiMayek => "Mtei", UnicodeScript.Batak => "Batk",
            UnicodeScript.Brahmi => "Brah", UnicodeScript.Mandaic => "Mand", UnicodeScript.Chakma => "Cakm",
            UnicodeScript.Miao => "Plrd", UnicodeScript.Adlam => "Adlm", UnicodeScript.Osage => "Osge",
            UnicodeScript.Osmanya => "Osma", UnicodeScript.Gothic => "Goth", UnicodeScript.Deseret => "Dsrt",
            UnicodeScript.Shavian => "Shaw", UnicodeScript.Braille => "Brai", UnicodeScript.Kaithi => "Kthi",
            UnicodeScript.Sharada => "Shrd", UnicodeScript.Takri => "Takr", UnicodeScript.Newa => "Newa",
            UnicodeScript.Wancho => "Wcho", UnicodeScript.HanifiRohingya => "Rohg", UnicodeScript.Grantha => "Gran",
            UnicodeScript.Modi => "Modi", UnicodeScript.Tirhuta => "Tirh", UnicodeScript.Ahom => "Ahom",
            UnicodeScript.Cuneiform => "Xsux", UnicodeScript.EgyptianHieroglyphs => "Egyp", UnicodeScript.Avestan => "Avst",
            UnicodeScript.Han => "Hani", UnicodeScript.Hiragana => "Hira", UnicodeScript.Katakana => "Kana",
            UnicodeScript.Bopomofo => "Bopo",
            _ => null,
        };

        /// <summary>Per-platform font file names known to cover <paramref name="s"/> (verified by cmap before use).</summary>
        internal static string[] HintFileNames(UnicodeScript s)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            switch (s)
            {
                case UnicodeScript.Devanagari: return new[] { "Nirmala.ttc", "Nirmala.ttf", "mangal.ttf" };
                case UnicodeScript.Bengali: return new[] { "Nirmala.ttc", "Nirmala.ttf", "vrinda.ttf" };
                case UnicodeScript.Gurmukhi: return new[] { "Nirmala.ttc", "Nirmala.ttf", "raavi.ttf" };
                case UnicodeScript.Gujarati: return new[] { "Nirmala.ttc", "Nirmala.ttf", "shruti.ttf" };
                case UnicodeScript.Oriya: return new[] { "Nirmala.ttc", "Nirmala.ttf", "kalinga.ttf" };
                case UnicodeScript.Tamil: return new[] { "Nirmala.ttc", "Nirmala.ttf", "latha.ttf" };
                case UnicodeScript.Telugu: return new[] { "Nirmala.ttc", "Nirmala.ttf", "gautami.ttf" };
                case UnicodeScript.Kannada: return new[] { "Nirmala.ttc", "Nirmala.ttf", "tunga.ttf" };
                case UnicodeScript.Malayalam: return new[] { "Nirmala.ttc", "Nirmala.ttf", "kartika.ttf" };
                case UnicodeScript.Sinhala: return new[] { "Nirmala.ttc", "Nirmala.ttf", "iskpota.ttf" };
                case UnicodeScript.OlChiki:
                case UnicodeScript.SoraSompeng:
                case UnicodeScript.Chakma:
                case UnicodeScript.MeeteiMayek: return new[] { "Nirmala.ttc", "Nirmala.ttf" };
                case UnicodeScript.Ethiopic: return new[] { "ebrima.ttf", "nyala.ttf" };
                case UnicodeScript.Nko:
                case UnicodeScript.Tifinagh:
                case UnicodeScript.Vai:
                case UnicodeScript.Osmanya:
                case UnicodeScript.Adlam: return new[] { "ebrima.ttf" };
                case UnicodeScript.Georgian:
                case UnicodeScript.Armenian: return new[] { "sylfaen.ttf", "segoeui.ttf" };
                case UnicodeScript.Tibetan: return new[] { "himalaya.ttf" };
                case UnicodeScript.Thai: return new[] { "LeelawUI.ttf", "leelawad.ttf", "tahoma.ttf" };
                case UnicodeScript.Lao: return new[] { "LeelawUI.ttf", "LaoUI.ttf" };
                case UnicodeScript.Khmer: return new[] { "LeelawUI.ttf", "KhmerUI.ttf" };
                case UnicodeScript.Buginese: return new[] { "LeelawUI.ttf" };
                case UnicodeScript.Myanmar: return new[] { "mmrtext.ttf" };
                case UnicodeScript.Cherokee:
                case UnicodeScript.CanadianAboriginal:
                case UnicodeScript.Osage: return new[] { "gadugi.ttf" };
                case UnicodeScript.Mongolian: return new[] { "monbaiti.ttf" };
                case UnicodeScript.Javanese: return new[] { "javatext.ttf" };
                case UnicodeScript.Yi: return new[] { "msyi.ttf" };
                case UnicodeScript.NewTaiLue: return new[] { "ntailu.ttf" };
                case UnicodeScript.TaiLe: return new[] { "taile.ttf" };
                case UnicodeScript.PhagsPa: return new[] { "phagspa.ttf" };
                case UnicodeScript.Thaana: return new[] { "mvboli.ttf" };
                case UnicodeScript.Syriac: return new[] { "seguihis.ttf", "estre.ttf" };
                case UnicodeScript.Latin:
                case UnicodeScript.Greek:
                case UnicodeScript.Cyrillic:
                case UnicodeScript.Hebrew:
                case UnicodeScript.Arabic:
                case UnicodeScript.Lisu: return new[] { "segoeui.ttf", "arial.ttf", "tahoma.ttf" };
                default: return new[] { "seguihis.ttf", "seguisym.ttf" };
            }
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            switch (s)
            {
                case UnicodeScript.Devanagari: return new[] { "Kohinoor.ttc", "ITFDevanagari.ttc", "Devanagari Sangam MN.ttc", "DevanagariMT.ttc" };
                case UnicodeScript.Bengali: return new[] { "KohinoorBangla.ttc", "Bangla Sangam MN.ttc", "Bangla MN.ttc" };
                case UnicodeScript.Gurmukhi: return new[] { "Gurmukhi Sangam MN.ttc", "Gurmukhi MN.ttc", "GurmukhiMN.ttc" };
                case UnicodeScript.Gujarati: return new[] { "KohinoorGujarati.ttc", "Gujarati Sangam MN.ttc", "GujaratiMT.ttc" };
                case UnicodeScript.Oriya: return new[] { "Oriya Sangam MN.ttc", "Oriya MN.ttc" };
                case UnicodeScript.Tamil: return new[] { "Tamil Sangam MN.ttc", "Tamil MN.ttc" };
                case UnicodeScript.Telugu: return new[] { "KohinoorTelugu.ttc", "Telugu Sangam MN.ttc", "Telugu MN.ttc" };
                case UnicodeScript.Kannada: return new[] { "Kannada Sangam MN.ttc", "Kannada MN.ttc" };
                case UnicodeScript.Malayalam: return new[] { "Malayalam Sangam MN.ttc", "Malayalam MN.ttc" };
                case UnicodeScript.Sinhala: return new[] { "Sinhala Sangam MN.ttc", "Sinhala MN.ttc" };
                case UnicodeScript.Myanmar: return new[] { "Myanmar Sangam MN.ttc", "Myanmar MN.ttc" };
                case UnicodeScript.Khmer: return new[] { "Khmer Sangam MN.ttf", "Khmer MN.ttc" };
                case UnicodeScript.Lao: return new[] { "Lao Sangam MN.ttf", "Lao MN.ttc" };
                case UnicodeScript.Thai: return new[] { "Thonburi.ttc", "Ayuthaya.ttf", "Silom.ttf" };
                case UnicodeScript.Tibetan: return new[] { "Kailasa.ttc" };
                case UnicodeScript.Ethiopic: return new[] { "Kefa.ttc" };
                case UnicodeScript.Armenian: return new[] { "Mshtakan.ttc", "Arial Unicode.ttf" };
                case UnicodeScript.Georgian: return new[] { "Arial Unicode.ttf", "Helvetica.ttc" };
                case UnicodeScript.Hebrew: return new[] { "ArialHB.ttc", "Arial Unicode.ttf" };
                case UnicodeScript.Arabic: return new[] { "GeezaPro.ttc", "Arial Unicode.ttf" };
                case UnicodeScript.Cherokee: return new[] { "Plantagenet Cherokee.ttf" };
                case UnicodeScript.CanadianAboriginal: return new[] { "EuphemiaCAS.ttc" };
                case UnicodeScript.Latin:
                case UnicodeScript.Greek:
                case UnicodeScript.Cyrillic: return new[] { "Helvetica.ttc", "Arial Unicode.ttf" };
                default: return new[] { "Arial Unicode.ttf" };
            }
#elif UNITY_ANDROID
            switch (s)
            {
                case UnicodeScript.Latin:
                case UnicodeScript.Greek:
                case UnicodeScript.Cyrillic: return new[] { "Roboto-Regular.ttf", "RobotoStatic-Regular.ttf" };
                default: return Array.Empty<string>();
            }
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            switch (s)
            {
                case UnicodeScript.Latin:
                case UnicodeScript.Greek:
                case UnicodeScript.Cyrillic:
                case UnicodeScript.Armenian:
                case UnicodeScript.Georgian: return new[] { "DejaVuSans.ttf", "FreeSans.ttf" };
                default: return Array.Empty<string>();
            }
#else
            return Array.Empty<string>();
#endif
        }

        #endregion

        #region cmap probe

        /// <summary>
        /// True when the font file at <paramref name="path"/> maps <paramref name="cp"/> to a non-zero
        /// glyph. For a collection (.ttc/.otc) <paramref name="faceIndex"/> is the first face that does.
        /// Reads only the table directory and the cmap table.
        /// </summary>
        internal static bool TryFindFace(string path, uint cp, out int faceIndex)
        {
            faceIndex = -1;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                var hdr = new byte[12];
                if (!ReadAt(fs, 0, hdr, 12)) return false;
                if (BE32(hdr, 0) == 0x74746366) // 'ttcf'
                {
                    var n = BE32(hdr, 8);
                    if (n == 0 || n > 256) return false;
                    var offs = new byte[4 * n];
                    if (!ReadAt(fs, 12, offs, offs.Length)) return false;
                    for (var i = 0; i < n; i++)
                    {
                        if (FaceCovers(fs, BE32(offs, 4 * i), cp))
                        {
                            faceIndex = i;
                            return true;
                        }
                    }
                    return false;
                }
                if (FaceCovers(fs, 0, cp))
                {
                    faceIndex = 0;
                    return true;
                }
            }
            catch (Exception)
            {
                // Unreadable / malformed file: not a candidate.
            }
            return false;
        }

        private static bool FaceCovers(FileStream fs, long offset, uint cp)
        {
            var h = new byte[12];
            if (!ReadAt(fs, offset, h, 12)) return false;
            var ver = BE32(h, 0);
            if (ver != 0x00010000 && ver != 0x4F54544F /*OTTO*/ && ver != 0x74727565 /*true*/) return false;
            int numTables = BE16(h, 4);
            if (numTables <= 0 || numTables > 1024) return false;
            var recs = new byte[16 * numTables];
            if (!ReadAt(fs, offset + 12, recs, recs.Length)) return false;
            for (var t = 0; t < numTables; t++)
            {
                if (BE32(recs, 16 * t) != 0x636D6170) continue; // 'cmap'
                var off = BE32(recs, 16 * t + 8);
                var len = BE32(recs, 16 * t + 12);
                if (len < 4 || len > 32u * 1024 * 1024) return false;
                var cmap = new byte[len];
                if (!ReadAt(fs, off, cmap, (int)len)) return false;
                return CmapCovers(cmap, cp);
            }
            return false;
        }

        /// <summary>True when a raw <c>cmap</c> table maps <paramref name="cp"/> to a non-zero glyph (formats 4, 12, 13).</summary>
        internal static bool CmapCovers(byte[] cmap, uint cp)
        {
            if (cmap.Length < 4) return false;
            int n = BE16(cmap, 2);
            for (var i = 0; i < n; i++)
            {
                var rec = 4 + 8 * i;
                if (rec + 8 > cmap.Length) return false;
                int platform = BE16(cmap, rec), encoding = BE16(cmap, rec + 2);
                var unicode = platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10));
                if (!unicode) continue;
                var sub = BE32(cmap, rec + 4);
                if (sub + 2 > cmap.Length) continue;
                int format = BE16(cmap, (int)sub);
                if (format == 4 && cp <= 0xFFFF && Format4(cmap, (int)sub, cp)) return true;
                if ((format == 12 || format == 13) && Format12(cmap, (int)sub, cp, format == 13)) return true;
            }
            return false;
        }

        private static bool Format4(byte[] b, int o, uint cp)
        {
            if (o + 14 > b.Length) return false;
            int segX2 = BE16(b, o + 6);
            int endBase = o + 14, startBase = endBase + segX2 + 2, deltaBase = startBase + segX2, rangeBase = deltaBase + segX2;
            if (rangeBase + segX2 > b.Length) return false;
            int lo = 0, hi = segX2 / 2 - 1;
            while (lo <= hi) // first segment whose endCode >= cp
            {
                var mid = (lo + hi) >> 1;
                if (BE16(b, endBase + 2 * mid) < cp) lo = mid + 1; else hi = mid - 1;
            }
            if (lo >= segX2 / 2) return false;
            int start = BE16(b, startBase + 2 * lo);
            if (start > cp) return false;
            int delta = BE16(b, deltaBase + 2 * lo);
            int ro = BE16(b, rangeBase + 2 * lo);
            int glyph;
            if (ro == 0) glyph = (int)((cp + delta) & 0xFFFF);
            else
            {
                var addr = rangeBase + 2 * lo + ro + 2 * (int)(cp - start);
                if (addr + 2 > b.Length) return false;
                glyph = BE16(b, addr);
                if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
            }
            return glyph != 0;
        }

        private static bool Format12(byte[] b, int o, uint cp, bool manyToOne)
        {
            if (o + 16 > b.Length) return false;
            var groups = BE32(b, o + 12);
            if (o + 16 + 12L * groups > b.Length) return false;
            long lo = 0, hi = (long)groups - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var g = o + 16 + 12 * (int)mid;
                uint start = BE32(b, g), end = BE32(b, g + 4);
                if (cp < start) hi = mid - 1;
                else if (cp > end) lo = mid + 1;
                else
                {
                    var startGlyph = BE32(b, g + 8);
                    return (manyToOne ? startGlyph : startGlyph + (cp - start)) != 0;
                }
            }
            return false;
        }

        private static bool ReadAt(FileStream fs, long offset, byte[] buf, int count)
        {
            if (offset < 0 || offset + count > fs.Length) return false;
            fs.Position = offset;
            var read = 0;
            while (read < count)
            {
                var r = fs.Read(buf, read, count - read);
                if (r <= 0) return false;
                read += r;
            }
            return true;
        }

        private static int BE16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
        private static uint BE32(byte[] b, int i) => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

        #endregion
    }
}
