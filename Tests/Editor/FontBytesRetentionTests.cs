using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LightSide.Tests
{
    /// <summary>
    /// How many copies of a font file stay resident once it is in use. Two <see cref="UniTextFont"/>
    /// assets built from the same file (Regular + Bold assets of one variable font) are compressed and
    /// reset to their just-loaded state (compressed container only), then used for shaping, including
    /// two variable-font instances. Every byte array / native buffer holding the font is then counted.
    /// Before the fix: each asset decoded its own raw array and every HarfBuzz cache entry (base and
    /// one per variation instance) made its own Marshal.AllocHGlobal copy of the whole file.
    /// Private state is read by reflection so this also runs against a build without the fix.
    /// </summary>
    public class FontBytesRetentionTests
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly uint Wght = ('w' << 24) | ('g' << 16) | ('h' << 8) | 't';

        private readonly List<Object> _created = new List<Object>();
        private FieldInfo _releaseFlag;
        private object _releaseFlagSaved;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            Shaper.ClearAllCaches();
            _releaseFlag = typeof(UniTextFont).GetField("ReleaseCompressedAfterDecode", Stat);
            _releaseFlagSaved = _releaseFlag?.GetValue(null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_releaseFlag != null) _releaseFlag.SetValue(null, _releaseFlagSaved);
            Shaper.ClearAllCaches();
            foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private UniTextFont LoadCompressed(string path)
        {
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: 64);
            if (font == null) Assert.Ignore("Font backend unavailable.");
            _created.Add(font);
            Assert.IsTrue(font.CompressStoredFontData(), "font compresses");
            // Simulate a fresh load from disk: only the serialized (compressed) container exists.
            typeof(UniTextFont).GetField("rawFontDataCache", Inst).SetValue(font, null);
            return font;
        }

        private struct Census
        {
            public int rawCopies, compressedCopies;
            public long rawBytes, compressedBytes, nativeCopyBytes;
            public long Total => rawBytes + compressedBytes + nativeCopyBytes;
            public override string ToString() =>
                $"raw copies={rawCopies} ({rawBytes} B), compressed copies={compressedCopies} ({compressedBytes} B), " +
                $"native copies={nativeCopyBytes} B, total={Total} B";
        }

        private static Census Count(IEnumerable<UniTextFont> fonts, IEnumerable<object> entries, int rawLength)
        {
            var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
            var c = new Census();
            void Add(byte[] a)
            {
                if (a == null || !seen.Add(a)) return;
                if (FontCompression.IsCompressed(a)) { c.compressedCopies++; c.compressedBytes += a.Length; }
                else { c.rawCopies++; c.rawBytes += a.Length; }
            }

            foreach (var f in fonts)
            {
                Add((byte[])typeof(UniTextFont).GetField("fontData", Inst).GetValue(f));
                Add((byte[])typeof(UniTextFont).GetField("rawFontDataCache", Inst).GetValue(f));
            }

            var seenEntries = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var e in entries)
            {
                if (e == null || !seenEntries.Add(e)) continue;
                var t = e.GetType();
                var native = t.GetField("unmanagedData", Inst);
                if (native != null && (IntPtr)native.GetValue(e) != IntPtr.Zero)
                {
                    c.nativeCopyBytes += rawLength;
                    c.rawCopies++;
                }
                var shared = t.GetProperty("SharedFontData", Inst);
                if (shared != null) Add((byte[])shared.GetValue(e));
            }
            return c;
        }

        private static List<object> UseForShaping(UniTextFont[] fonts)
        {
            var entries = new List<object>();
            foreach (var f in fonts)
            {
                Assert.AreNotEqual(0u, Shaper.GetGlyphIndex(f, 'A'), "font shapes");
                entries.Add(Shaper.GetOrCreateCacheByInstanceId(f));
                foreach (var w in new[] { 400, 700 })
                {
                    var key = VariationKey.FromQuantized(new[] { Wght }, new[] { w });
                    var e = Shaper.GetOrCreateVariationCache(f, key, new[] { Wght }, new[] { (float)w });
                    Assert.IsNotNull(e, "variation instance entry");
                    entries.Add(e);
                }
            }
            return entries;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>, IEqualityComparer<byte[]>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
            public bool Equals(byte[] a, byte[] b) => ReferenceEquals(a, b);
            public int GetHashCode(byte[] o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        private UniTextFont[] TwoVariantsOfOneFile(out int rawLength)
        {
            string path = MsdfTestUtil.FindNotoSansPath();
            if (path == null) Assert.Ignore("NotoSans-Regular.ttf fixture missing.");
            rawLength = (int)new FileInfo(path).Length;
            var fonts = new[] { LoadCompressed(path), LoadCompressed(path) };
            Shaper.ClearAllCaches(); // drop entries made during asset creation (pre-compression bytes)
            return fonts;
        }

        [Test]
        public void SameFileVariants_InUse_HoldOneRawCopy()
        {
            _releaseFlag?.SetValue(null, false); // Editor behaviour: keep the serialized container.
            var fonts = TwoVariantsOfOneFile(out int rawLength);

            GC.Collect(); GC.WaitForPendingFinalizers();
            long managedBefore = GC.GetTotalMemory(true);
            var entries = UseForShaping(fonts);
            long managedAfter = GC.GetTotalMemory(true);

            var census = Count(fonts, entries, rawLength);
            Debug.Log($"[PERF2][FONT] editor-mode, 2 assets of one {rawLength} B file, {new HashSet<object>(entries, ReferenceEqualityComparer.Instance).Count} HB entries: {census}; " +
                      $"managed heap delta during use={managedAfter - managedBefore} B");

            Assert.AreSame(fonts[0].FontData, fonts[1].FontData, "variants of one file share one decoded array");
            Assert.AreEqual(1, census.rawCopies,
                $"one raw copy of the font should be resident; found {census}");
            Assert.LessOrEqual(census.rawBytes + census.nativeCopyBytes, (long)(rawLength * 1.05));
        }

        [Test]
        public void PlayerMode_DropsCompressedContainerOnceDecoded()
        {
            if (_releaseFlag == null)
                Assert.Fail("UniTextFont keeps the compressed container resident next to the decoded bytes (no release hook)");
            _releaseFlag.SetValue(null, true);
            var fonts = TwoVariantsOfOneFile(out int rawLength);
            var entries = UseForShaping(fonts);

            var census = Count(fonts, entries, rawLength);
            Debug.Log($"[PERF2][FONT] player-mode, 2 assets of one {rawLength} B file, 3 HB entries: {census}");
            Assert.AreEqual(0, census.compressedCopies, $"compressed containers released: {census}");
            Assert.AreEqual(1, census.rawCopies, $"exactly one raw copy: {census}");
            Assert.IsFalse(fonts[0].IsFontDataCompressed);

            // The font still renders from the single shared copy.
            Assert.AreNotEqual(0u, Shaper.GetGlyphIndex(fonts[1], 'B'));
            uint gi = Shaper.GetGlyphIndex(fonts[0], 'C');
            fonts[0].ClearDynamicData();
            Assert.AreEqual(1, fonts[0].TryAddGlyphsBatch(new List<uint> { gi }), "glyph rasterizes from the shared bytes");
        }
    }
}
