using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// QA M3: UniTextSettings.Changed, UniTextAppearance.Changed, ModRegisterConfig.Changed and the
    /// component's subscriptions were inside <c>#if UNITY_EDITOR</c>, so in a PLAYER a runtime config
    /// change never marked components dirty. These tests drive the change through the RUNTIME API
    /// (NotifyChanged / SetInstance), not OnValidate. Edit mode cannot tell editor from player, so a
    /// source lint also asserts the events/subscriptions are not inside an editor-only block.
    /// </summary>
    public class RuntimeConfigChangedTests
    {
        private readonly List<Object> _junk = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _junk) if (o != null) Object.DestroyImmediate(o);
            _junk.Clear();
            UnifiedRenderBuilder.ResetShared();
            SharedGlyphAtlas.Clear();
            Shaper.ClearAllCaches();
        }

        private UniTextFontStack MakeStack()
        {
            if (!FT.IsInitialized) FT.Initialize();
            if (!FT.IsInitialized) Assert.Ignore("FreeType native unavailable.");
            string noto = MsdfTestUtil.FindNotoSansPath();
            if (noto == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            var font = UniTextFont.CreateFontAsset(File.ReadAllBytes(noto), 48);
            if (font == null) Assert.Ignore("font asset creation failed.");
            _junk.Add(font);
            var stack = ScriptableObject.CreateInstance<UniTextFontStack>(); _junk.Add(stack);
            stack.fonts.Add(font);
            return stack;
        }

        private UniText MakeText(UniTextFontStack stack, UniTextAppearance app)
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas)); _junk.Add(canvasGo);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("UniText", typeof(RectTransform)); _junk.Add(go);
            go.transform.SetParent(canvasGo.transform, false);
            var t = go.AddComponent<UniText>();
            t.FontStack = stack;
            if (app != null) t.Appearance = app;
            t.FontSize = 30f;
            t.Text = "Hello";
            Canvas.ForceUpdateCanvases();
            return t;
        }

        // Counts DirtyFlagsChanged notifications that carry a full rebuild.
        private sealed class DirtyProbe
        {
            public int count;
            public void On(UniText.DirtyFlags f) { if ((f & UniText.DirtyFlags.All) != 0) count++; }
        }

        [Test]
        public void Appearance_NotifyChanged_MarksComponentDirty_AndRebuilds()
        {
            var stack = MakeStack();
            var app = ScriptableObject.CreateInstance<UniTextAppearance>(); _junk.Add(app);
            var t = MakeText(stack, app);
            var probe = new DirtyProbe();
            t.DirtyFlagsChanged += probe.On;
            int applied = 0;
            void OnApplied() => applied++;
            UniText.MeshApplied += OnApplied;
            try
            {
                app.NotifyChanged();            // runtime path, NOT OnValidate
                Assert.Greater(probe.count, 0, "UniTextAppearance.NotifyChanged() must mark subscribed components dirty.");
                Canvas.ForceUpdateCanvases();
                Assert.Greater(applied, 0, "the component must rebuild after the appearance changed.");
            }
            finally { UniText.MeshApplied -= OnApplied; t.DirtyFlagsChanged -= probe.On; }
        }

        [Test]
        public void Settings_SetInstance_MarksComponentDirty()
        {
            var stack = MakeStack();
            var t = MakeText(stack, null);
            var probe = new DirtyProbe();
            t.DirtyFlagsChanged += probe.On;
            var previous = UniTextSettings.Instance;
            var fresh = ScriptableObject.CreateInstance<UniTextSettings>(); _junk.Add(fresh);
            try
            {
                UniTextSettings.SetInstance(fresh);   // runtime path raising UniTextSettings.Changed
                Assert.Greater(probe.count, 0, "UniTextSettings.Changed must mark subscribed components dirty.");
            }
            finally
            {
                t.DirtyFlagsChanged -= probe.On;
                UniTextSettings.SetInstance(previous);
            }
        }

        [Test]
        public void ModRegisterConfig_NotifyChanged_InvokesSubscribers()
        {
            var cfg = ScriptableObject.CreateInstance<ModRegisterConfig>(); _junk.Add(cfg);
            int n = 0;
            void H() => n++;
            cfg.Changed += H;
            cfg.NotifyChanged();
            cfg.Changed -= H;
            cfg.NotifyChanged();
            Assert.AreEqual(1, n, "ModRegisterConfig.NotifyChanged must raise Changed for current subscribers only.");
        }

        [Test]
        public void DisabledComponent_NoLongerListens_NoLeak()
        {
            var stack = MakeStack();
            var app = ScriptableObject.CreateInstance<UniTextAppearance>(); _junk.Add(app);
            var t = MakeText(stack, app);
            var probe = new DirtyProbe();
            t.DirtyFlagsChanged += probe.On;
            t.enabled = false;
            probe.count = 0;
            app.NotifyChanged();
            UniTextSettings.SetInstance(UniTextSettings.Instance);
            Assert.AreEqual(0, probe.count, "a disabled component must have unsubscribed from config events.");
            t.DirtyFlagsChanged -= probe.On;
        }

        // ---- source lint: the events / subscriptions must not sit inside #if UNITY_EDITOR -----------

        private static string PackageRoot()
        {
            var noto = MsdfTestUtil.FindNotoSansPath();
            var dir = new DirectoryInfo(Path.GetDirectoryName(noto ?? "."));
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Runtime"))) dir = dir.Parent;
            return dir?.FullName;
        }

        // Returns "file:line: text" for each line containing a needle that is inside an #if UNITY_EDITOR region.
        private static List<string> EditorOnlyMatches(string file, params string[] needles)
        {
            var hits = new List<string>();
            var stack = new Stack<bool>();
            int lineNo = 0;
            foreach (var raw in File.ReadAllLines(file))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.StartsWith("#if")) stack.Push(line.Contains("UNITY_EDITOR") && !line.Contains("!UNITY_EDITOR"));
                else if (line.StartsWith("#endif")) { if (stack.Count > 0) stack.Pop(); }
                else if (line.StartsWith("#else") || line.StartsWith("#elif"))
                {
                    if (stack.Count > 0) { stack.Pop(); stack.Push(false); }
                }
                else
                {
                    bool inEditor = false;
                    foreach (var e in stack) if (e) { inEditor = true; break; }
                    if (!inEditor || line.StartsWith("//")) continue;
                    foreach (var n in needles)
                        if (line.Contains(n)) { hits.Add(Path.GetFileName(file) + ":" + lineNo + ": " + line); break; }
                }
            }
            return hits;
        }

        [Test]
        public void ConfigChangedEvents_AndSubscriptions_AreNotEditorOnly()
        {
            var root = PackageRoot();
            if (root == null) Assert.Ignore("package root not found.");
            var all = new List<string>();
            all.AddRange(EditorOnlyMatches(Path.Combine(root, "Runtime", "Core", "UniTextSettings.cs"),
                "event Action Changed"));
            all.AddRange(EditorOnlyMatches(Path.Combine(root, "Runtime", "FontCore", "UniTextAppearance.cs"),
                "event Action Changed", "void NotifyChanged"));
            all.AddRange(EditorOnlyMatches(Path.Combine(root, "Runtime", "ModCore", "ModRegisterConfig.cs"),
                "event Action Changed", "void NotifyChanged"));
            all.AddRange(EditorOnlyMatches(Path.Combine(root, "Runtime", "Core", "Component", "UniText.cs"),
                "ListenConfigChanged", "UnlistenConfigChanged",
                "UniTextSettings.Changed", "appearance.Changed", "config.Changed"));
            Assert.IsEmpty(all, "config-change events/subscriptions inside #if UNITY_EDITOR (runtime changes would " +
                                "never refresh text in players):\n" + string.Join("\n", all));
        }
    }
}
