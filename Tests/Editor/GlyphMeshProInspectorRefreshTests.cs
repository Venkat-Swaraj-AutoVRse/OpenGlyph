using NUnit.Framework;
using OpenGlyph;
using UnityEditor;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression (QA B19, user report): changing Font Style (bold/italic) — or any other field the
    /// GlyphMeshProUGUI Inspector draws as a PropertyField — only showed after disabling and re-enabling
    /// the component. The Inspector writes the serialized field directly, then "re-applied" with
    /// <c>g.fontStyle = g.fontStyle</c>, which the setter's same-value check turned into a no-op, so
    /// the engine weight/axis were only refreshed by the next OnEnable. This edits the field exactly as
    /// the Inspector does (SerializedObject + ApplyModifiedProperties) and asserts the engine follows.
    /// </summary>
    public class GlyphMeshProInspectorRefreshTests
    {
        private GameObject _canvas;

        [TearDown]
        public void TearDown()
        {
            if (_canvas != null) Object.DestroyImmediate(_canvas);
        }

        private static float Width(GlyphMeshProUGUI g) => g.GetPreferredValues(10000f, 10000f).x;

        private GlyphMeshProUGUI Make()
        {
            _canvas = new GameObject("Canvas", typeof(Canvas));
            var go = new GameObject("GMP", typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            var g = go.AddComponent<GlyphMeshProUGUI>();
            g.text = "Hello";
            Canvas.ForceUpdateCanvases();
            return g;
        }

        [Test]
        public void InspectorFontStyleEdit_AppliesWithoutReEnable()
        {
            var g = Make();
            Assert.AreEqual(400, g.FontWeight, "precondition: regular weight");
            float regularWidth = Width(g);

            var so = new SerializedObject(g);
            so.FindProperty("m_fontStyle").intValue = (int)(FontStyles.Bold | FontStyles.Italic);
            so.ApplyModifiedProperties(); // what the Inspector does (it also runs OnValidate)
            g.RefreshFromSerializedStateIfPresent();
            Canvas.ForceUpdateCanvases();

            Assert.AreEqual(700, g.FontWeight, "bold weight not applied after an Inspector-style edit");
            Assert.AreEqual(StyleAxis.Italic, g.FontStyleAxis, "italic axis not applied after an Inspector-style edit");
            // QA B20: the engine state alone is not enough — GlyphMeshPro did not register the
            // Bold/Italic modifiers, so fontStyle = Bold set the weight but rendered regular.
            // Synthetic bold widens every glyph's advance.
            Assert.Greater(Width(g), regularWidth + 1f,
                $"fontStyle Bold did not render bolder (width {Width(g)} vs regular {regularWidth}).");
        }

        [Test]
        public void FreshComponent_BoldTagInText_RendersBold()
        {
            var g = Make();
            float regularWidth = Width(g);
            g.text = "<b>Hello</b>";
            Canvas.ForceUpdateCanvases();
            float w = Width(g);
            // Unparsed, "<b>Hello</b>" would be drawn literally (~2.4x wider); synthetic bold is a few % wider.
            Assert.Less(w, regularWidth * 1.5f, $"the <b> tag was not parsed (width {w} vs regular {regularWidth})");
            Assert.Greater(w, regularWidth + 1f, $"<b> did not render bolder (width {w} vs regular {regularWidth})");
        }
    }

    internal static class GlyphMeshProTestExtensions
    {
        // The Inspector calls RefreshFromSerializedState after ApplyModifiedProperties. Resolve it by
        // reflection so this test still compiles (and fails) against the pre-fix code, where the
        // Inspector's only "refresh" was the self-assignment no-op.
        public static void RefreshFromSerializedStateIfPresent(this GlyphMeshProUGUI g)
        {
            var m = typeof(GlyphMeshProUGUI).GetMethod("RefreshFromSerializedState");
            if (m != null) m.Invoke(g, null);
            else g.fontStyle = g.fontStyle; // pre-fix Inspector behaviour
        }
    }
}
