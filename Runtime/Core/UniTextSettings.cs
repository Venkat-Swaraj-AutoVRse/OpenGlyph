using System;
using UnityEngine;

// R2 sub-task 4: settings still carries the editor-only DefaultAppearance (the [Obsolete] type) so new
// components can seed from a project default during the deprecation window; suppress 618 for this file.
#pragma warning disable 618

namespace LightSide
{
    /// <summary>
    /// Global settings ScriptableObject for UniText configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Access via Edit → Project Settings → UniText.
    /// Contains editor-only default configurations for new UniText components.
    /// </para>
    /// </remarks>
    public sealed class UniTextSettings : ScriptableObject
    {
        private const string ResourcePath = "UniTextSettings";
        private const string UnicodeDataPath = "UnicodeData";

        private static TextAsset cachedUnicodeData;

        [Header("Runtime Assets")]
        [SerializeField]
        [Tooltip("Named gradients for <gradient=name> tags.")]
        private UniTextGradients gradients;

        [Header("Word Segmentation Dictionaries (opt-in)")]
        [SerializeField]
        [Tooltip("Dictionary assets enabling dictionary-based line breaking for scripts written " +
                 "without spaces (Thai, Lao, Khmer, Myanmar). Assign a .bytes dictionary per script " +
                 "to enable word-boundary line breaking for it. Leave a script unassigned to keep the " +
                 "default behaviour (no interior breaks) and ship nothing extra. Because these assets " +
                 "live OUTSIDE any Resources folder, an unassigned script adds zero bytes to the build.")]
        private SegmentationDictionaryEntry[] segmentationDictionaries = Array.Empty<SegmentationDictionaryEntry>();

        /// <summary>
        /// Returns the dictionary asset assigned for the given segmentation script, or
        /// <see langword="null"/> if none is assigned (script stays at default behaviour).
        /// </summary>
        public static TextAsset GetSegmentationDictionary(SegmentationScript script)
        {
            var inst = Instance;
            if (inst == null || inst.segmentationDictionaries == null)
                return null;
            foreach (var entry in inst.segmentationDictionaries)
                if (entry != null && entry.script == script)
                    return entry.dictionary;
            return null;
        }

        /// <summary>Gets or sets the named gradients asset.</summary>
        public static UniTextGradients Gradients
        {
            get => Instance.gradients;
            set
            {
                if (value != Instance.gradients)
                { 
                    Instance.gradients = value;
                    Changed?.Invoke();
                }
            }
        }

        [Header("Shared Glyph Atlas (Render Architecture R2)")]
        [SerializeField]
        [Tooltip("NOT ACTIVE YET — this budget is currently IGNORED by the runtime (see " +
                 "Documentation/Design/MemoryBudgets.md). The atlas runs unbounded regardless of this " +
                 "value because the render path does not reference-count glyphs or advance the atlas " +
                 "LRU clock, so enforcing a budget could evict on-screen glyphs and draw wrong text. " +
                 "The field is kept so existing assets load and for when budgets are properly wired.\n\n" +
                 "Intended meaning: max pages (Texture2DArray slices) PER shared glyph-atlas array " +
                 "before LRU eviction begins reusing pages. 0 (default) = unbounded: the array grows " +
                 "and never evicts, so behaviour is identical to the legacy per-font atlas until a " +
                 "budget is configured. Set a positive cap to bound glyph memory under variable fonts; " +
                 "least-recently-used, unreferenced glyphs are then evicted and re-rasterized on demand.")]
        [Min(0)]
        private int sharedAtlasPageBudget = 0;

        /// <summary>
        /// Max pages per shared glyph-atlas array before eviction. 0 = unbounded (no eviction;
        /// unchanged legacy behaviour). See <see cref="GlyphAtlasArray.PageBudget"/>.
        /// NOTE: this value is CURRENTLY IGNORED by the runtime — <see cref="SharedGlyphAtlas"/>
        /// forces every budget to 0 (see Documentation/Design/MemoryBudgets.md). The accessor still
        /// returns the configured value (used by the inactive-budget warning and tests).
        /// </summary>
        public static int SharedAtlasPageBudget => Instance != null ? Instance.sharedAtlasPageBudget : 0;

        [SerializeField]
        [Tooltip("NOT ACTIVE YET — currently IGNORED by the runtime (see " +
                 "Documentation/Design/MemoryBudgets.md); the atlas runs unbounded regardless of this " +
                 "value. Kept so existing assets load and for when budgets are properly wired.\n\n" +
                 "Intended meaning: max RESIDENT bytes PER shared glyph-atlas array before LRU " +
                 "eviction begins (pages x size^2 x bytesPerPixel). 0 (default) = unbounded. This is " +
                 "the mobile-relevant knob: a 1024x1024 Alpha8 page is 1 MB and an RGBA32 page is " +
                 "4 MB, so e.g. 8388608 (8 MB) caps an Alpha8 array at 8 pages. Least-recently-used, " +
                 "unreferenced glyphs are evicted and re-rasterized on demand. Enforced together " +
                 "with the page budget (whichever is hit first).")]
        [Min(0)]
        private long sharedAtlasByteBudgetPerArray = 0;

        /// <summary>
        /// Max resident bytes per shared glyph-atlas array before eviction. 0 = unbounded. The byte
        /// equivalent of <see cref="SharedAtlasPageBudget"/>; see <see cref="GlyphAtlasArray.ByteBudget"/>.
        /// NOTE: CURRENTLY IGNORED by the runtime (see <see cref="SharedGlyphAtlas"/> /
        /// Documentation/Design/MemoryBudgets.md); the accessor still returns the configured value.
        /// </summary>
        public static long SharedAtlasByteBudgetPerArray => Instance != null ? Instance.sharedAtlasByteBudgetPerArray : 0;

        [SerializeField]
        [Tooltip("NOT ACTIVE YET — currently IGNORED by the runtime (see " +
                 "Documentation/Design/MemoryBudgets.md). This global ceiling has no runtime enforcer " +
                 "at all; the atlas runs unbounded regardless of this value. Kept so existing assets " +
                 "load and for when budgets are properly wired.\n\n" +
                 "Intended meaning: max RESIDENT bytes across ALL shared glyph-atlas arrays combined " +
                 "before global LRU eviction. 0 (default) = unbounded. A process-wide ceiling on " +
                 "glyph memory, evaluated after each frame's atlas growth; when exceeded, the " +
                 "globally least-recently-used unreferenced glyphs are evicted across every array " +
                 "until the total is back under budget. Use this to bound total glyph memory on " +
                 "devices with a hard budget (Quest 3S), independent of how many fonts/variations " +
                 "are in play.")]
        [Min(0)]
        private long sharedAtlasByteBudgetGlobal = 0;

        /// <summary>
        /// Max resident bytes across all shared glyph-atlas arrays before global eviction. 0 =
        /// unbounded. See <see cref="SharedGlyphAtlas.EnforceGlobalByteBudget"/>.
        /// NOTE: CURRENTLY IGNORED by the runtime — the global budget has no active enforcer
        /// (see Documentation/Design/MemoryBudgets.md); the accessor still returns the configured value.
        /// </summary>
        public static long SharedAtlasByteBudgetGlobal => Instance != null ? Instance.sharedAtlasByteBudgetGlobal : 0;

        [SerializeField]
        [Tooltip("OPT-IN (default OFF). When ON, a UniText component skips the Unity mesh re-upload on " +
                 "a rebuild whose generated geometry is byte-identical to what its renderers already " +
                 "display (same vertices/UVs/colours). Behaviour-preserving: identical geometry draws " +
                 "identically. NOT free: the exact-geometry fingerprint costs ~25.5 ms per 100 objects " +
                 "x 2,405 chars, about +17% on every Windows full rebuild (148 ms) and more on Quest. " +
                 "It only pays off when text changes often WITHOUT changing rendered output (e.g. " +
                 "trailing-whitespace alternation, reassigning equivalent text); for general text it is " +
                 "a net cost. Leave OFF unless your workload is dominated by no-op geometry rebuilds.")]
        private bool skipUnchangedGeometryUpload = false;

        /// <summary>
        /// Opt-in (default false). When true, a component skips the Unity mesh re-upload when a rebuild
        /// produces geometry identical to what is already displayed. See UniText.DoApplyMesh.
        /// Behaviour-preserving, but the exact fingerprint adds ~17% to a Windows full rebuild, so it
        /// only pays off on workloads dominated by no-op geometry rebuilds.
        /// </summary>
        public static bool SkipUnchangedGeometryUpload => Instance != null && Instance.skipUnchangedGeometryUpload;

        [SerializeField]
        [Tooltip("Render Architecture R2: when ON, each UniText component draws through a SINGLE " +
                 "CanvasRenderer per draw group (at most two: SDF/coverage + MSDF/color) using the " +
                 "UniText/Uber shader, a shared Texture2DArray atlas and a float-texture style table, " +
                 "instead of one child CanvasRenderer per font/atlas/pass. ON is the DEFAULT. OFF " +
                 "keeps the legacy per-segment renderer path, byte-for-byte unchanged. A component " +
                 "may override this per-instance (UnifiedRenderer = ForceOff).")]
        private bool useUnifiedRenderer = true;

        /// <summary>
        /// Project-wide default for the Render-Architecture R2 unified single-renderer path. True
        /// (the default) draws each component through one CanvasRenderer per draw group; false keeps
        /// the legacy per-segment CanvasRenderer path unchanged. A <c>UniText</c> component may
        /// override this per instance via <c>UnifiedRenderer</c> (UseProjectSetting / ForceOn / ForceOff).
        /// </summary>
        public static bool UseUnifiedRenderer => Instance != null && Instance.useUnifiedRenderer;

        /// <summary>TEST ONLY: forces the project-wide unified-renderer default on the current instance.</summary>
        internal static void SetUseUnifiedRendererForTests(bool value)
        {
            if (Instance != null) { Instance.useUnifiedRenderer = value; Changed?.Invoke(); }
        }

        /// <summary>TEST ONLY: forces the geometry-skip default on the current instance (it is OFF by
        /// default, so a test that needs the skip to fire must enable it explicitly).</summary>
        internal static void SetSkipUnchangedGeometryUploadForTests(bool value)
        {
            if (Instance != null) { Instance.skipUnchangedGeometryUpload = value; Changed?.Invoke(); }
        }

        public static event Action Changed;

        // Serialized in players too: a component with no font stack / appearance of its own (created
        // from code via AddComponent, or a prefab/scene saved before the editor filled the field)
        // falls back to these at runtime. Editor-only, they were stripped from builds and every such
        // component threw ArgumentNullException(fontStack) each frame and rendered nothing on device.
        [Header("Defaults")]
        [SerializeField]
        [Tooltip("Default fonts for UniText components that have none assigned (editor and player).")]
        private UniTextFontStack defaultFontStack;

        [SerializeField]
        [Tooltip("Default appearance for UniText components that have none assigned (editor and player).")]
        private UniTextAppearance defaultAppearance;

        /// <summary>Default fonts for UniText components that have none assigned.</summary>
        public static UniTextFontStack DefaultFontStack => Instance?.defaultFontStack;

        /// <summary>Default appearance for UniText components that have none assigned.</summary>
        public static UniTextAppearance DefaultAppearance => Instance?.defaultAppearance;

    #if UNITY_EDITOR
        [SerializeField]
        [Tooltip("Render Architecture R2 sub-task 3: when ON, imported/changed prefabs and scenes are " +
                 "AUTO-migrated from legacy appearance/material settings to component styles via " +
                 "Tools/OpenGlyph/Migrate Appearance to Styles. OFF (default) — migration is manual only. " +
                 "Leave OFF unless you want every asset import to rewrite UniText components.")]
        private bool autoMigrateAppearanceOnImport = false;

        /// <summary>
        /// Render-Architecture R2 sub-task 3: project-wide opt-in for auto-migrating appearance/material
        /// settings to component styles on asset import. False (default) = manual migration only.
        /// </summary>
        public static bool AutoMigrateAppearanceOnImport => Instance != null && Instance.autoMigrateAppearanceOnImport;
    #endif

        /// <summary>Gets the compiled Unicode data asset, loaded from Resources.</summary>
        internal static TextAsset UnicodeDataAsset
        {
            get
            {
                if (cachedUnicodeData == null)
                {
                    cachedUnicodeData = Resources.Load<TextAsset>(UnicodeDataPath);
                    if (cachedUnicodeData == null)
                        Debug.LogError($"UnicodeData not found at Resources/{UnicodeDataPath}.bytes");
                }
                return cachedUnicodeData;
            }
        }

        private static UniTextSettings instance;

        /// <summary>True once the loud "missing settings asset" error has been emitted, so it fires exactly once.</summary>
        private static bool loggedMissingInstance;

        /// <summary>
        /// True when <see cref="Instance"/> is serving a runtime-created default because no
        /// <c>Resources/UniTextSettings.asset</c> was found. Exposed for diagnostics and tests.
        /// </summary>
        internal static bool IsUsingRuntimeDefault { get; private set; }

        /// <summary>Returns true if the instance is already loaded (without triggering load).</summary>
        internal static bool IsNull => instance == null;

        /// <summary>Gets the singleton settings instance, loading from Resources if needed.</summary>
        /// <remarks>
        /// Resolution order: (1) a project-authored <c>Resources/UniTextSettings.asset</c> — if present it
        /// always wins; (2) otherwise a runtime-created default instance with safe built-in values, so that
        /// every static accessor (Gradients, SharedAtlasPageBudget, UseUnifiedRenderer, …) keeps working
        /// instead of throwing a NullReferenceException. A project that never created the asset therefore
        /// still renders text with default configuration. A single, clear <see cref="Debug.LogError"/> names
        /// the missing asset and how to create it — emitted once, never silently.
        /// </remarks>
        public static UniTextSettings Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = Resources.Load<UniTextSettings>(ResourcePath);

                    if (instance == null)
                    {
                        // No project-authored asset. Fall back to a runtime default so the engine stays
                        // functional (default gradients/renderer/atlas config) rather than NRE-ing or,
                        // worse, rendering nothing. Emit ONE loud error naming the asset and the remedy.
                        if (!loggedMissingInstance)
                        {
                            loggedMissingInstance = true;
                            Debug.LogError(
                                $"[OpenGlyph] No UniTextSettings asset found at Resources/{ResourcePath}.asset. " +
                                "Falling back to built-in default settings so text still renders. For project-" +
                                "specific configuration (named gradients, atlas page budget, unified renderer " +
                                "default), create one via Assets > Create > UniText > Settings and place it in a " +
                                "Resources folder; the editor build check (UniTextBuildProcessor) also offers to " +
                                "create it before a build ships.");
                        }

                        instance = CreateInstance<UniTextSettings>();
                        instance.name = "UniTextSettings (runtime default)";
                        instance.hideFlags = HideFlags.HideAndDontSave;
                        IsUsingRuntimeDefault = true;
                    }
                }

                return instance;
            }
        }

        /// <summary>Manually sets the singleton instance (used for testing or custom initialization).</summary>
        /// <param name="settings">The settings instance to use.</param>
        public static void SetInstance(UniTextSettings settings)
        {
            instance = settings;
            IsUsingRuntimeDefault = false;
            Changed?.Invoke();
        }

        internal void InvokeChanged() => Changed?.Invoke();
        
#if UNITY_EDITOR
        private void OnValidate()
        {
            Changed?.Invoke();
        }
#endif
    }
}
#pragma warning restore 618
