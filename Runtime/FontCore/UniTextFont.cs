using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Font asset containing glyph data, metrics, and texture atlases for text rendering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UniTextFont is a ScriptableObject that stores:
    /// <list type="bullet">
    /// <item>Font file data (TTF/OTF bytes) for FreeType rendering</item>
    /// <item>Face information (metrics, ascender, descender)</item>
    /// <item>Glyph table with UV coordinates and metrics</item>
    /// <item>SDF texture atlas(es) for rendering</item>
    /// </list>
    /// </para>
    /// <para>
    /// Glyphs are rendered to the atlas at runtime when first needed.
    /// </para>
    /// </remarks>
    /// <seealso cref="UniTextFontProvider"/>
    /// <seealso cref="UniTextFontStack"/>
    [Serializable]
    public class UniTextFont : ScriptableObject
    {

        #region Serialized Fields

        [SerializeField]
        [Tooltip("Raw font file data (TTF/OTF bytes).")]
        protected byte[] fontData;

        [SerializeField]
        [Tooltip("Hash of font data for identification.")]
        protected int fontDataHash;

        [SerializeField]
        [Tooltip("Path to source font file (Editor only).")]
        private string sourceFontFilePath;

        [SerializeField]
        [Tooltip("Italic slant angle in degrees.")]
        private float italicStyle = 30;

        [SerializeField]
        [Tooltip("Font face metrics (ascender, descender, line height, etc.).")]
        internal FaceInfo faceInfo;

        [SerializeField]
        [Tooltip("Font design units per em (typically 1000 or 2048).")]
        internal int unitsPerEm = 1000;

        [SerializeField]
        [Tooltip("Visual scale multiplier for this font. Use to normalize fonts that appear too small or too large by design (e.g. Dongle). Applied after all metric conversions.")]
        [Range(0.1f, 5f)]
        internal float fontScale = 1f;

        [NonSerialized]
        internal List<Glyph> glyphTable = new();

        [NonSerialized]
        internal List<UniTextCharacter> characterTable = new();

        [NonSerialized]
        internal List<Texture2D> atlasTextures = new();

        [SerializeField]
        [Tooltip("Atlas texture size in pixels (square).")]
        internal int atlasSize = 1024;

        [SerializeField]
        [Tooltip("SDF spread as a fraction of point size (0-1). Padding = PointSize * SpreadStrength. " +
                 "Default 0.10 matches the display shader's REFERENCE_SPREAD_RATIO (and TMP's ratio) for " +
                 "crisp edges under magnification; raise it only when very wide outline/underlay/glow " +
                 "effects need more encoded distance (a wider spread softens edges and costs atlas space).")]
        [Range(0.1f, 1f)]
        // 0.10 (was 0.25): a spread ratio of 0.256 is 2.5x the shader's reference ratio and TMP's,
        // which widens the edge AA band ~73% and reads soft/wavy at 4-5x magnification on an 8-bit
        // Alpha8 atlas. See Documentation/Design/SDF-EdgeQuality-Investigation.md.
        internal float spreadStrength = 0.1f;

        [SerializeField]
        [Tooltip("Glyph rendering mode (SDF, bitmap, etc.).")]
        internal UniTextRenderMode atlasRenderMode = UniTextRenderMode.SDF;

        [SerializeField]
        [Tooltip("Pixel-perfect rendering: render Mono at the font's native pixel size (or an integer multiple), point-filter the atlas with no mipmaps, and snap glyph quads to the device pixel grid. Intended for pixel/bitmap fonts. Has no effect on SDF/MSDF/Smooth atlases.")]
        internal bool pixelPerfect = false;

        [SerializeField]
        [Tooltip("Cached result of pixel-font analysis (outline grid + bitmap strikes). Populated by DetectPixelFont(); serialized so it survives without re-probing the native outline export.")]
        internal PixelFontInfo pixelFontInfo;

        [SerializeField]
        [Tooltip("True once DetectPixelFont() has run and pixelFontInfo is authoritative.")]
        internal bool pixelFontInfoResolved = false;

        [NonSerialized]
        protected List<GlyphRect> usedGlyphRects;

        [NonSerialized]
        protected List<GlyphRect> freeGlyphRects;

        #endregion

        #region Runtime Fields

        private static readonly HashSet<UniTextFont> loadedFonts = new();

        internal Dictionary<uint, Glyph> glyphLookupDictionary;
        internal Dictionary<uint, UniTextCharacter> characterLookupDictionary;

        protected List<uint> glyphIndexList = new();

        private int cachedFaceIndex = -1;
        private int cachedInstanceId;
        public string CachedName { get; private set; }

        [ThreadStatic] private static HashSet<uint> toAddSet;

        protected const int PackingSpacing = 1;
        [NonSerialized] protected int shelfX;
        [NonSerialized] protected int shelfY;
        [NonSerialized] protected int shelfHeight;

        [NonSerialized] private IntPtr ftFace;

#if !UNITY_WEBGL || UNITY_EDITOR
        [NonSerialized] private FreeTypeFacePool sdfFacePool;
#endif

        // MSDF: latched once the native outline export is found missing, so we stop retrying and
        // fall back to SDF for the rest of this font's lifetime (with a one-time warning).
        [NonSerialized] private bool msdfOutlineUnavailable;
        [NonSerialized] private bool msdfFallbackWarned;
        [NonSerialized] private IntPtr msdfFace;

        // Effective render mode, resolved from the SERIALIZED atlasRenderMode by probing the native
        // outline export up front. When atlasRenderMode == Msdf but the export is missing, this
        // resolves to SDF so the atlas format, padding, and shader selection are all coherent.
        // UniTextRenderMode has no sentinel, so a separate "resolved" flag guards it.
        [NonSerialized] private bool msdfModeResolved;
        [NonSerialized] private UniTextRenderMode effectiveRenderMode;

        // Optional injected outline source (tests). When set, the native probe is bypassed and this
        // source's IsAvailable decides MSDF vs SDF, so both branches are testable deterministically
        // regardless of which native binary is installed.
        [NonSerialized] private Msdf.IGlyphOutlineSource injectedOutlineSource;

        [ThreadStatic] private static List<uint> toAddList;

        internal event Action Changed;
        
        #endregion

        /// <summary>Gets the cached Unity instance ID, initializing on first access.</summary>
        /// <returns>The font asset's instance ID for use as a dictionary key.</returns>
        public virtual int GetCachedInstanceId()
        {
            if (cachedInstanceId == 0)
            {
#if UNITY_6000_2_OR_NEWER
                cachedInstanceId = GetEntityId().GetHashCode();
#else
                cachedInstanceId = GetInstanceID();
#endif
                CachedName = name;
            }

            return cachedInstanceId;
        }

        #region Properties

        /// <summary>Gets the raw font file data (TTF/OTF bytes).</summary>
        public virtual byte[] FontData => fontData;

        /// <summary>Gets the italic slant angle in degrees.</summary>
        public float ItalicStyle => italicStyle;

        /// <summary>Gets the hash of the font data for identification.</summary>
        public virtual int FontDataHash => fontDataHash;

        /// <summary>Returns true if font file data is available.</summary>
        public virtual bool HasFontData => fontData != null && fontData.Length > 0;

        /// <summary>
        /// Computes a hash of font file data for identification.
        /// </summary>
        /// <param name="data">Font file bytes.</param>
        /// <returns>Hash value, or 0 if data is null/empty.</returns>
        public static int ComputeFontDataHash(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;
            unchecked
            {
                var hash = -2128831035;
                var len = data.Length;
                var step = len > 4096 ? len / 1024 : 1;
                for (var i = 0; i < len; i += step)
                    hash = (hash ^ data[i]) * 16777619;
                return (hash ^ len) * 16777619;
            }
        }


        /// <summary>Gets or sets the font face information (metrics, ascender, descender, etc.).</summary>
        public FaceInfo FaceInfo
        {
            get => faceInfo;
            internal set => faceInfo = value;
        }

        /// <summary>Gets or sets the font design units per em (typically 1000 or 2048).</summary>
        /// <remarks>
        /// This is the fundamental scaling unit for font metrics. All glyph measurements
        /// are expressed relative to this value. Industry standard values are 1000 (CFF/OTF)
        /// or 2048 (TrueType). Used for correct scaling: scale = fontSize / unitsPerEm.
        /// </remarks>
        public int UnitsPerEm
        {
            get => unitsPerEm > 0 ? unitsPerEm : 1000;
            internal set => unitsPerEm = value > 0 ? value : 1000;
        }

        /// <summary>Visual scale multiplier for this font asset.</summary>
        /// <remarks>
        /// Use to normalize fonts that appear too small or too large by design.
        /// For example, Dongle font renders visually smaller than other fonts at the same size —
        /// setting FontScale to 1.5 compensates for this. Applied as a post-conversion multiplier
        /// in all fontSize/UnitsPerEm scaling calculations.
        /// </remarks>
        public float FontScale
        {
            get => fontScale > 0f ? fontScale : 1f;
            set => fontScale = value > 0f ? value : 1f;
        }

        /// <summary>Gets the primary atlas texture.</summary>
        public Texture2D AtlasTexture
        {
            get
            {
                if (atlasTextures != null && atlasTextures.Count > 0)
                    return atlasTextures[0];
                return null;
            }
        }

        /// <summary>Gets or sets all atlas textures (multiple atlases for large character sets).</summary>
        public List<Texture2D> AtlasTextures => atlasTextures;

        /// <summary>Gets the atlas texture size in pixels (square).</summary>
        public int AtlasSize => atlasSize;

        /// <summary>Gets the SDF spread strength (0-1). Padding = PointSize * SpreadStrength.</summary>
        public float SpreadStrength => spreadStrength;

        /// <summary>Gets the padding between glyphs in the atlas. For SDF/MSDF: computed from SpreadStrength. For COLOR/bitmap: minimal.</summary>
        public int AtlasPadding
        {
            get
            {
                var mode = EffectiveRenderMode;
                if (mode != UniTextRenderMode.SDF && mode != UniTextRenderMode.Msdf)
                    return 1;
                return Mathf.Max(1, Mathf.RoundToInt(faceInfo.pointSize * spreadStrength));
            }
        }

        /// <summary>True when the atlas stores a distance field (SDF or MSDF) rather than a bitmap.</summary>
        internal bool IsDistanceFieldMode =>
            EffectiveRenderMode == UniTextRenderMode.SDF || EffectiveRenderMode == UniTextRenderMode.Msdf;

        /// <summary>True when the effective mode is a rasterised bitmap (Smooth grayscale or Mono 1-bit).</summary>
        internal bool IsBitmapMode =>
            EffectiveRenderMode == UniTextRenderMode.Smooth || EffectiveRenderMode == UniTextRenderMode.Mono;

        /// <summary>
        /// True when this font renders single-channel coverage bitmaps (Smooth/Mono) into an Alpha8
        /// atlas. Excludes color fonts (EmojiFont), whose Smooth atlas is RGBA color, not coverage.
        /// </summary>
        internal bool IsCoverageBitmapMode => IsBitmapMode && !IsColor;

        /// <summary>
        /// Gets the glyph render mode ACTUALLY in effect. Equal to the configured mode except that
        /// <see cref="UniTextRenderMode.Msdf"/> degrades to <see cref="UniTextRenderMode.SDF"/> when
        /// the native outline export (or an injected source) is unavailable. This is what the atlas
        /// format, padding, and shader selection follow, so a missing export can never leave the
        /// font in a half-MSDF state.
        /// </summary>
        public UniTextRenderMode AtlasRenderMode => EffectiveRenderMode;

        /// <summary>The configured (serialized) render mode, before MSDF availability resolution.</summary>
        public UniTextRenderMode ConfiguredRenderMode => atlasRenderMode;

        /// <summary>
        /// Resolves and caches the effective render mode. For Msdf, probes the native outline export
        /// (or the injected source) exactly once; if unavailable, degrades to SDF and warns once.
        /// </summary>
        internal UniTextRenderMode EffectiveRenderMode
        {
            get
            {
                if (msdfModeResolved) return effectiveRenderMode;

                if (atlasRenderMode != UniTextRenderMode.Msdf)
                {
                    effectiveRenderMode = atlasRenderMode;
                    msdfModeResolved = true;
                    return effectiveRenderMode;
                }

                bool available;
                if (injectedOutlineSource != null)
                {
                    available = injectedOutlineSource.IsAvailable;
                }
                else
                {
                    var face = EnsureMsdfFace();
                    available = face != IntPtr.Zero && Msdf.MsdfNative.Probe(face);
                }

                effectiveRenderMode = available ? UniTextRenderMode.Msdf : UniTextRenderMode.SDF;
                msdfModeResolved = true;
                msdfOutlineUnavailable = !available;

                if (!available && !msdfFallbackWarned)
                {
                    msdfFallbackWarned = true;
                    Cat.MeowWarnFormat("[MSDF] {0}: native outline export 'ut_ft_get_outline_data' unavailable; using SDF (Alpha8) atlas instead.", name);
                }
                return effectiveRenderMode;
            }
        }

        /// <summary>
        /// TEST HOOK: injects an <see cref="Msdf.IGlyphOutlineSource"/> so the MSDF/SDF-fallback
        /// decision and the RGB24 render path can be exercised deterministically without depending
        /// on which native binary is installed. Resets any prior mode resolution.
        /// </summary>
        internal void SetOutlineSourceForTesting(Msdf.IGlyphOutlineSource source)
        {
            injectedOutlineSource = source;
            msdfModeResolved = false;
            msdfOutlineUnavailable = false;
            msdfFallbackWarned = false;

            // Discard any atlas already created under the previous (possibly SDF-fallback) mode so
            // the next glyph batch re-derives the format from the freshly resolved effective mode.
            if (atlasTextures != null)
            {
                foreach (var tex in atlasTextures)
                    ObjectUtils.SafeDestroy(tex);
                atlasTextures.Clear();
            }
            glyphTable?.Clear();
            glyphLookupDictionary?.Clear();
            glyphIndexList?.Clear();
            usedGlyphRects?.Clear();
            freeGlyphRects?.Clear();
            shelfX = shelfY = shelfHeight = 0;
        }

        #region Pixel Font (Phase 1c)

        /// <summary>
        /// Whether pixel-perfect rendering is requested for this font. Only meaningful when the font
        /// is actually a pixel/bitmap font (see <see cref="PixelFont"/>.<c>IsPixelFont</c>); the
        /// renderer treats it as off for SDF/MSDF/Smooth atlases.
        /// </summary>
        public bool PixelPerfect
        {
            get => pixelPerfect;
            internal set => pixelPerfect = value;
        }

        /// <summary>
        /// The cached pixel-font analysis (outline grid + embedded bitmap strikes). Lazily computed
        /// via <see cref="DetectPixelFont"/> on first access. Read-only for callers.
        /// </summary>
        public PixelFontInfo PixelFont
        {
            get
            {
                if (!pixelFontInfoResolved)
                    DetectPixelFont();
                return pixelFontInfo;
            }
        }

        /// <summary>
        /// True when pixel-perfect rendering is BOTH requested and applicable (the font is a pixel or
        /// bitmap font). This is the flag the mesh generator / atlas path should gate on.
        /// </summary>
        public bool PixelPerfectActive => pixelPerfect && PixelFont.IsPixelFont;

        // Test hook: lets EditMode tests supply a managed outline provider (e.g. a TrueType glyf
        // reader) so grid detection is exercised without depending on the native outline export.
        [NonSerialized] private IPixelGridOutlineProvider injectedGridProvider;

        /// <summary>
        /// TEST HOOK: injects an outline provider for pixel-grid detection and forces re-analysis on
        /// next <see cref="PixelFont"/> access. Does not touch render mode or atlases.
        /// </summary>
        internal void SetGridProviderForTesting(IPixelGridOutlineProvider provider)
        {
            injectedGridProvider = provider;
            pixelFontInfoResolved = false;
        }

        /// <summary>
        /// Analyses this font for pixel-font characteristics and caches the result in
        /// <see cref="pixelFontInfo"/>. Reads embedded bitmap-strike sizes from the face, then probes
        /// the outline grid via the native outline export (or an injected managed provider in tests).
        /// Degrades gracefully when the outline export is unavailable — grid detection is simply
        /// skipped and <see cref="PixelFontInfo.outlineDataAvailable"/> is false. Never renders and
        /// never mutates the atlas or render mode.
        /// </summary>
        /// <returns>The freshly computed <see cref="PixelFontInfo"/>.</returns>
        public PixelFontInfo DetectPixelFont()
        {
            int[] strikeSizes = Array.Empty<int>();
            IPixelGridOutlineProvider provider = injectedGridProvider;

            if (provider == null && HasFontData)
            {
                var face = EnsureFTFace();
                if (face != IntPtr.Zero)
                {
                    var fi = FT.GetFaceInfo(face);
                    if (fi.numFixedSizes > 0)
                        strikeSizes = FT.GetAllFixedSizes(face) ?? Array.Empty<int>();

                    int upem = unitsPerEm > 0 ? unitsPerEm : (fi.unitsPerEm > 0 ? fi.unitsPerEm : 1000);
                    provider = new FreeTypeOutlineGridProvider(face, upem);
                }
            }

            pixelFontInfo = PixelFontDetection.Analyze(provider, strikeSizes);
            pixelFontInfoResolved = true;
            return pixelFontInfo;
        }

        /// <summary>
        /// Chooses the integer atlas sampling ppem for pixel-perfect Mono rendering, given a desired
        /// ppem. Returns the nearest integer MULTIPLE of the native pixels-per-em that is ≥ the
        /// native size. When the font has bitmap strikes but no detected grid, snaps to the nearest
        /// available strike. Fallback (documented): if neither applies, rounds the request up to the
        /// nearest whole pixel. Never returns less than 1.
        /// </summary>
        /// <param name="desiredPpem">Requested sampling size in pixels-per-em.</param>
        /// <returns>An integer ppem suitable for a crisp, aliasing-free Mono atlas.</returns>
        public int ChoosePixelPerfectPpem(float desiredPpem)
        {
            var info = PixelFont;

            if (info.isPixelGrid && info.nativePixelsPerEm > 0)
            {
                int native = info.nativePixelsPerEm;
                int mult = Mathf.Max(1, Mathf.RoundToInt(desiredPpem / native));
                return native * mult;
            }

            if (info.hasBitmapStrikes && info.bitmapStrikeSizes is { Length: > 0 })
            {
                int best = info.bitmapStrikeSizes[0];
                int bestDiff = Mathf.Abs(best - Mathf.RoundToInt(desiredPpem));
                for (int i = 1; i < info.bitmapStrikeSizes.Length; i++)
                {
                    int diff = Mathf.Abs(info.bitmapStrikeSizes[i] - Mathf.RoundToInt(desiredPpem));
                    if (diff < bestDiff) { bestDiff = diff; best = info.bitmapStrikeSizes[i]; }
                }
                return Mathf.Max(1, best);
            }

            // Documented fallback for a non-pixel font asked to render pixel-perfect: whole-pixel ppem.
            return Mathf.Max(1, Mathf.CeilToInt(desiredPpem));
        }

        #endregion

        /// <summary>Gets the glyph lookup table (glyph index → Glyph).</summary>
        public Dictionary<uint, Glyph> GlyphLookupTable
        {
            get
            {
                if (glyphLookupDictionary == null)
                {
                    Cat.MeowFormat("[GlyphLookupTable] {0}: dict is NULL, calling ReadFontAssetDefinition", name);
                    ReadFontAssetDefinition();
                }
                return glyphLookupDictionary;
            }
        }

        /// <summary>Gets the character lookup table (unicode → UniTextCharacter).</summary>
        internal Dictionary<uint, UniTextCharacter> CharacterLookupTable
        {
            get
            {
                if (characterLookupDictionary == null)
                {
                    Cat.MeowFormat("[CharacterLookupTable] {0}: dict is NULL, calling ReadFontAssetDefinition. glyphLookup={1}, glyphTable={2}, atlas={3}",
                        name, glyphLookupDictionary?.Count ?? -1, glyphTable?.Count ?? -1, atlasTextures?.Count ?? -1);
                    ReadFontAssetDefinition();
                }
                return characterLookupDictionary;
            }
        }
        
        public bool IsColor => this is EmojiFont;

        internal int GlyphLookupDiagCount => glyphLookupDictionary?.Count ?? -1;
        internal int AtlasTexturesDiagCount => atlasTextures?.Count ?? -1;
        internal int GlyphTableDiagCount => glyphTable?.Count ?? -1;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes lookup dictionaries from serialized glyph and character tables.
        /// </summary>
        public void ReadFontAssetDefinition()
        {
            Cat.MeowFormat("[ReadFontAssetDefinition] {0}: CALLED. glyphTable={1}, glyphLookup={2}, charLookup={3}, atlas={4}",
                name,
                glyphTable?.Count ?? -1,
                glyphLookupDictionary?.Count ?? -1,
                characterLookupDictionary?.Count ?? -1,
                atlasTextures?.Count ?? -1);
            Cat.MeowFormat("[ReadFontAssetDefinition] {0}: stacktrace:\n{1}", name, UnityEngine.StackTraceUtility.ExtractStackTrace());
            InitializeGlyphLookupDictionary();
            InitializeCharacterLookupDictionary();
            AddSynthesizedCharacters();
            Cat.MeowFormat("[ReadFontAssetDefinition] {0}: DONE. glyphLookup={1}, charLookup={2}",
                name, glyphLookupDictionary?.Count ?? -1, characterLookupDictionary?.Count ?? -1);
        }

        private void InitializeGlyphLookupDictionary()
        {
            glyphLookupDictionary ??= new Dictionary<uint, Glyph>();
            glyphLookupDictionary.Clear();

            glyphIndexList ??= new List<uint>();
            glyphIndexList.Clear();

            if (glyphTable == null) return;

            int zeroRectCount = 0;
            for (var i = 0; i < glyphTable.Count; i++)
            {
                var glyph = glyphTable[i];
                var index = glyph.index;

                if (glyphLookupDictionary.TryAdd(index, glyph))
                {
                    glyphIndexList.Add(index);
                    var r = glyph.glyphRect;
                    if (r.width == 0 || r.height == 0)
                        zeroRectCount++;
                }
            }

            if (glyphTable.Count > 0)
                Cat.MeowFormat("[InitGlyphLookup] {0}: read {1} from glyphTable, {2} zero-rect, atlas={3}",
                    name, glyphLookupDictionary.Count, zeroRectCount, atlasTextures?.Count ?? -1);
        }

        private void InitializeCharacterLookupDictionary()
        {
            characterLookupDictionary ??= new Dictionary<uint, UniTextCharacter>();
            characterLookupDictionary.Clear();

            if (characterTable == null) return;

            for (var i = 0; i < characterTable.Count; i++)
            {
                var character = characterTable[i];
                var unicode = character.unicode;

                if (characterLookupDictionary.TryAdd(unicode, character))
                {
                    if (glyphLookupDictionary.TryGetValue(character.glyphIndex, out var glyph))
                        character.glyph = glyph;
                }
            }
        }

        private void AddSynthesizedCharacters()
        {
            var fontLoaded = LoadFontFace() == UniTextFontError.Success;

            AddSynthesizedCharacter(UnicodeData.Tab, fontLoaded, true);
            AddSynthesizedCharacter(UnicodeData.LineFeed, fontLoaded);
            AddSynthesizedCharacter(UnicodeData.CarriageReturn, fontLoaded);
            AddSynthesizedCharacter(UnicodeData.ZeroWidthSpace, fontLoaded);
            AddSynthesizedCharacter(UnicodeData.LeftToRightMark, fontLoaded);
            AddSynthesizedCharacter(UnicodeData.RightToLeftMark, fontLoaded);
            AddSynthesizedCharacter(UnicodeData.LineSeparator, fontLoaded);
            AddSynthesizedCharacter(UnicodeData.ParagraphSeparator, fontLoaded);
            AddSynthesizedCharacter(UnicodeData.WordJoiner, fontLoaded);
            AddSynthesizedCharacter(UnicodeData.ArabicLetterMark, fontLoaded);
        }

        private void AddSynthesizedCharacter(int unicode, bool fontLoaded, bool addImmediately = false)
        {
            var cp = (uint)unicode;

            if (characterLookupDictionary.ContainsKey(cp))
                return;

            Glyph glyph;

            if (fontLoaded)
            {
                var glyphIdx = Shaper.GetGlyphIndex(this, cp);
                if (glyphIdx != 0)
                {
                    if (!addImmediately) return;

                    var face = EnsureFTFace();
                    if (face != IntPtr.Zero)
                    {
                        var pointSize = faceInfo.pointSize > 0 ? faceInfo.pointSize : 90;
                        FT.SetPixelSize(face, pointSize);
                        if (FT.LoadGlyph(face, glyphIdx, FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP))
                        {
                            var ftMetrics = FT.GetGlyphMetrics(face);
                            var metricsConversion = pointSize > 0 && pointSize != unitsPerEm
                                ? (float)unitsPerEm / pointSize : 1f;
                            var advance = (ftMetrics.advanceX / 64f) * metricsConversion;
                            glyph = new Glyph(glyphIdx,
                                new GlyphMetrics(
                                    ftMetrics.width * metricsConversion,
                                    ftMetrics.height * metricsConversion,
                                    ftMetrics.bearingX * metricsConversion,
                                    ftMetrics.bearingY * metricsConversion,
                                    advance),
                                GlyphRect.zero, 0);
                            characterLookupDictionary.Add(cp, new UniTextCharacter(cp, glyph));
                        }
                    }

                    return;
                }
            }

            glyph = new Glyph(0, new GlyphMetrics(0, 0, 0, 0, 0), GlyphRect.zero, 0);
            characterLookupDictionary.Add(cp, new UniTextCharacter(cp, glyph));
        }

        #endregion

        #region Font Loading

        /// <summary>
        /// Ensures a FreeType face handle is loaded for this font asset.
        /// </summary>
        /// <returns>FT_Face handle, or IntPtr.Zero if loading failed.</returns>
        protected IntPtr EnsureFTFace()
        {
            if (ftFace != IntPtr.Zero)
                return ftFace;

            if (fontData == null || fontData.Length == 0)
                return IntPtr.Zero;

            if (!FT.IsInitialized)
                FT.Initialize();

            if (cachedFaceIndex < 0)
                cachedFaceIndex = faceInfo.faceIndex;

            ftFace = FT.LoadFace(fontData, cachedFaceIndex < 0 ? 0 : cachedFaceIndex);
            Cat.MeowFormat("[EnsureFTFace] {0}: loaded face={1}", name, ftFace != IntPtr.Zero);
            return ftFace;
        }

        /// <summary>
        /// Releases the FreeType face handle if loaded.
        /// </summary>
        protected void ReleaseFTFace()
        {
            if (ftFace != IntPtr.Zero)
            {
                FT.UnloadFace(ftFace);
                ftFace = IntPtr.Zero;
            }
            if (msdfFace != IntPtr.Zero)
            {
                FT.UnloadFace(msdfFace);
                msdfFace = IntPtr.Zero;
            }
            // Force re-resolution of the effective render mode on next use (unless a test source
            // is injected, which owns availability itself).
            if (injectedOutlineSource == null)
            {
                msdfModeResolved = false;
                msdfOutlineUnavailable = false;
                msdfFallbackWarned = false;
            }
        }

        /// <summary>
        /// Loads the font face for glyph operations.
        /// </summary>
        /// <returns>Success if the font was loaded, error code otherwise.</returns>
        public virtual UniTextFontError LoadFontFace()
        {
            return EnsureFTFace() != IntPtr.Zero
                ? UniTextFontError.Success
                : UniTextFontError.InvalidFile;
        }

        #endregion

        #region Dynamic Character Loading

        /// <summary>
        /// Gets the glyph index for a Unicode codepoint.
        /// </summary>
        /// <param name="unicode">Unicode codepoint.</param>
        /// <returns>Glyph index, or 0 if the glyph is not available.</returns>
        public uint GetGlyphIndexForUnicode(uint unicode)
        {
            uint glyphIndex = 0;

            if (HasFontData)
                glyphIndex = Shaper.GetGlyphIndex(this, unicode);

            if (glyphIndex == 0)
            {
                uint specialCodepoint = unicode switch
                {
                    UnicodeData.NoBreakSpace => UnicodeData.Space,
                    UnicodeData.SoftHyphen => UnicodeData.Hyphen,
                    UnicodeData.NonBreakingHyphen => UnicodeData.Hyphen,
                    _ => 0
                };

                if (specialCodepoint != 0 && HasFontData)
                    glyphIndex = Shaper.GetGlyphIndex(this, specialCodepoint);
            }

            return glyphIndex;
        }

        /// <summary>
        /// Registers character-to-glyph mappings for later lookup.
        /// </summary>
        /// <param name="entries">List of (unicode, glyphIndex) pairs.</param>
        public void RegisterCharacterEntries(List<(uint unicode, uint glyphIndex)> entries)
        {
            if (entries == null || entries.Count == 0)
                return;

            if (characterLookupDictionary == null)
                ReadFontAssetDefinition();

            characterTable ??= new List<UniTextCharacter>();

            for (int i = 0; i < entries.Count; i++)
            {
                var (unicode, glyphIndex) = entries[i];

                if (characterLookupDictionary.ContainsKey(unicode))
                    continue;

                if (!glyphLookupDictionary.TryGetValue(glyphIndex, out var glyph))
                    continue;

                var character = new UniTextCharacter(unicode, glyphIndex) { glyph = glyph };
                characterTable.Add(character);
                characterLookupDictionary[unicode] = character;
            }
        }

        /// <summary>
        /// Filters glyph indices, removing zeros and already-known glyphs.
        /// Returns a reusable list of unique indices to add, or null if nothing to add.
        /// </summary>
        protected List<uint> FilterNewGlyphs(List<uint> glyphIndices)
        {
            toAddSet ??= new HashSet<uint>();
            toAddSet.Clear();
            for (var i = 0; i < glyphIndices.Count; i++)
            {
                var idx = glyphIndices[i];
                if (glyphLookupDictionary == null || !glyphLookupDictionary.ContainsKey(idx))
                    toAddSet.Add(idx);
            }

            if (toAddSet.Count == 0)
                return null;

            toAddList ??= new List<uint>(256);
            toAddList.Clear();
            foreach (var idx in toAddSet)
                toAddList.Add(idx);

            return toAddList;
        }

        /// <summary>
        /// Checks if a glyph is already rasterized in the atlas.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasGlyphInAtlas(uint glyphIndex)
        {
            return glyphLookupDictionary != null && glyphLookupDictionary.ContainsKey(glyphIndex);
        }

        // ---- Phase 2: variation-keyed atlas facet -------------------------------
        // Variable-font instances cannot share a glyph cell with each other or with the default
        // instance: wght 400 and 700 of the SAME face produce different outlines. We therefore key
        // a secondary glyph store by (VariationKey -> glyphIndex -> Glyph). VariationKey.None keeps
        // the original single-dictionary behaviour untouched (zero cost / no regression for static
        // fonts). The design coordinates are applied to the FreeType face before rasterizing, so the
        // stored bitmap is the varied shape across SDF, MSDF, Smooth and Mono.
        [NonSerialized] private Dictionary<VariationKey, Dictionary<uint, Glyph>> variationGlyphLookup;

        /// <summary>True when the glyph is rasterized for the given variation instance.</summary>
        public bool HasGlyphInAtlas(uint glyphIndex, VariationKey key)
        {
            if (key.IsNone) return HasGlyphInAtlas(glyphIndex);
            return variationGlyphLookup != null
                   && variationGlyphLookup.TryGetValue(key, out var d)
                   && d.ContainsKey(glyphIndex);
        }

        /// <summary>Gets a glyph for a specific variation instance (None == the default store).</summary>
        public bool TryGetGlyph(uint glyphIndex, VariationKey key, out Glyph glyph)
        {
            glyph = default;
            if (key.IsNone)
            {
                var tbl = GlyphLookupTable;
                return tbl != null && tbl.TryGetValue(glyphIndex, out glyph);
            }
            return variationGlyphLookup != null
                   && variationGlyphLookup.TryGetValue(key, out var d)
                   && d.TryGetValue(glyphIndex, out glyph);
        }

        // ---- Render-Architecture R2 step 1: shared Texture2DArray atlas facet (ADDITIVE) ----------
        // Publishes an already-packed glyph into the process-wide shared array for this font's format
        // (decision §5.1: Alpha8 for SDF/coverage, RGBA32 for MSDF/color). The legacy per-font
        // atlasTextures path stays authoritative; this only MIRRORS the glyph so the single draw-group
        // renderer (sub-task 2) can bind one array across fonts. Cells are keyed
        // (fontId, glyphIndex, VariationKey). Lazy + cached: a published glyph is not re-copied.
        [NonSerialized] private HashSet<GlyphAtlasArray.GlyphCellKey> _publishedCells;

        /// <summary>
        /// Ensures the glyph <paramref name="glyphIndex"/> (variation <paramref name="key"/>) is
        /// present in the shared <see cref="GlyphAtlasArray"/> for this font's format, copying its
        /// bytes from the legacy atlas on first request, and returns its
        /// <see cref="GlyphAtlasArray.GlyphCell"/>. Returns false when the glyph is not packed (zero
        /// rect / missing) or the atlas pixels are unavailable. Does NOT alter the legacy path.
        /// Main-thread only.
        /// </summary>
        public bool TryPublishToSharedAtlas(uint glyphIndex, VariationKey key, out GlyphAtlasArray.GlyphCell cell)
        {
            cell = default;
            if (!TryGetGlyph(glyphIndex, key, out var glyph))
                return false;
            var rect = glyph.glyphRect;
            if (rect.width <= 0 || rect.height <= 0)
                return false; // whitespace / zero-area glyph: nothing to pack
            if (atlasTextures == null || glyph.atlasIndex < 0 || glyph.atlasIndex >= atlasTextures.Count)
                return false;
            var srcTex = atlasTextures[glyph.atlasIndex];
            if (srcTex == null)
                return false;

            var mode = EffectiveRenderMode;
            var format = SharedGlyphAtlas.FormatFor(mode, IsColor);
            var arr = SharedGlyphAtlas.Get(format, atlasSize);

            var cellKey = new GlyphAtlasArray.GlyphCellKey(GetCachedInstanceId(), glyphIndex, key);
            _publishedCells ??= new HashSet<GlyphAtlasArray.GlyphCellKey>();

            if (arr.TryGetCell(cellKey, out cell))
                return true; // already resident in the shared array

            // Extract the glyph's bytes from the legacy atlas (CPU-side raw data; the legacy atlas is
            // kept readable because PackRenderedBatch calls Apply(false, false)).
            int srcChannels = srcTex.format == TextureFormat.RGBA32 ? 4
                : srcTex.format == TextureFormat.RGB24 ? 3 : 1;
            int dstChannels = format == TextureFormat.RGBA32 ? 4 : 1;

            byte[] glyphBytes = ExtractGlyphBytes(srcTex, rect, srcChannels, dstChannels);
            if (glyphBytes == null)
                return false;

            if (!arr.AddGlyph(cellKey, glyphBytes, rect.width, rect.height, dstChannels, out cell))
                return false;
            arr.Apply(false);
            _publishedCells.Add(cellKey);
            return true;
        }

        // Copies a glyph sub-rect out of a legacy atlas texture into a tightly packed byte[] in the
        // shared array's channel count. Alpha8 src -> expands to RGBA32 (alpha in .a, rgb = alpha) when
        // the shared array is RGBA32; RGB24/ RGBA src -> RGBA32. Same-channel copies are a straight blit.
        private static byte[] ExtractGlyphBytes(Texture2D srcTex, GlyphRect rect, int srcChannels, int dstChannels)
        {
            Unity.Collections.NativeArray<byte> raw;
            try { raw = srcTex.GetRawTextureData<byte>(); }
            catch { return null; }
            if (!raw.IsCreated || raw.Length == 0) return null;

            int atlasW = srcTex.width;
            int srcStride = atlasW * srcChannels;
            var dst = new byte[rect.width * rect.height * dstChannels];

            for (int y = 0; y < rect.height; y++)
            {
                int srcRow = (rect.y + y) * srcStride + rect.x * srcChannels;
                int dstRow = y * rect.width * dstChannels;
                for (int x = 0; x < rect.width; x++)
                {
                    int s = srcRow + x * srcChannels;
                    int d = dstRow + x * dstChannels;
                    if (dstChannels == 1)
                    {
                        // Alpha8 shared array: take source alpha (ch0 for Alpha8, ch3 for RGBA, else ch0).
                        dst[d] = srcChannels == 4 ? raw[s + 3] : raw[s];
                    }
                    else // dstChannels == 4 (RGBA32 shared array: MSDF or color)
                    {
                        if (srcChannels == 1)
                        {
                            byte a = raw[s];
                            dst[d] = a; dst[d + 1] = a; dst[d + 2] = a; dst[d + 3] = a;
                        }
                        else if (srcChannels == 3)
                        {
                            dst[d] = raw[s]; dst[d + 1] = raw[s + 1]; dst[d + 2] = raw[s + 2]; dst[d + 3] = 255;
                        }
                        else // 4
                        {
                            dst[d] = raw[s]; dst[d + 1] = raw[s + 1]; dst[d + 2] = raw[s + 2]; dst[d + 3] = raw[s + 3];
                        }
                    }
                }
            }
            return dst;
        }

        /// <summary>
        /// Ensures <paramref name="glyphIndices"/> are rasterized into the atlas for the variable-font
        /// instance <paramref name="key"/> (design coords <paramref name="coords"/> for axes
        /// <paramref name="tags"/>), stored under a per-key sub-dictionary so instances never share a
        /// cell. No-op for <see cref="VariationKey.None"/> or a static font (use the normal batch path).
        /// Returns the number of glyphs added. Main-thread only (atlas mutation).
        /// </summary>
        public int EnsureGlyphsForVariation(List<uint> glyphIndices, VariationKey key, uint[] tags, float[] coords)
        {
            if (key.IsNone || glyphIndices == null || glyphIndices.Count == 0) return 0;
            if (fontData == null || fontData.Length == 0) return 0;

            var face = EnsureFTFace();
            if (face == IntPtr.Zero) return 0;

            var mode0 = EffectiveRenderMode;
            // MSDF renders from a separate face; apply the variation to whichever face will raster.
            IntPtr msdfVarFace = IntPtr.Zero;
            if (mode0 == UniTextRenderMode.Msdf)
            {
                msdfVarFace = EnsureMsdfFace();
                if (msdfVarFace != IntPtr.Zero && tags != null && coords != null && coords.Length > 0)
                    FTVar.SetDesignCoordinates(msdfVarFace, coords);
            }
            // Apply the variation design coordinates to the coverage/SDF face before rasterizing.
            if (tags != null && coords != null && coords.Length > 0)
                FTVar.SetDesignCoordinates(face, coords);

            variationGlyphLookup ??= new Dictionary<VariationKey, Dictionary<uint, Glyph>>();
            if (!variationGlyphLookup.TryGetValue(key, out var store))
            {
                store = new Dictionary<uint, Glyph>();
                variationGlyphLookup[key] = store;
            }

            var pointSize = faceInfo.pointSize > 0 ? faceInfo.pointSize : 90;
            var spread = AtlasPadding;
            bool coverage = IsCoverageBitmapMode || PixelPerfectActive;
            if (coverage) spread = 0;
            var metricsConversion = pointSize > 0 && pointSize != unitsPerEm ? (float)unitsPerEm / pointSize : 1f;

            bool msdfAvailable = mode0 == UniTextRenderMode.Msdf && msdfVarFace != IntPtr.Zero && Msdf.MsdfNative.Probe(msdfVarFace);
            Msdf.IGlyphOutlineSource msdfSource = null;
            if (msdfAvailable)
            {
                var mf = msdfVarFace;
                msdfSource = injectedOutlineSource ?? new Msdf.FreeTypeOutlineSource(mf,
                    (gi, ppem) => FT.SetPixelSize(mf, ppem) && FT.LoadGlyph(mf, gi, FT.LOAD_DEFAULT | FT.LOAD_NO_HINTING));
            }

            int added = 0;
            foreach (var gi in glyphIndices)
            {
                if (store.ContainsKey(gi)) continue;

                SdfRenderedGlyph r;
                bool ok;
                if (mode0 == UniTextRenderMode.Mono)
                    ok = MonoGlyphRenderer.TryRender(face, gi, pointSize, out r);
                else if (mode0 == UniTextRenderMode.Smooth)
                    ok = SmoothGlyphRenderer.TryRender(face, gi, pointSize, out r);
                else if (msdfAvailable)
                    ok = Msdf.MsdfGlyphRenderer.TryRender(msdfVarFace, gi, pointSize, spread, msdfSource, out r, out _);
                else
                    ok = SdfGlyphRenderer.TryRender(face, gi, pointSize, FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP, spread, out r);

                if (!ok || !r.isValid) { if (ok) ReturnSdfPixels(ref r); continue; }

                var glyph = PackOneVariedGlyph(ref r, gi, spread, metricsConversion);
                store[gi] = glyph;
                added++;
            }

            // Restore both faces to the default instance so the plain path is unaffected.
            if (tags != null)
            {
                FTVar.SetNamedInstance(face, 0);
                if (msdfVarFace != IntPtr.Zero) FTVar.SetNamedInstance(msdfVarFace, 0);
            }
            if (atlasTextures != null && atlasTextures.Count > 0)
                atlasTextures[^1].Apply(false, false);
            return added;
        }

        // Packs one already-rendered varied glyph into the current atlas (shelf-packed), returning
        // its Glyph record. Mirrors the relevant branch of PackRenderedBatch for a single glyph.
        private unsafe Glyph PackOneVariedGlyph(ref SdfRenderedGlyph r, uint glyphIndex, int spread, float metricsConversion)
        {
            float advanceDU = (r.metricAdvanceX26_6 / 64f) * metricsConversion;

            if (r.sdfPixels == null)
            {
                return new Glyph(glyphIndex, new GlyphMetrics(
                    r.metricWidth * metricsConversion, r.metricHeight * metricsConversion,
                    r.metricBearingX * metricsConversion, r.metricBearingY * metricsConversion, advanceDU),
                    GlyphRect.zero, 0);
            }

            if (atlasTextures == null || atlasTextures.Count == 0)
                CreateNewAtlasTexture();

            if (!TryPackGlyphShelf(r.bmpWidth, r.bmpHeight, out var packRect))
            {
                atlasTextures[^1].Apply(false, false);
                CreateNewAtlasTexture();
                if (!TryPackGlyphShelf(r.bmpWidth, r.bmpHeight, out packRect))
                {
                    ReturnSdfPixels(ref r);
                    return new Glyph(glyphIndex, default, GlyphRect.zero, 0);
                }
            }

            var curAtlas = atlasTextures[^1];
            int atlasChannels = curAtlas.format == TextureFormat.RGB24 ? 3 : curAtlas.format == TextureFormat.RGBA32 ? 4 : 1;
            int glyphChannels = r.channels > 0 ? r.channels : 1;
            if (glyphChannels == atlasChannels)
            {
                var raw = curAtlas.GetRawTextureData<byte>();
                byte* atlasPtr = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(raw);
                CopySdfBitmapToAtlas(r.sdfPixels, r.bmpWidth, r.bmpHeight, packRect.x, packRect.y, curAtlas.width, atlasPtr, glyphChannels);
            }
            ReturnSdfPixels(ref r);

            int outlineW = Mathf.Max(0, r.bmpWidth - 2 * spread);
            int outlineH = Mathf.Max(0, r.bmpHeight - 2 * spread);
            float outlineBearingX = (r.bitmapLeft + spread) * metricsConversion;
            float outlineBearingY = (r.bitmapTop - spread) * metricsConversion;
            var glyphRect = new GlyphRect(packRect.x + spread, packRect.y + spread, outlineW, outlineH);

            return new Glyph(glyphIndex, new GlyphMetrics(
                outlineW * metricsConversion, outlineH * metricsConversion,
                outlineBearingX, outlineBearingY, advanceDU),
                glyphRect, atlasTextures.Count - 1);
        }

        /// <summary>
        /// Prepared batch data for the split rendering pipeline.
        /// Created by <see cref="PrepareGlyphBatch"/>, consumed by <see cref="RenderPreparedBatch"/> and <see cref="PackRenderedBatch"/>.
        /// </summary>
        public struct PreparedBatch
        {
            public List<uint> filteredGlyphs;
            public int pointSize;
            public int spread;
            public float metricsConversion;
            /// <summary>True when this batch must be rendered pixel-perfect (Mono, 0/255, no spread).</summary>
            public bool pixelPerfect;
            /// <summary>
            /// True when this batch renders single-channel coverage bitmaps (Smooth grayscale or Mono
            /// 1-bit) for a regular (non-color) font. Pixel-perfect is a special case of this.
            /// </summary>
            public bool coverageBitmap;
            /// <summary>The effective render mode captured when the batch was prepared.</summary>
            public UniTextRenderMode renderMode;
        }

        /// <summary>
        /// Phase 1: Filters glyph indices and prepares rendering parameters.
        /// Must be called on main thread (reads glyphLookupDictionary, ensures font resources).
        /// </summary>
        /// <param name="glyphIndices">Glyph indices to add.</param>
        /// <returns>Prepared batch, or null if nothing to add.</returns>
        public virtual PreparedBatch? PrepareGlyphBatch(List<uint> glyphIndices)
        {
            if (glyphIndices == null || glyphIndices.Count == 0)
                return null;

            if (glyphLookupDictionary == null)
                ReadFontAssetDefinition();

            if (fontData == null || fontData.Length == 0)
                return null;

            var toAdd = FilterNewGlyphs(glyphIndices);
            if (toAdd == null)
                return null;

            var pointSize = faceInfo.pointSize > 0 ? faceInfo.pointSize : 90;
            var spread = AtlasPadding;

            // Bitmap coverage modes (Smooth grayscale, Mono 1-bit) for regular fonts have no distance
            // field, so no spread. Pixel-perfect is a special case: it additionally samples at an
            // integer multiple of the font's native pixels-per-em.
            bool coverageBitmap = IsCoverageBitmapMode;
            bool pixelPerfect = PixelPerfectActive;
            if (coverageBitmap || pixelPerfect)
                spread = 0;
            if (pixelPerfect)
                pointSize = ChoosePixelPerfectPpem(pointSize);

            var metricsConversion = pointSize > 0 && pointSize != unitsPerEm
                ? (float)unitsPerEm / pointSize
                : 1f;

            glyphLookupDictionary ??= new Dictionary<uint, Glyph>();
            glyphTable ??= new List<Glyph>();
            glyphIndexList ??= new List<uint>();

#if !UNITY_WEBGL || UNITY_EDITOR
            sdfFacePool ??= new FreeTypeFacePool(fontData, cachedFaceIndex < 0 ? 0 : cachedFaceIndex, pointSize);
#else
            if (EnsureFTFace() == IntPtr.Zero) return null;
#endif

            var owned = new List<uint>(toAdd.Count);
            owned.AddRange(toAdd);

            return new PreparedBatch
            {
                filteredGlyphs = owned,
                pointSize = pointSize,
                spread = spread,
                metricsConversion = metricsConversion,
                pixelPerfect = pixelPerfect,
                coverageBitmap = coverageBitmap || pixelPerfect,
                renderMode = EffectiveRenderMode
            };
        }

        /// <summary>
        /// Phase 2: Renders glyphs to SDF bitmaps. Can run on any thread.
        /// </summary>
        /// <param name="batch">Prepared batch from <see cref="PrepareGlyphBatch"/>.</param>
        /// <returns>Rendered glyph data (SdfRenderedGlyph[] for SDF fonts). Null on failure.</returns>
        public virtual object RenderPreparedBatch(PreparedBatch batch)
        {
            // Coverage-bitmap path (Smooth grayscale + Mono 1-bit) for regular fonts. Renders
            // single-channel Alpha8 coverage at the batch ppem. Rendered sequentially on the shared
            // face (mono/normal render state is per-face). Bypasses SDF/MSDF entirely, so those modes
            // are byte-for-byte unaffected. EmojiFont overrides this method, so color never reaches here.
            if (batch.coverageBitmap)
            {
                var face = EnsureFTFace();
                if (face == IntPtr.Zero) return null;
                bool mono = batch.pixelPerfect || batch.renderMode == UniTextRenderMode.Mono;
                var cov = new SdfRenderedGlyph[batch.filteredGlyphs.Count];
                for (int i = 0; i < batch.filteredGlyphs.Count; i++)
                {
                    if (mono)
                        MonoGlyphRenderer.TryRender(face, batch.filteredGlyphs[i], batch.pointSize, out cov[i]);
                    else
                        SmoothGlyphRenderer.TryRender(face, batch.filteredGlyphs[i], batch.pointSize, out cov[i]);
                }
                return cov;
            }

            // Resolve the effective mode up front (probes the export exactly once). This is what the
            // atlas format follows, so MSDF is only ever attempted when it can actually be produced.
            if (EffectiveRenderMode == UniTextRenderMode.Msdf)
            {
                var msdf = RenderMsdfBatch(batch);
                if (msdf != null)
                    return msdf;
                // Should not happen after a successful probe, but stay safe: degrade permanently.
                if (!msdfFallbackWarned)
                {
                    msdfFallbackWarned = true;
                    Cat.MeowWarnFormat("[MSDF] {0}: outline rendering failed after a positive probe; falling back to SDF.", name);
                }
                effectiveRenderMode = UniTextRenderMode.SDF;
                msdfOutlineUnavailable = true;
            }

#if !UNITY_WEBGL || UNITY_EDITOR
            return sdfFacePool.RenderSdfBatch(batch.filteredGlyphs, batch.pointSize,
                FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP, batch.spread);
#else
            var face = EnsureFTFace();
            if (face == IntPtr.Zero) return null;
            var rendered = new SdfRenderedGlyph[batch.filteredGlyphs.Count];
            for (int i = 0; i < batch.filteredGlyphs.Count; i++)
                SdfGlyphRenderer.TryRender(face, batch.filteredGlyphs[i], batch.pointSize,
                    FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP, batch.spread, out rendered[i]);
            return rendered;
#endif
        }

        /// <summary>
        /// Renders a batch as MSDF using the injected source (tests) or a dedicated FreeType face
        /// via the native outline export. Returns null only on a hard failure after the up-front
        /// probe already reported the source available. Rendered sequentially to bound memory.
        /// </summary>
        private SdfRenderedGlyph[] RenderMsdfBatch(PreparedBatch batch)
        {
            Msdf.IGlyphOutlineSource source = injectedOutlineSource;
            IntPtr face = IntPtr.Zero;

            if (source == null)
            {
                face = EnsureMsdfFace();
                if (face == IntPtr.Zero) return null;
                source = new Msdf.FreeTypeOutlineSource(face,
                    (gi, ppem) =>
                    {
                        if (!FT.SetPixelSize(face, ppem)) return false;
                        return FT.LoadGlyph(face, gi, FT.LOAD_DEFAULT | FT.LOAD_NO_HINTING);
                    });
            }

            if (!source.IsAvailable)
                return null;

            var list = batch.filteredGlyphs;
            var results = new SdfRenderedGlyph[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                if (!Msdf.MsdfGlyphRenderer.TryRender(face, list[i], batch.pointSize, batch.spread,
                        source, out results[i], out bool unavailable))
                {
                    if (unavailable)
                    {
                        // Return already-rendered pixels to the pool before bailing.
                        for (int j = 0; j <= i; j++)
                            if (results[j].sdfPixels != null)
                            {
                                UniTextArrayPool<byte>.Return(results[j].sdfPixels);
                                results[j].sdfPixels = null;
                            }
                        return null;
                    }
                }
            }
            return results;
        }

        private IntPtr EnsureMsdfFace()
        {
            if (msdfFace != IntPtr.Zero) return msdfFace;
            if (fontData == null || fontData.Length == 0) return IntPtr.Zero;
            if (!FT.IsInitialized) FT.Initialize();
            msdfFace = FT.LoadFace(fontData, cachedFaceIndex < 0 ? 0 : cachedFaceIndex);
            return msdfFace;
        }

        /// <summary>
        /// Phase 3: Packs rendered glyphs into atlas textures and updates lookup dictionaries.
        /// Must be called on main thread (Unity API + dictionary mutation).
        /// </summary>
        /// <param name="renderedObj">Rendered glyph data from <see cref="RenderPreparedBatch"/>.</param>
        /// <param name="batch">Prepared batch from <see cref="PrepareGlyphBatch"/>.</param>
        /// <returns>Number of glyphs successfully added.</returns>
        public virtual int PackRenderedBatch(object renderedObj, PreparedBatch batch)
        {
            if (renderedObj == null) return 0;
            var rendered = (SdfRenderedGlyph[])renderedObj;

            var metricsConversion = batch.metricsConversion;
            var spread = batch.spread;
            int totalAdded = 0;

            unsafe
            {
            byte* cachedAtlasPtr = null;
            int cachedAtlasW = 0;
            Texture2D cachedAtlasTex = null;

            for (int i = 0; i < rendered.Length; i++)
            {
                ref var r = ref rendered[i];
                if (!r.isValid) continue;

                var glyphIndex = r.glyphIndex;
                if (glyphLookupDictionary.ContainsKey(glyphIndex))
                {
                    ReturnSdfPixels(ref r);
                    continue;
                }

                float advanceDU = (r.metricAdvanceX26_6 / 64f) * metricsConversion;

                if (r.sdfPixels == null)
                {
                    var glyph = new Glyph(glyphIndex,
                        new GlyphMetrics(
                            r.metricWidth * metricsConversion,
                            r.metricHeight * metricsConversion,
                            r.metricBearingX * metricsConversion,
                            r.metricBearingY * metricsConversion,
                            advanceDU),
                        GlyphRect.zero, 0);
                    glyphTable.Add(glyph);
                    glyphLookupDictionary[glyphIndex] = glyph;
                    glyphIndexList.Add(glyphIndex);
                    totalAdded++;
                    continue;
                }

                if (atlasTextures == null || atlasTextures.Count == 0)
                    CreateNewAtlasTexture();

                if (!TryPackGlyphShelf(r.bmpWidth, r.bmpHeight, out var packRect))
                {
                    atlasTextures[^1].Apply(false, false);
                    CreateNewAtlasTexture();
                    cachedAtlasTex = null;
                    if (!TryPackGlyphShelf(r.bmpWidth, r.bmpHeight, out packRect))
                    {
                        Cat.MeowWarnFormat("[PackRenderedBatch] {0}: glyph {1} too large for atlas ({2}x{3})",
                            name, glyphIndex, r.bmpWidth, r.bmpHeight);
                        ReturnSdfPixels(ref r);
                        continue;
                    }
                }

                var curAtlas = atlasTextures[^1];
                if (curAtlas != cachedAtlasTex)
                {
                    cachedAtlasTex = curAtlas;
                    cachedAtlasW = curAtlas.width;
                    var raw = curAtlas.GetRawTextureData<byte>();
                    cachedAtlasPtr = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(raw);
                }
                int glyphChannels = r.channels > 0 ? r.channels : 1;
                int atlasChannels = curAtlas.format == TextureFormat.RGB24 ? 3
                    : curAtlas.format == TextureFormat.RGBA32 ? 4 : 1;
                if (glyphChannels != atlasChannels)
                {
                    // Coherence guard: a glyph's channel count must match the atlas format. If they
                    // ever disagree (e.g. an SDF-fallback glyph reaching an RGB atlas), skip it
                    // rather than write mis-strided bytes that masquerade as multi-channel data.
                    Cat.MeowWarnFormat("[PackRenderedBatch] {0}: glyph {1} channels ({2}) != atlas channels ({3}); skipped.",
                        name, glyphIndex, glyphChannels, atlasChannels);
                    ReturnSdfPixels(ref r);
                    continue;
                }
                CopySdfBitmapToAtlas(r.sdfPixels, r.bmpWidth, r.bmpHeight, packRect.x, packRect.y, cachedAtlasW, cachedAtlasPtr, glyphChannels);
                ReturnSdfPixels(ref r);

                int outlineW = r.bmpWidth - 2 * spread;
                int outlineH = r.bmpHeight - 2 * spread;
                if (outlineW < 0) outlineW = 0;
                if (outlineH < 0) outlineH = 0;

                float outlineBearingX = (r.bitmapLeft + spread) * metricsConversion;
                float outlineBearingY = (r.bitmapTop - spread) * metricsConversion;

                var glyphRect = new GlyphRect(packRect.x + spread, packRect.y + spread, outlineW, outlineH);

                var glyphObj = new Glyph(glyphIndex,
                    new GlyphMetrics(
                        outlineW * metricsConversion,
                        outlineH * metricsConversion,
                        outlineBearingX,
                        outlineBearingY,
                        advanceDU),
                    glyphRect, atlasTextures.Count - 1);

                glyphTable.Add(glyphObj);
                glyphLookupDictionary[glyphIndex] = glyphObj;
                glyphIndexList.Add(glyphIndex);
                totalAdded++;
            }
            }

            if (atlasTextures is { Count: > 0 })
                atlasTextures[^1].Apply(false, false);

            return totalAdded;
        }

        /// <summary>
        /// Renders glyphs as SDF using FreeType and packs them into atlas textures.
        /// Convenience wrapper that calls PrepareGlyphBatch → RenderPreparedBatch → PackRenderedBatch.
        /// </summary>
        /// <param name="glyphIndices">List of glyph indices to add.</param>
        /// <returns>Number of glyphs successfully added.</returns>
        public virtual int TryAddGlyphsBatch(List<uint> glyphIndices)
        {
            var batch = PrepareGlyphBatch(glyphIndices);
            if (batch == null) return 0;
            var rendered = RenderPreparedBatch(batch.Value);
            return PackRenderedBatch(rendered, batch.Value);
        }

        private static void ReturnSdfPixels(ref SdfRenderedGlyph r)
        {
            if (r.sdfPixels != null)
            {
                UniTextArrayPool<byte>.Return(r.sdfPixels);
                r.sdfPixels = null;
            }
        }

        /// <summary>
        /// Copies a pre-rendered distance-field bitmap to the atlas. Handles 1-channel (Alpha8 SDF)
        /// and 3-channel (RGB24 MSDF) payloads; the source is already Y-oriented for the atlas.
        /// </summary>
        private static unsafe void CopySdfBitmapToAtlas(byte[] sdfPixels, int bw, int bh,
            int packX, int packY, int atlasW, byte* atlasPtr, int channels)
        {
            int rowBytes = bw * channels;
            int atlasStride = atlasW * channels;
            fixed (byte* src = sdfPixels)
            {
                for (int y = 0; y < bh; y++)
                {
                    int dstOffset = (packY + y) * atlasStride + packX * channels;
                    Buffer.MemoryCopy(src + y * rowBytes, atlasPtr + dstOffset, rowBytes, rowBytes);
                }
            }
        }

        /// <summary>
        /// Tries to pack a glyph bitmap into the current atlas using shelf-based packing.
        /// </summary>
        /// <param name="w">Bitmap width in pixels.</param>
        /// <param name="h">Bitmap height in pixels.</param>
        /// <param name="result">Output: position and size in the atlas.</param>
        /// <returns>True if packed successfully, false if atlas is full.</returns>
        protected bool TryPackGlyphShelf(int w, int h, out GlyphRect result)
        {
            result = default;
            int pw = w + PackingSpacing;
            int ph = h + PackingSpacing;

            if (shelfX + pw > atlasSize)
            {
                shelfY += shelfHeight + PackingSpacing;
                shelfX = 0;
                shelfHeight = 0;
            }

            if (shelfY + ph > atlasSize)
                return false;

            result = new GlyphRect(shelfX, shelfY, w, h);
            shelfX += pw;

            if (ph > shelfHeight)
                shelfHeight = ph;

            usedGlyphRects?.Add(result);
            return true;
        }

        protected unsafe void CreateNewAtlasTexture()
        {
            var mode = EffectiveRenderMode;
            var texFormat = mode == UniTextRenderMode.SDF ? TextureFormat.Alpha8
                : mode == UniTextRenderMode.Msdf ? TextureFormat.RGB24
                : TextureFormat.RGBA32;

            // Regular (non-color) Smooth/Mono render single-channel coverage — store it in Alpha8 so
            // the channel-coherence guard in PackRenderedBatch accepts it. Color Smooth (EmojiFont)
            // keeps RGBA32. SDF/MSDF are unchanged.
            if (IsCoverageBitmapMode)
                texFormat = TextureFormat.Alpha8;

            var texture = new Texture2D(atlasSize, atlasSize, texFormat, false);

            // Filtering: Mono and pixel-perfect require Point (no bilinear blur, crisp integer scale);
            // Smooth (AA coverage) uses the default Bilinear so its gradients interpolate smoothly.
            // SDF/MSDF and color Smooth keep the historical Bilinear default — byte-for-byte unchanged.
            if (PixelPerfectActive || (IsCoverageBitmapMode && mode == UniTextRenderMode.Mono))
                texture.filterMode = FilterMode.Point;

            var rawData = texture.GetRawTextureData<byte>();
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(
                Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(rawData),
                rawData.Length);

            atlasTextures ??= new List<Texture2D>();
            atlasTextures.Add(texture);

            texture.name = name + " Atlas " + (atlasTextures.Count - 1);
            texture.hideFlags = HideFlags.DontSave;

            freeGlyphRects ??= new List<GlyphRect>();
            freeGlyphRects.Clear();
            freeGlyphRects.Add(new GlyphRect(0, 0, atlasSize - PackingSpacing, atlasSize - PackingSpacing));

            usedGlyphRects ??= new List<GlyphRect>();
            usedGlyphRects.Clear();

            shelfX = 0;
            shelfY = 0;
            shelfHeight = 0;

            Cat.MeowFormat("[CreateNewAtlasTexture] {0}: created {1}x{2} {3} atlas (total: {4})", name, atlasSize, atlasSize, texFormat, atlasTextures.Count);
        }

        #endregion

        
        #region Static Creation Methods

        /// <summary>
        /// Creates a new font asset from raw font file bytes.
        /// </summary>
        /// <param name="fontBytes">TTF or OTF font file data.</param>
        /// <param name="samplingPointSize">Point size for rendering glyphs to atlas.</param>
        /// <param name="spreadStrength">SDF spread as fraction of point size (0-1). Padding = PointSize * SpreadStrength.</param>
        /// <param name="renderMode">Glyph rendering mode (SDF, bitmap, etc.).</param>
        /// <param name="atlasSize">Atlas texture size (square).</param>
        /// <returns>New font asset, or null if creation failed.</returns>
        public static UniTextFont CreateFontAsset(byte[] fontBytes, int samplingPointSize = 90, float spreadStrength = 0.1f,
            UniTextRenderMode renderMode = UniTextRenderMode.SDF, int atlasSize = 1024)
        {
            if (fontBytes == null || fontBytes.Length == 0)
            {
                Debug.LogError("UniTextFontAsset: Cannot create font asset from null or empty byte array.");
                return null;
            }

            if (!FT.IsInitialized) FT.Initialize();
            var face = FT.LoadFace(fontBytes, 0);
            if (face == IntPtr.Zero)
            {
                Debug.LogError("UniTextFontAsset: Failed to load font face from byte array.");
                return null;
            }

            var fontAsset = CreateInstance<UniTextFont>();
            fontAsset.fontData = fontBytes;
            fontAsset.fontDataHash = ComputeFontDataHash(fontBytes);

            int realUpem = Shaper.GetUpemFromFontData(fontBytes);
            fontAsset.unitsPerEm = realUpem;

            fontAsset.faceInfo = BuildFullFaceInfo(face, samplingPointSize);

            FT.UnloadFace(face);

            fontAsset.atlasSize = atlasSize;
            fontAsset.spreadStrength = Mathf.Clamp(spreadStrength, 0.1f, 1f);
            fontAsset.atlasRenderMode = renderMode;

            fontAsset.ReadFontAssetDefinition();

            return fontAsset;
        }

        /// <summary>
        /// Builds a complete FaceInfo from FreeType face data.
        /// Reads hhea (ascender/descender), OS/2 (cap height, x-height, strikeout, super/subscript),
        /// post (underline), and name (family/style) tables.
        /// </summary>
        internal static FaceInfo BuildFullFaceInfo(IntPtr face, int pointSize)
        {
            var ftInfo = FT.GetFaceInfo(face);
            var ext = FT.GetExtendedFaceInfo(face);

            var fi = new FaceInfo
            {
                faceIndex = ftInfo.faceIndex,
                familyName = ext.familyName,
                styleName = ext.styleName,
                pointSize = pointSize,
                unitsPerEm = ftInfo.unitsPerEm,
                ascentLine = ftInfo.ascender,
                descentLine = ftInfo.descender,
                lineHeight = ftInfo.height,
                underlineOffset = ext.underlinePosition,
                underlineThickness = ext.underlineThickness,
            };

            if (fi.lineHeight <= 0)
                fi.lineHeight = Mathf.RoundToInt((fi.ascentLine - fi.descentLine) * 1.2f);

            if (ext.hasOS2)
            {
                fi.capLine = ext.capHeight;
                fi.meanLine = ext.xHeight;
                fi.strikethroughOffset = ext.strikeoutPosition;
                fi.strikethroughThickness = ext.strikeoutSize;
                fi.superscriptOffset = ext.superscriptYOffset;
                fi.superscriptSize = ext.superscriptYSize;
                fi.subscriptOffset = ext.subscriptYOffset;
                fi.subscriptSize = ext.subscriptYSize;
            }
            else
            {
                int capBearingY = FT.GetGlyphBearingYUnscaled(face, 'H');
                fi.capLine = capBearingY > 0 ? capBearingY : Mathf.RoundToInt(fi.ascentLine * 0.75f);

                int xBearingY = FT.GetGlyphBearingYUnscaled(face, 'x');
                fi.meanLine = xBearingY > 0 ? xBearingY : Mathf.RoundToInt(fi.ascentLine * 0.5f);

                fi.strikethroughOffset = Mathf.RoundToInt(fi.meanLine * 0.5f);
                fi.strikethroughThickness = fi.underlineThickness > 0
                    ? fi.underlineThickness
                    : Mathf.RoundToInt(fi.ascentLine * 0.05f);

                fi.superscriptOffset = fi.ascentLine;
                fi.superscriptSize = pointSize;
                fi.subscriptOffset = fi.descentLine;
                fi.subscriptSize = pointSize;
            }

            int spaceAdvance = FT.GetGlyphAdvanceUnscaled(face, ' ');
            fi.tabWidth = spaceAdvance > 0 ? spaceAdvance : fi.ascentLine;

            return fi;
        }

        #endregion

        #region Dynamic Data Management

        /// <summary>
        /// Clears all dynamically generated glyph data and resets atlas textures.
        /// </summary>
        /// <remarks>
        /// Call this to force re-rendering of all glyphs. Useful when changing
        /// atlas parameters or for reducing memory usage.
        /// </remarks>
        public void ClearDynamicData()
        {
            Cat.MeowFormat("[ClearDynamicData] {0}: CALLED. glyphTable={1}, glyphLookup={2}, atlas={3}\n{4}",
                name,
                glyphTable?.Count ?? -1,
                glyphLookupDictionary?.Count ?? -1,
                atlasTextures?.Count ?? -1,
                UnityEngine.StackTraceUtility.ExtractStackTrace());

            glyphTable?.Clear();
            characterTable?.Clear();

            glyphLookupDictionary?.Clear();
            characterLookupDictionary?.Clear();
            glyphIndexList?.Clear();

            usedGlyphRects?.Clear();
            freeGlyphRects?.Clear();

            shelfX = 0;
            shelfY = 0;
            shelfHeight = 0;

            if (atlasTextures != null)
            {
                foreach (var texture in atlasTextures)
                    if (texture != null)
                        ObjectUtils.SafeDestroy(texture);

                atlasTextures.Clear();
            }

            ReleaseFTFace();

#if !UNITY_WEBGL || UNITY_EDITOR
            sdfFacePool?.Dispose();
            sdfFacePool = null;
#endif

            Shaper.ClearCache(GetCachedInstanceId());
            Cat.Meow($"UniTextFont [{name}]: Dynamic data cleared. Atlas will regenerate at runtime.");

            Changed?.Invoke();
        }

        public void InvokeChanged()
        {
            Changed?.Invoke();
        }
        
        #endregion

        #region Lifecycle

        private void OnEnable() => loadedFonts.Add(this);

        private void OnDisable() => loadedFonts.Remove(this);

        private void OnDestroy()
        {
            ReleaseFTFace();

#if !UNITY_WEBGL || UNITY_EDITOR
            sdfFacePool?.Dispose();
            sdfFacePool = null;
#endif

            if (atlasTextures != null)
            {
                foreach (var texture in atlasTextures)
                {
                    ObjectUtils.SafeDestroy(texture);
                }
                atlasTextures.Clear();
            }
        }

        /// <summary>
        /// Clears dynamic data for all loaded font assets and invalidates shared caches.
        /// </summary>
        public static void ClearRuntimeData()
        {
            foreach (var font in loadedFonts)
                font.ClearDynamicData();

            SharedFontCache.Clear();
        }

        #endregion

        #region Editor Support

    #if UNITY_EDITOR

        [SerializeField]
        [Tooltip("Unity Font asset to sync with (Editor only).")]
        public Font sourceFont;

        private void OnValidate()
        {
            Cat.MeowFormat("[UniTextFont.OnValidate] {0}: glyphLookup={1}, glyphTable={2}, atlas={3}",
                name, glyphLookupDictionary?.Count ?? -1, glyphTable?.Count ?? -1, atlasTextures?.Count ?? -1);
            Changed?.Invoke();
        }

        public void SetFontData(byte[] data)
        {
            ReleaseFTFace();
            fontData = data;
            fontDataHash = ComputeFontDataHash(data);

            if (data != null && data.Length > 0)
            {
                var face = EnsureFTFace();
                if (face != IntPtr.Zero)
                {
                    int ptSize = faceInfo.pointSize > 0 ? faceInfo.pointSize : 90;

                    faceInfo = BuildFullFaceInfo(face, ptSize);
                    unitsPerEm = faceInfo.unitsPerEm;
                }
            }
        }

        public void UpdateFromSourceFont()
        {
            if (sourceFont == null) return;

            var fontPath = UnityEditor.AssetDatabase.GetAssetPath(sourceFont);
            if (!string.IsNullOrEmpty(fontPath))
            {
                var bytes = System.IO.File.ReadAllBytes(fontPath);
                if (bytes.Length > 0)
                    SetFontData(bytes);
            }
        }
    #endif

        #endregion
    }

    /// <summary>
    /// Represents a character mapping from Unicode codepoint to glyph.
    /// </summary>
    /// <remarks>
    /// Stores the association between a Unicode codepoint and its corresponding
    /// glyph in the font. The glyph reference is resolved at runtime.
    /// </remarks>
    [Serializable]
    internal class UniTextCharacter
    {
        /// <summary>Unicode codepoint for this character.</summary>
        public uint unicode;
        /// <summary>Index of the glyph in the font's glyph table.</summary>
        public uint glyphIndex;
        /// <summary>Runtime reference to the glyph (not serialized).</summary>
        [NonSerialized] public Glyph glyph;

        /// <summary>Default constructor for serialization.</summary>
        public UniTextCharacter()
        {
        }

        /// <summary>
        /// Creates a character with the specified unicode and glyph index.
        /// </summary>
        public UniTextCharacter(uint unicode, uint glyphIndex)
        {
            this.unicode = unicode;
            this.glyphIndex = glyphIndex;
        }

        /// <summary>
        /// Creates a character with the specified unicode and glyph.
        /// </summary>
        public UniTextCharacter(uint unicode, Glyph glyph)
        {
            this.unicode = unicode;
            this.glyph = glyph;
            glyphIndex = glyph.index;
        }
    }
}
