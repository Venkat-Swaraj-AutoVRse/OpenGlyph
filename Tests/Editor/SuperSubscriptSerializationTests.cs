using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression (QA B7): <see cref="SuperSubscriptModifier"/> kept its Superscript/Subscript kind in a
    /// readonly ctor-set field that Unity did not serialize. After a scene save/reload (or Instantiate)
    /// the &lt;sub&gt; modifier came back as Superscript, shared the "superscript" flags buffer with the
    /// real one, and every sup/sub glyph was scaled twice (0.25x) and raised twice — rendered as specks.
    /// The field is read by reflection so this test compiles against the pre-fix code and fails there.
    /// </summary>
    public class SuperSubscriptSerializationTests
    {
        private static object KindOf(SuperSubscriptModifier m) =>
            typeof(SuperSubscriptModifier).GetField("kind", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m);

        [Test]
        public void SubscriptKind_SurvivesSerializationRoundTrip()
        {
            var original = new SuperSubscriptModifier(SuperSubscriptModifier.Kind.Subscript);
            var json = JsonUtility.ToJson(original);

            var restored = new SuperSubscriptModifier(); // the default ctor is what Unity's deserializer runs
            JsonUtility.FromJsonOverwrite(json, restored);

            Assert.AreEqual(SuperSubscriptModifier.Kind.Subscript, KindOf(restored),
                $"Subscript kind was lost on round trip (json: {json}).");
        }
    }
}
