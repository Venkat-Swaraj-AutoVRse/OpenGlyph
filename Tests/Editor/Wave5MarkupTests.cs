using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Wave 5: <c>UniText.RegisterDefaultMarkup()</c> and the opt-in project option
    /// <c>UniTextSettings.DefaultMarkupOnNewComponents</c> (default off: tags stay literal on a plain component).
    /// </summary>
    public class Wave5MarkupTests : Wave5TestBase
    {
        private const string Marked =
            "Valve <b>A</b> · <color=#38BDF8>open</color> <i>i</i> <u>u</u> <s>s</s> <size=20>z</size> " +
            "<link=x>l</link> <outline=#FFFFFF,0.15>o</outline> <gradient=ice>g</gradient> <sup>2</sup><sub>3</sub> " +
            "<cspace=2>c</cspace> <line-height=1.2>h</line-height> <mark=#FF000055>m</mark> <noparse><b></noparse>";
        private const string Clean = "Valve A · open i u s z l o g 23 c h m <b>";

        private UniText MakeText(bool world)
        {
            var go = new GameObject(world ? "world label" : "label", typeof(RectTransform));
            _junk.Add(go);
            if (!world) go.transform.SetParent(_canvasGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(1600, 80);
            var t = world ? (UniText)go.AddComponent<UniTextWorld>() : go.AddComponent<UniText>();
            t.FontStack = _stack;
            t.FontSize = 24;
            return t;
        }

        private static void SetOption(bool on) =>
            W4.Call(typeof(UniTextSettings), "SetDefaultMarkupOnNewComponentsForTests", on);

        [TestCase(false)]
        [TestCase(true)]
        public void RegisterDefaultMarkup_ParsesTheCommonTags_OnAFreshComponent(bool world)
        {
            var t = MakeText(world);
            t.Text = Marked;
            Update();
            Assert.AreEqual(Marked, t.CleanText, "without rules a plain component shows tags literally (by design)");

            var ret = W4.Call(t, "RegisterDefaultMarkup");
            Assert.AreSame(t, ret, "returns the component (chaining)");
            Update();
            Assert.AreEqual(Clean, t.CleanText, $"{(world ? "UniTextWorld" : "UniText")}: tags parsed");
            var count = t.ModRegisters.Count;
            Assert.GreaterOrEqual(count, 30, "the default tag set");
            W4.Call(t, "RegisterDefaultMarkup");
            Assert.AreEqual(count, t.ModRegisters.Count, "calling again registers nothing twice");

            var tags = (System.Collections.Generic.IReadOnlyList<string>)W4.Get(W4.Type("LightSide.UniTextDefaultMarkup"), "Tags");
            CollectionAssert.IsSubsetOf(new[] { "b", "i", "u", "s", "color", "size", "link", "outline", "gradient", "sup", "sub", "cspace", "line-height", "align" }, TagsToArray(tags));
        }

        private static string[] TagsToArray(System.Collections.Generic.IReadOnlyList<string> l)
        {
            var a = new string[l.Count];
            for (var i = 0; i < a.Length; i++) a[i] = l[i];
            return a;
        }

        [Test]
        public void RegisterDefaultMarkup_KeepsRulesTheComponentAlreadyHas()
        {
            var t = MakeText(false);
            var own = new ModRegister { Modifier = new ColorModifier(), Rule = new ColorParseRule() };
            t.RegisterModifier(own);
            W4.Call(t, "RegisterDefaultMarkup");
            var colorRules = 0;
            var ownKept = false;
            foreach (var r in t.ModRegisters) { if (r.Rule is ColorParseRule) colorRules++; if (r == own) ownKept = true; }
            Assert.AreEqual(1, colorRules, "an existing <color> rule is kept, not duplicated");
            Assert.IsTrue(ownKept);
            t.Text = "<color=red>x</color> <b>y</b>";
            Update();
            Assert.AreEqual("x y", t.CleanText);
        }

        [Test]
        public void ProjectOption_DefaultOff_Unchanged_On_NewComponentsParseTags()
        {
            Assert.IsFalse((bool)W4.Get(typeof(UniTextSettings), "DefaultMarkupOnNewComponents"), "the option is off by default");
            var plain = MakeText(false);
            plain.Text = "Valve <b>A</b>";
            Update();
            Assert.AreEqual("Valve <b>A</b>", plain.CleanText, "option off: 1.0 behaviour, literal tags");

            SetOption(true);
            try
            {
                foreach (var world in new[] { false, true })
                {
                    var t = MakeText(world);
                    t.Text = "Valve <b>A</b> · <color=#38BDF8>open</color>";
                    Update();
                    Assert.AreEqual("Valve A · open", t.CleanText, $"option on: a new {(world ? "UniTextWorld" : "UniText")} parses tags");
                    Assert.IsTrue((bool)W4.Get(t, "UsesProjectDefaultMarkup"));
                    Assert.AreEqual(0, t.ModRegisters.Count, "nothing is written into the serialized Mod Registers list");
                }

                // A component with rules of its own keeps exactly those.
                var own = MakeText(false);
                own.RegisterModifier(new ModRegister { Modifier = new BoldModifier(), Rule = new BoldParseRule() });
                own.Text = "<b>A</b> <color=red>r</color>";
                Update();
                Assert.AreEqual("A <color=red>r</color>", own.CleanText, "own rules only");

                // GlyphMeshPro registers its own TMP set; an input field still shows markup literally.
                var f = MakeField("<b>x</b>", activate: false);
                Update();
                Assert.AreEqual("<b>x</b>", f.TextComponent.CleanText, "input fields show tags as typed");
            }
            finally { SetOption(false); }

            var after = MakeText(false);
            after.Text = "<b>A</b>";
            Update();
            Assert.AreEqual("<b>A</b>", after.CleanText, "option off again: literal");
        }
    }
}
