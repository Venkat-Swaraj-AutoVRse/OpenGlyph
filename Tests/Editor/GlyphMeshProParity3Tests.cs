// GlyphMeshProUGUI TMP parity, round 3: the formerly stored-only properties now change layout and
// rendering (Phase A), and the missing features (small caps, highlight, layout tags, Page/Linked
// overflow) exist (Phase B). Each test fails on main (where the property was stored only / the tag
// was unknown). Where a real TextMeshProUGUI can be built (graphics device + TMP shaders) the same
// setup is measured in TMP and the effect compared within a tolerance; otherwise the TMP formula
// (TMP_Text units: em/100 of the font size) is the expectation and the TMP leg is logged as skipped.
//
// New API (pageToDisplay, linkedTextComponent, engine counters) is reached by reflection so this file
// compiles against main for the fail-without-fix proof.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using OpenGlyph;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;
using TextOverflowModes = OpenGlyph.TextOverflowModes;

namespace LightSide.Tests
{
    [TestFixture]
    public class GlyphMeshProParity3Tests
    {
        private const UniText.UnifiedRendererMode Legacy = UniText.UnifiedRendererMode.ForceOff;
        private const UniText.UnifiedRendererMode Unified = UniText.UnifiedRendererMode.ForceOn;
        private const float FontSize = 36f;
        private const float Em = FontSize * 0.01f; // TMP: currentEmScale = fontSize * 0.01

        private readonly List<Object> _junk = new();
        private UniTextFontStack _stack;
        private UniTextFont _font;
        private Canvas _canvas;
        private string _notoPath;
        private TMP_FontAsset _tmpFont;
        private bool _tmpTried;

        [SetUp]
        public void SetUp()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            SharedGlyphAtlas.Clear();
            _notoPath = MsdfTestUtil.FindNotoSansPath();
            if (_notoPath == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            _font = UniTextFont.CreateFontAsset(File.ReadAllBytes(_notoPath));
            if (_font == null) Assert.Ignore("font asset creation failed.");
            _font.name = "NotoSans-Regular";
            _junk.Add(_font);
            _stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(_stack);
            _stack.fonts.Add(_font);

            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            UnifiedRenderBuilder.SnapshotMeshesForTests = true;
        }

        [TearDown]
        public void TearDown()
        {
            UnifiedRenderBuilder.SnapshotMeshesForTests = false;
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            if (_tmpFont != null) Object.DestroyImmediate(_tmpFont);
            _tmpFont = null; _tmpTried = false;
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        // ------------------------------------------------------------------ helpers

        private GlyphMeshProUGUI Make(string text, float w = 600f, float h = 400f,
            UniText.UnifiedRendererMode mode = Legacy, Action<GlyphMeshProUGUI> configure = null)
        {
            var go = new GameObject("GMP", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(_canvas.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(w, h);
            var c = go.AddComponent<GlyphMeshProUGUI>();
            c.SetUnifiedRendererModeForTests(mode);
            c.FontStack = _stack;
            c.fontSize = FontSize;
            c.alignment = OpenGlyph.TextAlignmentOptions.TopLeft;
            configure?.Invoke(c);
            c.text = text;
            Canvas.ForceUpdateCanvases();
            return c;
        }

        private static void Render() => Canvas.ForceUpdateCanvases();

        /// <summary>A real TMP component on the same TTF, or null when TMP cannot be built here.</summary>
        private TextMeshProUGUI MakeTmp(string text, float w, float h, Action<TextMeshProUGUI> configure = null)
        {
            if (!_tmpTried)
            {
                _tmpTried = true;
                EnsureTmpEnvironment();
                try
                {
                    _tmpFont = TMP_FontAsset.CreateFontAsset(_notoPath, 0, 90, 9,
                        UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024);
                }
                catch (Exception e) { Debug.Log($"[GMP3] TMP font build failed: {e.GetType().Name}: {e.Message}"); }
                if (_tmpFont == null) Debug.Log("[GMP3] TMP comparison unavailable (no TMP font asset); using TMP formulas.");
            }
            if (_tmpFont == null) return null;
            try
            {
                var go = new GameObject("TMP", typeof(RectTransform)); _junk.Add(go);
                go.transform.SetParent(_canvas.transform, false);
                ((RectTransform)go.transform).sizeDelta = new Vector2(w, h);
                var t = go.AddComponent<TextMeshProUGUI>();
                t.font = _tmpFont;
                t.fontSize = FontSize;
                t.alignment = TMPro.TextAlignmentOptions.TopLeft;
                t.textWrappingMode = TMPro.TextWrappingModes.Normal;
                configure?.Invoke(t);
                t.text = text;
                t.ForceMeshUpdate(true, true);
                return t;
            }
            catch (Exception e)
            {
                Debug.Log($"[GMP3] TMP component failed: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// TMP needs its TMP Settings asset and an SDF shader to build a font asset. A test host without
        /// the TMP Essential Resources has neither, so give TMP a runtime settings instance and a stand-in
        /// UI shader (layout and textInfo — what these tests measure — do not depend on the shader).
        /// </summary>
        private static void EnsureTmpEnvironment()
        {
            try
            {
                const BindingFlags S = BindingFlags.NonPublic | BindingFlags.Static;
                var settings = typeof(TMP_Settings).GetField("s_Instance", S);
                if (settings != null && settings.GetValue(null) == null && Resources.Load<TMP_Settings>("TMP Settings") == null)
                {
                    var inst = ScriptableObject.CreateInstance<TMP_Settings>();
                    inst.hideFlags = HideFlags.DontSave;
                    settings.SetValue(null, inst);
                }
                var sdf = typeof(ShaderUtilities).GetField("k_ShaderRef_MobileSDF", S);
                if (sdf != null && sdf.GetValue(null) == null && Shader.Find("TextMeshPro/Mobile/Distance Field") == null)
                    sdf.SetValue(null, Shader.Find("UI/Default"));
            }
            catch (Exception e) { Debug.Log($"[GMP3] TMP environment shim failed: {e.Message}"); }
        }

        /// <summary>Every drawn quad (4 consecutive vertices) of the component's last rebuild, with colours.</summary>
        private static List<(Vector3[] v, Color32[] c)> Quads(UniText t)
        {
            var list = new List<(Vector3[], Color32[])>();
            foreach (var (mesh, _, _) in t.GetDrawnMeshMaterialsForTests())
            {
                var vs = mesh.vertices;
                var cs = mesh.colors32;
                for (var i = 0; i + 3 < vs.Length; i += 4)
                    list.Add((new[] { vs[i], vs[i + 1], vs[i + 2], vs[i + 3] },
                        cs.Length == vs.Length ? new[] { cs[i], cs[i + 1], cs[i + 2], cs[i + 3] } : new Color32[4]));
            }
            return list;
        }

        private static int QuadCount(UniText t) => Quads(t).Count;

        /// <summary>Visual lines: first-glyph left x per distinct baseline, top to bottom.</summary>
        private static List<(float y, float firstX, int firstCluster)> Lines(UniText t)
        {
            var byY = new SortedDictionary<float, (float x, int cl)>();
            foreach (var g in t.ResultGlyphs)
            {
                var key = Mathf.Round(g.y * 100f) / 100f;
                byY[key] = byY.TryGetValue(key, out var cur)
                    ? (Mathf.Min(cur.x, g.left), Math.Min(cur.cl, g.cluster))
                    : (g.left, g.cluster);
            }
            var r = new List<(float, float, int)>();
            foreach (var kv in byY) r.Add((kv.Key, kv.Value.x, kv.Value.cl));
            return r;
        }

        private static object GetProp(object o, string name) =>
            o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);

        private static void SetProp(object o, string name, object value)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(p, $"{o.GetType().Name}.{name} is missing (feature not implemented).");
            p.SetValue(o, value);
        }

        private static long StaticCounter(Type t, string name)
        {
            var f = t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(f, $"{t.Name}.{name} counter is missing.");
            return Convert.ToInt64(f.GetValue(null));
        }

        // ================================================================== Phase A

        [Test]
        public void CharacterSpacing_AddsEmHundredthsAfterEachCharacter_LikeTmp()
        {
            const string s = "abcd";
            const float cs = 10f;
            var c = Make(s);
            var w0 = c.GetPreferredValues(0, 0).x;
            c.characterSpacing = cs;
            var w1 = c.GetPreferredValues(0, 0).x;
            var gmpDelta = w1 - w0;
            // TMP: textWidth = xAdvance + last glyph advance -> (n - 1) spacings.
            var expected = (s.Length - 1) * cs * Em;
            var tmp = MakeTmp(s, 600, 400);
            float tmpDelta = float.NaN;
            if (tmp != null)
            {
                var t0 = tmp.GetPreferredValues().x; tmp.characterSpacing = cs; var t1 = tmp.GetPreferredValues().x;
                tmpDelta = t1 - t0; expected = tmpDelta;
            }
            Debug.Log($"[GMP3] characterSpacing={cs}: GMP delta={gmpDelta:F2} TMP delta={tmpDelta:F2} formula={(s.Length - 1) * cs * Em:F2}");
            Assert.AreEqual(expected, gmpDelta, 1.0f, "preferred width delta from characterSpacing");

            // Rendered: the last glyph moved right by (n-1) spacings.
            Render();
            var glyphs = c.ResultGlyphs;
            Assert.Greater(glyphs.Length, 3);
            var plain = Make(s);
            Assert.AreEqual(3 * cs * Em, glyphs[3].x - plain.ResultGlyphs[3].x, 0.5f, "4th glyph offset");
        }

        [Test]
        public void WordSpacing_AddsEmHundredthsAfterWhitespace_LikeTmp()
        {
            const string s = "a b c";
            const float ws = 20f;
            var c = Make(s);
            var w0 = c.GetPreferredValues(0, 0).x;
            c.wordSpacing = ws;
            var w1 = c.GetPreferredValues(0, 0).x;
            var expected = 2 * ws * Em;
            var tmp = MakeTmp(s, 600, 400);
            float tmpDelta = float.NaN;
            if (tmp != null)
            {
                var t0 = tmp.GetPreferredValues().x; tmp.wordSpacing = ws; var t1 = tmp.GetPreferredValues().x;
                tmpDelta = t1 - t0; expected = tmpDelta;
            }
            Debug.Log($"[GMP3] wordSpacing={ws}: GMP delta={w1 - w0:F2} TMP delta={tmpDelta:F2} formula={2 * ws * Em:F2}");
            Assert.AreEqual(expected, w1 - w0, 1.0f, "preferred width delta from wordSpacing (2 spaces)");
        }

        [TestCase(20f)]
        [TestCase(-20f)]
        public void LineSpacing_AddsEmHundredthsBetweenLines_IncludingNegative_LikeTmp(float ls)
        {
            const string s = "Ag\nAg\nAg";
            var c = Make(s);
            var h0 = c.GetPreferredValues(600, 400).y;
            var adv0 = Lines(c)[1].y - Lines(c)[0].y;
            c.lineSpacing = ls;
            var h1 = c.GetPreferredValues(600, 400).y;
            Render();
            var adv1 = Lines(c)[1].y - Lines(c)[0].y;
            var expected = ls * Em;
            var tmp = MakeTmp(s, 600, 400);
            float tmpAdv = float.NaN;
            if (tmp != null)
            {
                var a0 = tmp.textInfo.lineInfo[0].baseline - tmp.textInfo.lineInfo[1].baseline;
                tmp.lineSpacing = ls; tmp.ForceMeshUpdate(true, true);
                var a1 = tmp.textInfo.lineInfo[0].baseline - tmp.textInfo.lineInfo[1].baseline;
                tmpAdv = a1 - a0; expected = tmpAdv;
            }
            Debug.Log($"[GMP3] lineSpacing={ls}: GMP line advance delta={adv1 - adv0:F2} height delta={h1 - h0:F2} TMP advance delta={tmpAdv:F2} formula={ls * Em:F2}");
            Assert.AreEqual(expected, adv1 - adv0, 0.6f, "line advance delta");
            Assert.AreEqual(2 * expected, h1 - h0, 1.2f, "preferred height delta (2 gaps)");
        }

        [Test]
        public void ParagraphSpacing_AddsOnlyAfterParagraphBreaks_LikeTmp()
        {
            // "aaaa bbbb" wraps into two lines; the newline then starts a new paragraph.
            const string s = "aaaa bbbb\ncccc";
            var width = Make("aaaa").GetPreferredValues(0, 0).x * 1.4f;
            var c = Make(s, width);
            var l0 = Lines(c);
            Assert.AreEqual(3, l0.Count, "fixture: 2 wrapped lines + 1 paragraph");
            c.paragraphSpacing = 30f;
            Render();
            var l1 = Lines(c);
            var wrapGap = (l1[1].y - l1[0].y) - (l0[1].y - l0[0].y);
            var paraGap = (l1[2].y - l1[1].y) - (l0[2].y - l0[1].y);
            Debug.Log($"[GMP3] paragraphSpacing=30: wrap-gap delta={wrapGap:F2} paragraph-gap delta={paraGap:F2} formula={30 * Em:F2}");
            Assert.AreEqual(0f, wrapGap, 0.05f, "a soft wrap gets no paragraph spacing");
            Assert.AreEqual(30f * Em, paraGap, 0.5f, "the newline gap grows by paragraphSpacing * em");
        }

        [Test]
        public void Margin_InsetsTextArea_AndAddsToPreferredSize_LikeTmp()
        {
            const string s = "Hello margin world";
            var plain = Make(s, 400, 200);
            var p0 = plain.GetPreferredValues();
            var plainQuadX = Quads(plain)[0].v[0].x;
            var m = new Vector4(30, 10, 20, 5);
            var c = Make(s, 400, 200, Legacy, g => g.margin = m);
            var vx = Quads(c)[0].v[0].x - plainQuadX;
            var p1 = c.GetPreferredValues();
            Debug.Log($"[GMP3] margin {m}: preferred delta=({p1.x - p0.x:F2},{p1.y - p0.y:F2}) first quad dx={vx:F2}");
            Assert.AreEqual(m.x + m.z, p1.x - p0.x, 0.6f, "positive left+right margins add to preferred width");
            Assert.AreEqual(m.y + m.w, p1.y - p0.y, 0.6f, "positive top+bottom margins add to preferred height");
            Assert.AreEqual(m.x, vx, 0.5f, "the left margin moves the text right");

            // The text area width shrinks: a width that fits without margins wraps with them.
            var fitW = p0.x + 2f;
            var narrow = Make(s, fitW, 200, Legacy, g => g.margin = new Vector4(fitW * 0.3f, 0, 0, 0));
            Assert.Greater(Lines(narrow).Count, 1, "margins reduce the wrap width");
            var tmp = MakeTmp(s, fitW, 200, t => t.margin = new Vector4(fitW * 0.3f, 0, 0, 0));
            if (tmp != null) Assert.AreEqual(tmp.textInfo.lineCount, Lines(narrow).Count, "same wrap as TMP with margins");
        }

        [Test]
        public void RichTextOff_ShowsTagsLiterally()
        {
            const string s = "<b>x</b> <color=red>y</color>";
            var on = Make(s);
            var off = Make(s, configure: g => g.richText = false);
            Debug.Log($"[GMP3] richText on glyphs={on.ResultGlyphs.Length} off glyphs={off.ResultGlyphs.Length}");
            Assert.AreEqual(3, on.ResultGlyphs.Length, "rich text on: tags parsed (x, space, y)");
            Assert.AreEqual(s.Length, off.ResultGlyphs.Length, "rich text off: every character of the tags is laid out");
            Assert.AreEqual(s, off.CleanText, "clean text is the raw text");
            var tmp = MakeTmp(s, 600, 400, t => t.richText = false);
            if (tmp != null) Assert.AreEqual(tmp.textInfo.characterCount, off.ResultGlyphs.Length, "same character count as TMP");
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void VertexGradient_FourCorners_MultipliedWithColor(UniText.UnifiedRendererMode mode)
        {
            var g = new OpenGlyph.VertexGradient(Color.red, Color.green, Color.blue, new Color(1, 1, 1, 0.5f));
            var c = Make("H", 200, 100, mode, x =>
            {
                x.color = new Color(1f, 1f, 1f, 1f);
                x.colorGradient = g;
                x.enableVertexGradient = true;
            });
            var q = Quads(c);
            Assert.AreEqual(1, q.Count);
            var col = q[0].c; // 0 = BL, 1 = TL, 2 = TR, 3 = BR
            Assert.AreEqual((Color32)Color.blue, col[0], "bottom-left");
            Assert.AreEqual((Color32)Color.red, col[1], "top-left");
            Assert.AreEqual((Color32)Color.green, col[2], "top-right");
            Assert.AreEqual(new Color32(255, 255, 255, 128), col[3], "bottom-right (alpha multiplied)");

            c.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            Render();
            var col2 = Quads(c)[0].c;
            Assert.AreEqual(128, col2[1].r, 1, "gradient multiplies the vertex colour (TMP: gradient * colour)");
            Assert.AreEqual(0, col2[1].g, 1);
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void MaxVisibleCharacters_HidesGlyphs_TypewriterDoesNotReshapeOrRelayout(UniText.UnifiedRendererMode mode)
        {
            const string s = "Hello world";
            var c = Make(s, 600, 200, mode);
            var all = QuadCount(c);
            Assert.AreEqual(10, all, "fixture: 10 visible letters (the space draws nothing)");

            c.maxVisibleCharacters = 3;
            Render();
            Assert.AreEqual(3, QuadCount(c), "only the first 3 characters are drawn");
            var info = c.textInfo;
            var visible = 0;
            for (var i = 0; i < info.characterCount; i++) if (info.characterInfo[i].isVisible) visible++;
            Assert.AreEqual(3, visible, "textInfo reflects visibility");

            var tmp = MakeTmp(s, 600, 200, t => t.maxVisibleCharacters = 3);
            if (tmp != null)
            {
                var tv = 0;
                for (var i = 0; i < tmp.textInfo.characterCount; i++) if (tmp.textInfo.characterInfo[i].isVisible) tv++;
                Debug.Log($"[GMP3] maxVisibleCharacters=3: GMP visible={visible} TMP visible={tv}");
                Assert.AreEqual(tv, QuadCount(c), "same visible glyph count as TMP");
            }

            // Typewriter: animate every frame; no reshape (first pass) and no relayout.
            var firstPass0 = StaticCounter(typeof(TextProcessor), "FirstPassCount");
            var layout0 = StaticCounter(typeof(TextProcessor), "LayoutCount");
            for (var n = 0; n <= s.Length; n++)
            {
                c.maxVisibleCharacters = n;
                Render();
                var expectedQuads = 0;
                for (var i = 0; i < n && i < s.Length; i++) if (s[i] != ' ') expectedQuads++;
                Assert.AreEqual(expectedQuads, QuadCount(c), $"maxVisibleCharacters={n}");
            }
            Assert.AreEqual(firstPass0, StaticCounter(typeof(TextProcessor), "FirstPassCount"), "typewriter must not reshape");
            Assert.AreEqual(layout0, StaticCounter(typeof(TextProcessor), "LayoutCount"), "typewriter must not re-layout");
        }

        [Test]
        public void MaxVisibleWords_And_MaxVisibleLines_HideLikeTmp()
        {
            var words = Make("one two three", configure: g => g.maxVisibleWords = 2);
            var wordQuads = QuadCount(words);
            Assert.AreEqual(6, wordQuads, "two words visible (one, two)");
            var lines = Make("Ab\nCd\nEf", configure: g => g.maxVisibleLines = 2);
            Assert.AreEqual(4, QuadCount(lines), "two lines visible");
            var tw = MakeTmp("one two three", 600, 400, t => t.maxVisibleWords = 2);
            if (tw != null)
            {
                var v = 0;
                for (var i = 0; i < tw.textInfo.characterCount; i++) if (tw.textInfo.characterInfo[i].isVisible) v++;
                Debug.Log($"[GMP3] maxVisibleWords=2: GMP quads={wordQuads} TMP visible={v}");
                Assert.AreEqual(v, wordQuads);
            }
        }

        [Test]
        public void SettingEachProperty_AfterEnable_UpdatesNextMesh()
        {
            // No OnDisable/OnEnable: assign after the component rendered once, render, compare the mesh.
            string Sig(UniText t)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var (v, col) in Quads(t)) sb.Append(v[0].x.ToString("F1")).Append(',').Append(v[0].y.ToString("F1")).Append(',').Append(col[1].r).Append(';');
                return sb.Append('#').Append(Quads(t).Count).ToString();
            }

            var cases = new (string name, Action<GlyphMeshProUGUI> set)[]
            {
                ("characterSpacing", g => g.characterSpacing = 15),
                ("wordSpacing", g => g.wordSpacing = 40),
                ("lineSpacing", g => g.lineSpacing = 40),
                ("paragraphSpacing", g => g.paragraphSpacing = 40),
                ("margin", g => g.margin = new Vector4(25, 15, 0, 0)),
                ("richText", g => g.richText = false),
                ("enableVertexGradient", g => { g.colorGradient = new OpenGlyph.VertexGradient(Color.blue); g.enableVertexGradient = true; }),
                ("maxVisibleCharacters", g => g.maxVisibleCharacters = 2),
                ("maxVisibleWords", g => g.maxVisibleWords = 1),
                ("maxVisibleLines", g => g.maxVisibleLines = 1),
                ("fontStyle SmallCaps", g => g.fontStyle = OpenGlyph.FontStyles.SmallCaps),
                ("fontStyle Highlight", g => g.fontStyle = OpenGlyph.FontStyles.Highlight),
            };
            foreach (var (name, set) in cases)
            {
                var c = Make("ab <b>cd</b> ef\ngh ij");
                var before = Sig(c);
                set(c);
                Render();
                Assert.AreNotEqual(before, Sig(c), $"{name}: setting it at runtime must change the next mesh");
            }
        }

        // ================================================================== Phase B

        [Test]
        public void SmallCaps_UsesSmcpOrSyntheticCapitalsAtPointEight()
        {
            var plain = Make("Abc");
            var caps = Make("Abc", configure: g => g.fontStyle = OpenGlyph.FontStyles.SmallCaps);
            var qc = Quads(caps);
            var upper = Make("ABC");
            var qu = Quads(upper);
            var pg = plain.ResultGlyphs; var cg = caps.ResultGlyphs; var ug = upper.ResultGlyphs;
            Assert.AreEqual(3, cg.Length);
            Assert.AreEqual(pg[0].glyphId, cg[0].glyphId, "the capital stays a capital");
            Assert.AreNotEqual(pg[1].glyphId, cg[1].glyphId, "lowercase b is replaced (smcp glyph or synthetic capital)");
            Assert.AreNotEqual(pg[2].glyphId, cg[2].glyphId, "lowercase c is replaced");

            var probe = typeof(Shaper).GetMethod("FontSubstitutesFeature", BindingFlags.Public | BindingFlags.Static);
            var smcp = probe != null && (bool)probe.Invoke(null, new object[] { _font, (uint)(('s' << 24) | ('m' << 16) | ('c' << 8) | 'p') });
            var hCaps = qc[1].v[1].y - qc[1].v[0].y;
            var hUpper = qu[1].v[1].y - qu[1].v[0].y;
            Debug.Log($"[GMP3] smallcaps: font smcp={smcp} b glyph plain={pg[1].glyphId} caps={cg[1].glyphId} upper={ug[1].glyphId} quad h caps={hCaps:F2} upper={hUpper:F2}");
            if (!smcp)
            {
                Assert.AreEqual(ug[1].glyphId, cg[1].glyphId, "synthetic: the capital glyph");
                Assert.AreEqual(0.8f, hCaps / hUpper, 0.02f, "synthetic: drawn at 0.8x (TMP)");
            }
            else
            {
                Assert.Less(hCaps, hUpper, "smcp small capitals are shorter than full capitals");
            }
        }

        [TestCase(Legacy)]
        [TestCase(Unified)]
        public void MarkTag_DrawsLineHighBoxBehindRun(UniText.UnifiedRendererMode mode)
        {
            var c = Make("x<mark=#FF000080>abc</mark>y", 600, 200, mode);
            var quads = Quads(c);
            Assert.AreEqual(6, quads.Count, "5 glyphs + 1 highlight box");
            var box = quads.Find(q => q.c[0].r == 255 && q.c[0].g == 0 && q.c[0].b == 0);
            Assert.IsNotNull(box.v, "a red highlight quad exists");
            Assert.AreEqual(128, box.c[0].a, "highlight alpha from the tag");

            var fi = c.MainFont.FaceInfo;
            var scale = FontSize * c.MainFont.FontScale / c.MainFont.UnitsPerEm;
            var expectedH = (fi.ascentLine - fi.descentLine) * scale;
            Assert.AreEqual(expectedH, box.v[1].y - box.v[0].y, 0.6f, "box spans ascender..descender (TMP highlight)");
            var g = c.ResultGlyphs;
            Assert.AreEqual(g[3].right - g[1].left, box.v[2].x - box.v[0].x, 0.6f, "box spans the run's advances");

            // Behind the text: the box's triangles come first in its mesh.
            foreach (var (mesh, _, _) in c.GetDrawnMeshMaterialsForTests())
            {
                var tris = mesh.triangles;
                var cols = mesh.colors32;
                if (tris.Length == 0) continue;
                var first = cols[tris[0]];
                Assert.AreEqual(255, first.r, "first triangle drawn is the highlight (behind the glyphs)");
                Assert.AreEqual(0, first.g);
            }

            var fs = Make("abc", 600, 200, mode, x => x.fontStyle = OpenGlyph.FontStyles.Highlight);
            var hq = Quads(fs).Find(q => q.c[0].r == 255 && q.c[0].g == 255 && q.c[0].b == 0);
            Assert.IsNotNull(hq.v, "FontStyles.Highlight draws TMP's default #FFFF0040 box");
            Assert.AreEqual(64, hq.c[0].a);
        }

        [Test]
        public void NoBrTag_KeepsSpanOnOneLine()
        {
            // Width fits "aaa bbb " (the engine counts the space before a wrap) but not "aaa bbb ccc".
            var w = Make("aaa bbb ").GetPreferredValues(0, 0).x + 4f;
            var plain = Make("aaa bbb ccc", w);
            var nobr = Make("aaa <nobr>bbb ccc</nobr>", w);
            var lp = Lines(plain); var ln = Lines(nobr);
            Debug.Log($"[GMP3] nobr: plain lines={lp.Count} line2 start={lp[1].firstCluster}; nobr lines={ln.Count} line2 start={ln[1].firstCluster}");
            Assert.AreEqual(8, lp[1].firstCluster, "plain: breaks before ccc");
            Assert.AreEqual(4, ln[1].firstCluster, "nobr: 'bbb ccc' moves to the next line together");
            var tmp = MakeTmp("aaa <nobr>bbb ccc</nobr>", w, 400);
            if (tmp != null) Assert.AreEqual(tmp.textInfo.lineInfo[1].firstCharacterIndex, ln[1].firstCluster, "same break as TMP");
        }

        [Test]
        public void AlignTag_SetsPerParagraphAlignment()
        {
            var c = Make("<align=right>Right</align>\n<align=center>Mid</align>\nLeft", 600, 300);
            var l = Lines(c);
            Assert.AreEqual(3, l.Count);
            var g = c.ResultGlyphs.ToArray();
            float RightEdge(int line) { var e = float.MinValue; foreach (var x in g) if (Mathf.Abs(x.y - l[line].y) < 0.1f) e = Mathf.Max(e, x.right); return e; }
            Debug.Log($"[GMP3] align: right line [{l[0].firstX:F1}..{RightEdge(0):F1}] center [{l[1].firstX:F1}..{RightEdge(1):F1}] left start={l[2].firstX:F1}");
            Assert.AreEqual(600f, RightEdge(0), 1.5f, "<align=right> line ends at the right edge");
            Assert.AreEqual(300f, (l[1].firstX + RightEdge(1)) * 0.5f, 1.5f, "<align=center> line is centred");
            Assert.AreEqual(0f, l[2].firstX, 0.5f, "untagged paragraph keeps the component alignment");
        }

        [Test]
        public void IndentTag_IndentsWrappedLines_AndJumpsMidLine_LikeTmp()
        {
            var c = Make("<indent=60>one two three four five six seven eight</indent>", 300, 400);
            var l = Lines(c);
            Assert.Greater(l.Count, 1, "fixture wraps");
            foreach (var line in l) Assert.AreEqual(60f, line.firstX, 0.5f, "every line of the span starts at the indent");

            var hanging = Make("1.<indent=80>Hanging item text that wraps around</indent>", 300, 400);
            var hl = Lines(hanging);
            var g = hanging.ResultGlyphs.ToArray();
            float xOfCluster(int cl) { foreach (var x in g) if (x.cluster == cl) return x.left; return float.NaN; }
            Debug.Log($"[GMP3] indent: line starts {string.Join(",", l.ConvertAll(x => x.firstX.ToString("F1")))}; hanging 'H' x={xOfCluster(2):F1} line2 x={(hl.Count > 1 ? hl[1].firstX : -1):F1}");
            Assert.AreEqual(0f, hl[0].firstX, 0.5f, "the marker stays at the line start");
            Assert.AreEqual(80f, xOfCluster(2), 0.5f, "text after <indent> jumps to the indent (TMP m_xAdvance = indent)");
            Assert.Greater(hl.Count, 1);
            Assert.AreEqual(80f, hl[1].firstX, 0.5f, "wrapped lines start at the indent (hanging indent)");

            var pct = Make("<indent=10%>x</indent>", 300, 100);
            Assert.AreEqual(30f, Lines(pct)[0].firstX, 0.5f, "percent of the text-area width");
            var em = Make("<indent=1em>x</indent>", 300, 100);
            Assert.AreEqual(FontSize, Lines(em)[0].firstX, 0.5f, "em = font size");

            var tmp = MakeTmp("1.<indent=80>Hanging item text that wraps around</indent>", 300, 400);
            if (tmp != null)
            {
                Debug.Log($"[GMP3] TMP hanging 'H' x={tmp.textInfo.characterInfo[2].origin - tmp.rectTransform.rect.xMin:F1} line2 x={tmp.textInfo.characterInfo[tmp.textInfo.lineInfo[1].firstCharacterIndex].origin - tmp.rectTransform.rect.xMin:F1}");
                Assert.AreEqual(tmp.textInfo.lineCount, hl.Count, "same line count as TMP");
            }
        }

        [Test]
        public void LineIndentTag_IndentsFirstLineOfEachParagraph()
        {
            var c = Make("<line-indent=40>aa\nbb</line-indent>\ncc", 600, 300);
            var l = Lines(c);
            Assert.AreEqual(3, l.Count);
            Assert.AreEqual(40f, l[0].firstX, 0.5f);
            Assert.AreEqual(40f, l[1].firstX, 0.5f);
            Assert.AreEqual(0f, l[2].firstX, 0.5f, "outside the span");
        }

        [Test]
        public void FontTag_SwitchesFontForSpan_ResolvedByName()
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.openglyph.text");
            var silkPath = pkg != null ? Path.Combine(pkg.resolvedPath, "Tests", "Editor", "Fonts", "Silkscreen-Regular.ttf") : null;
            if (silkPath == null || !File.Exists(silkPath)) Assert.Ignore("Silkscreen fixture not found.");
            var silk = UniTextFont.CreateFontAsset(File.ReadAllBytes(silkPath));
            silk.name = "Silkscreen-Regular"; _junk.Add(silk);
            // Resolution through the component's stack by asset name (a fallback entry).
            _stack.fonts.Add(silk);
            var c = Make("A<font=\"Silkscreen-Regular\">B</font>C");
            var g = c.ResultGlyphs;
            Assert.AreEqual(3, g.Length);
            var silkId = UniTextFontProvider.GetFontId(silk);
            Assert.AreEqual(silkId, g[1].fontId, "B uses the named font");
            Assert.AreNotEqual(silkId, g[0].fontId, "A stays on the main font");
            Assert.AreNotEqual(silkId, g[2].fontId, "C stays on the main font");
            var def = Make("A<font=default>B</font>C");
            Assert.AreNotEqual(silkId, def.ResultGlyphs[1].fontId, "<font=default> keeps the component font");
        }

        [Test]
        public void OverflowPage_ShowsTheRequestedPageOfLines_LikeTmp()
        {
            const string s = "Alpha\nBravo\nCharlie\nDelta\nEcho\nFoxtrot";
            var lineH = Make("A\nB").GetPreferredValues(600, 0).y - Make("A").GetPreferredValues(600, 0).y;
            var h = Make("A").GetPreferredValues(600, 0).y + lineH + 2f; // two lines fit
            var c = Make(s, 600, h, Legacy, g => g.overflowMode = TextOverflowModes.Page);
            Assert.AreEqual(0, Lines(c)[0].firstCluster, "page 1 starts with Alpha");
            Assert.AreEqual(2, Lines(c).Count, "a page holds the lines that fit");
            SetProp(c, "pageToDisplay", 2);
            Render();
            var p2 = Lines(c);
            Assert.AreEqual(s.IndexOf("Charlie", StringComparison.Ordinal), p2[0].firstCluster, "page 2 starts with Charlie");
            Assert.AreEqual(Lines(Make(s, 600, h))[0].y, p2[0].y, 0.05f, "the page is laid out from the top");
            Assert.AreEqual(3, c.textInfo.pageCount, "6 lines / 2 per page");
            SetProp(c, "pageToDisplay", 99);
            Render();
            Assert.AreEqual(s.IndexOf("Echo", StringComparison.Ordinal), Lines(c)[0].firstCluster, "past the end clamps to the last page");

            var tmp = MakeTmp(s, 600, h, t => { t.overflowMode = TMPro.TextOverflowModes.Page; t.pageToDisplay = 2; });
            if (tmp != null)
            {
                var pi = tmp.textInfo.pageInfo[1];
                Debug.Log($"[GMP3] page 2: GMP first char={p2[0].firstCluster} lines={p2.Count}; TMP pages={tmp.textInfo.pageCount} page2 first char={pi.firstCharacterIndex} last={pi.lastCharacterIndex}");
                Assert.AreEqual(pi.firstCharacterIndex, p2[0].firstCluster, "same page start as TMP");
                Assert.AreEqual(tmp.textInfo.pageCount, c.textInfo.pageCount, "same page count as TMP");
            }
        }

        [Test]
        public void OverflowLinked_SendsOverflowingTextToLinkedComponent_LikeTmp()
        {
            const string s = "Alpha\nBravo\nCharlie\nDelta\nEcho\nFoxtrot";
            var lineH = Make("A\nB").GetPreferredValues(600, 0).y - Make("A").GetPreferredValues(600, 0).y;
            var h = Make("A").GetPreferredValues(600, 0).y + lineH + 2f; // two lines fit per box

            var third = Make(string.Empty, 600, h);
            var second = Make(string.Empty, 600, h, Legacy, g => g.overflowMode = TextOverflowModes.Linked);
            SetProp(second, "linkedTextComponent", third);
            var first = Make(string.Empty, 600, h, Legacy, g => g.overflowMode = TextOverflowModes.Linked);
            SetProp(first, "linkedTextComponent", second);
            first.text = s;
            Render();

            Debug.Log($"[GMP3] linked: first lines={Lines(first).Count} overflow={GetProp(first, "firstOverflowCharacterIndex")}; second text='{second.text?.Length}' first={GetProp(second, "firstVisibleCharacter")} lines={Lines(second).Count} overflow={GetProp(second, "firstOverflowCharacterIndex")}; third text len={third.text?.Length} first={GetProp(third, "firstVisibleCharacter")} lines={Lines(third).Count}");
            Assert.AreEqual(2, Lines(first).Count, "source keeps the lines that fit");
            Assert.AreEqual(s, second.text, "linked component receives the text");
            Assert.AreEqual(s.IndexOf("Charlie", StringComparison.Ordinal), GetProp(second, "firstVisibleCharacter"),
                "TMP firstVisibleCharacter = first overflowing character");
            Assert.AreEqual(s.IndexOf("Charlie", StringComparison.Ordinal), Lines(second)[0].firstCluster, "linked shows Charlie first");
            Assert.AreEqual(s.IndexOf("Echo", StringComparison.Ordinal), Lines(third)[0].firstCluster, "the chain continues");

            first.text = "Alpha";
            Render();
            Assert.AreEqual(string.Empty, second.text, "no overflow: the linked component is cleared (TMP)");
        }

        [Test]
        public void OverflowScrollRect_BehavesAsOverflow_WithoutWarning()
        {
            var c = Make("Alpha\nBravo\nCharlie", 600, 20f, Legacy, g => g.overflowMode = TextOverflowModes.ScrollRect);
            Assert.AreEqual(3, Lines(c).Count, "all lines laid out (overflow)");
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
        }
    }
}
