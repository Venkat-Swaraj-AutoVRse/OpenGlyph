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
    /// <summary>Wave 1: OpenType features: FontFeatures and the &lt;feature&gt; tag.</summary>
    [TestFixture]
    public class Wave1FeatureTests : Wave1TestBase
    {
        // ================================================================== 1. OpenType features

        [Test]
        public void FeatureParser_ParsesTagsValuesAndSigns()
        {
            var parser = W1.Type("LightSide.OpenTypeFeatures");
            var tryParse = parser.GetMethod("TryParse", new[] { typeof(string), typeof(uint).MakeByRefType(), typeof(uint).MakeByRefType() });
            Assert.IsNotNull(tryParse, "OpenTypeFeatures.TryParse(string, out uint, out uint) missing");

            (bool ok, uint tag, uint value) P(string s)
            {
                var args = new object[] { s, 0u, 0u };
                var ok = (bool)tryParse.Invoke(null, args);
                return (ok, (uint)args[1], (uint)args[2]);
            }

            Assert.AreEqual((true, HB.Tag('t', 'n', 'u', 'm'), 1u), P("tnum"));
            Assert.AreEqual((true, HB.Tag('l', 'i', 'g', 'a'), 0u), P("liga=0"));
            Assert.AreEqual((true, HB.Tag('l', 'i', 'g', 'a'), 0u), P(" liga = off "));
            Assert.AreEqual((true, HB.Tag('k', 'e', 'r', 'n'), 0u), P("-kern"));
            Assert.AreEqual((true, HB.Tag('s', 'm', 'c', 'p'), 1u), P("+smcp"));
            Assert.AreEqual((true, HB.Tag('a', 'a', 'l', 't'), 2u), P("aalt=2"));
            Assert.AreEqual((true, HB.Tag('s', 's', '0', '1'), 1u), P("ss01"));
            Assert.AreEqual((true, HB.Tag('c', 'v', '1', ' '), 1u), P("cv1"), "short tags are space padded");
            Assert.IsFalse(P("toolong").ok);
            Assert.IsFalse(P("li@a").ok);
            Assert.IsFalse(P("liga=x").ok);
            Assert.IsFalse(P("").ok);

            var parseList = parser.GetMethod("ParseList", new[] { typeof(string), typeof(List<uint>), typeof(List<uint>) });
            var tags = new List<uint>();
            var values = new List<uint>();
            var n = (int)parseList.Invoke(null, new object[] { "smcp,onum, tnum;liga=0 bogus!! ss02", tags, values });
            Assert.AreEqual(5, n);
            CollectionAssert.AreEqual(new[] { HB.Tag('s', 'm', 'c', 'p'), HB.Tag('o', 'n', 'u', 'm'), HB.Tag('t', 'n', 'u', 'm'), HB.Tag('l', 'i', 'g', 'a'), HB.Tag('s', 's', '0', '2') }, tags);
            CollectionAssert.AreEqual(new[] { 1u, 1u, 1u, 0u, 1u }, values);
        }

        [Test]
        public void FontFeatures_Tnum_MakesDigitAdvancesEqual()
        {
            const string digits = "0123456789";
            // Proportional figures first (pnum), so the test does not depend on the font's default figure style.
            var prop = Make(digits + "0", setup: t => SetFeatures(t, "pnum"));
            var pAdv = Advances(Glyphs(prop), 0, 10);
            if (AllEqual(pAdv)) Assert.Ignore("Font has no proportional figures (pnum): " + Fmt(pAdv));

            var tab = Make(digits + "0", setup: t => SetFeatures(t, "pnum", "tnum"));
            var tAdv = Advances(Glyphs(tab), 0, 10);
            Debug.Log($"[W1] pnum advances: {Fmt(pAdv)}; pnum+tnum advances: {Fmt(tAdv)}");
            Assert.IsTrue(AllEqual(tAdv), "tnum: every digit must advance the same: " + Fmt(tAdv));

            // The default (no list) is unchanged by the new code path: same as shaping with no features.
            var plain = Make(digits + "0");
            var plainFeatures = Make(digits + "0", setup: t => SetFeatures(t));
            CollectionAssert.AreEqual(Glyphs(plain).Select(g => g.glyphId), Glyphs(plainFeatures).Select(g => g.glyphId));
        }

        [Test]
        public void FeatureTag_TnumSpan_AppliesToExactlyItsRange()
        {
            // "1" is the digit whose proportional and tabular widths differ most.
            var t = Make("1111<feature=tnum>1111</feature>11110", setup: c => { AddFeatureTag(c); SetFeatures(c, "pnum"); });
            var g = Glyphs(t);
            Assert.AreEqual(13, g.Length, "one glyph per digit");
            var adv = Advances(g, 0, 12);
            Debug.Log($"[W1] pnum + <feature=tnum> span advances: {Fmt(adv)}");
            var outside = adv[0];
            var inside = adv[4];
            if (Mathf.Abs(outside - inside) < 0.01f) Assert.Ignore("Font: tnum and pnum '1' have the same width.");
            for (var i = 0; i < 12; i++)
            {
                var expected = i >= 4 && i < 8 ? inside : outside;
                Assert.AreEqual(expected, adv[i], 0.01f, $"digit {i}: {(i >= 4 && i < 8 ? "inside" : "outside")} the span. All: {Fmt(adv)}");
            }

            // The component-wide tnum gives the same in-span width.
            var all = Make("11110", setup: c => SetFeatures(c, "tnum"));
            Assert.AreEqual(inside, Advances(Glyphs(all), 0, 1)[0], 0.01f);
        }

        [Test]
        public void FontFeatures_LigaOff_DisablesFiLigature_AndSpanOverridesComponent()
        {
            const string word = "fish";
            var lig = Make(word);
            if (Glyphs(lig).Length >= word.Length) Assert.Ignore("Font has no 'fi' ligature.");
            Assert.AreEqual(word.Length - 1, Glyphs(lig).Length, "default: fi is one glyph");

            var off = Make(word, setup: c => SetFeatures(c, "liga=0"));
            Assert.AreEqual(word.Length, Glyphs(off).Length, "liga=0: one glyph per letter");

            // Span on a component without the list: only the span is unligated.
            var span = Make("<feature=liga=0>fish</feature> fish", setup: AddFeatureTag);
            var sg = Glyphs(span);
            Assert.AreEqual(4, sg.Count(x => x.cluster < 4), "inside <feature=liga=0>: 4 glyphs");
            Assert.AreEqual(3, sg.Count(x => x.cluster >= 5), "after the span: fi ligated again");
            Assert.AreEqual(Glyphs(lig)[0].glyphId, sg.First(x => x.cluster >= 5).glyphId, "the second word uses the fi ligature glyph");

            // Span re-enabling a feature the component turned off.
            var reOn = Make("fish <feature=liga>fish</feature>", setup: c => { AddFeatureTag(c); SetFeatures(c, "liga=0"); });
            var rg = Glyphs(reOn);
            Assert.AreEqual(4, rg.Count(x => x.cluster < 4), "component liga=0 outside the span");
            Assert.AreEqual(3, rg.Count(x => x.cluster >= 5), "<feature=liga> re-enables it inside the span");
        }

        [Test]
        public void FontFeatures_SettingAfterRender_Reshapes()
        {
            var t = Make("fish");
            var before = Glyphs(t).Length;
            if (before >= 4) Assert.Ignore("Font has no 'fi' ligature.");
            SetFeatures(t, "liga=0");
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(4, Glyphs(t).Length, "FontFeatures setter must reshape the next rebuild");
            SetFeatures(t);
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(before, Glyphs(t).Length);
        }

        [Test]
        public void FeaturesAndLanguage_ParallelPath_MatchesSerial()
        {
            if (!UniTextWorkerPool.IsParallelSupported) Assert.Ignore("Single-core host: parallel path not supported.");
            UniTextThreadGuard.CaptureMainThread();

            UniText.UseParallel = true;
            UniTextThreadGuard.ResetViolationCount();
            var parallel = BuildFeatureBatch("P");
            var tookParallel = typeof(UniText).GetField("useParallel", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (tookParallel is bool b0) Assert.IsTrue(b0, "precondition: the batch must take the parallel (worker) path");
            Assert.AreEqual(0L, System.Threading.Interlocked.Read(ref UniTextThreadGuard.ViolationCount), "parallel pass touched main-thread-only APIs");
            var p = parallel.Select(c => c.ResultGlyphs.ToArray()).ToList();

            UnityEngine.Object.DestroyImmediate(_canvasGo);
            _canvasGo = new GameObject("Canvas", typeof(Canvas));
            _canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            UniText.UseParallel = false;
            var s = BuildFeatureBatch("S").Select(c => c.ResultGlyphs.ToArray()).ToList();

            for (var i = 0; i < s.Count; i++)
            {
                Assert.AreEqual(s[i].Length, p[i].Length, $"component {i}: glyph count");
                for (var g = 0; g < s[i].Length; g++)
                {
                    Assert.AreEqual(s[i][g].glyphId, p[i][g].glyphId, $"component {i} glyph {g}: id");
                    Assert.AreEqual(s[i][g].x, p[i][g].x, 1e-3f, $"component {i} glyph {g}: x");
                }
            }
        }

        private List<UniText> BuildFeatureBatch(string prefix)
        {
            var list = new List<UniText>();
            var total = 0;
            for (var i = 0; i < 6; i++)
            {
                var s = $"Office {i}: fish <feature=liga=0>fish office</feature> 1111 <feature=tnum>1111 2024</feature> " +
                        $"<lang=sr>бб гг</lang> бб the quick brown fox jumps over the lazy dog, {i}{i}{i} files.";
                total += s.Length;
                list.Add(Make(s, name: prefix + i, update: false, setup: c =>
                {
                    AddFeatureTag(c);
                    AddLangTag(c);
                    SetFeatures(c, i % 2 == 0 ? "pnum" : "onum");
                }));
            }
            Assert.Greater(total, 500, "precondition: batch must cross the parallel threshold");
            Canvas.ForceUpdateCanvases();
            return list;
        }
    }
}
