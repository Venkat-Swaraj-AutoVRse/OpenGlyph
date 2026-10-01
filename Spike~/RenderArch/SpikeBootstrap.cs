// THROWAWAY SPIKE — runtime bootstrap. NOT production.
// Builds the Texture2DArray + uber-shader + StructuredBuffer mesh at runtime so
// the shader is included in the player build and compiled for the target
// graphics API (GLES3/Vulkan on Android).
using UnityEngine;
using UnityEngine.UI;

public class SpikeBootstrap : MonoBehaviour
{
    void Start()
    {
        var shader = Shader.Find("OpenGlyphSpike/Uber");
        if (shader == null) { Debug.LogError("[SPIKE] shader missing in build"); return; }

        const int A = 64, S = 5;
        var arr = new Texture2DArray(A, A, S, TextureFormat.RGBA32, false);
        for (int s = 0; s < S; s++)
        {
            var px = new Color32[A*A];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(128,128,128,200);
            arr.SetPixels32(px, s);
        }
        arr.Apply();

        var buf = new ComputeBuffer(1, sizeof(float)*16);
        buf.SetData(new float[16]{1,1,1,1, 0,0,0,1, 0,0,0,0, 0.25f,0,0,0});

        var canvasGo = new GameObject("Canvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGo.AddComponent<CanvasScaler>();

        var mat = new Material(shader);
        mat.SetTexture("_MainTexArray", arr);
        mat.SetBuffer("_Styles", buf);

        var child = new GameObject("run");
        child.transform.SetParent(canvasGo.transform, false);
        child.AddComponent<RectTransform>();
        var cr = child.AddComponent<CanvasRenderer>();

        var m = new Mesh();
        m.vertices = new[]{ new Vector3(0,0), new Vector3(0,50), new Vector3(50,50), new Vector3(50,0) };
        m.colors = new[]{ Color.white, Color.white, Color.white, Color.white };
        m.SetUVs(0, new System.Collections.Generic.List<Vector4>{
            new Vector4(0,0,20,1), new Vector4(0,1,20,1), new Vector4(1,1,20,1), new Vector4(1,0,20,1) });
        m.SetUVs(1, new System.Collections.Generic.List<Vector4>{
            new Vector4(0.1f,3,1,0), new Vector4(0.1f,3,1,0), new Vector4(0.1f,3,1,0), new Vector4(0.1f,3,1,0) });
        m.triangles = new[]{0,1,2,2,3,0};
        cr.SetMesh(m); cr.materialCount = 1; cr.SetMaterial(mat, 0);

        Debug.Log("[SPIKE] bootstrap built array+shader mesh, gfx=" + SystemInfo.graphicsDeviceType);
    }
}
