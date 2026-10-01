using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide.Tests
{
    /// <summary>
    /// Phase 1c review fix (B): the HALF-PIXEL regression test. The earlier integration tests assert
    /// <c>pixelSnapPhase + local·deviceScale</c> is integral, but they derive the phase from the SAME
    /// field the production code writes, so they cannot catch a wrong phase. These tests instead
    /// compute each vertex's FINAL screen-pixel coordinate INDEPENDENTLY — world-transform the
    /// mesh-local vertex, then <see cref="RectTransformUtility.WorldToScreenPoint"/> — under a canvas
    /// whose pivot lands on a half pixel (odd screen via an odd-sized Camera RenderTexture, and an
    /// Overlay element nudged half a device pixel). A phase measured relative to the root-canvas pivot
    /// (screen centre) would be half a pixel off on an odd screen; the real screen-space phase is not.
    /// World Space snapping stays off throughout.
    /// </summary>
    [TestFixture]
    public class PixelFontHalfPixelTests
    {
        private const int NativePpem = 8;
        private GameObject _canvasGo, _textGo;
        private Canvas _canvas;
        private UniText _text;
        private UniTextFont _font;
        private UniTextFontStack _stack;
        private UniTextAppearance _appearance;

        private static string FindSilkscreen()
        {
            string[] c =
            {
                "Packages/com.openglyph.text/Tests/Editor/Fonts/Silkscreen-Regular.ttf",
                Path.Combine(UnityEngine.Application.dataPath ?? "", "..", "Packages", "com.openglyph.text", "Tests", "Editor", "Fonts", "Silkscreen-Regular.ttf"),
            };
            foreach (var p in c) if (File.Exists(p)) return p;
            string root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath ?? ".", ".."));
            try { foreach (var f in Directory.EnumerateFiles(root, "Silkscreen-Regular.ttf", SearchOption.AllDirectories)) return f; }
            catch { }
            return null;
        }

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable in this environment.");

            string path = FindSilkscreen();
            if (path == null) Assert.Ignore("Silkscreen-Regular.ttf not found; run fetch_fonts_pixel.ps1.");

            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(path), samplingPointSize: NativePpem,
                renderMode: UniTextRenderMode.Mono);
            Assert.IsNotNull(_font);
            _font.PixelPerfect = true;
            var pf = _font.DetectPixelFont();
            if (!pf.IsPixelFont)
                Assert.Ignore($"Native outline export unavailable so pixel grid not detected here ({pf}); skipping integration.");

            _stack = ScriptableObject.CreateInstance<UniTextFontStack>();
            _stack.fonts.Add(_font);
            _appearance = ScriptableObject.CreateInstance<UniTextAppearance>();

            _canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
            _canvas = _canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            _textGo = new GameObject("UniText", typeof(RectTransform));
            _textGo.transform.SetParent(_canvasGo.transform, worldPositionStays: false);
            _text = _textGo.AddComponent<UniText>();
            _text.FontStack = _stack;
            _text.Appearance = _appearance;
            _text.FontSize = NativePpem;
            _text.Text = "Ag5";
        }

        [TearDown]
        public void TearDown()
        {
            if (_textGo != null) UnityEngine.Object.DestroyImmediate(_textGo);
            if (_canvasGo != null) UnityEngine.Object.DestroyImmediate(_canvasGo);
            if (_font != null) UnityEngine.Object.DestroyImmediate(_font);
            if (_stack != null) UnityEngine.Object.DestroyImmediate(_stack);
            if (_appearance != null) UnityEngine.Object.DestroyImmediate(_appearance);
        }

        /// <summary>
        /// INDEPENDENT final-coordinate check: world-transforms each engine mesh vertex and projects
        /// it to screen pixels with the real camera (null for Overlay). This never touches the
        /// pixelSnapPhase field, so a wrong phase shows up as a non-integer screen coordinate.
        /// </summary>
        private void AssertEveryVertexOnIntegerScreenPixel(Camera cam, float tol, string ctx)
        {
            var verts = _text.GetGeneratedVerticesForEditorTests();
            if (verts.Count == 0) Assert.Ignore($"Engine produced no vertices in this environment ({ctx}).");

            var tr = _textGo.transform;
            int n = 0;
            foreach (var v in verts)
            {
                Vector3 world = tr.TransformPoint(v);
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, world);
                Assert.AreEqual(Mathf.Round(screen.x), screen.x, tol,
                    $"[{ctx}] vertex {v} -> screen x {screen.x} not integral.");
                Assert.AreEqual(Mathf.Round(screen.y), screen.y, tol,
                    $"[{ctx}] vertex {v} -> screen y {screen.y} not integral.");
                n++;
            }
            Assert.Greater(n, 0, $"[{ctx}] no vertices checked.");
        }

        private void Rebuild()
        {
            _text.SetVerticesDirty();
            _text.SetLayoutDirty();
            Canvas.ForceUpdateCanvases();
        }

        // --------------------------------------------------------------------------------------------
        // A. Screen Space - Camera with an ODD-sized RenderTexture: the camera's pixelRect is odd, so
        //    its centre sits on a half pixel. A pivot-relative phase would push glyphs half a pixel
        //    off; the real screen-space phase keeps every vertex on an integer screen pixel.
        // --------------------------------------------------------------------------------------------
        [TestCase(1.0f)]
        [TestCase(2.0f)]
        [TestCase(1.5f)]
        public void CameraCanvas_OddRenderTexture_VertexScreenCoordsAreIntegral(float scaleFactor)
        {
            var camGo = new GameObject("Cam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            RenderTexture rt = null;
            try
            {
                // ODD dimensions on both axes -> centre at (.5, .5) screen px.
                rt = new RenderTexture(641, 481, 0);
                Assert.IsTrue(rt.Create());
                cam.targetTexture = rt;

                _canvas.renderMode = RenderMode.ScreenSpaceCamera;
                _canvas.worldCamera = cam;
                _canvas.planeDistance = 100f;
                _canvas.scaleFactor = scaleFactor;

                var rtr = _textGo.GetComponent<RectTransform>();
                rtr.anchorMin = rtr.anchorMax = new Vector2(0f, 0f);
                rtr.pivot = new Vector2(0f, 0f);
                rtr.anchoredPosition = new Vector2(3f, 2f);

                Rebuild();
                AssertEveryVertexOnIntegerScreenPixel(cam, 3e-3f, $"CameraOddRT sf={scaleFactor}");
            }
            finally
            {
                cam.targetTexture = null;
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                UnityEngine.Object.DestroyImmediate(camGo);
            }
        }

        // --------------------------------------------------------------------------------------------
        // B. Screen Space - Overlay with the element nudged half a DEVICE pixel: a parent RectTransform
        //    offset by 0.5 device px puts the element origin on a half pixel. The snap must still land
        //    every vertex on an integer screen pixel; the pivot-relative phase + the odd-screen centre
        //    would compound into a non-integer. Projected with null camera (Overlay).
        // --------------------------------------------------------------------------------------------
        [TestCase(1.0f)]
        [TestCase(2.0f)]
        [TestCase(1.5f)]
        public void OverlayCanvas_HalfDevicePixelParentOffset_VertexScreenCoordsAreIntegral(float scaleFactor)
        {
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.scaleFactor = scaleFactor;
            _canvasGo.transform.localScale = Vector3.one * scaleFactor; // Overlay canvas scaled by scaleFactor

            // Parent offset of half a DEVICE pixel: 0.5 device px = (0.5 / scaleFactor) reference px.
            var parentGo = new GameObject("HalfPxParent", typeof(RectTransform));
            parentGo.transform.SetParent(_canvasGo.transform, worldPositionStays: false);
            var prt = parentGo.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = new Vector2(0f, 0f);
            prt.pivot = new Vector2(0f, 0f);
            prt.anchoredPosition = new Vector2(0.5f / scaleFactor, 0.5f / scaleFactor);

            _textGo.transform.SetParent(parentGo.transform, worldPositionStays: false);
            var rtr = _textGo.GetComponent<RectTransform>();
            rtr.anchorMin = rtr.anchorMax = new Vector2(0f, 0f);
            rtr.pivot = new Vector2(0f, 0f);
            rtr.anchoredPosition = new Vector2(10f, 7f);

            try
            {
                Rebuild();
                AssertEveryVertexOnIntegerScreenPixel(null, 3e-3f, $"OverlayHalfPx sf={scaleFactor}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parentGo); // destroys the reparented text too
                _textGo = null;
            }
        }
    }
}
