using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LightSide.Tests
{
    /// <summary>
    /// Shared real-engine render harness: draws OpenGlyph's own <see cref="UniTextMeshGenerator"/>
    /// meshes (geometry + UVs from the engine, pixels from the engine's SDF/MSDF atlas) through the
    /// SDF/MSDF display shader into a <see cref="RenderTexture"/> via a <see cref="CommandBuffer"/> +
    /// orthographic projection, and reads them back. No external rasteriser. Extracted from
    /// <c>MsdfVisualEvidenceTests</c>/<c>SegmentationRenderTests</c> so evidence tests share one path.
    /// </summary>
    internal static class EngineRenderHarness
    {
        /// <summary>One standalone engine mesh plus its atlas texture.</summary>
        public struct Item { public Mesh mesh; public Texture tex; }

        /// <summary>
        /// A Cull-Off, alpha-blended passthrough that samples the engine SDF/MSDF atlas: SDF uses the
        /// alpha channel; MSDF reconstructs via the RGB median. smoothstep around the 0.5 mid-level
        /// gives the AA edge. Shapes/UVs are the engine's; only this final display shader is ours.
        /// </summary>
        public static Material BuildAtlasMaterial(bool msdf = false)
        {
            string field = msdf
                ? "float d = med3(t.r, t.g, t.b);"   // MSDF: median of RGB (RGB24 atlas, alpha is 1)
                : "float d = t.a;";                   // SDF: Alpha8 distance field
            string src = @"
Shader ""Hidden/OpenGlyphPhase2Preview"" {
  Properties { _MainTex (""Atlas"", 2D) = ""black"" {} }
  SubShader {
    Tags { ""Queue""=""Transparent"" ""RenderType""=""Transparent"" }
    Blend SrcAlpha OneMinusSrcAlpha
    Cull Off ZWrite Off ZTest Always
    Pass {
      CGPROGRAM
      #pragma vertex vert
      #pragma fragment frag
      #include ""UnityCG.cginc""
      sampler2D _MainTex;
      struct appdata { float4 vertex:POSITION; float4 uv:TEXCOORD0; float4 color:COLOR; };
      struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float4 col:COLOR; };
      v2f vert(appdata v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.uv.xy; o.col=v.color; return o; }
      float med3(float a,float b,float c){ return max(min(a,b),min(max(a,b),c)); }
      fixed4 frag(v2f i):SV_Target {
        fixed4 t = tex2D(_MainTex, i.uv);
        " + field + @"
        float aa = fwidth(d) + 1e-4;
        float cov = smoothstep(0.5 - aa, 0.5 + aa, d);
        return fixed4(i.col.rgb, i.col.a * cov);
      }
      ENDCG
    }
  }
}";
            var shader = ShaderUtilCompat.CreateRuntimeShader(src);
            return new Material(shader != null ? shader : Shader.Find("Unlit/Transparent"));
        }

        /// <summary>
        /// Draws <paramref name="rows"/> (each a list of engine meshes to place at a given top-left y)
        /// into a <paramref name="w"/>x<paramref name="h"/> RGBA texture. Each row is placed at its own
        /// y with an 8px x inset; glyphs render at native 1:1 scale so real advances show. Returns the
        /// read-back texture (caller destroys it).
        /// </summary>
        public static Texture2D RenderRows(List<(List<Item> items, float topY)> rows, int w, int h, Color bg, bool msdf = false)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            var mat = BuildAtlasMaterial(msdf);
            Texture2D readback;
            try
            {
                var cb = new CommandBuffer { name = "OpenGlyphPhase2Render" };
                cb.SetRenderTarget(rt);
                cb.ClearRenderTarget(true, true, bg);
                cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0, w, h, 0, -100f, 100f));

                foreach (var (items, topY) in rows)
                {
                    if (items == null || items.Count == 0) continue;
                    // Combined bounds so we can seat the row's baseline near topY (layout is y-down).
                    var min = new Vector3(float.MaxValue, float.MaxValue, 0);
                    var max = new Vector3(float.MinValue, float.MinValue, 0);
                    foreach (var it in items)
                    {
                        if (it.mesh == null) continue;
                        min = Vector3.Min(min, it.mesh.bounds.min);
                        max = Vector3.Max(max, it.mesh.bounds.max);
                    }
                    if (min.x > max.x) continue;
                    foreach (var it in items)
                    {
                        if (it.mesh == null) continue;
                        var block = new MaterialPropertyBlock();
                        if (it.tex != null) block.SetTexture("_MainTex", it.tex);
                        var m = Matrix4x4.TRS(new Vector3(8f, topY, 0f), Quaternion.identity, new Vector3(1f, -1f, 1f))
                              * Matrix4x4.Translate(new Vector3(-min.x, -max.y, 0f));
                        cb.DrawMesh(it.mesh, m, mat, 0, 0, block);
                    }
                }

                Graphics.ExecuteCommandBuffer(cb);
                cb.Release();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                readback = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readback.Apply();
                RenderTexture.active = prev;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mat);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
            return readback;
        }

        /// <summary>Counts pixels that are not close to the background colour (drew-something check).</summary>
        public static int NonBackgroundPixels(Texture2D tex, Color bg)
        {
            var px = tex.GetPixels();
            int n = 0;
            foreach (var p in px)
                if (Mathf.Abs(p.r - bg.r) + Mathf.Abs(p.g - bg.g) + Mathf.Abs(p.b - bg.b) > 0.15f) n++;
            return n;
        }
    }
}
