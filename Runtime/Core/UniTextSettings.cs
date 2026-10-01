using System;
using UnityEngine;

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

        /// <summary>Returns true if the instance is already loaded (without triggering load).</summary>
        internal static bool IsNull => instance == null;

        /// <summary>Gets the singleton settings instance, loading from Resources if needed.</summary>
        public static UniTextSettings Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = Resources.Load<UniTextSettings>(ResourcePath);

                    if (instance == null)
                        Debug.LogError(
                            $"UniTextSettings not found at Resources/{ResourcePath}.asset. " +
                            "Create it via Assets > Create > UniText > Settings and place in Resources folder.");
                }

                return instance;
            }
        }

        /// <summary>Manually sets the singleton instance (used for testing or custom initialization).</summary>
        /// <param name="settings">The settings instance to use.</param>
        public static void SetInstance(UniTextSettings settings)
        {
            instance = settings;
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
