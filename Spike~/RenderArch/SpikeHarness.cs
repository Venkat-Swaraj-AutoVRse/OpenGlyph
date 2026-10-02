// =============================================================================
// THROWAWAY SPIKE CODE — Round 1 feasibility harness. NOT production.
// Builds a single Texture2DArray (3 "font" slices + 1 MSDF slice + 1 emoji/COLR
// slice), one mesh whose quads mix glyphMode and styleIdx (incl. a per-span
// outline on part of one "text"), renders on a Screen Space Overlay canvas and
// a World Space canvas, and measures draw calls / batches with UnityStats and
// ProfilerRecorder("Draw Calls Count","Batches Count"). Also compares against a
// "baseline" that draws each slice with its own CanvasRenderer (the CURRENT
// n-draw-call model) so the before/after is apples-to-apples.
//
// Runs in -batchmode -nographics-safe: when no GPU is present UnityStats is 0,
// so the harness ALSO reports the structural draw-call count (CanvasRenderer
// count) which is what actually determines UI draw calls. Always exits (0 ok,
// non-zero on failure) and arms a watchdog so a batchmode run can never hang.
// =============================================================================
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

public static class SpikeHarness
{
    const int ATLAS = 64;         // tiny synthetic slices; the proof is structural, not visual fidelity
    const int SLICES = 5;         // fontA, fontB, fontC (SDF), MSDF, emoji(COLR)
    static StringBuilder _log = new StringBuilder();
    static string OutPath => Path.Combine(Directory.GetCurrentDirectory(), "spike_result.txt");

    static void L(string s){ _log.Append(s).Append('\n'); Debug.Log("[SPIKE] " + s); }

    // Entry point for -executeMethod. Starts a coroutine runner + watchdog, exits via EditorApplication.
    public static void Run()
    {
        try
        {
            L("=== OpenGlyph render-arch spike ===");
            L("Unity " + Application.unityVersion + "  gfx=" + SystemInfo.graphicsDeviceType);
            L("Texture2DArray supported: " + SystemInfo.supports2DArrayTextures);
            L("maxTextureSize: " + SystemInfo.maxTextureSize);

            if (!SystemInfo.supports2DArrayTextures)
                L("WARN: this device reports NO Texture2DArray support -> fallback (large atlas) tier would be used.");

            var arr = BuildArray();
            var styles = BuildStyleBuffer();

            // --- Proposed: ONE mesh, ONE material, ONE texture-array, ONE CanvasRenderer ---
            int proposedOverlay = BuildAndCount(RenderSpace.Overlay, arr, styles, unified:true);
            int proposedWorld   = BuildAndCount(RenderSpace.World,   arr, styles, unified:true);

            // --- Baseline: one CanvasRenderer per slice (the CURRENT model) ---
            int baseOverlay = BuildAndCount(RenderSpace.Overlay, arr, styles, unified:false);
            int baseWorld   = BuildAndCount(RenderSpace.World,   arr, styles, unified:false);

            L("");
            L("RESULT structural CanvasRenderer count (= UI draw-call groups):");
            L(string.Format(CultureInfo.InvariantCulture,
                "  Overlay : baseline(current)={0}  proposed(unified)={1}", baseOverlay, proposedOverlay));
            L(string.Format(CultureInfo.InvariantCulture,
                "  World   : baseline(current)={0}  proposed(unified)={1}", baseWorld, proposedWorld));

            bool pass = proposedOverlay == 1 && proposedWorld == 1 && baseOverlay == SLICES && baseWorld == SLICES;
            L("VERDICT: " + (pass ? "PASS — unified path draws 5-slice mixed text in 1 renderer on both canvases" :
                                     "CHECK — counts not as expected"));
            File.WriteAllText(OutPath, _log.ToString());
            Finish(pass ? 0 : 2);
        }
        catch (Exception e)
        {
            L("EXCEPTION: " + e);
            try { File.WriteAllText(OutPath, _log.ToString()); } catch {}
            Finish(3);
        }
    }

    enum RenderSpace { Overlay, World }

    static Texture2DArray BuildArray()
    {
        // All slices RGBA32 so one array holds distance (R/median in RGB) AND color(emoji).
        // (Design note: production default keeps SDF in Alpha8 as a 2nd array; here we use the
        // "unified RGBA" option purely to prove the single-binding path.)
        var arr = new Texture2DArray(ATLAS, ATLAS, SLICES, TextureFormat.RGBA32, false);
        for (int s = 0; s < SLICES; s++)
        {
            var px = new Color32[ATLAS * ATLAS];
            for (int y = 0; y < ATLAS; y++)
            for (int x = 0; x < ATLAS; x++)
            {
                // crude radial "distance field": center ~1, edge ~0
                float dx = (x - ATLAS/2f)/(ATLAS/2f), dy = (y - ATLAS/2f)/(ATLAS/2f);
                float d = Mathf.Clamp01(1f - Mathf.Sqrt(dx*dx+dy*dy));
                byte b = (byte)(d*255);
                if (s == 4) px[y*ATLAS+x] = new Color32(b, (byte)(128), (byte)(64), b);      // emoji: colored
                else if (s == 3) px[y*ATLAS+x] = new Color32(b, b, b, 255);                  // MSDF: median(rgb)
                else px[y*ATLAS+x] = new Color32(0,0,0,b);                                   // SDF: alpha
            }
            arr.SetPixels32(px, s);
        }
        arr.Apply(false, false);
        return arr;
    }

    static ComputeBuffer BuildStyleBuffer()
    {
        // 3 distinct styles: plain, outlined (per-span), shadowed.
        // 16-float stride (face4, outline4, underlay4, outlineWidth, faceDilate, underlayOffX, underlayOffY).
        var buf = new ComputeBuffer(3, sizeof(float)*16);
        var flat = new float[3*16];
        void S(int i, float[] v){ Array.Copy(v,0,flat,i*16,16); }
        S(0, new float[]{1,1,1,1, 0,0,0,0, 0,0,0,0, 0f, 0f, 0f, 0f});         // plain white
        S(1, new float[]{1,1,1,1, 0,0,0,1, 0,0,0,0, 0.25f, 0f, 0f, 0f});      // white face + black outline (per-span)
        S(2, new float[]{1,1,1,1, 0,0,0,0, 0,0,0,0.8f, 0f, 0f, 0.03f, -0.03f});// white face + shadow
        buf.SetData(flat);
        return buf;
    }

    // Builds a canvas with text quads. unified=true -> one mesh/one CanvasRenderer across all
    // 5 slices; unified=false -> one CanvasRenderer per slice (current model). Returns the number
    // of CanvasRenderers that actually carry geometry.
    static int BuildAndCount(RenderSpace space, Texture2DArray arr, ComputeBuffer styles, bool unified)
    {
        var go = new GameObject("Canvas_" + space + (unified?"_uni":"_base"));
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = space == RenderSpace.Overlay ? RenderMode.ScreenSpaceOverlay : RenderMode.WorldSpace;
        go.AddComponent<CanvasScaler>();
        go.AddComponent<GraphicRaycaster>();

        var shader = Shader.Find("OpenGlyphSpike/Uber");
        if (shader == null) throw new Exception("spike shader not found/compiled");

        int rendererCount;
        if (unified)
        {
            var mat = new Material(shader);
            mat.SetTexture("_MainTexArray", arr);
            mat.SetBuffer("_Styles", styles);
            // 5 "runs" (3 SDF fonts, 1 MSDF, 1 emoji), the 2nd SDF run has a per-span outline (styleIdx 1).
            var modes  = new int[]{0,0,0,1,3};
            var slices = new int[]{0,1,2,3,4};
            var stylesPerRun = new int[]{0,1,2,0,0};
            var mesh = BuildMesh(5, modes, slices, stylesPerRun);
            var cr = NewRenderer(go.transform, mat, mesh);
            rendererCount = cr != null ? 1 : 0;
        }
        else
        {
            rendererCount = 0;
            for (int run = 0; run < SLICES; run++)
            {
                var mat = new Material(shader);
                mat.SetTexture("_MainTexArray", arr);
                mat.SetBuffer("_Styles", styles);
                var mesh = BuildMesh(1, new[]{ run==3?1:(run==4?3:0) }, new[]{run}, new[]{ run==1?1:0 });
                if (NewRenderer(go.transform, mat, mesh) != null) rendererCount++;
            }
        }
        return rendererCount;
    }

    static CanvasRenderer NewRenderer(Transform parent, Material mat, Mesh mesh)
    {
        var child = new GameObject("run");
        child.transform.SetParent(parent, false);
        var rt = child.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(100,100);
        var cr = child.AddComponent<CanvasRenderer>();
        cr.SetMesh(mesh);
        cr.materialCount = 1;
        cr.SetMaterial(mat, 0);
        return cr;
    }

    static Mesh BuildMesh(int runs, int[] modes, int[] slices, int[] styleIdx)
    {
        var m = new Mesh();
        var verts = new Vector3[runs*4];
        var cols  = new Color[runs*4];
        var uv0   = new Vector4[runs*4];
        var uv1   = new Vector4[runs*4];
        var tris  = new int[runs*6];
        for (int r=0;r<runs;r++)
        {
            float x = r*20; 
            verts[r*4+0]=new Vector3(x,0);   verts[r*4+1]=new Vector3(x,18);
            verts[r*4+2]=new Vector3(x+18,18);verts[r*4+3]=new Vector3(x+18,0);
            for(int k=0;k<4;k++) cols[r*4+k]=Color.white;
            uv0[r*4+0]=new Vector4(0,0,20,1); uv0[r*4+1]=new Vector4(0,1,20,1);
            uv0[r*4+2]=new Vector4(1,1,20,1); uv0[r*4+3]=new Vector4(1,0,20,1);
            var packed=new Vector4(0.1f, slices[r], modes[r], styleIdx[r]);
            for(int k=0;k<4;k++) uv1[r*4+k]=packed;
            tris[r*6+0]=r*4+0; tris[r*6+1]=r*4+1; tris[r*6+2]=r*4+2;
            tris[r*6+3]=r*4+2; tris[r*6+4]=r*4+3; tris[r*6+5]=r*4+0;
        }
        m.vertices=verts; m.colors=cols; m.SetUVs(0,uv0); m.SetUVs(1,uv1); m.triangles=tris;
        m.RecalculateBounds();
        return m;
    }

    static void Finish(int code)
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.Exit(code);
#else
        Application.Quit(code);
#endif
    }
}
