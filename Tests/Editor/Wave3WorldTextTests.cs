using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 3, items 1 and 3: UniTextWorld / GlyphMeshPro draw the engine's text through a MeshRenderer
    /// without a Canvas: mesh and bounds, shaping and geometry identical to Canvas UniText, markup,
    /// animations, one shared material for N labels, options, and links clickable in world space
    /// through the EventSystem (PhysicsRaycaster) or a ray.
    /// </summary>
    public class Wave3WorldTextTests : Wave3TestBase
    {
        private const string Mixed = "Hello world, שלום עולם 123";

        // ------------------------------------------------------------------ mesh, bounds

        [TestCase(true)]
        [TestCase(false)]
        public void World_ProducesMesh_WithCorrectBounds_NoCanvas(bool unified)
        {
            var t = MakeWorld("Hello World", unified, width: 600, height: 120, fontSize: 60f);
            Assert.IsNull(t.GetComponentInParent<Canvas>(), "world text must not need a Canvas");
            Assert.IsNull(t.Highlighter, "no Canvas-only highlight graphics on world text");
            var mesh = WorldMeshOf(t);
            Assert.IsNotNull(mesh, "MeshFilter.sharedMesh not assigned");
            var quads = QuadsOf(mesh);
            Assert.AreEqual(10, quads.Count, "one quad per visible glyph (space has none)");
            Assert.AreEqual(t.ResultGlyphs.Length - 1, quads.Count);

            var rt = t.rectTransform.rect;
            var b = mesh.bounds;
            Log($"bounds unified={unified}: {b} rect={rt}");
            Assert.Greater(b.size.x, 200f); Assert.Greater(b.size.y, 30f);
            Assert.GreaterOrEqual(b.min.x, rt.xMin - 1f); Assert.LessOrEqual(b.max.x, rt.xMax + 1f);
            Assert.GreaterOrEqual(b.min.y, rt.yMin - 1f); Assert.LessOrEqual(b.max.y, rt.yMax + 1f);
            // Bounds enclose every vertex exactly (culling uses them).
            var all = mesh.vertices;
            Assert.AreEqual(all.Min(v => v.x), b.min.x, 1e-3f); Assert.AreEqual(all.Max(v => v.x), b.max.x, 1e-3f);
            Assert.AreEqual(all.Min(v => v.y), b.min.y, 1e-3f); Assert.AreEqual(all.Max(v => v.y), b.max.y, 1e-3f);

            // Renderer (world) bounds follow the transform.
            t.transform.position = new Vector3(5, 2, 10);
            t.transform.localScale = Vector3.one * 0.01f;
            var mr = t.GetComponent<MeshRenderer>();
            var wb = mr.bounds;
            Assert.AreEqual(5f + b.center.x * 0.01f, wb.center.x, 1e-3f);
            Assert.AreEqual(2f + b.center.y * 0.01f, wb.center.y, 1e-3f);
            Assert.AreEqual(b.size.x * 0.01f, wb.size.x, 1e-3f);

            // Text change re-uploads; empty text clears.
            t.Text = "Hi";
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(2, QuadsOf(WorldMeshOf(t)).Count);
            t.Text = "";
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(0, WorldMeshOf(t).vertexCount, "empty text clears the mesh");
            t.Text = "Again";
            Canvas.ForceUpdateCanvases();
            t.enabled = false;
            Assert.AreEqual(0, WorldMeshOf(t).vertexCount, "disabled text draws nothing");
            t.enabled = true;
            Canvas.ForceUpdateCanvases();
            Assert.AreEqual(5, QuadsOf(WorldMeshOf(t)).Count);
        }

        // ------------------------------------------------------------------ parity with Canvas UniText

        [TestCase(true)]
        [TestCase(false)]
        public void World_ShapingAndGeometry_IdenticalToCanvasUniText(bool unified)
        {
            void Setup(UniText u)
            {
                u.HorizontalAlignment = HorizontalAlignment.Center;
                u.VerticalAlignment = VerticalAlignment.Middle;
                u.WordWrap = true;
            }
            var reference = MakeCanvasText(Mixed, unified, Setup, 420, 200, 44f);
            var world = MakeWorld(Mixed, unified, Setup, 420, 200, 44f);

            var a = reference.ResultGlyphs.ToArray();
            var b = world.ResultGlyphs.ToArray();
            Assert.Greater(a.Length, 20);
            Assert.AreEqual(a.Length, b.Length, "glyph count");
            for (var i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].glyphId, b[i].glyphId, $"glyph id [{i}]");
                Assert.AreEqual(a[i].cluster, b[i].cluster, $"cluster [{i}]");
                Assert.AreEqual(a[i].fontId, b[i].fontId, $"font [{i}]");
                Assert.AreEqual(a[i].x, b[i].x, 1e-4f, $"x [{i}]");
                Assert.AreEqual(a[i].y, b[i].y, 1e-4f, $"y [{i}]");
            }

            // Line breaking happened (two lines in a 420-wide box) without a Canvas layout pass.
            Assert.Greater(b.Select(g => g.y).Distinct().Count(), 1, "world text wrapped onto several lines");

            var qa = ByX(CanvasQuads(reference));
            var qb = ByX(QuadsOf(WorldMeshOf(world)));
            Assert.AreEqual(qa.Count, qb.Count, "quad count");
            for (var i = 0; i < qa.Count; i++)
            {
                Assert.AreEqual(qa[i].v0.x, qb[i].v0.x, 1e-3f, $"quad {i} x");
                Assert.AreEqual(qa[i].v0.y, qb[i].v0.y, 1e-3f, $"quad {i} y");
                Assert.AreEqual(qa[i].Width, qb[i].Width, 1e-3f, $"quad {i} w");
                Assert.AreEqual(qa[i].Height, qb[i].Height, 1e-3f, $"quad {i} h");
            }
            Log($"parity unified={unified}: {a.Length} glyphs, {qa.Count} quads identical");
        }

        [Test]
        public void World_UnifiedVertexData_MatchesCanvas_Uv0Uv1Colors()
        {
            var reference = MakeCanvasText("Abc xyz", true);
            var world = MakeWorld("Abc xyz", true);
            var cm = reference.CanvasRenderers.First(r => r.GetMesh() != null && r.GetMesh().vertexCount > 0).GetMesh();
            var wm = WorldMeshOf(world);
            var cu0 = new List<Vector4>(); cm.GetUVs(0, cu0);
            var wu0 = new List<Vector4>(); wm.GetUVs(0, wu0);
            var cu1 = new List<Vector4>(); cm.GetUVs(1, cu1);
            var wu1 = new List<Vector4>(); wm.GetUVs(1, wu1);
            Assert.AreEqual(cu0.Count, wu0.Count);
            for (var i = 0; i < cu0.Count; i++)
            {
                Assert.AreEqual(cu0[i].z, wu0[i].z, 1e-5f, "uv0.z gradientScale");
                Assert.AreEqual(cu0[i].w, wu0[i].w, 1e-5f, "uv0.w xScale");
                Assert.AreEqual(cu1[i].x, wu1[i].x, 1e-5f, "uv1.x spreadRatio");
                Assert.AreEqual(cu1[i].z, wu1[i].z, 1e-5f, "uv1.z glyph mode");
            }
            CollectionAssert.AreEqual(cm.colors32, wm.colors32);
        }

        // ------------------------------------------------------------------ markup + animation

        [TestCase(true)]
        [TestCase(false)]
        public void World_Markup_ColorBoldSize(bool unified)
        {
            var t = MakeWorld("<color=#FF0000>red</color> plain <size=80>Big</size>", unified, AddBasicTags, 1000, 200, 40f);
            var q = ByX(QuadsOf(WorldMeshOf(t)));
            Assert.AreEqual(11, q.Count);
            for (var i = 0; i < 3; i++)
                Assert.AreEqual(255, q[i].c0.r, $"glyph {i} red"); 
            for (var i = 0; i < 3; i++) Assert.AreEqual(0, q[i].c0.g, $"glyph {i} red has no green");
            Assert.AreEqual(255, q[3].c0.g, "'plain' keeps the default colour");
            // <size=80> doubles the glyph height of 'B' against 'p'/'l' style capitals in the plain run.
            var bigB = q[8];
            var plainL = q[4]; // 'l' of plain (ascender height)
            Assert.Greater(bigB.Height, plainL.Height * 1.6f, "size tag scales the glyphs");
        }

        [Test]
        public void World_SpanAnimation_MovesWorldMeshVertices()
        {
            var animT = typeof(UniText).Assembly.GetType("LightSide.TextAnimationModifier");
            if (animT == null) Assert.Ignore("wave-2 animations not in this build");
            var clock = typeof(UniText).Assembly.GetType("LightSide.UniTextAnimationClock");
            W3Clock(clock, true, 0f);
            try
            {
                var t = MakeWorld("<wave amp=10 freq=1 phase=0>abc</wave>", true,
                    u => animT.GetMethod("RegisterAll").Invoke(null, new object[] { u }));
                var y0 = QuadsOf(WorldMeshOf(t)).Select(q => q.Min.y).ToArray();
                W3Clock(clock, true, 0.25f);
                Canvas.ForceUpdateCanvases();
                var y1 = QuadsOf(WorldMeshOf(t)).Select(q => q.Min.y).ToArray();
                Assert.AreEqual(3, y1.Length);
                for (var i = 0; i < 3; i++) Assert.AreEqual(y0[i] + 10f, y1[i], 0.05f, $"glyph {i} lifted by amp at a quarter period");
                // Bounds follow the animated vertices (culling).
                Assert.AreEqual(y1.Min(), WorldMeshOf(t).bounds.min.y, 1e-3f);
            }
            finally { W3Clock(clock, false, 0f); }
        }

        private static void W3Clock(System.Type clock, bool on, float time)
        {
            if (clock == null) return;
            clock.GetProperty("UseManualTime")?.SetValue(null, on);
            clock.GetProperty("ManualTime")?.SetValue(null, time);
        }

        [Test]
        public void World_AutoSize_FitsTheRect_WithoutCanvasLayout()
        {
            var t = MakeWorld("Auto sized world label text", true, u =>
            {
                u.AutoSize = true; u.MinFontSize = 5; u.MaxFontSize = 200; u.WordWrap = false;
            }, 400, 100);
            Assert.Less(t.CurrentFontSize, 200f, "shrunk to fit");
            Assert.LessOrEqual(t.ResultGlyphs.ToArray().Max(g => g.right), 400.5f, "fits the width");
            var reference = MakeCanvasText("Auto sized world label text", true, u =>
            {
                u.AutoSize = true; u.MinFontSize = 5; u.MaxFontSize = 200; u.WordWrap = false;
            }, 400, 100);
            Assert.AreEqual(reference.CurrentFontSize, t.CurrentFontSize, 1e-4f, "same fitted size as Canvas UniText");
            Log($"auto size: world {t.CurrentFontSize:0.###} canvas {reference.CurrentFontSize:0.###}");
        }

        // ------------------------------------------------------------------ materials

        [Test]
        public void World_NComponents_ShareOneMaterial_NoPerRendererState()
        {
            const int n = 24;
            var list = new List<UniText>();
            for (var i = 0; i < n; i++)
                list.Add(MakeWorld("Label " + i, true, update: false));
            Canvas.ForceUpdateCanvases();
            var mats = list.Select(t => t.GetComponent<MeshRenderer>().sharedMaterials).ToList();
            Assert.IsTrue(mats.All(m => m.Length == 1), "one draw (one material) per label");
            var distinct = mats.Select(m => m[0]).Distinct().ToList();
            Assert.AreEqual(1, distinct.Count, "N labels -> 1 shared material");
            Assert.AreEqual("UniText/World/Uber", distinct[0].shader.name);
            Assert.IsTrue(list.All(t => !t.GetComponent<MeshRenderer>().HasPropertyBlock()), "no MaterialPropertyBlock (SRP Batcher friendly)");
            Assert.IsNotNull(distinct[0].GetTexture("_MainTexArray"), "atlas array bound on the shared material");
            Assert.IsNotNull(distinct[0].GetTexture("_StyleTex"), "style table bound on the shared material");

            // A different option set gets its own (shared) material; same options share again.
            W3.Set(list[0], "Lighting", "Lit");
            W3.Set(list[1], "Lighting", "Lit");
            W3.Set(list[2], "DoubleSided", true);
            Canvas.ForceUpdateCanvases();
            var lit0 = list[0].GetComponent<MeshRenderer>().sharedMaterial;
            Assert.AreSame(lit0, list[1].GetComponent<MeshRenderer>().sharedMaterial);
            Assert.AreNotSame(lit0, distinct[0]);
            Assert.IsTrue(lit0.IsKeywordEnabled("_UNITEXT_LIT"));
            Assert.AreEqual(2f, list[3].GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Cull"), "default: back faces culled");
            Assert.AreEqual(0f, list[2].GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Cull"), "double-sided: Cull Off");
            Assert.AreEqual(3, list.Select(t => t.GetComponent<MeshRenderer>().sharedMaterial).Distinct().Count());
        }

        [Test]
        public void World_OptionChange_Applies_WithGeometryUploadSkipOn()
        {
            var saved = UniTextSettings.SkipUnchangedGeometryUpload;
            UniTextSettings.SetSkipUnchangedGeometryUploadForTests(true);
            try
            {
                var t = MakeWorld("Same glyphs", true);
                var unlit = t.GetComponent<MeshRenderer>().sharedMaterial;
                W3.Set(t, "Lighting", "Lit");
                Canvas.ForceUpdateCanvases();
                var lit = t.GetComponent<MeshRenderer>().sharedMaterial;
                Assert.AreNotSame(unlit, lit, "an option change re-binds the material although the geometry is identical");
                Assert.IsTrue(lit.IsKeywordEnabled("_UNITEXT_LIT"));
            }
            finally { UniTextSettings.SetSkipUnchangedGeometryUploadForTests(saved); }
        }

        [Test]
        public void World_DepthWrite_Option_SetsMaterialState()
        {
            var t = MakeWorld("Depth", true, u => W3.Set(u, "DepthWrite", true));
            var m = t.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.AreEqual(1f, m.GetFloat("_ZWrite"));
            Assert.IsTrue(m.IsKeywordEnabled("_UNITEXT_ALPHACLIP"), "depth write clips low coverage");
            Assert.AreEqual(2450, m.renderQueue, "AlphaTest queue");
            var plain = MakeWorld("Plain", true).GetComponent<MeshRenderer>().sharedMaterial;
            Assert.AreEqual(0f, plain.GetFloat("_ZWrite"));
            Assert.AreEqual(3000, plain.renderQueue);
        }

        [Test]
        public void World_Legacy_SharesFontMaterials()
        {
            var a = MakeWorld("Legacy one", false);
            var b = MakeWorld("Legacy two", false);
            var ma = a.GetComponent<MeshRenderer>().sharedMaterials;
            var mb = b.GetComponent<MeshRenderer>().sharedMaterials;
            Assert.Greater(ma.Length, 0);
            CollectionAssert.AreEqual(ma, mb, "legacy world text uses the shared font materials");
            Assert.IsTrue(WorldMeshOf(a).HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal),
                "legacy copy carries the normal the Canvas shaders read");
        }

        [Test]
        public void World_SortingAndRendererDefaults()
        {
            var t = MakeWorld("Sort", true);
            W3.Set(t, "SortingOrder", 7);
            Assert.AreEqual(7, t.GetComponent<MeshRenderer>().sortingOrder);
            Assert.AreEqual(7, (int)W3.Get(t, "SortingOrder"));
            var g = MakeWorld("Gmp", true, typeName: "OpenGlyph.GlyphMeshPro");
            W3.Set(g, "sortingOrder", 3);
            Assert.AreEqual(3, g.GetComponent<MeshRenderer>().sortingOrder);
        }

        [Test]
        public void World_OverflowClip_SetsObjectSpaceClipRect()
        {
            var t = MakeWorld("A long line of text that overflows the rect", true, u =>
            {
                u.WordWrap = false;
                u.Overflow = TextOverflow.Clip;
            }, 200, 60);
            var mr = t.GetComponent<MeshRenderer>();
            Assert.IsTrue(mr.HasPropertyBlock(), "clip rect is per renderer");
            var block = new MaterialPropertyBlock();
            mr.GetPropertyBlock(block);
            var clip = block.GetVector("_ClipRect");
            var r = t.rectTransform.rect;
            Assert.AreEqual(new Vector4(r.xMin, r.yMin, r.xMax, r.yMax), clip);
            t.Overflow = TextOverflow.Overflow;
            Canvas.ForceUpdateCanvases();
            Assert.IsFalse(mr.HasPropertyBlock(), "no block when not clipping");
        }

        // ------------------------------------------------------------------ GlyphMeshPro (TMP TextMeshPro)

        [Test]
        public void GlyphMeshPro_World_MatchesGlyphMeshProUGUI()
        {
            var gmpType = W3.Type("OpenGlyph.GlyphMeshPro");
            var uguiType = W3.Type("OpenGlyph.GlyphMeshProUGUI");
            Assert.IsTrue(uguiType.IsAssignableFrom(gmpType), "GlyphMeshPro is the TMP adapter (same API as GlyphMeshProUGUI)");

            var reference = MakeCanvasText("", true, componentType: uguiType, update: false);
            var world = MakeWorld("", true, typeName: "OpenGlyph.GlyphMeshPro", update: false);
            foreach (var c in new[] { reference, world })
            {
                W3.Set(c, "text", "TMP <b>bold</b> text");
                W3.Set(c, "fontSize", 40f);
                W3.Set(c, "alignment", "Center");
                W3.Set(c, "characterSpacing", 5f);
            }
            Canvas.ForceUpdateCanvases();
            var qa = ByX(CanvasQuads(reference));
            var qb = ByX(QuadsOf(WorldMeshOf(world)));
            Assert.Greater(qb.Count, 10, "GlyphMeshPro draws through its MeshRenderer");
            Assert.AreEqual(qa.Count, qb.Count);
            for (var i = 0; i < qa.Count; i++)
                Assert.AreEqual(qa[i].Center.x, qb[i].Center.x, 1e-3f, $"glyph {i}");
            var size = (Vector2)W3.Call(world, "GetPreferredValues");
            Assert.Greater(size.x, 100f, "TMP layout queries work in world space");
            Assert.AreSame(WorldMeshOf(world), W3.Get(world, "mesh"));
        }

        // ------------------------------------------------------------------ interaction

        private (UniText t, LinkModifier link, List<string> clicked) MakeLinkText(bool unified)
        {
            var clicked = new List<string>();
            LinkModifier link = null;
            var t = MakeWorld("Go <link=alpha>first</link> or <link=beta>second</link>", unified,
                u => link = AddLinks(u), 900, 120, 50f);
            t.RangeClicked += h => clicked.Add(h.range.data);
            // An angled, scaled, offset label, as in a scene.
            t.transform.position = new Vector3(1f, 1.5f, 3f);
            t.transform.rotation = Quaternion.Euler(10f, 35f, 5f);
            t.transform.localScale = Vector3.one * 0.005f;
            return (t, link, clicked);
        }

        /// <summary>The glyph in the middle of <paramref name="word"/> (by cluster) and the word's cluster range.</summary>
        private static (PositionedGlyph g, int start, int end) GlyphIn(UniText t, string word)
        {
            var cps = t.Buffers.codepoints;
            var s = new string(Enumerable.Range(0, cps.count).Select(i => (char)cps.data[i]).ToArray());
            var idx = s.IndexOf(word, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(idx, 0, $"'{word}' in clean text '{s}'");
            var inWord = t.ResultGlyphs.ToArray().Where(x => x.cluster >= idx && x.cluster < idx + word.Length).ToArray();
            Assert.IsNotEmpty(inWord, $"glyphs of '{word}' (clusters {string.Join(",", t.ResultGlyphs.ToArray().Select(x => x.cluster))})");
            return (inWord[inWord.Length / 2], idx, idx + word.Length);
        }

        /// <summary>World-space centre of a glyph box (layout rect = RectTransform rect, no padding).</summary>
        private static Vector3 WorldPointOf(UniText t, PositionedGlyph g)
        {
            var rect = t.rectTransform.rect;
            var local = new Vector3(rect.xMin + (g.left + g.right) * 0.5f, rect.yMax - (g.top + g.bottom) * 0.5f, 0f);
            return t.transform.TransformPoint(local);
        }
        [TestCase(true)]
        [TestCase(false)]
        public void World_HitTestWorldAndRay_FindTheLink(bool unified)
        {
            var (t, _, clicked) = MakeLinkText(unified);
            var (g, start, end) = GlyphIn(t, "second");
            var c = g.cluster;
            var p = WorldPointOf(t, g);

            var hit = W3.Hit(W3.Call(t, "HitTestWorld", p, 20f));
            Assert.IsTrue(hit.hit);
            Assert.AreEqual(c, hit.cluster, "world point -> the glyph under it");

            // A controller ray from an arbitrary origin through the point.
            var origin = p + new Vector3(-0.4f, 0.3f, -1.2f);
            var ray = new Ray(origin, (p - origin).normalized);
            hit = W3.Hit(W3.Call(t, "HitTestRay", ray, 20f));
            Assert.AreEqual(c, hit.cluster, "ray -> same glyph");
            var away = W3.Hit(W3.Call(t, "HitTestRay", new Ray(origin, -(p - origin).normalized), 20f));
            Assert.IsFalse(away.hit, "a ray pointing away hits nothing");

            W3.Call(t, "ClickAtWorldPoint", WorldPointOf(t, GlyphIn(t, "first").g));
            CollectionAssert.AreEqual(new[] { "alpha" }, clicked);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void World_EventSystem_PhysicsRaycaster_ClicksTheLink(bool unified)
        {
            var (t, _, clicked) = MakeLinkText(unified);

            var box = t.GetComponent<BoxCollider>();
            Assert.IsNotNull(box, "interactive world text keeps a BoxCollider for PhysicsRaycaster");
            Assert.IsTrue(box.enabled);
            var r = t.rectTransform.rect;
            Assert.AreEqual(r.width, box.size.x, 1e-3f); Assert.AreEqual(r.height, box.size.y, 1e-3f);
            Assert.AreEqual(r.center.x, box.center.x, 1e-3f); Assert.AreEqual(r.center.y, box.center.y, 1e-3f);
            var esGo = new GameObject("ES", typeof(EventSystem)); _junk.Add(esGo);
            var camGo = new GameObject("Cam", typeof(Camera), typeof(PhysicsRaycaster)); _junk.Add(camGo);
            var cam = camGo.GetComponent<Camera>();
            var rt = new RenderTexture(800, 600, 24); _junk.Add(rt);
            cam.targetTexture = rt;
            var target = WorldPointOf(t, GlyphIn(t, "second").g);
            // Look at the label from an angle.
            cam.transform.position = target + t.transform.rotation * new Vector3(0.6f, 0.2f, -2f);
            cam.transform.LookAt(target);
            Physics.SyncTransforms();

            var ed = new PointerEventData(esGo.GetComponent<EventSystem>()) { position = cam.WorldToScreenPoint(target) };
            var results = new List<RaycastResult>();
            camGo.GetComponent<PhysicsRaycaster>().Raycast(ed, results);
            Assert.IsTrue(results.Any(x => x.gameObject == t.gameObject), "PhysicsRaycaster hits the text collider");
            var rr = results.First(x => x.gameObject == t.gameObject);
            ed.pointerCurrentRaycast = rr;
            ed.pointerPressRaycast = rr;
            ExecuteEvents.Execute(t.gameObject, ed, ExecuteEvents.pointerClickHandler);
            CollectionAssert.AreEqual(new[] { "beta" }, clicked, "the click raised the link under the pointer");

            // Hover enter/exit through the same path.
            var entered = new List<string>();
            t.RangeEntered += h => entered.Add(h.range.data);
            ExecuteEvents.Execute(t.gameObject, ed, ExecuteEvents.pointerMoveHandler);
            CollectionAssert.AreEqual(new[] { "beta" }, entered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void World_RayHitWithoutRaycasterModule_ClicksTheLink(bool unified)
        {
            // A custom XR ray (no BaseRaycaster) reports the world hit with module == null, so
            // RaycastResult.isValid is false. Found on Quest: links ignored such clicks.
            var (t, _, clicked) = MakeLinkText(unified);
            var esGo = new GameObject("ES", typeof(EventSystem)); _junk.Add(esGo);
            var target = WorldPointOf(t, GlyphIn(t, "second").g);
            var rr = new RaycastResult { gameObject = t.gameObject, worldPosition = target, worldNormal = -t.transform.forward, module = null };
            Assert.IsFalse(rr.isValid);
            var ed = new PointerEventData(esGo.GetComponent<EventSystem>()) { position = new Vector2(-1000f, -1000f) };
            ed.pointerCurrentRaycast = rr;
            ed.pointerPressRaycast = rr;
            ExecuteEvents.Execute(t.gameObject, ed, ExecuteEvents.pointerClickHandler);
            CollectionAssert.AreEqual(new[] { "beta" }, clicked, "the world hit point decides the link, not the screen position");
        }

        [Test]
        public void World_Collider_OnlyWhenInteractive_ByDefault()
        {
            var plain = MakeWorld("No links here", true);
            var box = plain.GetComponent<BoxCollider>();
            Assert.IsTrue(box == null || !box.enabled, "plain text keeps no collider by default");
            plain.TextClicked += _ => { };
            W3.Call(plain, "UpdateWorldCollider");
            box = plain.GetComponent<BoxCollider>();
            Assert.IsNotNull(box); Assert.IsTrue(box.enabled, "a click subscriber makes it interactive");
            Assert.IsTrue((box.hideFlags & HideFlags.DontSave) != 0, "the collider is not saved into the scene");

            var always = MakeWorld("Always", true, u => W3.Set(u, "Collider", "Always"));
            Assert.IsTrue(always.GetComponent<BoxCollider>().enabled);
            var none = MakeWorld("None <link=x>link</link>", true, u => { AddLinks(u); W3.Set(u, "Collider", "None"); });
            var nb = none.GetComponent<BoxCollider>();
            Assert.IsTrue(nb == null || !nb.enabled);
        }

        // ------------------------------------------------------------------ shader

        [Test]
        public void WorldShader_Compiles_HasPipelinesAndOptions()
        {
            var sh = Shader.Find("UniText/World/Uber");
            Assert.IsNotNull(sh, "UniText/World/Uber missing");
            Assert.IsTrue(sh.isSupported, "world shader supported on this device");
#if UNITY_EDITOR
            Assert.IsFalse(UnityEditor.ShaderUtil.ShaderHasError(sh), "world shader has compile errors");
            var msgs = UnityEditor.ShaderUtil.GetShaderMessages(sh);
            Assert.IsFalse(msgs.Any(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error),
                string.Join("\n", msgs.Select(m => m.message)));
#endif
            Assert.AreEqual(2, sh.subshaderCount, "URP + Built-in subshaders");
            foreach (var p in new[] { "_MainTexArray", "_StyleTex", "_Cull", "_ZWrite", "_Lit", "_LightWrap", "_AlphaClip", "_ClipRect" })
                Assert.GreaterOrEqual(sh.FindPropertyIndex(p), 0, $"property {p}");
            var kw = sh.keywordSpace.keywordNames;
            CollectionAssert.IsSubsetOf(new[] { "_UNITEXT_LIT", "_UNITEXT_ALPHACLIP" }, kw);
            var tags = sh.FindSubshaderTagValue(0, new UnityEngine.Rendering.ShaderTagId("RenderPipeline"));
            Assert.AreEqual("UniversalPipeline", tags.name);
        }

        [Test]
        public void WorldShader_DeclaresStereoMacros_AndIsPinnedForBuilds()
        {
            var shaderPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(UnityEditor.AssetDatabase.GetAssetPath(
                Shader.Find("UniText/Uber"))) ?? "", "UniText_UberWorld.shader");
            Assert.IsTrue(System.IO.File.Exists(shaderPath), "Shaders/UniText_UberWorld.shader missing");
            var dir = System.IO.Path.GetDirectoryName(shaderPath);
            var src = System.IO.File.ReadAllText(shaderPath) +
                      System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "UniText_UberWorld.cginc")) +
                      System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "UniText_UberCore.cginc"));
            foreach (var m in new[] { "UNITY_VERTEX_INPUT_INSTANCE_ID", "UNITY_VERTEX_OUTPUT_STEREO", "UNITY_SETUP_INSTANCE_ID",
                         "UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO", "multi_compile_instancing" })
                StringAssert.Contains(m, src, $"{m} (single-pass instanced / multiview on Quest)");

            var type = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("LightSide.UniTextBuildProcessor")).First(x => x != null);
            var list = ((string name, string consequence)[])type.GetField("RuntimeFoundShaders",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            Assert.IsTrue(list.Any(s => s.name == "UniText/World/Uber"), "world shader pinned into Always-Included Shaders");
        }
    }
}
