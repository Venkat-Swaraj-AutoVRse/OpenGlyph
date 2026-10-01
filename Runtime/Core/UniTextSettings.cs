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
