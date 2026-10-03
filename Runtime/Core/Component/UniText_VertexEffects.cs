using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Wave 2: the per-frame vertex-effect pass (span animations, reveal). See <see cref="IUniTextVertexEffect"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Cost model.</b> A component without effects pays nothing: the glyph-tagging hook on its mesh
    /// generator is only subscribed while it has effects, and it is never ticked. A component WITH effects
    /// pays, on each rebuild, one read-back of its final meshes into its own buffers; and per frame (only
    /// while some effect reports a change) a copy of the captured positions/colours, the effects' math per
    /// glyph, and a positions + colours upload to its own mesh. No shaping, layout or mesh generation runs
    /// per frame, and the per-frame path allocates no managed memory.</para>
    /// </remarks>
    public partial class UniText
    {
        // ---------------------------------------------------------------- effect registry

        private readonly List<IUniTextVertexEffect> vertexEffects = new();
        private UniTextVertexEffectContext effectContext;
        private bool effectUploadPending;
        private bool effectClockStarted;
        private float effectLastClock;
        private float effectAnimTime;

        [SerializeField]
        [Tooltip("Speed multiplier for this component's text animations and reveal (1 = real time).")]
        private float animationTimeScale = 1f;

        [SerializeField]
        [Tooltip("Animate with unscaled time (keeps running while Time.timeScale is 0, e.g. in a pause menu).")]
        private bool animationUnscaledTime;

        [SerializeField]
        [Tooltip("Offset in seconds added to this component's animation time, to de-synchronise identical labels.")]
        private float animationPhase;

        /// <summary>Speed multiplier for this component's vertex effects (span animations and reveal).</summary>
        public float AnimationTimeScale { get => animationTimeScale; set => animationTimeScale = Mathf.Max(0f, value); }

        /// <summary>When true, effects follow unscaled time (<see cref="Time.unscaledTime"/>).</summary>
        public bool AnimationUnscaledTime { get => animationUnscaledTime; set => animationUnscaledTime = value; }

        /// <summary>Seconds added to the component's animation time (span animations only; reveal keeps its own playback time).</summary>
        public float AnimationPhase { get => animationPhase; set => animationPhase = value; }

        /// <summary>True while at least one vertex effect is attached.</summary>
        public bool HasVertexEffects => vertexEffects.Count > 0;

        /// <summary>The effect context of the last capture (null before the first capture).</summary>
        public UniTextVertexEffectContext VertexEffectContext => effectContext;

        /// <summary>
        /// Attaches a vertex effect (main thread). The component rebuilds its mesh once so the effect has
        /// a capture to work on. Adding the same effect twice is a no-op.
        /// </summary>
        public void AddVertexEffect(IUniTextVertexEffect effect)
        {
            if (!AddVertexEffectInternal(effect)) return;
            SetDirty(DirtyFlags.Color);
        }

        /// <summary>Detaches a vertex effect (main thread). With no effects left, the plain mesh is restored.</summary>
        public void RemoveVertexEffect(IUniTextVertexEffect effect)
        {
            if (!RemoveVertexEffectInternal(effect)) return;
            if (vertexEffects.Count == 0 && isActiveAndEnabled)
            {
                // Re-upload the plain mesh; the geometry-identity skip must not keep the animated one.
                hasAppliedGeometry = false;
                SetDirty(DirtyFlags.Color);
            }
        }

        /// <summary>
        /// Registration used by modifiers during parsing (which may run on a worker thread for this
        /// component). No dirtying: the parse is already part of a rebuild.
        /// </summary>
        internal bool AddVertexEffectInternal(IUniTextVertexEffect effect)
        {
            if (effect == null || vertexEffects.Contains(effect)) return false;
            var i = vertexEffects.Count;
            while (i > 0 && vertexEffects[i - 1].Order > effect.Order) i--;
            vertexEffects.Insert(i, effect);
            return true;
        }

        internal bool RemoveVertexEffectInternal(IUniTextVertexEffect effect) => vertexEffects.Remove(effect);

        // ---------------------------------------------------------------- glyph tagging (during generation)

        private bool effectHooked;
        private UniTextMeshGenerator effectHookedGenerator;
        private int[] effectVertexTag = System.Array.Empty<int>();
        private int[] effectRecordStart = System.Array.Empty<int>();
        private int effectRecordCount;
        private int effectTaggedUpTo;
        private System.Action effectOnGlyph;
        private System.Action effectOnRebuildStart;

        /// <summary>
        /// MAIN THREAD, before generation: subscribe the tagging hook only while effects are attached, so
        /// components without effects pay nothing per glyph.
        /// </summary>
        private void SyncVertexEffectHook()
        {
            effectOnGlyph ??= OnEffectGlyph;
            effectOnRebuildStart ??= OnEffectRebuildStart;
            var want = vertexEffects.Count > 0 && meshGenerator != null;
            if (effectHooked && (!want || effectHookedGenerator != meshGenerator))
            {
                if (effectHookedGenerator != null)
                {
                    effectHookedGenerator.OnGlyph -= effectOnGlyph;
                    effectHookedGenerator.OnRebuildStart -= effectOnRebuildStart;
                }
                effectHooked = false;
                effectHookedGenerator = null;
            }
            if (want && !effectHooked)
            {
                meshGenerator.OnGlyph += effectOnGlyph;
                meshGenerator.OnRebuildStart += effectOnRebuildStart;
                effectHooked = true;
                effectHookedGenerator = meshGenerator;
            }
        }

        private void OnEffectRebuildStart()
        {
            effectRecordCount = 0;
            effectTaggedUpTo = 0;
        }

        // Tags the glyph's 4 vertices (generator indices) with its record index; untagged gaps get -1.
        private void OnEffectGlyph()
        {
            var gen = UniTextMeshGenerator.Current;
            if (gen == null) return;
            var s = gen.currentGlyphVertexStart;
            var end = s + 4;
            if (effectVertexTag.Length < end) System.Array.Resize(ref effectVertexTag, Mathf.NextPowerOfTwo(end));
            for (var i = effectTaggedUpTo; i < s; i++) effectVertexTag[i] = -1;
            var rec = effectRecordCount++;
            if (effectRecordStart.Length <= rec) System.Array.Resize(ref effectRecordStart, Mathf.NextPowerOfTwo(rec + 1));
            effectRecordStart[rec] = s;
            for (var i = s; i < end; i++) effectVertexTag[i] = rec;
            if (end > effectTaggedUpTo) effectTaggedUpTo = end;
            effectRecordCluster ??= new List<int>(64);
            if (effectRecordCluster.Count <= rec) effectRecordCluster.Add(gen.currentCluster);
            else effectRecordCluster[rec] = gen.currentCluster;
        }

        private List<int> effectRecordCluster;

        // ---------------------------------------------------------------- capture (main thread, after UpdateRendering)

        private static readonly List<Vector3> s_effV = new();
        private static readonly List<Vector3> s_effN = new();
        private static readonly List<Color32> s_effC = new();
        private static readonly List<Vector4> s_effUv = new();
        private static readonly List<int> s_effTri = new();
        private static readonly List<UnifiedRenderBuilder.SourceRange> s_legacyRange = new(1);

        /// <summary>TEST INSTRUMENTATION: number of vertex-effect captures (one per rebuild of an animated component).</summary>
        internal static long VertexEffectCaptures;

        /// <summary>TEST INSTRUMENTATION: number of per-frame vertex-effect uploads.</summary>
        internal static long VertexEffectUploads;

        private void CaptureVertexEffects(List<UniTextRenderData> data, bool unified)
        {
            if (vertexEffects.Count == 0 || data == null || meshGenerator == null || !effectHooked)
            {
                ReleaseVertexEffects();
                return;
            }

            System.Threading.Interlocked.Increment(ref VertexEffectCaptures);
            effectContext ??= new UniTextVertexEffectContext(this);
            var ctx = effectContext;
            ctx.BeginCapture();

            // Every vertex the generator produced after the last glyph is untagged (ellipsis, bullets…).
            var genSegs = meshGenerator.GeneratedSegments;
            var genVertexTotal = 0;
            if (genSegs != null)
                for (var s = 0; s < genSegs.Count; s++)
                    genVertexTotal = Mathf.Max(genVertexTotal, genSegs.buffer[s].vertexStart + genSegs.buffer[s].vertexCount);
            if (effectVertexTag.Length < genVertexTotal) System.Array.Resize(ref effectVertexTag, Mathf.NextPowerOfTwo(genVertexTotal));
            for (var i = effectTaggedUpTo; i < genVertexTotal; i++) effectVertexTag[i] = -1;

            for (var ei = 0; ei < data.Count; ei++)
            {
                var entry = ctx.NextEntry();
                var src = data[ei].mesh;
                entry.count = 0;
                if (src == null || src.vertexCount == 0) continue;

                List<UnifiedRenderBuilder.SourceRange> ranges;
                if (unified) ranges = UnifiedRenderBuilder.LastBuildSources(ei);
                else
                {
                    s_legacyRange.Clear();
                    if (genSegs != null && ei < genSegs.Count)
                        s_legacyRange.Add(new UnifiedRenderBuilder.SourceRange
                            { srcStart = genSegs.buffer[ei].vertexStart, count = genSegs.buffer[ei].vertexCount, dstStart = 0 });
                    ranges = s_legacyRange;
                }

                ReadBack(src, entry);
                var n = entry.count;

                // Glyph table: walk the source ranges; a tagged vertex that starts its record begins a quad.
                if (ranges != null)
                {
                    for (var r = 0; r < ranges.Count; r++)
                    {
                        var range = ranges[r];
                        for (var j = 0; j < range.count; j++)
                        {
                            var dst = range.dstStart + j;
                            if (dst >= n) break;
                            var gIdx = range.srcStart >= 0 ? range.srcStart + j : -1;
                            var tag = gIdx >= 0 && gIdx < effectVertexTag.Length ? effectVertexTag[gIdx] : -1;
                            if (tag >= 0 && effectRecordStart[tag] == gIdx && j + 3 < range.count && dst + 3 < n)
                            {
                                var v = entry.baseVerts;
                                var minX = Mathf.Min(v[dst].x, v[dst + 2].x);
                                var maxX = Mathf.Max(v[dst].x, v[dst + 2].x);
                                var minY = Mathf.Min(v[dst].y, v[dst + 1].y);
                                var maxY = Mathf.Max(v[dst].y, v[dst + 1].y);
                                ctx.AddGlyph(new AnimatedGlyph
                                {
                                    cluster = effectRecordCluster[tag],
                                    entry = ei,
                                    vertexStart = dst,
                                    center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f),
                                    height = maxY - minY,
                                });
                                j += 3;
                            }
                            else if (tag < 0)
                            {
                                ctx.AddDecorationVertex(ei, dst);
                            }
                        }
                    }
                }
            }

            effectUploadPending = true;
            for (var i = 0; i < vertexEffects.Count; i++) vertexEffects[i].OnCaptured(ctx);
            RegisterAnimated(this);
        }

        private static void ReadBack(Mesh src, UniTextVertexEffectContext.Entry e)
        {
            s_effV.Clear(); src.GetVertices(s_effV);
            var n = s_effV.Count;
            e.Ensure(n);
            e.count = n;
            s_effV.CopyTo(e.baseVerts);
            s_effC.Clear(); src.GetColors(s_effC);
            if (s_effC.Count == n) s_effC.CopyTo(e.baseColors);
            else for (var i = 0; i < n; i++) e.baseColors[i] = new Color32(255, 255, 255, 255);

            if (e.mesh == null)
            {
                e.mesh = new Mesh { name = "UniText VertexEffects", hideFlags = HideFlags.HideAndDontSave };
                e.mesh.MarkDynamic();
            }
            var m = e.mesh;
            m.Clear();
            m.SetVertices(e.baseVerts, 0, n);
            s_effN.Clear(); src.GetNormals(s_effN);
            if (s_effN.Count == n) m.SetNormals(s_effN);
            m.SetColors(e.baseColors, 0, n);
            s_effUv.Clear(); src.GetUVs(0, s_effUv); if (s_effUv.Count == n) m.SetUVs(0, s_effUv);
            s_effUv.Clear(); src.GetUVs(1, s_effUv); if (s_effUv.Count == n) m.SetUVs(1, s_effUv);
            var sub = src.subMeshCount;
            m.subMeshCount = sub;
            for (var s = 0; s < sub; s++)
            {
                s_effTri.Clear();
                src.GetTriangles(s_effTri, s);
                m.SetTriangles(s_effTri, s, false);
            }
            m.RecalculateBounds();
        }

        private void ReleaseVertexEffects()
        {
            UnregisterAnimated(this);
            effectContext?.ReleaseMeshes();
            effectUploadPending = false;
        }

        // ---------------------------------------------------------------- per-frame tick

        private static readonly List<UniText> animatedComponents = new();
        private bool isAnimatedRegistered;

        private static void RegisterAnimated(UniText t)
        {
            if (t.isAnimatedRegistered) return;
            t.isAnimatedRegistered = true;
            animatedComponents.Add(t);
        }

        private static void UnregisterAnimated(UniText t)
        {
            if (!t.isAnimatedRegistered) return;
            t.isAnimatedRegistered = false;
            animatedComponents.Remove(t);
        }

        /// <summary>
        /// Runs every animated component's effects once (main thread). Called from
        /// <c>Canvas.willRenderCanvases</c> after the frame's rebuilds; public so tools and tests can
        /// step effects explicitly.
        /// </summary>
        public static void TickVertexEffects()
        {
            for (var i = animatedComponents.Count - 1; i >= 0; i--)
            {
                var t = animatedComponents[i];
                if (t == null) { animatedComponents.RemoveAt(i); continue; }
                t.TickVertexEffectsInstance();
            }
        }

        private void TickVertexEffectsInstance()
        {
            var ctx = effectContext;
            if (ctx == null || !ctx.HasCapture || vertexEffects.Count == 0 || !isActiveAndEnabled) return;

            var now = UniTextAnimationClock.Now(animationUnscaledTime);
            var dt = effectClockStarted ? Mathf.Max(0f, now - effectLastClock) : 0f;
            effectClockStarted = true;
            effectLastClock = now;
            var scaledDt = dt * animationTimeScale;
            effectAnimTime += scaledDt;
            ctx.Time = effectAnimTime + animationPhase;
            ctx.DeltaTime = scaledDt;

            var changed = effectUploadPending;
            for (var i = 0; i < vertexEffects.Count; i++)
                changed |= vertexEffects[i].Prepare(ctx);
            if (!changed) return;
            effectUploadPending = false;

            ctx.ResetWork();
            for (var i = 0; i < vertexEffects.Count; i++) vertexEffects[i].Apply(ctx);

            for (var i = 0; i < ctx.entryCount && i < subMeshRenderers.Count; i++)
            {
                var e = ctx.entries[i];
                var r = subMeshRenderers[i].renderer;
                if (e.count == 0 || e.mesh == null || r == null) continue;
                e.mesh.SetVertices(e.verts, 0, e.count);
                e.mesh.SetColors(e.colors, 0, e.count);
                r.SetMesh(e.mesh);
            }
            System.Threading.Interlocked.Increment(ref VertexEffectUploads);
        }

        /// <summary>Resets the integrated animation time to 0 (span animations restart their cycle).</summary>
        public void ResetAnimationTime()
        {
            effectAnimTime = 0f;
            effectClockStarted = false;
            effectUploadPending = true;
        }

#if UNITY_EDITOR
        /// <summary>EDITOR/TEST: the work (animated) positions of entry <paramref name="entry"/> after the last tick.</summary>
        internal Vector3[] EffectWorkVerticesForTests(int entry, out int count)
        {
            count = 0;
            if (effectContext == null || entry >= effectContext.entryCount) return null;
            var e = effectContext.entries[entry];
            count = e.count;
            return e.verts;
        }

        /// <summary>EDITOR/TEST: the work (animated) colours of entry <paramref name="entry"/> after the last tick.</summary>
        internal Color32[] EffectWorkColorsForTests(int entry, out int count)
        {
            count = 0;
            if (effectContext == null || entry >= effectContext.entryCount) return null;
            var e = effectContext.entries[entry];
            count = e.count;
            return e.colors;
        }

        /// <summary>EDITOR/TEST: the mesh currently set on sub-mesh renderer <paramref name="index"/>.</summary>
        internal Mesh RendererMeshForTests(int index) =>
            index < subMeshRenderers.Count && subMeshRenderers[index].renderer != null ? subMeshRenderers[index].renderer.GetMesh() : null;
#endif
    }
}
