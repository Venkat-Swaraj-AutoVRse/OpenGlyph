using System;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

// R2 sub-task 4: the provider still exposes the [Obsolete] UniTextAppearance + calls GetMaterials as
// the runtime bridge feeding the shim during the deprecation window. Suppress 618 for this file.
#pragma warning disable 618

namespace LightSide
{
    /// <summary>
    /// Manages font assets and provides font lookup services for text rendering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The font provider handles:
    /// <list type="bullet">
    /// <item>Main font and fallback font resolution</item>
    /// <item>Font scaling based on requested font size</item>
    /// <item>Glyph caching in texture atlases</item>
    /// <item>Material management for different fonts</item>
    /// </list>
    /// </para>
    /// <para>
    /// Uses <see cref="SharedFontCache"/> for fast codepoint-to-font mapping.
    /// </para>
    /// </remarks>
    /// <seealso cref="UniTextFont"/>
    /// <seealso cref="UniTextFontStack"/>
    public sealed class UniTextFontProvider
    {
        private readonly FastIntDictionary<UniTextFont> fontAssets = new();

        private UniTextFontStack fontStackAsset;
        private UniTextFont mainFont;
        private int mainFontId;

        private float fontSize = 36f;
        private float fontScale = 1f;

        [ThreadStatic] private static HashSet<int> searchedFontAssets;

        /// <summary>Gets or sets the current font size in points.</summary>
        public float FontSize
        {
            get => fontSize;
            set
            {
                fontSize = value;
                UpdateFontScale();
            }
        }

        /// <summary>Sets the font size in points.</summary>
        /// <param name="size">Font size in points.</param>
        public void SetFontSize(float size)
        {
            FontSize = size;
        }

        /// <summary>Gets the main (primary) font asset.</summary>
        public UniTextFont MainFont => mainFont;
        /// <summary>Gets the unique identifier for the main font.</summary>
        public int MainFontId => mainFontId;

        /// <summary>Gets the appearance settings for font materials.</summary>
        public UniTextAppearance Appearance { get; set; }


        /// <summary>
        /// Initializes the font provider with the specified fonts and appearance.
        /// </summary>
        /// <param name="fontStack">Font collection containing main and fallback fonts.</param>
        /// <param name="appearance">Appearance settings for materials.</param>
        /// <param name="fontSize">Initial font size in points.</param>
        public UniTextFontProvider(UniTextFontStack fontStack, UniTextAppearance appearance, float fontSize = 36f)
        {
            if (fontStack == null || fontStack.MainFont == null)
                throw new ArgumentNullException(nameof(fontStack));

            fontStackAsset = fontStack;
            Appearance = appearance;
            mainFont = fontStack.MainFont;
            this.fontSize = fontSize;

            mainFontId = GetFontId(mainFont);
            RegisterFontAsset(mainFontId, mainFont);
            UpdateFontScale();

            Cat.MeowFormat("[FontProvider] Created: mainFont={0} (id={1}), fallbacks={2}",
                mainFont.CachedName, mainFontId, fontStack.fonts.Count - 1);
            for (int i = 0; i < fontStack.fonts.Count; i++)
            {
                var font = fontStack.fonts[i];
                var t = font.CharacterLookupTable;
                font.GetCachedInstanceId();
                Cat.MeowFormat("[FontProvider]   [{0}] {1} (id={2})", i, font.CachedName, GetFontId(font));
            }
        }

        private void UpdateFontScale()
        {
            fontScale = fontSize * mainFont.FontScale / mainFont.UnitsPerEm;
        }

        /// <summary>
        /// Gets the unique font identifier for a font asset.
        /// </summary>
        /// <param name="font">The font asset.</param>
        /// <returns>Font ID based on font data hash, or 0 if null.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetFontId(UniTextFont font)
        {
            if (font == null) return 0;
            if (font is EmojiFont) return EmojiFont.FontId;
            return font.FontDataHash;
        }

        /// <summary>
        /// Registers a font asset with the provider.
        /// </summary>
        /// <param name="fontId">Unique font identifier.</param>
        /// <param name="font">Font asset to register.</param>
        public void RegisterFontAsset(int fontId, UniTextFont font)
        {
            if (font == null || fontId == 0) return;
            fontAssets[fontId] = font;
        }

        /// <summary>
        /// Gets a font asset by its identifier.
        /// </summary>
        /// <param name="fontId">The font identifier.</param>
        /// <returns>The font asset, or main font if not found.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public UniTextFont GetFontAsset(int fontId)
        {
            if (fontId == mainFontId)
                return mainFont;

            if (fontId == EmojiFont.FontId)
                return EmojiFont.Instance;

            if (fontAssets.TryGetValue(fontId, out var asset))
                return asset;

            return mainFont;
        }

        /// <summary>
        /// Gets line metrics scaled to the specified font size.
        /// </summary>
        /// <param name="size">Target font size in points.</param>
        /// <param name="ascender">Output: distance from baseline to top of tallest glyph.</param>
        /// <param name="descender">Output: distance from baseline to bottom (typically negative).</param>
        /// <param name="lineHeight">Output: total line height.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetLineMetrics(float size, out float ascender, out float descender, out float lineHeight)
        {
            var faceInfo = mainFont.FaceInfo;
            var scale = size * mainFont.FontScale / mainFont.UnitsPerEm;
            ascender = faceInfo.ascentLine * scale;
            descender = faceInfo.descentLine * scale;
            lineHeight = faceInfo.lineHeight * scale;

            if (lineHeight <= 0)
                lineHeight = (ascender - descender) * 1.2f;
        }

        /// <summary>
        /// Gets the cap height (top of capital letters) scaled to the specified font size.
        /// </summary>
        /// <param name="size">Target font size in points.</param>
        /// <returns>Scaled cap height, or 0 if unavailable.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetCapHeight(float size)
        {
            var faceInfo = mainFont.FaceInfo;
            if (faceInfo.capLine <= 0) return 0f;
            return faceInfo.capLine * (size * mainFont.FontScale / mainFont.UnitsPerEm);
        }

        /// <summary>
        /// Calculates total text height given line metrics and line count.
        /// </summary>
        /// <param name="ascender">Ascender value from font metrics.</param>
        /// <param name="descender">Descender value from font metrics.</param>
        /// <param name="lineCount">Number of lines.</param>
        /// <param name="lineHeight">Line height from font metrics.</param>
        /// <param name="lineSpacing">Additional spacing between lines.</param>
        /// <returns>Total text height in pixels.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CalculateTextHeight(float ascender, float descender, int lineCount, float lineHeight,
            float lineSpacing = 0f)
        {
            return ascender - descender + (lineCount - 1) * (lineHeight + lineSpacing);
        }

        /// <summary>
        /// Finds the best font to render a codepoint, using fallback chain if needed.
        /// </summary>
        /// <param name="codepoint">Unicode codepoint to find a font for.</param>
        /// <returns>Font ID of the font that can render this codepoint.</returns>
        public int FindFontForCodepoint(int codepoint)
        {
            if (SharedFontCache.TryGet(codepoint, mainFontId, out var cachedFontId))
            {
                if (cachedFontId == mainFontId || fontAssets.ContainsKey(cachedFontId))
                    return cachedFontId;
                Cat.MeowWarnFormat("[FontProvider] Cache hit but fontAssets miss: cp=U+{0:X4}, cachedFontId={1}",
                    codepoint, cachedFontId);
            }

            searchedFontAssets ??= new HashSet<int>();
            searchedFontAssets.Clear();

            var unicode = (uint)codepoint;
            var foundFont = fontStackAsset?.FindFontForCodepoint(unicode, searchedFontAssets);

            if (foundFont == null)
                return mainFontId;

            var fontId = GetFontId(foundFont);
            if (!fontAssets.ContainsKey(fontId))
            {
                RegisterFontAsset(fontId, foundFont);
                Cat.MeowFormat("[FontProvider] Fallback font registered: {0}", foundFont.CachedName);
            }

            SharedFontCache.Set(codepoint, mainFontId, fontId);
            return fontId;
        }

        /// <summary>
        /// Resolves the font id for a styled run (weight/width/style + <c>&lt;b&gt;</c>/<c>&lt;i&gt;</c>
        /// markup) via the stack's <see cref="UniTextFontStack.ResolveStyledFont"/>. Registers the
        /// resolved face so <see cref="GetFontAsset"/>/<see cref="GetMaterials"/> can find it, and
        /// reports whether synthetic bold/italic is still required. Returns the main font id when no
        /// stack/family is available (unchanged legacy behaviour).
        /// </summary>
        public int ResolveStyledFontId(FontStyleSpec spec, bool markupBold, bool markupItalic,
            out bool realBold, out bool realItalic)
        {
            realBold = false;
            realItalic = false;

            if (fontStackAsset == null)
                return mainFontId;

            var face = fontStackAsset.ResolveStyledFont(spec, markupBold, markupItalic, out var how);
            if (face == null)
                return mainFontId;

            // A real styled face was chosen when synthesis is NOT needed for the requested axis.
            bool wantsBold = markupBold || spec.weight >= FontStyleSpec.BoldWeight;
            bool wantsItalic = markupItalic || spec.style != StyleAxis.Normal;
            realBold = wantsBold && !how.synthesizeBold;
            realItalic = wantsItalic && !how.synthesizeItalic;

            var fontId = GetFontId(face);
            if (!fontAssets.ContainsKey(fontId))
                RegisterFontAsset(fontId, face);
            return fontId;
        }

        // Per-face variation mapper cache (fvar read once per face).
        private readonly FastIntDictionary<VariationMapper> variationMappers = new();

        private VariationMapper GetVariationMapper(int fontId)
        {
            if (variationMappers.TryGetValue(fontId, out var m))
                return m;

            var font = GetFontAsset(fontId);
            m = null;
            if (font != null && font.HasFontData && FT.IsInitialized)
            {
                var face = FT.LoadFace(font.FontData, 0);
                if (face != IntPtr.Zero)
                {
                    try { m = VariationMapper.Read(face); }
                    finally { FT.UnloadFace(face); }
                }
            }
            variationMappers[fontId] = m;
            return m;
        }

        /// <summary>
        /// Computes the <see cref="VariationKey"/> for a styled run on <paramref name="fontId"/>.
        /// Returns <see cref="VariationKey.None"/> for a static (non-variable) face. When
        /// <paramref name="opticalSize"/> &gt; 0 the font's <c>opsz</c> axis is auto-driven from it.
        /// </summary>
        public VariationKey GetVariationKeyForFont(int fontId, FontStyleSpec spec, float opticalSize)
        {
            var m = GetVariationMapper(fontId);
            if (m == null || !m.isVariable)
                return VariationKey.None;
            return m.Map(spec, opticalSize, out _, out _);
        }

        /// <summary>
        /// Resolves the full variation coordinate vector (tags + design coords) for a styled run on a
        /// variable face, for passing to the shaper / FreeType. Returns null tags for a static face.
        /// </summary>
        public VariationKey GetVariationCoords(int fontId, FontStyleSpec spec, float opticalSize,
            out uint[] tags, out float[] coords)
        {
            tags = null; coords = null;
            var m = GetVariationMapper(fontId);
            if (m == null || !m.isVariable)
                return VariationKey.None;
            return m.Map(spec, opticalSize, out tags, out coords);
        }

        /// <summary>
        /// Gets all materials for rendering a specific font.
        /// </summary>
        /// <param name="fontId">Font identifier.</param>
        /// <returns>Materials array. Single for normal, two for 2-pass (outline + face).</returns>
        public Material[] GetMaterials(int fontId)
        {
            // A component whose legacy appearance was cleared (e.g. by the migration tool's
            // "Clear the legacy appearance reference" option) has Appearance == null. Fall back to
            // the project default appearance; with none, return null, which the mesh generator
            // already treats as "no materials" (the unified path never reads these).
            // UniTextSettings.DefaultAppearance is editor-only, so players get no fallback.
            var appearance = Appearance;
#if UNITY_EDITOR
            if (appearance == null) appearance = UniTextSettings.DefaultAppearance;
#endif
            if (appearance == null) return null;
            return appearance.GetMaterials(GetFontAsset(fontId));
        }

        /// <summary>
        /// Gets the raw font data (TTF/OTF bytes) for a font.
        /// </summary>
        /// <param name="fontId">Font identifier.</param>
        /// <returns>Font file data, or null if not available.</returns>
        public byte[] GetFontData(int fontId)
        {
            return GetFontAsset(fontId)?.FontData;
        }

    }

}
#pragma warning restore 618
