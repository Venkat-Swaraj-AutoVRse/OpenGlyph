using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Shared helpers for the dictionary-segmentation tests.
    /// </summary>
    internal static class SegHelper
    {
        private static bool _init;
        private static bool _dictsAssigned;
        private static readonly Dictionary<string, TextAsset> _dicts = new Dictionary<string, TextAsset>();

        public static void EnsureUnicode()
        {
            if (_init) return;
            UnicodeData.EnsureInitialized();
            if (!UnicodeData.IsInitialized)
                Assert.Ignore("UnicodeData could not be initialized (Resources/UnicodeData.bytes missing in host project).");
            _init = true;
        }

        /// <summary>
        /// Loads the four dictionary .bytes assets from the package's Dictionaries/ folder
        /// and assigns them into a UniTextSettings instance so the opt-in segmenter can
        /// resolve them. EditMode only (uses AssetDatabase to locate the package assets).
        /// </summary>
        public static void AssignDictionaries()
        {
#if UNITY_EDITOR
            if (_dictsAssigned) return;

            var entries = new List<SegmentationDictionaryEntry>();
            var map = new (string name, SegmentationScript script)[]
            {
                ("ThaiDict", SegmentationScript.Thai),
                ("LaoDict", SegmentationScript.Lao),
                ("KhmerDict", SegmentationScript.Khmer),
                ("MyanmarDict", SegmentationScript.Myanmar),
            };

            foreach (var (name, script) in map)
            {
                var asset = FindDictionary(name);
                _dicts[script.ToString()] = asset;
                if (asset != null)
                    entries.Add(new SegmentationDictionaryEntry { script = script, dictionary = asset });
            }

            // Build a settings instance carrying the current UnicodeData + our dictionaries.
            var settings = UnityEngine.Resources.Load<UniTextSettings>("UniTextSettings");
            if (settings == null)
                settings = ScriptableObject.CreateInstance<UniTextSettings>();
            var so = new UnityEditor.SerializedObject(settings);
            var prop = so.FindProperty("segmentationDictionaries");
            prop.arraySize = entries.Count;
            for (int i = 0; i < entries.Count; i++)
            {
                var el = prop.GetArrayElementAtIndex(i);
                el.FindPropertyRelative("script").enumValueIndex = (int)entries[i].script;
                el.FindPropertyRelative("dictionary").objectReferenceValue = entries[i].dictionary;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            UniTextSettings.SetInstance(settings);
            _dictsAssigned = true;
#endif
        }

        public static TextAsset Dictionary(string script)
        {
            _dicts.TryGetValue(script, out var a);
            return a;
        }

        /// <summary>
        /// Forgets that dictionaries were assigned, so a later <see cref="AssignDictionaries"/>
        /// re-runs. Used by tests that swap the <see cref="UniTextSettings"/> instance (e.g. the
        /// no-dictionary fallback test) and then need the shipped dictionaries restored.
        /// </summary>
        public static void ResetDictionaryAssignment()
        {
            _dictsAssigned = false;
        }

#if UNITY_EDITOR
        private static TextAsset FindDictionary(string name)
        {
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets(name))
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("/Dictionaries/" + name + ".bytes", StringComparison.Ordinal)
                    || path.EndsWith("\\Dictionaries\\" + name + ".bytes", StringComparison.Ordinal)
                    || path.Replace('\\','/').EndsWith("/Dictionaries/" + name + ".bytes", StringComparison.Ordinal))
                    return UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            }
            return null;
        }
#endif

        /// <summary>Converts a UTF-16 string to a codepoint array (fixtures are all BMP).</summary>
        public static int[] ToCodepoints(string s)
        {
            var list = new List<int>(s.Length);
            for (int i = 0; i < s.Length;)
            {
                int cp = char.ConvertToUtf32(s, i);
                i += char.IsSurrogatePair(s, i) ? 2 : 1;
                list.Add(cp);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Runs UAX #14 + dictionary segmentation and returns the set of codepoint
        /// indices at which an interior break opportunity (Optional or Mandatory) exists.
        /// </summary>
        public static HashSet<int> BreakIndices(int[] cps, bool withSegmentation)
        {
            var breaks = new LineBreakType[cps.Length + 1];
            var lba = SharedPipelineComponents.LineBreakAlgorithm;
            if (withSegmentation)
                lba.GetBreakOpportunitiesWithSegmentation(cps, breaks);
            else
                lba.GetBreakOpportunities(cps, breaks);

            var set = new HashSet<int>();
            for (int i = 1; i < cps.Length; i++)
                if (breaks[i] != LineBreakType.None)
                    set.Add(i);
            return set;
        }
    }

    [TestFixture]
    public class SegmentationTests
    {
        [OneTimeSetUp]
        public void Setup() { SegHelper.EnsureUnicode(); SegHelper.AssignDictionaries(); }

        private static void AssertFixtures(SegmentationFixtures.Case[] cases, string script)
        {
            Assert.GreaterOrEqual(cases.Length, 10, $"{script}: need at least 10 reference sentences.");
            int totalBoundaries = 0;

            foreach (var c in cases)
            {
                var cps = SegHelper.ToCodepoints(c.Text);
                var got = SegHelper.BreakIndices(cps, withSegmentation: true);

                foreach (var b in c.Boundaries)
                {
                    Assert.IsTrue(got.Contains(b),
                        $"{script}: expected an ICU-verified word boundary at index {b} in \"{c.Text}\" but none was produced.");
                    totalBoundaries++;
                }
            }

            Debug.Log($"[SegmentationTests] {script}: {cases.Length} sentences, {totalBoundaries} ICU-verified boundaries all present.");
        }

        [Test] public void Thai_MatchesIcuBoundaries()    => AssertFixtures(SegmentationFixtures.Thai, "Thai");
        [Test] public void Lao_MatchesIcuBoundaries()     => AssertFixtures(SegmentationFixtures.Lao, "Lao");
        [Test] public void Khmer_MatchesIcuBoundaries()   => AssertFixtures(SegmentationFixtures.Khmer, "Khmer");
        [Test] public void Myanmar_MatchesIcuBoundaries() => AssertFixtures(SegmentationFixtures.Myanmar, "Myanmar");

        [Test]
        public void SegmentationAddsBreaks_ThatBaselineLacks()
        {
            // Sanity: the baseline UAX#14 pass treats a Thai run as one token (SA -> AL),
            // so segmentation must ADD interior breaks that the baseline does not have.
            var cps = SegHelper.ToCodepoints(SegmentationFixtures.Thai[0].Text);
            var baseline = SegHelper.BreakIndices(cps, withSegmentation: false);
            var withSeg = SegHelper.BreakIndices(cps, withSegmentation: true);

            Assert.IsTrue(withSeg.IsSupersetOf(baseline),
                "Segmentation must only ADD break opportunities, never remove baseline ones.");
            Assert.Greater(withSeg.Count, baseline.Count,
                "Segmentation should introduce at least one interior Thai break opportunity.");
        }

        [Test]
        public void NeverBreaksInsideGraphemeCluster()
        {
            // A Thai base + combining tone/vowel mark cluster must never receive an
            // interior break. Build words separated only by clusters and assert no break
            // ever lands between a base and its following combining mark.
            var gb = new GraphemeBreaker(UnicodeData.Provider);

            foreach (var c in SegmentationFixtures.Thai)
            {
                var cps = SegHelper.ToCodepoints(c.Text);
                var grapheme = new bool[cps.Length + 1];
                gb.GetBreakOpportunities(cps, grapheme);
                var breaks = SegHelper.BreakIndices(cps, withSegmentation: true);

                foreach (var b in breaks)
                    Assert.IsTrue(grapheme[b],
                        $"Break at index {b} in \"{c.Text}\" is inside a grapheme cluster (not a UAX#29 boundary).");
            }
        }
    }
}
