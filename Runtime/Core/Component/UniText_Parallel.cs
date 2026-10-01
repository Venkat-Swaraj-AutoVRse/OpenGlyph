using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
#if !UNITY_WEBGL || UNITY_EDITOR
using System.Threading.Tasks;
#endif

namespace LightSide
{
    /// <summary>
    /// UniText partial class handling batched and parallel text processing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Manages the static batch processing system that processes all dirty UniText
    /// components together. Uses <see cref="UniTextWorkerPool"/> for parallel execution
    /// when multiple components need updating.
    /// </para>
    /// <para>
    /// Processing occurs in two phases:
    /// <list type="number">
    /// <item>Pre-render: First pass (shaping, BiDi, script analysis) - can be parallel</item>
    /// <item>Will-render: Mesh generation and atlas updates - main thread only</item>
    /// </list>
    /// </para>
    /// </remarks>
    public partial class UniText
    {
        #region Cached Data for Parallel

        /// <summary>Cached transform data for parallel processing (avoids Unity API calls from worker threads).</summary>
        public struct CachedTransformData
        {
            /// <summary>The RectTransform.</summary>
            public RectTransform rectTransform;
            /// <summary>The RectTransform rect.</summary>
            public Rect rect;
            /// <summary>The transform's lossy scale X component.</summary>
            public float lossyScale;
            /// <summary>Whether the canvas has a world camera.</summary>
            public bool hasWorldCamera;
            /// <summary>
            /// Device pixels per local text-mesh unit, for pixel-perfect snapping. Captured on the
            /// main thread (reads Canvas + transform). 0 disables snapping (World Space canvases, or
            /// no canvas). See <see cref="ComputePixelSnapDeviceScale"/>.
            /// </summary>
            public float pixelSnapDeviceScale;
            /// <summary>
            /// Device-pixel position of this element's local origin along X/Y relative to the root
            /// canvas, i.e. where mesh-local (0,0) lands on the physical pixel grid. Snapping must
            /// round <c>phase + local·deviceScale</c> (not just <c>local·deviceScale</c>) so a glyph
            /// stays crisp even when the element itself sits at a fractional device pixel (e.g. the
            /// RectTransform is at x = 10.3). Captured on the main thread. See
            /// <see cref="ComputePixelSnapPhase"/>.
            /// </summary>
            public float pixelSnapPhaseX;
            /// <summary>Device-pixel position of this element's local origin along Y. See <see cref="pixelSnapPhaseX"/>.</summary>
            public float pixelSnapPhaseY;
        }

        /// <summary>Cached transform data captured before parallel processing.</summary>
        public CachedTransformData cachedTransformData;

        /// <summary>
        /// Computes device pixels per local text-mesh unit for pixel-perfect snapping.
        /// </summary>
        /// <remarks>
        /// Device pixels per local unit = <c>scaleFactor × (element.lossyScale / root.lossyScale)</c>.
        /// <para>
        /// For <b>Screen Space - Overlay</b> the root canvas GameObject is itself scaled by its
        /// <c>scaleFactor</c>, so <c>root.lossyScale == scaleFactor</c> and the expression reduces to
        /// the element's own <c>lossyScale</c> (unchanged from before).
        /// </para>
        /// <para>
        /// For <b>Screen Space - Camera</b> the root canvas is positioned on a plane in front of the
        /// camera and its world <c>lossyScale</c> is driven by camera distance / viewport, NOT by
        /// <c>scaleFactor</c>. Using the raw element <c>lossyScale</c> there is wrong (it is in world
        /// units, not device pixels). Dividing by the root's world lossyScale cancels that world
        /// factor, and multiplying by <c>scaleFactor</c> restores reference→device pixels; any nested
        /// Canvas / RectTransform scaling between the element and the root survives in the
        /// <c>element/root</c> ratio.
        /// </para>
        /// For a <b>World Space</b> canvas there is no fixed device-pixel grid (it depends on camera
        /// distance / viewport), so snapping is disabled (returns 0).
        /// </remarks>
        private float ComputePixelSnapDeviceScale()
        {
            var c = canvas;
            if (c == null) return 0f;

            var root = c.rootCanvas != null ? c.rootCanvas : c;
            if (root.renderMode == RenderMode.WorldSpace)
                return 0f; // no device pixel grid in world space

            var elementLossy = transform.lossyScale.x;
            var rootLossy = root.transform.lossyScale.x;
            if (elementLossy <= 0f || float.IsNaN(elementLossy) || float.IsInfinity(elementLossy))
                return 0f;
            if (rootLossy <= 0f || float.IsNaN(rootLossy) || float.IsInfinity(rootLossy))
                return 0f;

            // scaleFactor × (element lossyScale / root lossyScale). For Overlay root.lossyScale ==
            // scaleFactor so this is exactly element.lossyScale; for Camera it cancels the world
            // scale the root plane introduces.
            var devScale = c.scaleFactor * (elementLossy / rootLossy);
            if (devScale <= 0f || float.IsNaN(devScale) || float.IsInfinity(devScale))
                return 0f;
            return devScale;
        }

        /// <summary>
        /// Device-pixel position of this element's local origin (mesh-local 0,0) relative to the root
        /// canvas, used as the snapping phase. Final device coordinate of a vertex is
        /// <c>phase + local·deviceScale</c>; snapping rounds that whole expression, so the phase must
        /// carry the element's own (possibly fractional) placement on the pixel grid.
        /// </summary>
        /// <remarks>
        /// The phase is the element origin's REAL screen-space pixel position, obtained with
        /// <see cref="RectTransformUtility.WorldToScreenPoint"/> (null camera for Overlay, the
        /// canvas <c>worldCamera</c> for Camera mode). This is deliberately NOT
        /// <c>rootCanvas.InverseTransformPoint(position) × scaleFactor</c>: that expresses the origin
        /// relative to the root-canvas PIVOT (the screen centre for Screen Space - Overlay), so when
        /// the screen width or height is ODD the centre sits on a half pixel and every glyph would
        /// snap half a pixel off the physical grid. The screen point already carries that half-pixel
        /// centre offset, so <c>phase + local·deviceScale</c> equals the vertex's actual screen pixel
        /// coordinate and rounding it lands on the true device grid. Returns (0,0) when there is no
        /// canvas or in World Space (snapping is disabled there anyway).
        /// </remarks>
        private Vector2 ComputePixelSnapPhase()
        {
            var c = canvas;
            if (c == null) return Vector2.zero;
            var root = c.rootCanvas != null ? c.rootCanvas : c;
            if (root.renderMode == RenderMode.WorldSpace)
                return Vector2.zero;

            // Overlay renders with no camera; Camera/other screen-space modes use the canvas camera.
            var cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera;
            // Real screen-pixel position of the element origin (mesh-local 0,0 == transform.position).
            var phase = RectTransformUtility.WorldToScreenPoint(cam, transform.position);
            if (float.IsNaN(phase.x) || float.IsInfinity(phase.x) ||
                float.IsNaN(phase.y) || float.IsInfinity(phase.y))
                return Vector2.zero;
            return phase;
        }

        private void PrepareForParallel()
        {
            var scale = transform.lossyScale.x;

            if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale))
                scale = 1f;

            cachedTransformData = new CachedTransformData
            {
                rectTransform = rectTransform,
                rect = rectTransform.rect,
                lossyScale = scale,
                hasWorldCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay,
                pixelSnapDeviceScale = ComputePixelSnapDeviceScale(),
            };
            var snapPhase = ComputePixelSnapPhase();
            cachedTransformData.pixelSnapPhaseX = snapPhase.x;
            cachedTransformData.pixelSnapPhaseY = snapPhase.y;

            PrepareModifiersForParallel();
        }
        

        private void PrepareModifiersForParallel()
        {
            for (var i = 0; i < modRegisters.Count; i++)
            {
                var reg = modRegisters[i];
                if (reg.IsRegistered)
                    reg.Modifier.PrepareForParallel();
            }

            for (var i = 0; i < runtimeConfigCopies.Count; i++)
            {
                var config = runtimeConfigCopies[i];
                if (config == null) continue;
                var configMods = config.modRegisters;
                for (var j = 0; j < configMods.Count; j++)
                {
                    var reg = configMods[j];
                    if (reg.IsRegistered)
                        reg.Modifier.PrepareForParallel();
                }
            }
        }

        #endregion

        #region Static Batch Processing

        /// <summary>Gets or sets whether parallel processing is enabled for multiple UniText components.</summary>
        public static bool UseParallel { get; set; } = true;

        /// <summary>Raised before canvas rendering begins, after all UniText components are processed.</summary>
        public static event Action MeshApplied;
        private static PooledBuffer<UniText> componentsBuffer;
        private static bool isInitialized;
        private static bool useParallel;

        private const int ParallelCharacterThreshold = 500;
        private const int MinComponentsForParallel = 3;

        #region Parallel Atlas Pipeline

        private struct FontBatchEntry
        {
            public UniTextFont font;
            public HashSet<uint> glyphSet;
            public List<(uint unicode, uint glyphIndex)> characterEntries;
            public UniTextFont.PreparedBatch? prepared;
            public object rendered;
        }

        private sealed class FontReferenceComparer : IEqualityComparer<UniTextFont>
        {
            public static readonly FontReferenceComparer Instance = new();
            public bool Equals(UniTextFont x, UniTextFont y) => ReferenceEquals(x, y);
            public int GetHashCode(UniTextFont obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static Dictionary<UniTextFont, int> fontIndexMap;
        private static FontBatchEntry[] fontBatches;
        private static int fontBatchCount;
        private static Stack<HashSet<uint>> glyphSetPool;
        private static Stack<List<(uint, uint)>> charEntryPool;
        private static List<uint> tempGlyphList;
        private static List<uint> variedGlyphList; // Phase 2: per-run varied glyph indices

        private static int CollectGlyphRequestsFromAllComponents(PooledBuffer<UniText> components, int count)
        {
            fontIndexMap ??= new Dictionary<UniTextFont, int>(16, FontReferenceComparer.Instance);
            glyphSetPool ??= new Stack<HashSet<uint>>();
            charEntryPool ??= new Stack<List<(uint, uint)>>();
            fontBatches ??= new FontBatchEntry[8];

            for (int i = 0; i < fontBatchCount; i++)
            {
                ref var prev = ref fontBatches[i];
                if (prev.glyphSet != null) { prev.glyphSet.Clear(); glyphSetPool.Push(prev.glyphSet); }
                if (prev.characterEntries != null) { prev.characterEntries.Clear(); charEntryPool.Push(prev.characterEntries); }
                prev = default;
            }
            fontIndexMap.Clear();
            fontBatchCount = 0;

            for (int c = 0; c < count; c++)
            {
                var tp = components[c].textProcessor;
                if (tp == null || !tp.HasValidFirstPassData) continue;
                if (tp.HasValidGlyphsInAtlas) continue;

                var fontProvider = tp.FontProviderForAtlas;
                if (fontProvider == null) continue;

                var shapedRuns = tp.buf.shapedRuns.Span;
                var shapedGlyphs = tp.buf.shapedGlyphs.Span;

                for (int r = 0; r < shapedRuns.Length; r++)
                {
                    ref readonly var run = ref shapedRuns[r];
                    var fontAsset = fontProvider.GetFontAsset(run.fontId);
                    if (fontAsset == null) continue;

                    // Phase 2: a run bound to a variable-font instance rasterizes into the per-
                    // VariationKey atlas store (coords applied before raster) rather than the shared
                    // glyph set. Done here on the main thread, before the parallel render pass.
                    if (!run.variationKey.IsNone)
                    {
                        variedGlyphList ??= new List<uint>(64);
                        variedGlyphList.Clear();
                        var vend = run.glyphStart + run.glyphCount;
                        for (int g = run.glyphStart; g < vend; g++)
                        {
                            var gi = (uint)shapedGlyphs[g].glyphId;
                            if (gi == 0) continue;
                            if (fontAsset.HasGlyphInAtlas(gi, run.variationKey)) continue;
                            variedGlyphList.Add(gi);
                        }
                        if (variedGlyphList.Count > 0)
                        {
                            fontProvider.GetVariationCoords(run.fontId, run.styleSpec,
                                tp.buf.shapingFontSize, out var vtags, out var vcoords);
                            fontAsset.EnsureGlyphsForVariation(variedGlyphList, run.variationKey, vtags, vcoords);
                        }
                        continue; // varied glyphs handled; do not add to the plain set
                    }

                    var codepoints = tp.buf.codepoints;
                    var provider = UnicodeData.Provider;
                    var end = run.glyphStart + run.glyphCount;
                    for (int g = run.glyphStart; g < end; g++)
                    {
                        var glyphIndex = (uint)shapedGlyphs[g].glyphId;
                        if (glyphIndex == 0)
                        {
                            var cp = codepoints.data[shapedGlyphs[g].cluster];
                            var cat = provider.GetGeneralCategory(cp);
                            if (cat is GeneralCategory.Cc or GeneralCategory.Cf
                                or GeneralCategory.Zl or GeneralCategory.Zp)
                                continue;
                        }
                        if (fontAsset.HasGlyphInAtlas(glyphIndex)) continue;

                        GetOrCreateEntry(fontAsset).glyphSet.Add(glyphIndex);
                    }
                }

                var vc = tp.buf.virtualCodepoints;
                for (int i = 0; i < vc.count; i++)
                {
                    var unicode = vc.data[i];
                    var fontId = fontProvider.FindFontForCodepoint((int)unicode);
                    var fontAsset = fontProvider.GetFontAsset(fontId);
                    if (fontAsset == null) continue;

                    var glyphIndex = fontAsset.GetGlyphIndexForUnicode(unicode);
                    if (fontAsset.HasGlyphInAtlas(glyphIndex)) continue;

                    ref var entry = ref GetOrCreateEntry(fontAsset);
                    entry.glyphSet.Add(glyphIndex);

                    entry.characterEntries ??= charEntryPool.Count > 0
                        ? charEntryPool.Pop()
                        : new List<(uint, uint)>(64);
                    entry.characterEntries.Add((unicode, glyphIndex));
                }

                tp.HasValidGlyphsInAtlas = true;
            }

            return fontBatchCount;
        }

        private static ref FontBatchEntry GetOrCreateEntry(UniTextFont font)
        {
            if (!fontIndexMap.TryGetValue(font, out var index))
            {
                index = fontBatchCount++;
                if (fontBatches.Length <= index)
                    Array.Resize(ref fontBatches, fontBatches.Length * 2);

                fontBatches[index] = new FontBatchEntry
                {
                    font = font,
                    glyphSet = glyphSetPool.Count > 0 ? glyphSetPool.Pop() : new HashSet<uint>(),
                };
                fontIndexMap[font] = index;
            }
            return ref fontBatches[index];
        }

        #endregion

        private static void EnsureInitialized()
        {
    #if UNITY_EDITOR
            if (UnityEditor.BuildPipeline.isBuildingPlayer) return;
    #endif
            if (isInitialized) return;

            var d = CanvasUpdateRegistry.instance;
            EmojiFont.EnsureInitialized();
            Canvas.preWillRenderCanvases += OnPreWillRenderCanvases;
            Canvas.willRenderCanvases += OnWillRenderCanvases;
            componentsBuffer.EnsureCapacity(64);
            isInitialized = true;

            Cat.Meow("[UniText] Initialized");
        }

        private static void RegisterDirty(UniText component)
        {
            EnsureInitialized();

            if (component.isRegisteredDirty)
                return;

            component.isRegisteredDirty = true;
            componentsBuffer.Add(component);
        }

        private static void UnregisterDirty(UniText component)
        {
            component.isRegisteredDirty = false;
        }

        private static bool CanWork
        {
            get
            {
                if (!UnicodeData.IsInitialized)
                {
                    UnicodeData.EnsureInitialized();
                    if (!UnicodeData.IsInitialized)
                    {
                        UniTextDebug.EndSample();
                        return false;
                    }
                }

                return true;
            }
        }

        private static void FilterAndPrepareComponents(bool validate)
        {
            for (var i = componentsBuffer.count - 1; i >= 0; i--)
            {
                var comp = componentsBuffer[i];
                if (comp == null || !comp.isRegisteredDirty || comp.sourceText.IsEmpty ||
                    (validate && !comp.ValidateAndInitialize()))
                {
                    if (comp != null)
                        comp.isRegisteredDirty = false;
                    componentsBuffer.SwapRemoveAt(i);
                    continue;
                }

                comp.isRegisteredDirty = false;
            }

            for (var i = 0; i < componentsBuffer.count; i++)
            {
                componentsBuffer[i].isRegisteredDirty = true;
                componentsBuffer[i].PrepareForParallel();
            }
        }
        

        private static void OnPreWillRenderCanvases()
        {
    #if UNITY_EDITOR
            if (UnityEditor.BuildPipeline.isBuildingPlayer) return;
    #endif
            if (componentsBuffer.count == 0) return;
            if (!CanWork) return;

            UniTextDebug.BeginSample("UniText.PreWillRender.FirstPass");

            FilterAndPrepareComponents(true);
            var count = componentsBuffer.count;
            var totalChars = 0;

            for (var i = 0; i < count; i++)
                totalChars += componentsBuffer[i].sourceText.Length;

            useParallel = totalChars > ParallelCharacterThreshold &&
                          count >= MinComponentsForParallel &&
                          UniTextWorkerPool.IsParallelSupported;

            LogBatchInfo(count, totalChars, useParallel && UseParallel);

            if (useParallel && UseParallel)
            {
                UniTextWorkerPool.Execute(componentsBuffer.data, count, static comp => comp.DoFirstPass());
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    componentsBuffer[i].DoFirstPass();
                }
            }

            UniTextDebug.EndSample();

            Cat.Meow("[UniText] OnPreWillRenderCanvases completed");
        }

        private static void OnWillRenderCanvases()
        {
    #if UNITY_EDITOR
            if (UnityEditor.BuildPipeline.isBuildingPlayer) return;
    #endif
            if (componentsBuffer.count == 0) return;
            if (!CanWork) return;

            UniTextDebug.BeginSample("UniText.WillRender.MeshGeneration");

            FilterAndPrepareComponents(false);
            var count = componentsBuffer.count;

            UniTextDebug.BeginSample("Rasterization");

            var batchCount = CollectGlyphRequestsFromAllComponents(componentsBuffer, count);

            if (batchCount > 0)
            {
                tempGlyphList ??= new List<uint>(256);

                for (int i = 0; i < batchCount; i++)
                {
                    ref var batch = ref fontBatches[i];
                    if (batch.glyphSet.Count == 0) continue;

                    tempGlyphList.Clear();
                    foreach (var glyph in batch.glyphSet)
                        tempGlyphList.Add(glyph);

                    batch.prepared = batch.font.PrepareGlyphBatch(tempGlyphList);

                    if (!batch.prepared.HasValue)
                        batch.font.TryAddGlyphsBatch(tempGlyphList);
                }

                int fontsToRender = 0;
                for (int i = 0; i < batchCount; i++)
                {
                    if (fontBatches[i].prepared.HasValue)
                    {
                        fontsToRender++;
                    }
                }
                
#if !UNITY_WEBGL || UNITY_EDITOR
                if (fontsToRender > 1)
                {
                    Parallel.For(0, batchCount, i =>
                    {
                        if (fontBatches[i].prepared.HasValue)
                            fontBatches[i].rendered = fontBatches[i].font.RenderPreparedBatch(fontBatches[i].prepared.Value);
                    });
                }
                else
#endif
                {
                    for (int i = 0; i < batchCount; i++)
                    {
                        if (fontBatches[i].prepared.HasValue)
                            fontBatches[i].rendered = fontBatches[i].font.RenderPreparedBatch(fontBatches[i].prepared.Value);
                    }
                }
                
                for (int i = 0; i < batchCount; i++)
                {
                    ref var batch = ref fontBatches[i];
                    if (batch.rendered != null)
                        batch.font.PackRenderedBatch(batch.rendered, batch.prepared.Value);
                    if (batch.characterEntries is { Count: > 0 })
                        batch.font.RegisterCharacterEntries(batch.characterEntries);
                }
            }

            UniTextDebug.EndSample();

            UniTextDebug.BeginSample("MeshDataGeneration");
            if (useParallel && UseParallel)
            {
                UniTextWorkerPool.Execute(componentsBuffer.data, count, static comp => comp.DoGenerateMeshData());
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    componentsBuffer[i].DoGenerateMeshData();
                }
            }
            UniTextDebug.EndSample();

            UniTextDebug.BeginSample("ApplyMeshes");

            for (var i = 0; i < count; i++)
            {
                componentsBuffer[i].DoApplyMesh();
            }

            MeshApplied?.Invoke();

            UniTextDebug.EndSample();

            for (var i = 0; i < componentsBuffer.count; i++)
                componentsBuffer[i].isRegisteredDirty = false;
            componentsBuffer.Clear();

            UniTextDebug.EndSample();

            Cat.Meow("[UniText] OnWillRenderCanvases completed");
        }

        #endregion

        #region Instance Batch Methods

        private void DoFirstPass()
        {
            if (sourceText.IsEmpty) return;

            var textSpan = ParseOrGetParsedAttributes();
            var shapingFontSize = autoSize ? maxFontSize : fontSize;
            var settings = new TextProcessSettings
            {
                fontSize = shapingFontSize,
                baseDirection = baseDirection
            };
            textProcessor.EnsureFirstPass(textSpan, settings);
        }

        private void DoGenerateMeshData()
        {
            if (textProcessor == null || !textProcessor.HasValidFirstPassData) return;
            if (meshGenerator == null) return;

            Rebuilding?.Invoke();

            ref readonly var cached = ref cachedTransformData;

            var effectiveFontSize = autoSize
                ? (cachedEffectiveFontSize > 0 ? cachedEffectiveFontSize : maxFontSize)
                : fontSize;

            var positionsInvalid = !textProcessor.HasValidPositionedGlyphs;

            if (positionsInvalid)
            {
                var settings = CreateProcessSettings(cached.rect, effectiveFontSize);
                textProcessor.EnsurePositions(settings);
            }

            var glyphs = textProcessor.PositionedGlyphs;
            if (glyphs.IsEmpty) return;

            meshGenerator.FontSize = effectiveFontSize;
            meshGenerator.defaultColor = color;
            meshGenerator.SetCanvasParametersCached(cached.lossyScale, cached.hasWorldCamera);
            meshGenerator.PixelSnapDeviceScale = cached.pixelSnapDeviceScale;
            meshGenerator.PixelSnapPhaseX = cached.pixelSnapPhaseX;
            meshGenerator.PixelSnapPhaseY = cached.pixelSnapPhaseY;
            meshGenerator.SetRectOffset(cached.rect);
            meshGenerator.SetHorizontalAlignment(horizontalAlignment);

            meshGenerator.GenerateMeshDataOnly(glyphs);
        }

        private void DoApplyMesh()
        {
            if (sourceText.IsEmpty || meshGenerator == null || !meshGenerator.HasGeneratedData)
            {
                DeInit();
                dirtyFlags = DirtyFlags.None;
                return;
            }

            renderData = meshGenerator.ApplyMeshesToUnity();
    #if UNITEXT_TESTS
            CopyMeshesForTests();
    #endif
            meshGenerator.ReturnInstanceBuffers();

            if (textProcessor != null)
            {
                resultWidth = textProcessor.ResultWidth;
                resultHeight = textProcessor.ResultHeight;
            }

            UpdateRendering();

            dirtyFlags = DirtyFlags.None;
        }

        #endregion

        #region Debug

        [Conditional("UNITEXT_DEBUG")]
        private static void LogBatchInfo(int componentCount, int totalChars, bool parallel)
        {
            Cat.MeowFormat("[UniText] Batch: {0} components, {1} chars, parallel={2}", componentCount, totalChars, parallel);
        }

        #endregion

        public struct TestSegmentFontInfo
        {
            public int fontId;
            public int atlasIndex;
        }

    #if UNITY_EDITOR
        /// <summary>
        /// EDITOR-ONLY: returns a copy of the vertices of every generated sub-mesh, in world-local
        /// (mesh) space, exactly as the engine produced them on the last rebuild. Used by pixel-snap
        /// integration tests to read back real geometry after <c>Canvas.ForceUpdateCanvases()</c>.
        /// Never compiled into player builds.
        /// </summary>
        internal System.Collections.Generic.List<Vector3> GetGeneratedVerticesForEditorTests()
        {
            var result = new System.Collections.Generic.List<Vector3>();
            if (renderData == null) return result;
            foreach (var rd in renderData)
                if (rd.mesh != null)
                    result.AddRange(rd.mesh.vertices);
            return result;
        }
    #endif

    #if UNITEXT_TESTS
        #region Test Support
        private List<Mesh> testMeshSnapshots;
        private List<TestSegmentFontInfo> testSegmentFontInfo;
        private static List<Vector4> tempUvBuffer;
        public IReadOnlyList<Mesh> TestMeshSnapshots => testMeshSnapshots;
        public IReadOnlyList<TestSegmentFontInfo> TestSegmentFontInfoList => testSegmentFontInfo;

        private void CopyMeshesForTests()
        {
            if (renderData == null || renderData.Count == 0) return;

            testMeshSnapshots ??= new List<Mesh>();
            testSegmentFontInfo ??= new List<TestSegmentFontInfo>();
            tempUvBuffer ??= new List<Vector4>();

            foreach (var m in testMeshSnapshots)
            {
                ObjectUtils.SafeDestroy(m);
            }
            testMeshSnapshots.Clear();
            testSegmentFontInfo.Clear();

            foreach (var rd in renderData)
            {
                var copy = new Mesh();
                copy.vertices = rd.mesh.vertices;
                copy.triangles = rd.mesh.triangles;

                tempUvBuffer.Clear();
                rd.mesh.GetUVs(0, tempUvBuffer);
                copy.SetUVs(0, tempUvBuffer);

                copy.colors32 = rd.mesh.colors32;
                testMeshSnapshots.Add(copy);
            }

            var segments = meshGenerator.GeneratedSegments;
            if (segments != null)
            {
                for (var s = 0; s < segments.Count; s++)
                {
                    var seg = segments[s];
                    testSegmentFontInfo.Add(new TestSegmentFontInfo
                    {
                        fontId = seg.fontId,
                        atlasIndex = seg.atlasIndex
                    });
                }
            }
        }

        #endregion
    #endif
    }

}
