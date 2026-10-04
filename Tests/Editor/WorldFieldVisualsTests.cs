using NUnit.Framework;
using UnityEngine;

namespace LightSide.Tests
{
    /// <summary>
    /// A world-space input field must be readable in a headset: it gets a panel behind the text and light
    /// text colours (on Quest 3S the old dark-grey text with no background was unreadable).
    /// </summary>
    public class WorldFieldVisualsTests
    {
        private GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
        }

        [Test]
        public void WorldField_HasPanelBehindText_AndLightText()
        {
            root = new GameObject("root");
            var field = UniTextInputField.Create(root.transform, world: true, size: new Vector2(400f, 60f));
            var bg = field.GetComponent<UniTextInputWorldBackground>();
            Assert.IsNotNull(bg, "world field gets a background panel");

            var mf = field.transform.Find("Background")?.GetComponent<MeshFilter>();
            Assert.IsNotNull(mf, "panel child with a mesh");
            var mesh = mf.sharedMesh;
            Assert.IsNotNull(mesh);
            Assert.Greater(mesh.vertexCount, 0);
            var b = mesh.bounds;
            var r = ((RectTransform)field.transform).rect;
            Assert.AreEqual(r.width, b.size.x, 0.01f, "panel covers the field width");
            Assert.AreEqual(r.height, b.size.y, 0.01f, "panel covers the field height");
            Assert.Greater(mf.transform.localPosition.z, 0f, "panel sits behind the text");

            Assert.Greater(field.TextComponent.color.grayscale, 0.7f, "text is light on the dark panel");
            Assert.Greater(((UniText)field.Placeholder).color.grayscale, 0.4f, "placeholder is readable");
        }

        [Test]
        public void CanvasField_KeepsImageAndDarkText()
        {
            root = new GameObject("root", typeof(RectTransform), typeof(Canvas));
            var field = UniTextInputField.Create(root.transform, world: false);
            Assert.IsNull(field.GetComponent<UniTextInputWorldBackground>());
            Assert.IsNotNull(field.GetComponent<UnityEngine.UI.Image>());
            Assert.Less(field.TextComponent.color.grayscale, 0.4f);
        }

        [Test]
        public void WorldBackground_FollowsRectSize()
        {
            root = new GameObject("root");
            var field = UniTextInputField.Create(root.transform, world: true, size: new Vector2(400f, 60f));
            ((RectTransform)field.transform).sizeDelta = new Vector2(600f, 80f);
            field.GetComponent<UniTextInputWorldBackground>().Rebuild();
            var b = field.transform.Find("Background").GetComponent<MeshFilter>().sharedMesh.bounds;
            Assert.AreEqual(600f, b.size.x, 0.01f);
            Assert.AreEqual(80f, b.size.y, 0.01f);
        }
        [Test]
        public void WorldBackground_SortsBelowTextAndSelection()
        {
            root = new GameObject("root");
            var field = UniTextInputField.Create(root.transform, world: true, size: new Vector2(400f, 60f));
            field.TextComponent.WorldRenderer.sortingOrder = 7;
            field.GetComponent<UniTextInputWorldBackground>().Rebuild();
            var bg = field.transform.Find("Background").GetComponent<MeshRenderer>();
            var text = field.TextComponent.WorldRenderer;
            var ph = ((UniText)field.Placeholder).WorldRenderer;
            Assert.Less(bg.sortingOrder, text.sortingOrder, "panel never draws over the text (distance sorting flipped it on device)");
            Assert.Less(bg.sortingOrder, text.sortingOrder - 1, "panel also below the selection overlay (text - 1)");
            Assert.LessOrEqual(bg.sortingOrder, ph.sortingOrder - 1, "panel below the placeholder");
            Assert.AreEqual(text.sortingLayerID, bg.sortingLayerID);
        }
    }
}
