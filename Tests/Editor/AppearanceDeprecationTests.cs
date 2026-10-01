using System;
using NUnit.Framework;

#pragma warning disable 618 // this test inspects the [Obsolete] type by design

namespace LightSide.Tests
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 4: deprecation. Verifies <see cref="UniTextAppearance"/>
    /// carries <c>[Obsolete(..., false)]</c> — a WARNING, not an error, so existing assets still compile,
    /// load, and render through the shim during the deprecation window — and that the message points at
    /// the migration tool. (That old assets render UNCHANGED with the flag off is covered by the full
    /// suite staying green flag-off and the equivalence tests.)
    /// </summary>
    public class AppearanceDeprecationTests
    {
        [Test]
        public void UniTextAppearance_IsObsolete_AsWarning_NotError()
        {
            var attrs = typeof(UniTextAppearance).GetCustomAttributes(typeof(ObsoleteAttribute), false);
            Assert.AreEqual(1, attrs.Length, "UniTextAppearance must be marked [Obsolete]");
            var o = (ObsoleteAttribute)attrs[0];
            Assert.IsFalse(o.IsError, "deprecation must be a WARNING (error=false), so old assets still compile/render");
            StringAssert.Contains("Migrate Appearance to Styles", o.Message,
                "the obsolete message must point users at the migration tool");
        }

        [Test]
        public void DeprecationMessage_IsPublicConstant()
        {
            Assert.IsNotEmpty(UniTextAppearance.DeprecationMessage);
            StringAssert.Contains("deprecated", UniTextAppearance.DeprecationMessage);
        }
    }
}
#pragma warning restore 618
