using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Shared materials for world-space text on the unified renderer (<c>UniText/World/Uber</c>).
    /// </summary>
    /// <remarks>
    /// One material per (Canvas shared Uber material = atlas format, <see cref="WorldTextOptions"/> render
    /// key). Every world component with the same options draws with the same material: no per-component
    /// copies, so N labels cost N draws that the SRP Batcher can batch (and the Built-in pipeline can
    /// dynamic-batch small ones). The atlas array / style table bindings are kept in sync with the
    /// Canvas shared material by <see cref="UnifiedRenderBuilder"/> (registered like a stencil copy).
    /// </remarks>
    public static class UniTextWorldMaterials
    {
        /// <summary>Name of the world-space shader (pinned into builds by the build processor).</summary>
        public const string ShaderName = "UniText/World/Uber";

        /// <summary>Render queue used with depth write (AlphaTest: drawn before transparents).</summary>
        public const int DepthWriteQueue = 2450;

        private static Shader _shader;
        private static bool _warned;
        private static readonly Dictionary<(int src, int key), (Material src, Material world)> Cache = new();
        private static readonly List<(int, int)> s_dead = new();

        private static readonly int LitId = Shader.PropertyToID("_Lit");
        private static readonly int AlphaClipId = Shader.PropertyToID("_AlphaClip");
        private static readonly int CullId = Shader.PropertyToID("_Cull");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");

        private static Shader WorldShader => _shader != null ? _shader : (_shader = Shader.Find(ShaderName));

        /// <summary>Number of live shared world materials (test / diagnostics).</summary>
        public static int Count => Cache.Count;

        /// <summary>
        /// The shared world material for the Canvas shared Uber material <paramref name="shared"/> and
        /// <paramref name="options"/>. Falls back to <paramref name="shared"/> when the world shader is missing.
        /// </summary>
        public static Material Get(Material shared, in WorldTextOptions options)
        {
            if (shared == null) return null;
            var key = (shared.GetInstanceID(), options.MaterialKey);
            if (Cache.TryGetValue(key, out var hit) && hit.world != null && hit.src != null) return hit.world;

            PurgeDead();
            var sh = WorldShader;
            if (sh == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning($"[OpenGlyph] {ShaderName} not found (stripped from the build?). World text " +
                                     "falls back to the Canvas Uber shader. The build processor adds it to Always-Included Shaders.");
                }
                return shared;
            }

            var m = new Material(sh)
            {
                name = $"UniText World Shared [{shared.name}] {Describe(options)}",
                hideFlags = HideFlags.DontSave,
            };
            Configure(m, options);
            UnifiedRenderBuilder.TrackStencilCopy(shared, m); // copies + keeps the array/style bindings current
            Cache[key] = (shared, m);
            return m;
        }

        /// <summary>Applies <paramref name="options"/> to a <c>UniText/World/Uber</c> material.</summary>
        public static void Configure(Material m, in WorldTextOptions options)
        {
            var lit = options.lighting == WorldTextLighting.Lit;
            m.SetFloat(LitId, lit ? 1 : 0);
            if (lit) m.EnableKeyword("_UNITEXT_LIT"); else m.DisableKeyword("_UNITEXT_LIT");

            m.SetFloat(ZWriteId, options.depthWrite ? 1 : 0);
            m.SetFloat(AlphaClipId, options.depthWrite ? 1 : 0);
            if (options.depthWrite) m.EnableKeyword("_UNITEXT_ALPHACLIP"); else m.DisableKeyword("_UNITEXT_ALPHACLIP");
            m.renderQueue = options.depthWrite ? DepthWriteQueue : (int)UnityEngine.Rendering.RenderQueue.Transparent;

            m.SetFloat(CullId, options.doubleSided ? (float)UnityEngine.Rendering.CullMode.Off : (float)UnityEngine.Rendering.CullMode.Back);
        }

        private static string Describe(in WorldTextOptions o) =>
            $"{o.lighting}{(o.depthWrite ? " ZWrite" : "")}{(o.doubleSided ? " 2-Sided" : "")}";

        private static void PurgeDead()
        {
            s_dead.Clear();
            foreach (var kv in Cache)
                if (kv.Value.src == null || kv.Value.world == null) s_dead.Add(kv.Key);
            for (var i = 0; i < s_dead.Count; i++)
            {
                var w = Cache[s_dead[i]].world;
                if (w != null) Object.DestroyImmediate(w);
                Cache.Remove(s_dead[i]);
            }
        }

        /// <summary>Destroys every shared world material (test reset; called by <see cref="UnifiedRenderBuilder.ResetShared"/>).</summary>
        public static void Reset()
        {
            foreach (var kv in Cache)
                if (kv.Value.world != null) Object.DestroyImmediate(kv.Value.world);
            Cache.Clear();
        }

        /// <summary>True when <paramref name="m"/> is one of the shared world materials.</summary>
        public static bool IsShared(Material m)
        {
            if (m == null) return false;
            foreach (var kv in Cache) if (kv.Value.world == m) return true;
            return false;
        }
    }
}
