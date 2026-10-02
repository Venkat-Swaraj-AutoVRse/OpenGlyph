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
        [Tooltip("Max pages (Texture2DArray slices) PER shared glyph-atlas array before LRU eviction " +
                 "begins reusing pages. 0 (default) = unbounded: the array grows and never evicts, so " +
                 "behaviour is identical to the legacy per-font atlas until a budget is configured. " +
                 "Set a positive cap to bound glyph memory under variable fonts; least-recently-used, " +
                 "unreferenced glyphs are then evicted and re-rasterized on demand.")]
        [Min(0)]
        private int sharedAtlasPageBudget = 0;

        /// <summary>
        /// Max pages per shared glyph-atlas array before eviction. 0 = unbounded (no eviction;
        /// unchanged legacy behaviour). See <see cref="GlyphAtlasArray.PageBudget"/>.
        /// </summary>
        public static int SharedAtlasPageBudget => Instance != null ? Instance.sharedAtlasPageBudget : 0;

        [SerializeField]
        [Tooltip("Render Architecture R2: when ON, each UniText component draws through a SINGLE " +
                 "CanvasRenderer per draw group (at most two: SDF/coverage + MSDF/color) using the " +
                 "UniText/Uber shader, a shared Texture2DArray atlas and a float-texture style table, " +
                 "instead of one child CanvasRenderer per font/atlas/pass. OFF (default) keeps the " +
                 "legacy per-segment renderer path, byte-for-byte unchanged. A component may override " +
                 "this per-instance.")]
        private bool useUnifiedRenderer = false;

        /// <summary>
        /// Project-wide default for the Render-Architecture R2 unified single-renderer path. False
        /// (default) keeps the legacy per-segment CanvasRenderer path unchanged. A
        /// <c>UniText</c> component may override this per instance.
        /// </summary>
        public static bool UseUnifiedRenderer => Instance != null && Instance.useUnifiedRenderer;

        /// <summary>TEST ONLY: forces the project-wide unified-renderer default on the current instance.</summary>
        internal static void SetUseUnifiedRendererForTests(bool value)
        {
            if (Instance != null) { Instance.useUnifiedRenderer = value; Changed?.Invoke(); }
        }

        public static event Action Changed;

    #if UNITY_EDITOR
        [Header("Editor Defaults")]
        [SerializeField]
        [Tooltip("Default fonts assigned to new UniText components.")]
        private UniTextFontStack defaultFontStack;

        [SerializeField]
        [Tooltip("Default appearance assigned to new UniText components.")]
        private UniTextAppearance defaultAppearance;

        /// <summary>Gets the default fonts for new UniText components (Editor only).</summary>
        public static UniTextFontStack DefaultFontStack => Instance?.defaultFontStack;

        /// <summary>Gets the default appearance for new UniText components (Editor only).</summary>
        public static UniTextAppearance DefaultAppearance => Instance?.defaultAppearance;

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
