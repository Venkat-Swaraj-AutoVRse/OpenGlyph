using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 2, item 3: soft outer glow, inner shadow and a second outline band: markup, style-table
    /// packing, and real GPU renders through UniText/Uber (unified) and UniText/Mobile/SDF (legacy glow)
    /// into a RenderTexture.
    /// </summary>
    public class Wave2EffectTests : Wave2TestBase
    {
        private static readonly Color Bg = Color.black;

        private sealed class Stage : IDisposable
        {
            public GameObject canvasGo, camGo;
            public Camera cam;
            public RenderTexture rt;
            public int w, h;
            public void Dispose()
            {
                if (canvasGo != null) UnityEngine.Object.DestroyImmediate(canvasGo);
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
            }
        }

        private static Stage NewStage(int w, int h)
        {
            var s = new Stage { w = w, h = h };
            s.rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            s.rt.Create();
            s.canvasGo = new GameObject("W2Canvas", typeof(Canvas));
            var canvas = s.canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
            ((RectTransform)s.canvasGo.transform).sizeDelta = new Vector2(w, h);
            s.camGo = new GameObject("W2Cam", typeof(Camera));
            s.cam = s.camGo.GetComponent<Camera>();
            s.cam.orthographic = true; s.cam.orthographicSize = h / 2f; s.cam.aspect = (float)w / h;
            s.cam.transform.position = new Vector3(0, 0, -10);
            s.cam.clearFlags = CameraClearFlags.SolidColor; s.cam.backgroundColor = Bg;
            s.cam.targetTexture = s.rt;
            canvas.worldCamera = s.cam;
            return s;
        }

        private static Color32[] Render(Stage s)
        {
            Canvas.ForceUpdateCanvases();
            Canvas.ForceUpdateCanvases();
            s.cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = s.rt;
            var tex = new Texture2D(s.w, s.h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, s.w, s.h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex);
            return px;
        }

        private Color32[] RenderText(string text, bool unified, Action<UniText> setup, int w = 512, int h = 200, float size = 110f)
        {
            using var s = NewStage(w, h);
            var t = Make(text, unified, setup, w, h, size, parent: s.canvasGo);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Middle;
            var px = Render(s);
            var dump = Environment.GetEnvironmentVariable("OPENGLYPH_W2_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                System.IO.Directory.CreateDirectory(dump);
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(px); tex.Apply();
                var name = new string(text.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dump, $"{(unified ? "u" : "l")}_{name}.png"), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            UnifiedRenderBuilder.ResetShared();
            return px;
        }

        private static int Count(Color32[] px, Func<Color32, bool> pred)
        {
            var n = 0;
            foreach (var p in px) if (pred(p)) n++;
            return n;
        }

        private static bool Greenish(Color32 p) => p.g > 60 && p.g > p.r + 40 && p.g > p.b + 40;
        private static bool Blueish(Color32 p) => p.b > 60 && p.b > p.r + 40 && p.b > p.g + 40;
        private static bool White(Color32 p) => p.r > 230 && p.g > 230 && p.b > 230;
        private static bool Grey(Color32 p) => p.r > 20 && p.r < 200 && Math.Abs(p.r - p.g) < 12 && Math.Abs(p.r - p.b) < 12;

        private static void RegisterSpanStyles(UniText t)
        {
            t.RegisterModifier(new ModRegister { Modifier = new SpanStyleModifier(SpanStyleModifier.Kind.Outline), Rule = new OutlineParseRule() });
            Reg(t, "Glow", "LightSide.GlowParseRule");
            Reg(t, "InnerShadow", "LightSide.InnerShadowParseRule");
            Reg(t, "Outline2", "LightSide.Outline2ParseRule");
        }

        private static void Reg(UniText t, string kind, string rule)
        {
            var kindT = typeof(SpanStyleModifier.Kind);
            if (!Enum.IsDefined(kindT, kind)) Assert.Fail($"SpanStyleModifier.Kind.{kind} missing");
            t.RegisterModifier(new ModRegister
            {
                Modifier = new SpanStyleModifier((SpanStyleModifier.Kind)Enum.Parse(kindT, kind)),
                Rule = (IParseRule)Activator.CreateInstance(W2.Type(rule)),
            });
        }

        // ------------------------------------------------------------------ markup / data

        [Test]
        public void Markup_ParsesGlowInnerShadowOutline2()
        {
            var markup = typeof(SpanStyleMarkup);
            var args = new object[] { "#00FF0080,0.6,1,2", null, null, null };
            Assert.IsTrue((bool)W2.CallStatic(markup, "TryParseGlow", args));
            var c = (Color)args[1];
            Assert.AreEqual(1f, c.g, 1e-3f);
            Assert.AreEqual(128 / 255f * 2f, c.a, 1e-3f, "intensity multiplies alpha");
            Assert.AreEqual(0.6f, (float)args[2], 1e-5f, "size");
            Assert.AreEqual(3f, (float)args[3], 1e-5f, "softness 1 -> falloff exponent 3");

            var a2 = new object[] { "#0000FF,0.25,0.5", null, null, null };
            Assert.IsTrue((bool)W2.CallStatic(markup, "TryParseOutline2", a2));
            Assert.AreEqual(0.25f, (float)a2[2], 1e-5f);
            Assert.AreEqual(0.5f, (float)a2[3], 1e-5f);
            Assert.IsFalse((bool)W2.CallStatic(markup, "TryParseGlow", new object[] { "nope", null, null, null }));
        }

        [Test]
        public void SpanOverride_NestsAndComposes_NewLayers()
        {
            object glow = new SpanStyleOverride();
            W2.SetField(glow, "set", Enum.Parse(typeof(SpanStyleOverride.F), "Glow"));
            W2.SetField(glow, "glowColor", Color.green);
            W2.SetField(glow, "glowOuter", 0.7f);
            W2.SetField(glow, "glowPower", 2f);
            object inner = new SpanStyleOverride();
            W2.SetField(inner, "set", Enum.Parse(typeof(SpanStyleOverride.F), "Outline2"));
            W2.SetField(inner, "outline2Color", Color.blue);
            W2.SetField(inner, "outline2Width", 0.2f);
            var merged = ((SpanStyleOverride)glow).MergeOver((SpanStyleOverride)inner);
            var s = (object)merged.ComposeOnto(GlyphStyle.Default);
            Assert.AreEqual(Color.green, (Color)W2.Get(s, "glowColor"));
            Assert.AreEqual(0.7f, (float)W2.Get(s, "glowOuter"), 1e-5f);
            Assert.AreEqual(Color.blue, (Color)W2.Get(s, "outline2Color"));
            Assert.AreEqual(0.2f, (float)W2.Get(s, "outline2Width"), 1e-5f);
        }

        [Test]
        public void StyleTable_PacksNewColumns()
        {
            object s = GlyphStyle.Default;
            W2.SetField(s, "outline2Color", new Color(0.1f, 0.2f, 0.3f, 0.4f));
            W2.SetField(s, "outline2Width", 0.25f);
            W2.SetField(s, "outline2Softness", 0.5f);
            W2.SetField(s, "innerShadowColor", new Color(0.5f, 0.6f, 0.7f, 0.8f));
            W2.SetField(s, "innerShadowOffsetX", 1f);
            W2.SetField(s, "innerShadowOffsetY", -1f);
            W2.SetField(s, "innerShadowDilate", 0.1f);
            W2.SetField(s, "innerShadowSoftness", 0.2f);
            var table = new StyleTable();
            try
            {
                var row = table.GetOrAdd((GlyphStyle)s);
                Assert.GreaterOrEqual(StyleTable.ColumnsPerStyle, 11);
                Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 0.4f), table.ReadPacked(row, 7), "col7 outline2 colour");
                Assert.AreEqual(new Color(0.5f, 0.6f, 0.7f, 0.8f), table.ReadPacked(row, 8), "col8 inner shadow colour");
                Assert.AreEqual(new Color(0.25f, 0.5f, 0, 0), table.ReadPacked(row, 9), "col9 outline2 width/softness");
                Assert.AreEqual(new Color(1f, -1f, 0.1f, 0.2f), table.ReadPacked(row, 10), "col10 inner shadow params");
            }
            finally { table.Dispose(); }
        }

        [Test]
        public void Shim_MapsInnerUnderlayKeywordToInnerShadow()
        {
            var sh = Shader.Find("UniText/SDF");
            if (sh == null) Assert.Ignore("UniText/SDF shader missing");
            var m = new Material(sh); _junk.Add(m);
            m.EnableKeyword("UNDERLAY_INNER");
            m.SetColor("_UnderlayColor", new Color(0, 0, 0, 0.75f));
            m.SetFloat("_UnderlayOffsetX", 0.5f);
            object s = AppearanceStyleShim.StyleFromMaterials(new[] { m });
            Assert.AreEqual(0.75f, ((Color)W2.Get(s, "innerShadowColor")).a, 1e-4f, "inner keyword -> inner shadow");
            Assert.AreEqual(0.5f, (float)W2.Get(s, "innerShadowOffsetX"), 1e-4f);
            Assert.AreEqual(0f, ((GlyphStyle)s).underlayColor.a, 1e-4f, "not an outer drop shadow");
        }

        // ------------------------------------------------------------------ GPU renders (unified Uber)

        [Test]
        public void Uber_SpanGlow_DrawsAHaloAroundTheGlyphs()
        {
            var plain = RenderText("HI", true, RegisterSpanStyles);
            var glow = RenderText("<glow=#00FF00FF,0.9,0.4,1>HI</glow>", true, RegisterSpanStyles);
            int g0 = Count(plain, Greenish), g1 = Count(glow, Greenish);
            int w0 = Count(plain, White), w1 = Count(glow, White);
            Debug.Log($"[W2] glow: green px plain={g0} glow={g1}; white px plain={w0} glow={w1}");
            Assert.AreEqual(0, g0);
            Assert.Greater(g1, 800, "a green halo surrounds the glyphs");
            Assert.AreEqual(w0, w1, w0 / 20 + 20, "the face itself is unchanged");
        }

        [Test]
        public void Uber_WholeTextStyleGlow_FromComponentStyle()
        {
            var glow = RenderText("HI", true, t =>
            {
                var st = UniTextStyle.Default;
                st.glowColor = new Color(0, 1, 0, 1);
                st.glowOuter = 0.9f;
                st.glowPower = 1.5f;
                t.Style = st;
                t.OverrideStyle = true;
            });
            var n = Count(glow, Greenish);
            Debug.Log($"[W2] whole-text glow green px={n}");
            Assert.Greater(n, 800, "the component Style's glow renders in the unified renderer");
        }

        [Test]
        public void Uber_InnerShadow_DarkensInsideTheFace()
        {
            var plain = RenderText("HI", true, RegisterSpanStyles, size: 150f);
            var shadow = RenderText("<innershadow=#000000FF,0.4,-0.4,0,0.2>HI</innershadow>", true, RegisterSpanStyles, size: 150f);
            int w0 = Count(plain, White), w1 = Count(shadow, White);
            int g0 = Count(plain, Grey), g1 = Count(shadow, Grey);
            Debug.Log($"[W2] inner shadow: white px {w0} -> {w1}; grey px {g0} -> {g1}");
            Assert.Less(w1, w0 * 0.9f, "part of the face is shadowed");
            Assert.Greater(w1, w0 * 0.3f, "most of the face stays lit");
            Assert.Greater(g1, g0 + 200, "the shadow edge is a soft grey band inside the face");
        }

        [Test]
        public void Uber_SecondOutline_DrawsABandOutsideTheOutline()
        {
            var one = RenderText("<outline=#FF0000FF,0.15>HI</outline>", true, RegisterSpanStyles);
            var two = RenderText("<outline=#FF0000FF,0.15><outline2=#0000FFFF,0.25>HI</outline2></outline>", true, RegisterSpanStyles);
            int b0 = Count(one, Blueish), b1 = Count(two, Blueish);
            int r0 = Count(one, p => p.r > 150 && p.g < 80), r1 = Count(two, p => p.r > 150 && p.g < 80);
            Debug.Log($"[W2] outline2: blue px {b0} -> {b1}; red px {r0} -> {r1}");
            Assert.AreEqual(0, b0);
            Assert.Greater(b1, 500, "blue band drawn");
            Assert.Greater(r1, r0 * 0.6f, "the first outline is still drawn (the band is outside it)");
        }

        // ------------------------------------------------------------------ legacy renderer: mobile SDF glow

        [Test]
        public void LegacyMobileSdf_GlowKeyword_RendersGlow()
        {
            var sh = Shader.Find("UniText/Mobile/SDF");
            if (sh == null) Assert.Ignore("UniText/Mobile/SDF missing");
            var mat = new Material(sh); _junk.Add(mat);
            var app = ScriptableObject.CreateInstance<UniTextAppearance>(); _junk.Add(app);
            app.SetDefaultMaterials(mat);
            var off = RenderText("HI", false, t => t.Appearance = app);
            mat.EnableKeyword("GLOW_ON");
            mat.SetColor("_GlowColor", new Color(0, 1, 0, 1));
            mat.SetFloat("_GlowOuter", 0.9f);
            mat.SetFloat("_GlowPower", 1.5f);
            var on = RenderText("HI", false, t => t.Appearance = app);
            int g0 = Count(off, Greenish), g1 = Count(on, Greenish);
            int w0 = Count(off, White), w1 = Count(on, White);
            Debug.Log($"[W2] mobile SDF glow (legacy renderer): green px off={g0} on={g1}; white px {w0} -> {w1}");
            Assert.Greater(w0, 500, "legacy render produced glyphs");
            Assert.AreEqual(0, g0);
            Assert.Greater(g1, 800, "GLOW_ON draws a halo in UniText/Mobile/SDF");
        }
    }
}
