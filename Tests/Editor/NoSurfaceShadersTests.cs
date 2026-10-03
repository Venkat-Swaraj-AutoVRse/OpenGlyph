using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>B5: the Surface shaders render pink under URP and were removed; none may reappear.</summary>
    public class NoSurfaceShadersTests
    {
        [TestCase("UniText/SDF (Surface)")]
        [TestCase("UniText/Mobile/SDF (Surface)")]
        [TestCase("UniText/MSDF (Surface)")]
        [TestCase("UniText/Mobile/MSDF (Surface)")]
        public void SurfaceShader_IsNotShipped(string name)
        {
            Assert.IsNull(Shader.Find(name), $"'{name}' must not exist (URP does not support surface shaders).");
        }

        [Test]
        public void ShadersFolder_HasNoSurfaceShaderFiles()
        {
            var noto = MsdfTestUtil.FindNotoSansPath();
            var dir = new DirectoryInfo(Path.GetDirectoryName(noto ?? "."));
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Shaders"))) dir = dir.Parent;
            if (dir == null) Assert.Ignore("package Shaders folder not found.");
            var shaders = Path.Combine(dir.FullName, "Shaders");
            Assert.IsEmpty(Directory.GetFiles(shaders, "*Surface*"), "no *Surface* files may ship in Shaders/.");
        }
    }
}
