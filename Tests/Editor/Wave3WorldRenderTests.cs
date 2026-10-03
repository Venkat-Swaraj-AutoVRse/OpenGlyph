using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 3, item 2: real GPU renders of world text (UniText/World/Uber through a MeshRenderer) into a
    /// RenderTexture with a perspective camera at an angle: matches the same text on a World Space Canvas,
    /// lit variant follows the main light, single/double-sided culling, depth write, legacy path.
    /// </summary>
    public class Wave3WorldRenderTests : Wave3TestBase
    {
        private const int W = 512, H = 256;
        private GameObject _camGo, _lightGo;
        private Camera _cam;
        private RenderTexture _rt;
        private AmbientMode _savedAmbientMode;
        private Color _savedAmbient;

        [SetUp]
        public void StageSetUp()
        {
            if (!HasGpu) Assert.Ignore("needs a graphics device");
            _rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            _rt.Create();
            _camGo = new GameObject("W3Cam", typeof(Camera));
            _cam = _camGo.GetComponent<Camera>();
            _cam.fieldOfView = 40f;
            _cam.transform.position = new Vector3(0, 0, -3f);
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            _cam.targetTexture = _rt;
            _savedAmbientMode = RenderSettings.ambientMode;
            _savedAmbient = RenderSettings.ambientLight;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.1f, 0.1f, 0.1f);
        }

        [TearDown]
        public void StageTearDown()
        {
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
            if (_lightGo != null) UnityEngine.Object.DestroyImmediate(_lightGo);
            if (_rt != null) { _rt.Release(); UnityEngine.Object.DestroyImmediate(_rt); }
            RenderSettings.ambientMode = _savedAmbientMode;
            RenderSettings.ambientLight = _savedAmbient;
        }

        private static void Place(Transform t)
        {
            t.position = Vector3.zero;
            t.rotation = Quaternion.Euler(0f, 30f, 0f); // seen at an angle
            t.localScale = Vector3.one * 0.005f;          // 400 x 120 rect -> 2 m x 0.6 m
        }

        private Color32[] Render()
        {
            Canvas.ForceUpdateCanvases();
            Canvas.ForceUpdateCanvases();
            _cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            Dump(tex);
            UnityEngine.Object.DestroyImmediate(tex);
            return px;
        }

        private static int _dumpIndex;
        private static void Dump(Texture2D tex)
        {
            var dir = Environment.GetEnvironmentVariable("OPENGLYPH_W3_DUMP");
            if (string.IsNullOrEmpty(dir)) return;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"w3_{TestContext.CurrentContext.Test.Name}_{_dumpIndex++}.png"
                .Replace('(', '_').Replace(')', '_').Replace(',', '_')), tex.EncodeToPNG());
        }

        private static int Count(Color32[] px, Func<Color32, bool> pred) => px.Count(pred);
        private static bool Bright(Color32 p) => p.r > 128 && p.g > 128 && p.b > 128;
        private static bool Lit(Color32 p) => p.r > 24 || p.g > 24 || p.b > 24;
        private static float MeanLuma(Color32[] px) =>
            (float)px.Where(Lit).Select(p => (p.r + p.g + p.b) / 3.0).DefaultIfEmpty(0).Average();

        private const string Sample = "World text Ag";

        private UniText World(bool unified, Action<UniText> setup = null)
        {
            var t = MakeWorld(Sample, unified, u =>
            {
                u.HorizontalAlignment = HorizontalAlignment.Center;
                u.VerticalAlignment = VerticalAlignment.Middle;
                setup?.Invoke(u);
            }, 400, 120, 70f, update: false);
            Place(t.transform);
            return t;
        }

        [Test]
        public void Render_WorldUnlit_MatchesWorldSpaceCanvas_AtAnAngle()
        {
            var world = World(true);
            var a = Render();
            world.gameObject.SetActive(false);

            var canvasText = MakeCanvasText(Sample, true, u =>
            {
                u.HorizontalAlignment = HorizontalAlignment.Center;
                u.VerticalAlignment = VerticalAlignment.Middle;
            }, 400, 120, 70f, update: false);
            var canvas = canvasText.GetComponentInParent<Canvas>();
            canvas.worldCamera = _cam;
            Place(canvas.transform);
            var b = Render();

            int ca = Count(a, Bright), cb = Count(b, Bright);
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                if (Mathf.Abs(a[i].r - b[i].r) > 48 || Mathf.Abs(a[i].g - b[i].g) > 48) diff++;
            Log($"angle render: world bright={ca} canvas bright={cb} differing px={diff} of {a.Length}");
            Assert.Greater(ca, 1500, "world text drew glyphs");
            Assert.AreEqual(cb, ca, cb * 0.05f, "same coverage as the World Space Canvas");
            Assert.Less(diff, a.Length * 0.005f, "pixel-for-pixel the same as Canvas text");
        }

        [Test]
        public void Render_Lit_FollowsTheMainLight_UnlitDoesNot()
        {
            _lightGo = new GameObject("Sun", typeof(Light));
            var light = _lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1f;
            // The test scene may already hold a directional light (the default scene's sun), which the
            // Built-in pipeline would pick as the main light: switch the others off for this test.
            var others = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Where(l => l != light && l.enabled).ToArray();
            foreach (var o in others) o.enabled = false;
            var savedSun = RenderSettings.sun;
            RenderSettings.sun = light;
            try
            {
            float Luma(bool lit, Vector3 lightForward)
            {
                _lightGo.transform.rotation = Quaternion.LookRotation(lightForward);
                var t = World(true, u => { if (lit) W3.Set(u, "Lighting", "Lit"); });
                var px = Render();
                UnityEngine.Object.DestroyImmediate(t.gameObject);
                return MeanLuma(px);
            }

            // Light travelling +Z hits the text front (the side the camera sees); -Z lights the back.
            var litFront = Luma(true, Vector3.forward);
            var litBack = Luma(true, Vector3.back);
            var unlitFront = Luma(false, Vector3.forward);
            var unlitBack = Luma(false, Vector3.back);
            Log($"lit front={litFront:0.0} back={litBack:0.0}; unlit front={unlitFront:0.0} back={unlitBack:0.0}");
            Assert.Greater(litFront, litBack * 1.8f, "lit text is bright when the light faces it, dark (ambient) when not");
            Assert.AreEqual(unlitFront, unlitBack, 1f, "unlit text ignores the light");
            Assert.Greater(litFront, 100f);
            }
            finally
            {
                foreach (var o in others) if (o != null) o.enabled = true;
                RenderSettings.sun = savedSun;
            }
        }

        [Test]
        public void Render_SingleSided_HiddenFromBehind_DoubleSidedVisible()
        {
            // Camera behind the label (looking at its back face).
            _cam.transform.position = new Vector3(0, 0, 3f);
            _cam.transform.rotation = Quaternion.Euler(0, 180f, 0);
            var single = World(true);
            var s = Count(Render(), Bright);
            UnityEngine.Object.DestroyImmediate(single.gameObject);
            var both = World(true, u => W3.Set(u, "DoubleSided", true));
            var d = Count(Render(), Bright);
            Log($"from behind: single-sided bright={s} double-sided bright={d}");
            Assert.AreEqual(0, s, "back faces culled by default");
            Assert.Greater(d, 1500, "double-sided text is visible from behind");
        }

        [Test]
        public void Render_DepthWrite_OccludesALaterTransparent()
        {
            // A red transparent quad BEHIND the text that is drawn AFTER it (queue 3100). Without depth
            // write it paints over the glyphs; with depth write the glyph pixels keep their colour.
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _junk.Add(quad);
            UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.position = new Vector3(0, 0, 1f);
            quad.transform.localScale = new Vector3(6, 3, 1);
            var mat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3100, color = new Color(1, 0, 0, 1) };
            _junk.Add(mat);
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;

            int WhiteText(bool depth)
            {
                var t = World(true, u => W3.Set(u, "DepthWrite", depth));
                var px = Render();
                UnityEngine.Object.DestroyImmediate(t.gameObject);
                return Count(px, p => p.g > 200 && p.b > 200);
            }

            var without = WhiteText(false);
            var with = WhiteText(true);
            Log($"depth write: white glyph px without={without} with={with}");
            Assert.Less(without, 50, "the later quad covers transparent text");
            Assert.Greater(with, 1200, "depth-writing text occludes the later quad");
        }

        [Test]
        public void Render_LegacyRenderer_World_DrawsText()
        {
            World(false);
            var c = Count(Render(), Bright);
            Log($"legacy world bright={c}");
            Assert.Greater(c, 1200, "legacy SDF materials render through the MeshRenderer");
        }

        [Test]
        public void Render_OverflowClip_ClipsOutsideTheRect()
        {
            var t = MakeWorld("Clipped clipped clipped clipped clipped", true, u =>
            {
                u.WordWrap = false;
                u.Overflow = TextOverflow.Clip;
                u.HorizontalAlignment = HorizontalAlignment.Left;
                u.VerticalAlignment = VerticalAlignment.Middle;
            }, 200, 120, 60f, update: false);
            t.transform.position = Vector3.zero;
            t.transform.localScale = Vector3.one * 0.005f;
            var px = Render();
            // Screen x of the rect's right edge: nothing bright to its right.
            var right = _cam.WorldToScreenPoint(t.transform.TransformPoint(new Vector3(t.rectTransform.rect.xMax + 2, 0, 0))).x;
            var outside = 0; var inside = 0;
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                if (!Bright(px[y * W + x])) continue;
                if (x > right) outside++; else inside++;
            }
            Log($"clip: inside={inside} outside={outside}");
            Assert.Greater(inside, 500);
            Assert.AreEqual(0, outside, "Overflow Clip cuts the glyphs at the rect edge");
        }
    }
}
