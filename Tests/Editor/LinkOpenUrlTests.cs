using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Auto-open only real URLs: on Quest, &lt;link=manual&gt; went to Application.OpenURL("manual"),
    /// which Android turned into file:///manual and threw.
    /// </summary>
    public class LinkOpenUrlTests
    {
        [TestCase("https://example.com", true)]
        [TestCase("http://x", true)]
        [TestCase("mailto:a@b.c", true)]
        [TestCase("tel:+15551234", true)]
        [TestCase("myapp+v2://open", true)]
        [TestCase("manual", false)]
        [TestCase("safety-sheet", false)]
        [TestCase("step:3", true)]
        [TestCase(@"C:\docs.pdf", false)]
        [TestCase(":nope", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsOpenableUrl(string data, bool expected)
        {
            Assert.AreEqual(expected, LinkModifier.IsOpenableUrl(data));
        }
    }
}
