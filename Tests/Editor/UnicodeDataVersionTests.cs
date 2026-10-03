using NUnit.Framework;

namespace LightSide.Tests
{
    /// <summary>
    /// The shipped <c>Resources/UnicodeData.bytes</c> header encodes its Unicode version as
    /// <c>(major &lt;&lt; 16) | (minor &lt;&lt; 8) | update</c>. The conformance suites run against the
    /// 17.0.0 UCD test files, so the data they validate must itself be 17.0.0.
    /// </summary>
    [TestFixture]
    public class UnicodeDataVersionTests
    {
        [Test]
        public void ShippedUnicodeData_ReportsVersion_17_0_0()
        {
            UnicodeData.EnsureInitialized();
            if (!UnicodeData.IsInitialized)
                Assert.Ignore("UnicodeData could not be initialized (Resources/UnicodeData.bytes missing).");

            int raw = UnicodeData.Provider.UnicodeVersionRaw;
            int major = (raw >> 16) & 0xFF, minor = (raw >> 8) & 0xFF, update = raw & 0xFF;
            Assert.AreEqual("17.0.0", $"{major}.{minor}.{update}", $"UnicodeVersionRaw=0x{raw:X8}");
        }
    }
}
