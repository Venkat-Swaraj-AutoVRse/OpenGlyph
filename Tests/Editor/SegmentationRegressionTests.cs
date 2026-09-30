using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// Guards that dictionary segmentation changes NOTHING for scripts that do not use
    /// line-break class SA. The two entry points must produce identical break buffers.
    /// </summary>
    [TestFixture]
    public class SegmentationRegressionTests
    {
        [OneTimeSetUp]
        public void Setup() => SegHelper.EnsureUnicode();

        private static readonly (string name, string text)[] Samples =
        {
            ("Latin",        "The quick brown fox jumps over the lazy dog."),
            ("Latin-hyphen", "well-known state-of-the-art co-operate"),
            ("Latin-num",    "Order 1,234.56 items for $99 by 2025-01-01."),
            ("Arabic",       "\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645 \u0648\u0631\u062D\u0645\u0629 \u0627\u0644\u0644\u0647"),
            ("Hebrew",       "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD \u05D6\u05D4 \u05D8\u05E7\u05E1\u05D8"),
            ("CJK",          "\u4ECA\u65E5\u306F\u3044\u3044\u5929\u6C17\u3067\u3059\u3002\u4E2D\u6587\u6D4B\u8BD5\u3002"),
            ("CJK-mixed",    "Hello \u4E16\u754C world \u3053\u3093\u306B\u3061\u306F"),
            ("Mixed-RTL",    "Price: \u0627\u0644\u0633\u0639\u0631 100 USD"),
            ("Emoji",        "Family \uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67 flags \uD83C\uDDFA\uD83C\uDDF8"),
            ("Empty",        ""),
            ("Newlines",     "line one\nline two\r\nline three"),
        };

        [Test]
        public void NonSaScripts_BreakBufferIsUnchanged()
        {
            var lba = SharedPipelineComponents.LineBreakAlgorithm;

            foreach (var (name, text) in Samples)
            {
                var cps = SegHelper.ToCodepoints(text);
                var baseline = new LineBreakType[cps.Length + 1];
                var withSeg = new LineBreakType[cps.Length + 1];

                lba.GetBreakOpportunities(cps, baseline);
                lba.GetBreakOpportunitiesWithSegmentation(cps, withSeg);

                for (int i = 0; i <= cps.Length; i++)
                    Assert.AreEqual(baseline[i], withSeg[i],
                        $"[{name}] break type differs at index {i}: baseline={baseline[i]} withSeg={withSeg[i]}. " +
                        "Segmentation must not affect non-SA scripts.");
            }
        }
    }
}
