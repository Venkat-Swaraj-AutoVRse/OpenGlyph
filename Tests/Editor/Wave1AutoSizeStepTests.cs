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
    /// <summary>Wave 1: Auto Size fit steps.</summary>
    [TestFixture]
    public class Wave1AutoSizeStepTests : Wave1TestBase
    {
        // ================================================================== 4. Auto Size fit steps

        private bool FitsAt(string text, float width, float height, float size)
        {
            var probe = Make(text, width, height, c => c.FontSize = size, name: "fit");
            var h = PreferredHeight(probe);
            UnityEngine.Object.DestroyImmediate(probe.gameObject);
            return h <= height + 0.01f;
        }

        [Test]
        public void AutoSizeStep_PicksTheLargestFittingMultiple()
        {
            const string text = "Auto size fit steps keep labels at stable sizes instead of arbitrary fractional sizes.";
            const float w = 420, h = 110;
            var t = Make(text, w, h, c => { c.AutoSize = true; c.MinFontSize = 6; c.MaxFontSize = 80; });
            var continuous = t.CurrentFontSize;

            W1.Set(t, "AutoSizeStep", 1f);
            Canvas.ForceUpdateCanvases();
            var whole = t.CurrentFontSize;

            W1.Set(t, "AutoSizeStep", 0.5f);
            Canvas.ForceUpdateCanvases();
            var half = t.CurrentFontSize;
            Debug.Log($"[W1] auto size: continuous={continuous} step1={whole} step0.5={half}");

            Assert.AreEqual(Mathf.Round(whole), whole, 1e-4f, "step 1: a whole size");
            Assert.IsTrue(FitsAt(text, w, h, whole), $"{whole} must fit");
            Assert.IsFalse(FitsAt(text, w, h, whole + 1f), $"{whole + 1} must not fit");
            Assert.AreEqual(Mathf.Round(half * 2f), half * 2f, 1e-4f, "step 0.5: a multiple of 0.5");
            Assert.IsTrue(FitsAt(text, w, h, half));
            Assert.IsFalse(FitsAt(text, w, h, half + 0.5f));
            Assert.GreaterOrEqual(half, whole);

            W1.Set(t, "AutoSizeStep", 0f);
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(continuous, t.CurrentFontSize, 1e-4f, "step 0: the continuous result, unchanged");
        }

        [Test]
        public void AutoSizeStep_NoWrap_FloorsTheWidthLimitedSize()
        {
            var t = Make("One line that must shrink", 300, 200, c => { c.AutoSize = true; c.WordWrap = false; c.MinFontSize = 6; c.MaxFontSize = 80; });
            var continuous = t.CurrentFontSize;
            Assume.That(continuous, Is.LessThan(80f));
            W1.Set(t, "AutoSizeStep", 2f);
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(Mathf.Floor(continuous / 2f) * 2f, t.CurrentFontSize, 1e-3f, $"continuous {continuous}");
        }
    }
}
