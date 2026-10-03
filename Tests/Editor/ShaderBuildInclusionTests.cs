using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// Regression (QA B22, found on Quest): the build processor pins the shaders the runtime loads via
    /// Shader.Find into Always-Included Shaders. A case-insensitive rename turned "UniText/Uber" into
    /// "UniText/shader", so the lookup failed, the processor only logged a warning, and every player
    /// shipped without the Uber shader: the unified renderer and GlyphMeshPro fell back to UI/Default and
    /// drew solid white quads on device while the editor (where Shader.Find always works) looked fine.
    /// </summary>
    public class ShaderBuildInclusionTests
    {
        private static (string name, string consequence)[] RuntimeFoundShaders()
        {
            var type = System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("LightSide.UniTextBuildProcessor"))
                .FirstOrDefault(t => t != null);
            Assert.IsNotNull(type, "UniTextBuildProcessor not found");
            var field = type.GetField("RuntimeFoundShaders", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "UniTextBuildProcessor.RuntimeFoundShaders not found");
            return ((string, string)[])field.GetValue(null);
        }

        [Test]
        public void EveryRuntimeFoundShader_ResolvesToARealShader()
        {
            var missing = RuntimeFoundShaders().Where(s => Shader.Find(s.name) == null).Select(s => s.name).ToArray();
            Assert.IsEmpty(missing, "build processor would fail to include: " + string.Join(", ", missing));
        }

        [Test]
        public void UberShader_IsPinnedForBuilds()
        {
            Assert.IsTrue(RuntimeFoundShaders().Any(s => s.name == "UniText/Uber"),
                "UniText/Uber (unified renderer, GlyphMeshPro) must be in the build processor's list.");
        }
    }
}
