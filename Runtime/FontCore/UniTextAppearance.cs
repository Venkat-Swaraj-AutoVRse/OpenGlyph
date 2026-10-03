using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// ScriptableObject that maps fonts to their rendering materials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allows different fonts to use different materials (e.g., for different
    /// SDF shaders, colors, or effects). The default material is used when
    /// no specific material is assigned to a font.
    /// </para>
    /// <para>
    /// Create via Assets menu: Create → UniText → Appearance
    /// </para>
    /// </remarks>
    /// <seealso cref="UniTextFontProvider"/>
    [Obsolete(UniTextAppearance.DeprecationMessage, false)]
    [CreateAssetMenu(fileName = "UniTextAppearance", menuName = "UniText/Appearance")]
    public class UniTextAppearance : ScriptableObject, ISerializationCallbackReceiver
    {
        /// <summary>
        /// Render-Architecture R2 sub-task 4: the deprecation notice. UniTextAppearance + its material
        /// API are superseded by the component UniTextStyle (and per-span style markup). It is a WARNING
        /// (error=false) so existing assets still compile, load, and render through the shim during the
        /// deprecation window; removal is a later phase.
        /// </summary>
        public const string DeprecationMessage =
            "UniTextAppearance and the material API are deprecated; style is now on the UniText " +
            "component (UniTextStyle) + per-span markup. Migrate with Tools/OpenGlyph/Migrate Appearance " +
            "to Styles. Old assets still load and render through the compatibility shim during the " +
            "deprecation window.";

        /// <summary>
        /// Associates a font with materials for rendering.
        /// </summary>
        /// <remarks>
        /// For single-pass rendering, use only materials[0].
        /// For 2-pass rendering (outline first, then face), use materials[0] for outline and materials[1] for face.
        /// </remarks>
        [Serializable]
        internal struct FontMaterialPair
        {
            /// <summary>The font asset.</summary>
            public UniTextFont font;
            /// <summary>Materials for this font. Single material for normal rendering, two for 2-pass (outline + face).</summary>
            public StyledList<Material> materials;
        }

        [SerializeField]
        [Tooltip("Default materials used when no font-specific material is assigned. Single for normal, two for 2-pass.")]
        private StyledList<Material> defaultMaterials;

        [SerializeField]
        [Tooltip("Font-specific material overrides.")]
        private StyledList<FontMaterialPair> fontMaterials;

        private Material[] defaultMaterialsArr;
        private Dictionary<int, Material[]> materialsByFontId;
        private static Material[] emojiMaterials;

        /// <summary>
        /// Sets the default materials used when no font-specific override applies (single material for
        /// normal rendering, two for 2-pass outline+face). Rebuilds the lookup immediately. Enables
        /// configuring outline/underlay/glow at runtime without an authored asset.
        /// </summary>
        public void SetDefaultMaterials(params Material[] materials)
        {
            defaultMaterials ??= new StyledList<Material>();
            defaultMaterials.Clear();
            if (materials != null)
                foreach (var m in materials) defaultMaterials.Add(m);
            RebuildLookup();
        }

        private void OnEnable()
        {
            RebuildLookup();
        }

        /// <summary>
        /// Raised when the appearance changes: from the Inspector (editor) or when runtime code calls
        /// <see cref="NotifyChanged"/>. Subscribed components rebuild. Available in players too.
        /// </summary>
        public event Action Changed;

        /// <summary>Signals that this appearance (or its materials) was modified at runtime, so components using it refresh.</summary>
        public void NotifyChanged()
        {
            RebuildLookup();
            Changed?.Invoke();
        }

    #if UNITY_EDITOR
        // OnValidate does not exist in players; runtime code uses NotifyChanged().
        private void OnValidate()
        {
            NotifyChanged();
        }
    #endif

        private void RebuildLookup()
        {
            var newDict = new Dictionary<int, Material[]>();

            if (fontMaterials != null)
            {
                for (var i = 0; i < fontMaterials.Length; i++)
                {
                    var pair = fontMaterials[i];
                    if (pair.font != null && pair.materials != null && pair.materials.Length > 0)
                        newDict[pair.font.GetCachedInstanceId()] = pair.materials.ToArray();
                }
            }

            materialsByFontId = newDict;
            defaultMaterialsArr = defaultMaterials != null ? defaultMaterials.ToArray() : Array.Empty<Material>();
        }

        /// <summary>
        /// Gets all materials for rendering the specified font.
        /// </summary>
        /// <param name="font">The font to get materials for.</param>
        /// <returns>Font-specific materials array, or default materials. For 2-pass: [0]=outline, [1]=face.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Material[] GetMaterials(UniTextFont font)
        {
            // Threading tripwire: this can Shader.Find / new Material / write the plain msdfVariantCache
            // Dictionary, all of which are main-thread-only / not thread-safe. In parallel mesh
            // generation the materials are resolved up front on the main-thread prepare step and
            // handed to workers; reaching here off the main thread means that prepare was bypassed.
            UniTextThreadGuard.AssertMainThread("UniTextAppearance.GetMaterials");

            if (font is EmojiFont)
            {
                emojiMaterials ??= new[] { EmojiFont.Material };
                return emojiMaterials;
            }

            var mats = materialsByFontId.TryGetValue(font.GetCachedInstanceId(), out var m) ? m : defaultMaterialsArr;

            // Phase 2 fix: an MSDF-mode font MUST be drawn with an MSDF shader. The default/assigned
            // materials carry an SDF shader; sampling the RGB24 MSDF atlas with an SDF (alpha) shader
            // renders solid blocks. When the chosen material is not already MSDF, substitute an
            // MSDF-shader clone (created once, cached), so an MSDF font works on the default appearance.
            if (mats != null && mats.Length > 0 && font.AtlasRenderMode == UniTextRenderMode.Msdf
                && mats[0] != null && !IsMsdfShader(mats[0].shader))
                mats = GetOrCreateMsdfVariant(mats);

            return mats;
        }

        /// <summary>
        /// Render-Architecture R2 sub-task 3: the appearance's DEFAULT material array (the materials
        /// used when a font has no override). Used by the migration tool to synthesise a component style
        /// when no font is assigned on the component. Returns an empty array when none are set.
        /// </summary>
        public Material[] GetDefaultMaterials() => defaultMaterialsArr ?? Array.Empty<Material>();

        [NonSerialized] private Dictionary<Material[], Material[]> msdfVariantCache;
        [NonSerialized] private Shader cachedMsdfShader;

        private static bool IsMsdfShader(Shader s) =>
            s != null && s.name != null && s.name.IndexOf("MSDF", System.StringComparison.OrdinalIgnoreCase) >= 0;

        // Clones the given materials with their shader swapped to the matching MSDF variant, preserving
        // the texture/color/property state. Cached per source array so we build each variant once.
        private Material[] GetOrCreateMsdfVariant(Material[] source)
        {
            msdfVariantCache ??= new Dictionary<Material[], Material[]>();
            if (msdfVariantCache.TryGetValue(source, out var cached)) return cached;

            cachedMsdfShader ??= Shader.Find("UniText/MSDF SSD")
                                 ?? Shader.Find("UniText/MSDF") ?? Shader.Find("UniText/MSDF Overlay");
            if (cachedMsdfShader == null)
            {
                // No MSDF shader available (stripped build): leave as-is rather than render wrong.
                msdfVariantCache[source] = source;
                return source;
            }

            var variant = new Material[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == null) { variant[i] = null; continue; }
                var mv = new Material(source[i]) { name = source[i].name + " (MSDF)" };
                mv.shader = cachedMsdfShader;
                variant[i] = mv;
            }
            msdfVariantCache[source] = variant;
            return variant;
        }

        private Dictionary<int, float> cachedPropertyDeltas;
        private int cachedDeltaFrame = -1;

        /// <summary>
        /// Caches the delta of two float shader properties for all materials in this appearance.
        /// Must be called from the main thread. Caches once per frame (subsequent calls are no-ops).
        /// </summary>
        /// <param name="propertyIdA">First shader property ID.</param>
        /// <param name="propertyIdB">Second shader property ID.</param>
        internal void CachePropertyDelta(int propertyIdA, int propertyIdB)
        {
            var frame = Time.frameCount;
            if (cachedDeltaFrame == frame)
                return;

            cachedDeltaFrame = frame;
            cachedPropertyDeltas ??= new Dictionary<int, float>(8);
            cachedPropertyDeltas.Clear();

            CacheArrayDelta(defaultMaterialsArr, cachedPropertyDeltas, propertyIdA, propertyIdB);
            if (materialsByFontId != null)
                foreach (var kvp in materialsByFontId)
                    CacheArrayDelta(kvp.Value, cachedPropertyDeltas, propertyIdA, propertyIdB);
        }

        /// <summary>
        /// Gets a previously cached property delta for a material.
        /// Thread-safe for reading after <see cref="CachePropertyDelta"/> completes on the main thread.
        /// </summary>
        /// <param name="materialIdentityHash">The material's identity hash from <see cref="RuntimeHelpers.GetHashCode"/>.</param>
        /// <returns>The cached delta, or 0 if not found.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float GetCachedPropertyDelta(int materialIdentityHash)
        {
            return cachedPropertyDeltas != null &&
                   cachedPropertyDeltas.TryGetValue(materialIdentityHash, out var delta)
                ? delta
                : 0f;
        }

        private static void CacheArrayDelta(Material[] mats, Dictionary<int, float> cache, int propA, int propB)
        {
            if (mats == null) return;
            for (var i = 0; i < mats.Length; i++)
            {
                var mat = mats[i];
                if (mat == null) continue;
                var id = RuntimeHelpers.GetHashCode(mat);
                if (cache.ContainsKey(id)) continue;
                cache[id] = mat.GetFloat(propA) - mat.GetFloat(propB);
            }
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            materialsByFontId = null;
        }
    }
}
