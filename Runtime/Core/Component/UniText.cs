using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

// R2 sub-task 4: UniText is the deprecation BRIDGE — it still holds the [Obsolete] UniTextAppearance
// field/property so existing assets load and render through the shim during the deprecation window.
// Suppress the obsolete-usage warning for this file to keep the package warning-clean (#pragma).
#pragma warning disable 618

namespace LightSide
{
    /// <summary>
    /// Main text rendering component for Unity UI with full Unicode support.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UniText is a drop-in replacement for Unity's Text/TextMeshPro with proper support for:
    /// <list type="bullet">
    /// <item>Bidirectional text (Arabic, Hebrew) via UAX #9</item>
    /// <item>Complex script shaping (Devanagari, Thai, etc.) via HarfBuzz</item>
    /// <item>Proper line breaking via UAX #14</item>
    /// <item>Color emoji rendering</item>
    /// <item>Extensible markup system via <see cref="IParseRule"/> (HTML tags, Markdown, custom markers)</item>
    /// </list>
    /// </para>
    /// <para>
    /// The component uses a batched rendering pipeline with optional parallel processing
    /// for multiple UniText instances. Text is processed in two passes: shaping (can be parallel)
    /// and mesh generation (main thread for atlas updates).
    /// </para>
    /// </remarks>
    /// <seealso cref="TextProcessor"/>
    /// <seealso cref="UniTextMeshGenerator"/>
    /// <seealso cref="BaseModifier"/>
    [RequireComponent(typeof(CanvasRenderer))]
    [RequireComponent(typeof(RectTransform))]
    [ExecuteAlways]
    public partial class UniText : MaskableGraphic
    #if UNITY_EDITOR
        , ISerializationCallbackReceiver
    #endif
    {
        /// <summary>Flags indicating which parts of the text need rebuilding.</summary>
        [Flags]
        public enum DirtyFlags
        {
            /// <summary>No rebuild needed.</summary>
            None = 0,
            /// <summary>Color changed, vertex colors need update.</summary>
            Color = 1 << 0,
            /// <summary>Alignment changed, positions need recalculation.</summary>
            Alignment = 1 << 1,
            /// <summary>Layout changed, line breaking needs recalculation.</summary>
            Layout = 1 << 2,
            /// <summary>Font size changed.</summary>
            FontSize = 1 << 3,
            /// <summary>Font asset changed, full rebuild required.</summary>
            Font = 1 << 4,
            /// <summary>Text direction changed.</summary>
            Direction = 1 << 5,
            /// <summary>Text content changed, full rebuild required.</summary>
            Text = 1 << 6,
            /// <summary>Material changed.</summary>
            Material = 1 << 7,
            /// <summary>Layout or font size changed.</summary>
            LayoutRebuild = Layout | FontSize,
            /// <summary>Text, font, or direction changed.</summary>
            FullRebuild = Text | Font | Direction,
            /// <summary>Everything needs rebuilding.</summary>
            All = Color | Alignment | Layout | FontSize | FullRebuild
        }

        #region Serialized Fields

        [TextArea(3, 10)]
        [SerializeField]
        [Tooltip("The text content to display. Supports Unicode, emoji, and custom markup.")]
        private string text = "";

        [NonSerialized] private ReadOnlyMemory<char> sourceText;
        [NonSerialized] private bool isTextFromBuffer;
        
        [SerializeField]
        [Tooltip("Font collection with main font and fallback chain.")]
        private UniTextFontStack fontStack;

        [SerializeField]
        [Tooltip("Material and rendering appearance settings.")]
        private UniTextAppearance appearance;

        [SerializeField]
        [Tooltip("Base font size in points.")]
        private float fontSize = 36f;

        [SerializeField]
        [Tooltip("Phase 2: base font weight (CSS usWeightClass; 400 normal, 700 bold). Selects the matching face from a FontFamily, or drives the variable 'wght' axis; synthetic bold applies only when the family has no heavier face.")]
        private int fontWeight = FontStyleSpec.NormalWeight;

        [SerializeField]
        [Tooltip("Phase 2: base font style (Normal/Italic/Oblique). Selects the matching face from a FontFamily or drives the variable italic/slant axis; synthetic slant applies only when the family has no italic/oblique face.")]
        private StyleAxis fontStyleAxis = StyleAxis.Normal;
        
        [SerializeField]
        [Tooltip("Base text direction. Auto detects from first strong directional character.")]
        private TextDirection baseDirection = TextDirection.Auto;

        [SerializeField]
        [Tooltip("Enable word wrapping at container boundaries.")]
        private bool wordWrap = true;

        [SerializeField]
        [Tooltip("Vertical overflow handling: Overflow spills out of the rect, Truncate drops lines that do not fit, Ellipsis truncates and ends the last line with an ellipsis, Clip hides pixels outside the rect.")]
        private TextOverflow overflow = TextOverflow.Overflow;
        
        [SerializeField]
        [Tooltip("Horizontal text alignment within the container.")]
        private HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left;

        [SerializeField]
        [Tooltip("Vertical text alignment within the container.")]
        private VerticalAlignment verticalAlignment = VerticalAlignment.Top;

        [SerializeField]
        [Tooltip("Top edge metric for text box trimming. CapHeight removes space above capital letters.")]
        private TextOverEdge overEdge = TextOverEdge.Ascent;

        [SerializeField]
        [Tooltip("Bottom edge metric for text box trimming. Baseline removes space below the last line.")]
        private TextUnderEdge underEdge = TextUnderEdge.Descent;

        [SerializeField]
        [Tooltip("How extra leading from line-height is distributed: HalfLeading (CSS), LeadingAbove (Figma), LeadingBelow (Android).")]
        private LeadingDistribution leadingDistribution = LeadingDistribution.HalfLeading;

        [SerializeField]
        [Tooltip("Automatically adjust font size to fit container.")]
        private bool autoSize;

        [SerializeField]
        [Tooltip("Minimum font size when auto-sizing.")]
        private float minFontSize = 10f;

        [SerializeField]
        [Tooltip("Maximum font size when auto-sizing.")]
        private float maxFontSize = 72f;

        [SerializeField]
        [Tooltip("Modifier/rule pairs that define how markup is parsed and applied (e.g., color, bold, links).")]
        private StyledList<ModRegister> modRegisters = new();

        [SerializeField]
        [Tooltip("Shared modifier configurations (ScriptableObjects) to apply in addition to local modRegisters.")]
        private StyledList<ModRegisterConfig> modRegisterConfigs = new();

        /// <summary>Runtime copies of modRegisterConfigs to avoid ownership conflicts.</summary>
        private readonly List<ModRegisterConfig> runtimeConfigCopies = new();
        
        [SerializeReference]
        [TypeSelector]
        [Tooltip("Text highlighter for visual feedback (click, hover, selection). Set to null to disable.")]
        private TextHighlighter highlighter = new DefaultTextHighlighter();

        #endregion

        #region Runtime State

        private TextProcessor textProcessor;
        private UniTextFontProvider fontProvider;
        [NonSerialized] private bool loggedMissingFonts;
        private UniTextMeshGenerator meshGenerator;
        private AttributeParser attributeParser;
        private UniTextBuffers buffers;

        private DirtyFlags dirtyFlags = DirtyFlags.All;

        /// <summary>Gets the current dirty flags indicating what needs rebuilding.</summary>
        public DirtyFlags CurrentDirtyFlags => dirtyFlags;
        private bool textIsParsed;
        private bool isRegisteredDirty;

        private float resultWidth;
        private float resultHeight;

        /// <summary>Cached sub-mesh renderer data to avoid GetComponent calls.</summary>
        private struct SubMeshRenderer
        {
            public CanvasRenderer renderer;
            public RectTransform rectTransform;
        }

        private readonly List<SubMeshRenderer> subMeshRenderers = new();
        private readonly List<Material> stencilMaterials = new();
        private List<UniTextRenderData> renderData;

        // Geometry-identity re-upload skip (see DoApplyMesh): the fingerprint of the geometry
        // currently on the renderers, so a rebuild producing identical geometry can skip the Unity
        // mesh upload. Reset whenever the renderers are cleared, so a re-populate always uploads.
        private ulong lastAppliedGeometryFingerprint;
        private bool hasAppliedGeometry;

        /// <summary>Per-component override for the Render-Architecture R2 unified single-renderer path.</summary>
        public enum UnifiedRendererMode { UseProjectSetting, ForceOn, ForceOff }

        [SerializeField]
        [Tooltip("Render Architecture R2 unified single-renderer path for THIS component. " +
                 "UseProjectSetting follows UniTextSettings.UseUnifiedRenderer; ForceOn/ForceOff override it.")]
        private UnifiedRendererMode unifiedRendererMode = UnifiedRendererMode.UseProjectSetting;

        /// <summary>
        /// Per-component override for the Render-Architecture R2 unified single-renderer path.
        /// <see cref="UnifiedRendererMode.UseProjectSetting"/> follows
        /// <see cref="UniTextSettings.UseUnifiedRenderer"/>; ForceOn/ForceOff override it. Setting it
        /// marks the component dirty so the next rebuild uses the chosen path.
        /// </summary>
        public UnifiedRendererMode UnifiedRenderer
        {
            get => unifiedRendererMode;
            set
            {
                if (unifiedRendererMode == value) return;
                unifiedRendererMode = value;
                // Changing the render path must re-run mesh generation (the unified builder re-shades
                // glyphs). SetVerticesDirty() routes to UniText's empty Rebuild() and does nothing, so
                // drive UniText's own dirty/rebuild path instead (same as text/fontSize/appearance).
                SetDirty(DirtyFlags.Material);
            }
        }

        /// <summary>
        /// Whether this component renders through the R2 unified single-CanvasRenderer-per-draw-group
        /// path (UniText/Uber + shared Texture2DArray + style table). Resolves the per-component
        /// <see cref="unifiedRendererMode"/> against the project default. False keeps the legacy path.
        /// </summary>
        public bool UseUnifiedRenderer => unifiedRendererMode switch
        {
            UnifiedRendererMode.ForceOn => true,
            UnifiedRendererMode.ForceOff => false,
            _ => UniTextSettings.UseUnifiedRenderer,
        };

        [SerializeField]
        [Tooltip("Render Architecture R2 sub-task 1: when ON, the unified render path shades every " +
                 "glyph from the component Style below INSTEAD of synthesising one from a legacy " +
                 "appearance/material via AppearanceStyleShim. OFF keeps the shim fallback so a " +
                 "component still referencing a legacy UniTextAppearance keeps rendering unchanged.")]
        private bool overrideStyle = false;

        [SerializeField]
        [Tooltip("Render Architecture R2 sub-task 1: the component-authored text style (face / outline " +
                 "/ underlay-shadow / glow). Replaces a UniTextAppearance + material asset. Active only " +
                 "when Override Style is ON and the unified renderer is used.")]
        private UniTextStyle style = UniTextStyle.Default;

        /// <summary>
        /// Render-Architecture R2 sub-task 1: whether this component shades from its own
        /// <see cref="Style"/> (true) or falls back to the <see cref="AppearanceStyleShim"/> reading a
        /// legacy appearance/material (false). Only consulted on the unified render path.
        /// </summary>
        public bool OverrideStyle
        {
            get => overrideStyle;
            set
            {
                if (overrideStyle == value) return;
                overrideStyle = value;
                // Re-shade via UniText's own rebuild path, not the inherited no-op Rebuild().
                SetDirty(DirtyFlags.Material);
            }
        }

        /// <summary>
        /// The component-authored <see cref="UniTextStyle"/>. Used by the unified render path when
        /// <see cref="OverrideStyle"/> is true. Setting it marks the component dirty so the next
        /// rebuild re-shades.
        /// </summary>
        public UniTextStyle Style
        {
            get => style;
            set
            {
                if (style.Equals(value)) return;
                style = value;
                // Re-generate the mesh so the new style re-shades (unified path). The inherited
                // SetVerticesDirty() goes to UniText's empty Rebuild(); use the real dirty path.
                if (overrideStyle) SetDirty(DirtyFlags.Material);
            }
        }

        /// <summary>R2 unified path: per-component builder + its merged (≤2) output. Lazy; disposed in OnDestroy.</summary>
        private UnifiedRenderBuilder unifiedBuilder;
        private List<UniTextRenderData> unifiedRenderData;

        [SerializeField]
        [Tooltip("Render Architecture R2 sub-task 2: named styles addressable from <style=Name> per-span " +
                 "markup. Optional — only needed if the text uses <style=…> tags.")]
        private UniTextStyleSheet styleSheet;

        /// <summary>
        /// Render-Architecture R2 sub-task 2: the <see cref="UniTextStyleSheet"/> that resolves
        /// <c>&lt;style=Name&gt;</c> per-span markup. Null when no named styles are used.
        /// </summary>
        public UniTextStyleSheet StyleSheet
        {
            get => styleSheet;
            set { if (styleSheet == value) return; styleSheet = value; SetDirty(DirtyFlags.Material); }
        }

        /// <summary>R2 sub-task 2: per-component collector mapping composed per-span styles to local ids (0 = base).</summary>
        private SpanStyleCollector spanStyleCollector;
        /// <summary>True once this component's span-style OnBeforeMesh/OnGlyph coordinator is subscribed.</summary>
        private bool spanStyleHooked;
        /// <summary>The component base GlyphStyle captured at generation start, that span overrides layer onto.</summary>
        private GlyphStyle spanBaseStyle;
        /// <summary>
        /// Threading: the base <see cref="GlyphStyle"/> resolved on the MAIN-THREAD prepare step
        /// (<see cref="PrepareForMeshGenerationMainThread"/>). Synthesising it from the legacy
        /// appearance reads Material properties (main-thread only), so it must not be computed inside
        /// the worker-thread <c>OnSpanStyleBeforeMesh</c>. When <see cref="hasPreparedBaseStyle"/> is
        /// set, the coordinator uses this instead of calling the shim on the worker.
        /// </summary>
        private GlyphStyle preparedBaseStyle;
        private bool hasPreparedBaseStyle;

        private Rect cachedClipRect;
        private bool cachedValidClip;
        private Vector4 cachedClipSoftness;
        private int cachedStencilDepth;
        private bool stencilDepthDirty = true;
        private Vector2 lastSyncedPivot;

        private float lastKnownWidth = -1;
        private float lastKnownHeight = -1;
        private RenderMode cachedCanvasRenderMode;

        /// <summary>Raised before text is rebuilt.</summary>
        public event Action Rebuilding;

        /// <summary>Raised when the RectTransform height changes.</summary>
        public event Action RectHeightChanged;

        /// <summary>Raised when dirty flags change, indicating what needs rebuilding.</summary>
        public event Action<DirtyFlags> DirtyFlagsChanged;

        #endregion

        #region Public API

        /// <summary>Gets the text processor instance handling shaping and layout.</summary>
        public TextProcessor TextProcessor => textProcessor;

        /// <summary>Gets the mesh generator instance.</summary>
        public UniTextMeshGenerator MeshGenerator => meshGenerator;

        /// <summary>Gets the font provider managing font assets and fallbacks.</summary>
        public UniTextFontProvider FontProvider => fontProvider;

        /// <summary>Gets the buffer container for text processing.</summary>
        public UniTextBuffers Buffers => buffers;

        /// <summary>Gets the text with markup stripped.</summary>
        public string CleanText => attributeParser?.CleanText ?? Text;

        /// <summary>Gets or sets the text highlighter for visual feedback on interactions.</summary>
        public TextHighlighter Highlighter
        {
            get => highlighter;
            set
            {
                if (highlighter == value) return;
                highlighter?.Destroy();
                highlighter = value;
                highlighter?.Initialize(this);
            }
        }

        /// <summary>Gets the computed size of the rendered text.</summary>
        public Vector2 ResultSize => new(resultWidth, resultHeight);

        /// <summary>Gets the positioned glyphs after processing.</summary>
        public ReadOnlySpan<PositionedGlyph> ResultGlyphs => textProcessor != null ? textProcessor.PositionedGlyphs : ReadOnlySpan<PositionedGlyph>.Empty;

        /// <summary>Gets the main font from the font collection.</summary>
        public UniTextFont MainFont => fontStack?.MainFont;

        /// <summary>Gets the current effective font size (accounts for auto-sizing).</summary>
        public float CurrentFontSize => autoSize
            ? (cachedEffectiveFontSize > 0 ? cachedEffectiveFontSize : maxFontSize)
            : fontSize;

        /// <summary>Gets the list of registered modifiers.</summary>
        public IReadOnlyList<ModRegister> ModRegisters => modRegisters;

        /// <summary>Gets the list of modifier configuration assets.</summary>
        public IReadOnlyList<ModRegisterConfig> ModRegisterConfigs => modRegisterConfigs;

        /// <summary>Gets all canvas renderers used for sub-meshes.</summary>
        public IEnumerable<CanvasRenderer> CanvasRenderers
        {
            get
            {
                for (var i = 0; i < subMeshRenderers.Count; i++)
                    yield return subMeshRenderers[i].renderer;
            }
        }

        /// <summary>Gets or sets the source text, which may contain markup parsed by registered <see cref="IParseRule"/> implementations.</summary>
        public string Text
        {
            get
            {
                if (isTextFromBuffer)
                {
                    text = new string(sourceText.Span);
                    isTextFromBuffer = false;
                }
                return text;
            }
            set
            {
                if (value != null && value.IndexOf('\r') >= 0)
                    value = NormalizeLineEndings(value);

                if (!isTextFromBuffer && text == value) return;
                text = value;
                sourceText = (value ?? "").AsMemory();
                isTextFromBuffer = false;
                if (sourceText.IsEmpty)
                {
                    DeInit();
                }
                else
                {
                    SetDirty(DirtyFlags.Text);
                }
            }
        }

        /// <summary>
        /// Sets text content from a char array without allocating a string.
        /// Ideal for frequently updated text (timers, scores, etc.).
        /// </summary>
        /// <param name="source">Source character array.</param>
        /// <param name="start">Starting index in the array.</param>
        /// <param name="length">Number of characters to use.</param>
        public void SetText(char[] source, int start, int length)
        {
            var memory = new ReadOnlyMemory<char>(source, start, length);
            SetText(memory);
        }
        
        /// <summary>
        /// Sets text content from a memory without allocating a string.
        /// Ideal for frequently updated text (timers, scores, etc.).
        /// </summary>
        /// <param name="source">Source memory.</param>
        public void SetText(ReadOnlyMemory<char> source)
        {
            sourceText = source;
            isTextFromBuffer = true;
            if (source.Length == 0)
            {
                DeInit();
            }
            else
            {
                SetDirty(DirtyFlags.Text);
            }
        }

        private static string NormalizeLineEndings(string input)
        {
            var crlfCount = 0;
            for (var i = 0; i < input.Length - 1; i++)
            {
                if (input[i] == '\r' && input[i + 1] == '\n')
                    crlfCount++;
            }

            return string.Create(input.Length - crlfCount, input, static (span, src) =>
            {
                var writePos = 0;
                for (var i = 0; i < src.Length; i++)
                {
                    var c = src[i];
                    if (c == '\r')
                    {
                        if (i + 1 < src.Length && src[i + 1] == '\n')
                            continue;
                        span[writePos++] = '\n';
                    }
                    else
                    {
                        span[writePos++] = c;
                    }
                }
            });
        }

        /// <summary>Gets or sets the font collection.</summary>
        public UniTextFontStack FontStack
        {
            get => fontStack;
            set
            {
                if (fontStack == value) return;
                
                UnlistenConfigChanged();
                if (fontStack != null) fontStack.Changed -= OnConfigChanged;
                fontStack = value;
                if (fontStack != null) fontStack.Changed += OnConfigChanged;

                ListenConfigChanged();
                SetDirty(DirtyFlags.Font);
            }
        }

        /// <summary>Gets or sets the appearance configuration.</summary>
        public UniTextAppearance Appearance
        {
            get => appearance;
            set
            {
                if (appearance == value) return;
                UnlistenConfigChanged();

                appearance = value;
                if (fontProvider != null) fontProvider.Appearance = value;
                ListenConfigChanged();
                SetDirty(DirtyFlags.Material);
            }
        }

        /// <summary>Gets or sets the base font size in points.</summary>
        public float FontSize
        {
            get => fontSize;
            set
            {
                if (Mathf.Approximately(fontSize, value)) return;
                fontSize = Mathf.Max(1f, value);
                SetDirty(DirtyFlags.FontSize);
            }
        }

        /// <summary>Gets or sets the base text direction (LTR, RTL, or Auto-detect).</summary>
        public TextDirection BaseDirection
        {
            get => baseDirection;
            set
            {
                if (baseDirection == value) return;
                baseDirection = value;
                SetDirty(DirtyFlags.Direction);
            }
        }

        /// <summary>
        /// Phase 2: base font weight (CSS usWeightClass; 400 normal, 700 bold). When the component's
        /// <see cref="FontStack"/> carries a <see cref="FontFamily"/>, this selects the matching real
        /// face (or drives a variable font's <c>wght</c> axis); synthetic bold is used only when no
        /// heavier face exists.
        /// </summary>
        public int FontWeight
        {
            get => fontWeight;
            set
            {
                var v = Mathf.Clamp(value, 1, 1000);
                if (fontWeight == v) return;
                fontWeight = v;
                SetDirty(DirtyFlags.Text); // full rebuild: itemization + face resolution change
            }
        }

        /// <summary>
        /// Phase 2: base font style (Normal/Italic/Oblique). Selects the matching real face from a
        /// <see cref="FontFamily"/> (or drives the variable italic/slant axis); synthetic slant is
        /// used only when no italic/oblique face exists.
        /// </summary>
        public StyleAxis FontStyleAxis
        {
            get => fontStyleAxis;
            set
            {
                if (fontStyleAxis == value) return;
                fontStyleAxis = value;
                SetDirty(DirtyFlags.Text);
            }
        }

        /// <summary>Gets or sets whether word wrapping is enabled.</summary>
        public bool WordWrap
        {
            get => wordWrap;
            set
            {
                if (wordWrap == value) return;
                wordWrap = value;
                SetDirty(DirtyFlags.Layout);
            }
        }

        /// <summary>
        /// Gets or sets how text that does not fit the RectTransform vertically is handled. Default
        /// <see cref="TextOverflow.Overflow"/> keeps the text spilling out of the rect.
        /// </summary>
        /// <remarks>
        /// <see cref="TextOverflow.Truncate"/> / <see cref="TextOverflow.Ellipsis"/> drop the lines that do not
        /// fit (after auto-size has shrunk the text, when enabled); <see cref="TextOverflow.Clip"/> keeps the
        /// layout and clips the drawn pixels to the rect.
        /// <para>Notes: <see cref="preferredHeight"/> still reports the FULL text height, so a ContentSizeFitter /
        /// LayoutGroup grows the rect to fit and truncation then never triggers. For <see cref="HorizontalAlignment.Justified"/>
        /// the kept last line of a truncated/ellipsized text is treated as the paragraph's last line (not justified).</para>
        /// </remarks>
        public TextOverflow Overflow
        {
            get => overflow;
            set
            {
                if (overflow == value) return;
                overflow = value;
                SetDirty(DirtyFlags.Layout);
                // Clip is a renderer state, not geometry: refresh it now (the rebuild may skip an
                // identical mesh upload) so toggling it never waits for a geometry change.
                RefreshOverflowClip();
            }
        }

        /// <summary>Gets or sets the horizontal text alignment. Left/Right are the paragraph's start/end
        /// edge (an RTL paragraph aligned Left is flush right); see <see cref="LightSide.HorizontalAlignment"/>.</summary>
        public HorizontalAlignment HorizontalAlignment
        {
            get => horizontalAlignment;
            set
            {
                if (horizontalAlignment == value) return;
                // Justified/Flush use a wider line-break tolerance (TMP parity), so a change that
                // crosses the justify/non-justify boundary must RE-BREAK lines, not just reposition
                // glyphs. Other alignment changes only move glyphs on already-broken lines.
                bool wasJustify = horizontalAlignment == HorizontalAlignment.Justified
                                  || horizontalAlignment == HorizontalAlignment.Flush;
                bool isJustify = value == HorizontalAlignment.Justified
                                 || value == HorizontalAlignment.Flush;
                horizontalAlignment = value;
                SetDirty(wasJustify || isJustify ? DirtyFlags.Layout : DirtyFlags.Alignment);
            }
        }

        /// <summary>Gets or sets the vertical text alignment.</summary>
        public VerticalAlignment VerticalAlignment
        {
            get => verticalAlignment;
            set
            {
                if (verticalAlignment == value) return;
                verticalAlignment = value;
                SetDirty(DirtyFlags.Alignment);
            }
        }

        /// <summary>Gets or sets the top edge metric for text box trimming.</summary>
        public TextOverEdge OverEdge
        {
            get => overEdge;
            set
            {
                if (overEdge == value) return;
                overEdge = value;
                SetDirty(DirtyFlags.Layout);
            }
        }

        /// <summary>Gets or sets the bottom edge metric for text box trimming.</summary>
        public TextUnderEdge UnderEdge
        {
            get => underEdge;
            set
            {
                if (underEdge == value) return;
                underEdge = value;
                SetDirty(DirtyFlags.Layout);
            }
        }

        /// <summary>Gets or sets how extra leading from line-height is distributed.</summary>
        public LeadingDistribution LeadingDistribution
        {
            get => leadingDistribution;
            set
            {
                if (leadingDistribution == value) return;
                leadingDistribution = value;
                SetDirty(DirtyFlags.Layout);
            }
        }

        /// <summary>Gets or sets whether automatic font sizing is enabled.</summary>
        public bool AutoSize
        {
            get => autoSize;
            set
            {
                if (autoSize == value) return;
                autoSize = value;
                SetDirty(DirtyFlags.Layout);
            }
        }

        /// <summary>Gets or sets the minimum font size for auto-sizing.</summary>
        public float MinFontSize
        {
            get => minFontSize;
            set
            {
                value = Mathf.Max(1f, value);
                if (Mathf.Approximately(minFontSize, value)) return;
                minFontSize = value;
                if (autoSize) SetDirty(DirtyFlags.Layout);
            }
        }

        /// <summary>Gets or sets the maximum font size for auto-sizing.</summary>
        public float MaxFontSize
        {
            get => maxFontSize;
            set
            {
                value = Mathf.Max(1f, value);
                if (Mathf.Approximately(maxFontSize, value)) return;
                maxFontSize = value;
                if (autoSize) SetDirty(DirtyFlags.Layout);
            }
        }

        /// <inheritdoc/>
        public override Color color
        {
            get => base.color;
            set
            {
                if (base.color == value) return;
                base.color = value;
                SetDirty(DirtyFlags.Color);
            }
        }

        /// <summary>Marks the specified aspects of the text as needing rebuild.</summary>
        /// <param name="flags">Flags indicating what needs rebuilding.</param>
        public void SetDirty(DirtyFlags flags)
        {
            if (flags == DirtyFlags.None) return;
            Cat.MeowFormat("[UniText] SetDirty: {0}, {1}", flags, name);
            dirtyFlags |= flags;

            if ((flags & DirtyFlags.Font) != 0)
            {
                DeinitializeAllModifiers();
                fontProvider = null;
                meshGenerator?.Dispose();
                meshGenerator = null;
            }

            if ((flags & DirtyFlags.FullRebuild) != 0)
            {
                textIsParsed = false;
                textProcessor?.InvalidateFirstPassData();
                InvalidateLayoutCache();
            }
            else if ((flags & DirtyFlags.LayoutRebuild) != 0)
            {
                textProcessor?.InvalidateLayoutData();
                InvalidateLayoutCache();
            }
            else if ((flags & DirtyFlags.Alignment) != 0)
            {
                textProcessor?.InvalidatePositionedGlyphs();
            }

            RegisterDirty(this);

            DirtyFlagsChanged?.Invoke(flags);
            if ((flags & (DirtyFlags.FullRebuild | DirtyFlags.LayoutRebuild)) != 0)
            {
                LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            }
        }

        #endregion

        #region Modifiers

        /// <summary>Registers a modifier/rule pair for text processing at runtime.</summary>
        /// <param name="register">The modifier registration containing the rule and modifier.</param>
        public void RegisterModifier(ModRegister register)
        {
            if (!register.IsValid) return;

            if (register.IsRegistered && register.Owner == this) return;

            if (register.Owner != null && register.Owner != this)
            {
                Debug.LogError($"[UniText] ModRegister already owned by {register.Owner.name}. Cannot register to {name}.");
                return;
            }

            modRegisters.Add(register);

            if (textProcessor != null)
            {
                EnsureAttributeParserCreated();
                register.Register(this, attributeParser);
                SetDirty(DirtyFlags.Text);
            }
        }

        /// <summary>Unregisters a modifier/rule pair at runtime.</summary>
        /// <param name="register">The modifier registration to remove.</param>
        public bool UnregisterModifier(ModRegister register)
        {
            var removed = modRegisters.Remove(register);
            if (!removed) return false;

            if (register.IsRegistered && register.Owner == this)
            {
                register.Unregister(attributeParser);
                SetDirty(DirtyFlags.Text);
            }

            if (modRegisters.Count == 0 && !HasAnyModRegisterConfigs())
            {
                DestroyAttributeParser();
            }

            return true;
        }

        /// <summary>Removes all registered modifiers.</summary>
        public void ClearModifiers()
        {
            for (var i = 0; i < modRegisters.Count; i++)
            {
                modRegisters[i].Unregister(attributeParser);
            }
            modRegisters.Clear();
            DestroyAttributeParser();
        }

        /// <summary>
        /// Registers a ModRegister with the parser. Called by ModRegister during hot-swap.
        /// </summary>
        internal void RegisterModifierWithParser(ModRegister register)
        {
            if (attributeParser == null) return;
            register.Register(this, attributeParser);
        }

        /// <summary>
        /// Unregisters a ModRegister from the parser. Called by ModRegister during hot-swap.
        /// </summary>
        internal void UnregisterModifierFromParser(ModRegister register)
        {
            register.Unregister(attributeParser);
        }

        /// <summary>Reinitializes all registered modifiers (used by Editor/OnValidate).</summary>
        private void ReInitModifiers()
        {
            DestroyAttributeParser();
            EnsureAttributeParserCreated();
        }

        /// <summary>Deinitializes all modifiers but keeps them registered (for font changes).</summary>
        private void DeinitializeAllModifiers()
        {
            for (var i = 0; i < modRegisters.Count; i++)
            {
                modRegisters[i].DeinitializeModifier();
            }
            for (var i = 0; i < runtimeConfigCopies.Count; i++)
            {
                var config = runtimeConfigCopies[i];
                for (var j = 0; j < config.modRegisters.Count; j++)
                {
                    config.modRegisters[j].DeinitializeModifier();
                }
            }
        }

        /// <summary>Resets all ModRegister states (for deserialization/Editor reload).</summary>
        private void ResetAllModRegisterStates()
        {
            for (var i = 0; i < modRegisters.Count; i++)
            {
                modRegisters[i].ResetState();
            }
            for (var i = 0; i < runtimeConfigCopies.Count; i++)
            {
                var config = runtimeConfigCopies[i];
                for (var j = 0; j < config.modRegisters.Count; j++)
                {
                    config.modRegisters[j].ResetState();
                }
            }
        }

        private void EnsureAttributeParserCreated()
        {
            if (attributeParser != null) return;
            if (textProcessor == null) return;

            if (modRegisters is { Count: > 0 } || HasAnyModRegisterConfigs())
            {
                EnsureRuntimeConfigCopiesCreated();

                attributeParser = new AttributeParser();
                RegisterModsWithParser(modRegisters);
                for (var i = 0; i < runtimeConfigCopies.Count; i++)
                {
                    RegisterModsWithParser(runtimeConfigCopies[i].modRegisters);
                }
                textProcessor.Parsed += attributeParser.Apply;
                SetDirty(DirtyFlags.Text);
            }
        }

        private void EnsureRuntimeConfigCopiesCreated()
        {
            if (runtimeConfigCopies.Count > 0) return;

            for (var i = 0; i < modRegisterConfigs.Count; i++)
            {
                var config = modRegisterConfigs[i];
                if (config != null)
                {
                    runtimeConfigCopies.Add(Instantiate(config));
                }
            }
        }

        private bool HasAnyModRegisterConfigs()
        {
            for (var i = 0; i < modRegisterConfigs.Count; i++)
            {
                var config = modRegisterConfigs[i];
                if (config != null && config.modRegisters is { Count: > 0 })
                    return true;
            }
            return false;
        }

        /// <summary>Registers all valid ModRegisters with the parser.</summary>
        private void RegisterModsWithParser(StyledList<ModRegister> mods)
        {
            for (var i = 0; i < mods.Count; i++)
            {
                var mod = mods[i];
                if (mod is { IsValid: true })
                {
                    mod.Register(this, attributeParser);
                }
            }
        }

        private void DestroyAttributeParser()
        {
            if (attributeParser == null) return;

            attributeParser.DeinitializeModifiers();
            ResetAllModRegisterStates();
            DestroyRuntimeConfigCopies();

            attributeParser.Release();
            if (textProcessor != null)
            {
                textProcessor.Parsed -= attributeParser.Apply;
            }

            attributeParser = null;
            SetDirty(DirtyFlags.Text);
        }

        #endregion

        #region Lifecycle

        protected override void OnEnable()
        {
            base.OnEnable();
            Cat.Meow($"[UniText] OnEnable, {name}", this);
            sourceText = (text ?? "").AsMemory();
            Sub();
            CollectExistingSubMeshRenderers();
            cachedCanvasRenderMode = canvas != null ? canvas.renderMode : RenderMode.ScreenSpaceOverlay;
            SetDirty(DirtyFlags.All);
            highlighter?.Initialize(this);
        }

        /// <summary>
        /// Ensures the parent Canvas provides vertex data channels required by UniText shaders.
        /// Without TexCoord1, spreadRatio is zero → normFactor becomes 100x too large →
        /// all SDF effects (outline, underlay) are massively distorted.
        /// </summary>
        private static void EnsureCanvasShaderChannels(Canvas c)
        {
            const AdditionalCanvasShaderChannels required =
                AdditionalCanvasShaderChannels.TexCoord1 |
                AdditionalCanvasShaderChannels.Normal;

            var current = c.additionalShaderChannels;
            var missing = required & ~current;
            if (missing != 0)
                c.additionalShaderChannels = current | missing;
        }

        protected override void OnDisable()
        {
            UnSub();
            base.OnDisable();
            DeInit();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            highlighter?.Destroy();
            DeInit();
            UnhookSpanStyle();
            unifiedBuilder?.Dispose();
            unifiedBuilder = null;
            DestroyRuntimeConfigCopies();
        }

        private bool syncingCanvasColor;
        private int crossFadeStartFrame;
        private Color lastCanvasRendererColor = Color.white;

        public override void CrossFadeColor(Color targetColor, float duration, bool ignoreTimeScale, bool useAlpha)
        {
            base.CrossFadeColor(targetColor, duration, ignoreTimeScale, useAlpha);
            syncingCanvasColor = true;
            crossFadeStartFrame = Time.frameCount;
        }

        public override void CrossFadeAlpha(float alpha, float duration, bool ignoreTimeScale)
        {
            base.CrossFadeAlpha(alpha, duration, ignoreTimeScale);
            syncingCanvasColor = true;
            crossFadeStartFrame = Time.frameCount;
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            // Undo, prefab revert and the Inspector write the serialized overflow field directly,
            // bypassing the setter: re-lay out and re-apply the clip.
            if (!isActiveAndEnabled) return;
            SetDirty(DirtyFlags.Layout);
            RefreshOverflowClip();
        }
#endif

        private void Update()
        {
            if (syncingCanvasColor)
            {
                var crColor = canvasRenderer.GetColor();
                if (crColor != lastCanvasRendererColor)
                {
                    lastCanvasRendererColor = crColor;
                    for (var i = 0; i < subMeshRenderers.Count; i++)
                    {
                        var r = subMeshRenderers[i].renderer;
                        if (r != null) r.SetColor(crColor);
                    }
                }
                else if (Time.frameCount > crossFadeStartFrame + 1)
                {
                    syncingCanvasColor = false;
                }
            }

            if (overflow == TextOverflow.Clip || hasAppliedClip) RefreshOverflowClip();
            highlighter?.Update();
            var c = canvas;
            
            if (c != null)
            {
                EnsureCanvasShaderChannels(c);

                var mode = c.renderMode;
                if (mode != cachedCanvasRenderMode)
                {
                    cachedCanvasRenderMode = mode;
                    SetDirty(DirtyFlags.Alignment);
                }
            }
        }

        private void DestroyRuntimeConfigCopies()
        {
            for (var i = 0; i < runtimeConfigCopies.Count; i++)
            {
                ObjectUtils.SafeDestroy(runtimeConfigCopies[i]);
            }
            runtimeConfigCopies.Clear();
        }

        private void Sub()
        {
            if (fontStack != null) fontStack.Changed += OnConfigChanged;
            ListenConfigChanged();
            EmojiFont.DisableChanged += OnEmojiFontDisableChanged;
        }

        private void UnSub()
        {
            if (fontStack != null) fontStack.Changed -= OnConfigChanged;
            UnlistenConfigChanged();
            EmojiFont.DisableChanged -= OnEmojiFontDisableChanged;
        }

        private void DeInit()
        {
            ClearAllRenderers();
            DestroyAttributeParser();
            MeshApplied?.Invoke();

            textProcessor = null;
            fontProvider = null;
            meshGenerator?.Dispose();
            meshGenerator = null;

            ReleaseSubMeshStencilMaterials();
            buffers?.EnsureReturnBuffers();
            UnregisterDirty(this);
        }

        private void OnEmojiFontDisableChanged()
        {
            SetDirty(DirtyFlags.All);
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            var rect = rectTransform.rect;
            var width = rect.width;
            var height = rect.height;

            var widthChanged = !Mathf.Approximately(width, lastKnownWidth);
            var heightChanged = !Mathf.Approximately(height, lastKnownHeight);

            if (heightChanged)
            {
                lastKnownHeight = height;
                RectHeightChanged?.Invoke();
            }

            if (widthChanged)
            {
                lastKnownWidth = width;

                var effectiveFontSize = autoSize ? maxFontSize : fontSize;
                var canReuse = textProcessor != null && textProcessor.CanReuseLines(width, effectiveFontSize, wordWrap);

                if (canReuse)
                {
                    SetDirty(DirtyFlags.Alignment);
                }
                else
                {
                    SetDirty(DirtyFlags.Layout);
                }
            }
            else
            {
                SetDirty(DirtyFlags.Alignment);
            }
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            SetDirty(DirtyFlags.Layout);
        }



        /// <summary>Configs we subscribed to Changed event (for correct unsubscription).</summary>
        private readonly List<ModRegisterConfig> subscribedConfigs = new();

        private void ListenConfigChanged()
        {
            UniTextSettings.Changed += OnConfigChanged;
            if (appearance != null) appearance.Changed += OnConfigChanged;
            ListenModRegisterConfig();
        }

        private void UnlistenConfigChanged()
        {
            UniTextSettings.Changed -= OnConfigChanged;
            if (appearance != null) appearance.Changed -= OnConfigChanged;
            UnlistenModRegisterConfig();
        }

        internal void ListenModRegisterConfig()
        {
            for (var i = 0; i < modRegisterConfigs.Count; i++)
            {
                var config = modRegisterConfigs[i];
                if (config != null)
                {
                    config.Changed += OnModRegisterConfigChanged;
                    subscribedConfigs.Add(config);
                }
            }
        }

        internal void UnlistenModRegisterConfig()
        {
            for (var i = 0; i < subscribedConfigs.Count; i++)
            {
                var config = subscribedConfigs[i];
                if (config != null)
                    config.Changed -= OnModRegisterConfigChanged;
            }
            subscribedConfigs.Clear();
        }

        private void OnModRegisterConfigChanged()
        {
            ReInitModifiers();
        }

        // Editor AND player: a component with no font stack / appearance of its own falls back to the
        // project defaults in UniTextSettings. (Editor-only, the player threw every frame instead.)
        private bool TryInitFontsAndAppearance()
        {
            var changed = false;

            if (fontStack == null)
            {
                fontStack = UniTextSettings.DefaultFontStack;
                changed = true;
            }

            if (appearance == null)
            {
                appearance = UniTextSettings.DefaultAppearance;
                changed = true;
            }

            // Keep an already-created provider in sync with the refilled references. Without this, a
            // component whose Appearance was set to null after the provider existed (the migration
            // tool's "clear legacy appearance" option does exactly that) got its field refilled here
            // while the provider kept null, and every mesh build threw in GetMaterials.
            if (changed && fontProvider != null)
            {
                if (fontProvider.Appearance != appearance) fontProvider.Appearance = appearance;
            }

    #if UNITY_EDITOR
            if (changed && !Application.isPlaying) UnityEditor.EditorUtility.SetDirty(this);
    #endif

            return fontStack != null && appearance != null;
        }

        private void OnConfigChanged()
        {
            SetDirty(DirtyFlags.All);
        }
        
        #endregion

        #region Rebuild

        /// <inheritdoc/>
        public override void Rebuild(CanvasUpdate update) { }

        private bool ValidateAndInitialize()
        {
            UniTextDebug.BeginSample("UniText.ValidateAndInitialize");

            if (!TryInitFontsAndAppearance())
            {
                if (!loggedMissingFonts)
                {
                    loggedMissingFonts = true;
                    Debug.LogError($"[OpenGlyph] '{name}' has no Font Stack/Appearance and the project UniTextSettings " +
                                   "defines no default, so it cannot render. Assign one on the component or set the " +
                                   "defaults in UniTextSettings.", this);
                }
                UniTextDebug.EndSample();
                return false;
            }

            buffers ??= new UniTextBuffers();
            buffers.EnsureRentBuffers(sourceText.Length);

            if (textProcessor == null)
            {
                textProcessor = new TextProcessor(buffers);
                Cat.Meow("[UniText] TextProcessor created", this);
            }

            EnsureAttributeParserCreated();

            if (fontProvider == null)
            {
                fontProvider = new UniTextFontProvider(fontStack, appearance);
                meshGenerator = new UniTextMeshGenerator(fontProvider, buffers);
                EnsureSpanStyleHooked();
                textProcessor.SetFontProvider(fontProvider);
                Cat.Meow("[UniText] FontProvider created", this);
            }

            UniTextDebug.EndSample();
            return true;
        }

        private ReadOnlySpan<char> ParseOrGetParsedAttributes()
        {
            if (!textIsParsed)
            {
                UniTextDebug.BeginSample("UniText.ParseAttributes");
                attributeParser?.ResetModifiers();
                attributeParser?.Parse(sourceText.Span);
                textIsParsed = true;
                UniTextDebug.EndSample();
            }

            return attributeParser != null ? attributeParser.CleanTextSpan : sourceText.Span;
        }

        private TextProcessSettings CreateProcessSettings(Rect rect, float effectiveFontSize) => new()
        {
            MaxWidth = rect.width,
            MaxHeight = rect.height,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            OverEdge = overEdge,
            UnderEdge = underEdge,
            LeadingDistribution = leadingDistribution,
            fontSize = effectiveFontSize,
            baseDirection = baseDirection,
            enableWordWrap = wordWrap,
            Overflow = overflow,
            TmpJustification = UseTmpJustification,
            PhysicalAlignment = UsePhysicalAlignment
        };

        /// <summary>
        /// Whether Left/Right alignment is PHYSICAL (TextMeshPro semantics: the rect's left/right edge for
        /// every paragraph). Plain <c>UniText</c> returns false: Left/Right are the paragraph's start/end
        /// edge, so an RTL paragraph aligned Left is flush right. <c>GlyphMeshProUGUI</c> overrides this
        /// to true.
        /// </summary>
        protected virtual bool UsePhysicalAlignment => false;

        /// <summary>
        /// Whether this component uses the opt-in TextMeshPro-compatible justification (5% wrap overrun
        /// + word/character spacing split). Plain <c>UniText</c> returns false so its Justified/Flush
        /// layout is unchanged; the TMP-parity components (<c>GlyphMeshProUGUI</c>/<c>GlyphMeshPro</c>)
        /// override this to true.
        /// </summary>
        protected virtual bool UseTmpJustification => false;

        #endregion

        #region Rendering

        private void UpdateRendering()
        {
            UniTextDebug.BeginSample("UniText.UpdateRendering");

            if (renderData == null || renderData.Count == 0)
            {
                ClearAllRenderers();
                UniTextDebug.EndSample();
                return;
            }

            // Render-Architecture R2: when the unified path is enabled, collapse the per-segment
            // renderData into ≤2 merged draw groups on the UniText/Uber material. The legacy
            // generation is untouched; this only re-packs its output. When OFF, renderData flows to
            // UpdateSubMeshes exactly as before (default — guards the existing test suite).
            if (UseUnifiedRenderer)
            {
                // CRITICAL: the uber shader reads per-glyph (sliceIdx, glyphMode, styleIdx) from UV1
                // (TEXCOORD1). A Canvas only uploads the vertex channels in additionalShaderChannels —
                // UV1 is NOT included by default, so without this the shader reads UV1 = 0 →
                // glyphMode = 0 (SDF) for EVERY glyph, and an MSDF glyph on the RGBA32 array then
                // samples .a (= 255 from the RGB24→RGBA32 copy) → a SOLID WHITE BLOCK. Enabling
                // TexCoord1 lets the per-glyph data reach the shader.
                var cv = canvas;
                if (cv != null)
                    cv.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1
                                                 | AdditionalCanvasShaderChannels.Normal;

                unifiedBuilder ??= new UnifiedRenderBuilder();
                unifiedRenderData ??= new List<UniTextRenderData>(2);
                spanStyleCollector ??= new SpanStyleCollector();
                EnsureSpanStyleHooked();
                // R2 sub-task 1: prefer the component-authored style when Override Style is ON;
                // otherwise fall back to the shim synthesising one from the legacy appearance/material
                // (so old assets still render identically during the deprecation window). Reuse the
                // base style already synthesised on the main-thread prepare step when available (one
                // source of truth; avoids a second Material-property read here on the main thread).
                var style = hasPreparedBaseStyle
                    ? preparedBaseStyle
                    : (overrideStyle
                        ? this.style.ToGlyphStyle()
                        : AppearanceStyleShim.StyleFor(fontProvider?.Appearance, fontProvider?.MainFont));
                // The collector was reset at OnBeforeMesh with the base style and populated per glyph
                // (local ids in UV1.w). The builder maps each glyph's local id -> a shared StyleTable row.
                unifiedBuilder.Build(renderData, style, spanStyleCollector, unifiedRenderData);
                UpdateSubMeshes(unifiedRenderData);
                UniTextDebug.EndSample();
                return;
            }

            WarnIfLegacyDropsSpanStyles();
            UpdateSubMeshes();

            UniTextDebug.EndSample();
        }

        protected override void UpdateMaterial() { }

        private bool warnedLegacySpanStyles;

        /// <summary>
        /// MAIN-THREAD (mesh-apply time): the legacy renderer draws per font material and cannot render
        /// per-glyph span styles (outline/underlay/dilate/softness/style tags). Logs ONE warning per
        /// component instance when the parsed text carries any. Checked here, not in per-glyph
        /// callbacks, because mesh generation may run on worker threads.
        /// </summary>
        private void WarnIfLegacyDropsSpanStyles()
        {
            if (warnedLegacySpanStyles) return;
            var attr = Buffers?.GetAttributeData<PooledArrayAttribute<SpanStyleOverride>>(AttributeKeys.SpanStyle);
            var buf = attr?.buffer.data;
            if (buf == null) return;
            var n = Math.Min(buf.Length, Buffers.codepoints.count);
            for (var i = 0; i < n; i++)
            {
                if (buf[i].IsNone) continue;
                warnedLegacySpanStyles = true;
                Debug.LogWarning($"[OpenGlyph] '{name}' uses <outline>/<underlay>/... span styles, which only render " +
                                 "with the unified renderer. Set UnifiedRenderer = ForceOn (or enable it project-wide in UniTextSettings).", this);
                return;
            }
        }

        // ---- Render-Architecture R2 sub-task 2: per-span style coordinator ---------------------------
        // Exactly ONE OnGlyph per glyph composes the cluster's accumulated SpanStyleOverride over the
        // component base style, dedups it to a local id, and writes that id into the glyph's UV1.w.
        // The SpanStyleModifier instances only populate the shared per-cluster override buffer (data);
        // this is the single place that stamps vertices, so a glyph is composed once regardless of how
        // many span tags cover it.

        private void EnsureSpanStyleHooked()
        {
            if (spanStyleHooked || meshGenerator == null) return;
            meshGenerator.OnBeforeMesh += OnSpanStyleBeforeMesh;
            meshGenerator.OnGlyph += OnSpanStyleGlyph;
            spanStyleHooked = true;
        }

        private void UnhookSpanStyle()
        {
            if (!spanStyleHooked || meshGenerator == null) return;
            meshGenerator.OnBeforeMesh -= OnSpanStyleBeforeMesh;
            meshGenerator.OnGlyph -= OnSpanStyleGlyph;
            spanStyleHooked = false;
        }

        private void OnSpanStyleBeforeMesh()
        {
            if (!UseUnifiedRenderer) return;
            // Use the base style resolved on the MAIN-THREAD prepare step. Synthesising it from the
            // legacy appearance reads Material properties, which is main-thread only — doing it here
            // would run on a worker in the parallel path. hasPreparedBaseStyle is set by
            // PrepareForMeshGenerationMainThread; fall back to a live resolve only on the serial path
            // (e.g. a direct generation that bypassed the batch prepare), still on the main thread.
            spanBaseStyle = hasPreparedBaseStyle
                ? preparedBaseStyle
                : (overrideStyle
                    ? this.style.ToGlyphStyle()
                    : AppearanceStyleShim.StyleFor(fontProvider?.Appearance, fontProvider?.MainFont));
            spanStyleCollector ??= new SpanStyleCollector();
            spanStyleCollector.Reset(spanBaseStyle);
        }

        private void OnSpanStyleGlyph()
        {
            if (!UseUnifiedRenderer) return;
            var gen = UniTextMeshGenerator.Current;
            if (gen == null) return;
            if (spanStyleCollector == null) { spanStyleCollector = new SpanStyleCollector(); spanStyleCollector.Reset(spanBaseStyle); }

            // Read the accumulated per-span override for this glyph's cluster from the shared buffer.
            var attr = Buffers?.GetAttributeData<PooledArrayAttribute<SpanStyleOverride>>(AttributeKeys.SpanStyle);
            int localId = 0;
            if (attr != null)
            {
                var buf = attr.buffer.data;
                int cluster = gen.currentCluster;
                if (buf != null && (uint)cluster < (uint)buf.Length)
                {
                    ref readonly var ov = ref buf[cluster];
                    if (!ov.IsNone)
                        localId = spanStyleCollector.GetOrAdd(ov.ComposeOnto(spanBaseStyle));
                }
            }

            // Stamp the local id into UV1.w of this glyph's 4 verts (builder maps local id -> shared row).
            int baseIdx = gen.vertexCount - 4;
            var uv1 = gen.Uvs1;
            if (uv1 == null || baseIdx < 0 || baseIdx + 3 >= uv1.Length) return;
            for (int k = 0; k < 4; k++)
            {
                var v = uv1[baseIdx + k];
                v.w = localId;
                uv1[baseIdx + k] = v;
            }
        }


        /// <summary>Sets the clipping rectangle for masking, applying to all sub-mesh renderers.</summary>
        /// <inheritdoc/>
        public override void SetClipRect(Rect clipRect, bool validRect)
        {
            base.SetClipRect(clipRect, validRect);
            cachedClipRect = clipRect;
            cachedValidClip = validRect;
            ApplyClipToRenderers(true);
        }

        // Scratch for GetWorldCorners (main thread only; no per-call allocation).
        private static readonly Vector3[] overflowCorners = new Vector3[4];

        /// <summary>
        /// The rect clipping the sub-mesh renderers should use, in root-canvas space (the space
        /// <c>CanvasRenderer.EnableRectClipping</c> and RectMask2D use): this component's own rect when
        /// <see cref="TextOverflow.Clip"/> is on, intersected with the parent RectMask2D clip when there is one.
        /// </summary>
        private bool TryGetEffectiveClip(out Rect clip)
        {
            clip = cachedClipRect;
            var valid = cachedValidClip;

            if (overflow == TextOverflow.Clip && TryGetOwnClipRect(out var own))
            {
                if (!valid) { clip = own; return true; }

                var xMin = Mathf.Max(own.xMin, clip.xMin);
                var yMin = Mathf.Max(own.yMin, clip.yMin);
                var xMax = Mathf.Min(own.xMax, clip.xMax);
                var yMax = Mathf.Min(own.yMax, clip.yMax);
                // Disjoint rects clip everything: an empty rect.
                clip = new Rect(xMin, yMin, Mathf.Max(0f, xMax - xMin), Mathf.Max(0f, yMax - yMin));
                return true;
            }

            return valid;
        }

        private bool TryGetOwnClipRect(out Rect rect)
        {
            rect = default;
            var c = canvas;
            if (c == null) return false;
            var root = c.rootCanvas != null ? c.rootCanvas.transform : c.transform;

            rectTransform.GetWorldCorners(overflowCorners);
            var min = (Vector2)root.InverseTransformPoint(overflowCorners[0]);
            var max = min;
            for (var i = 1; i < 4; i++)
            {
                var p = (Vector2)root.InverseTransformPoint(overflowCorners[i]);
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return true;
        }

        private void ApplyClipToRenderers(bool resetCullWhenInvalid)
        {
            var valid = TryGetEffectiveClip(out var clip);
            if (valid) { lastAppliedClip = clip; hasAppliedClip = true; }
            else hasAppliedClip = false;

            for (var i = 0; i < subMeshRenderers.Count; i++)
            {
                var r = subMeshRenderers[i].renderer;
                if (r == null) continue;
                if (valid) r.EnableRectClipping(clip);
                else
                {
                    r.DisableRectClipping();
                    if (resetCullWhenInvalid) r.cull = false;
                }
            }
        }

        /// <summary>
        /// Re-evaluates the overflow clip (own rect for <see cref="TextOverflow.Clip"/>, plus parent mask) and
        /// pushes it to the renderers when it changed. Cheap enough to call every frame in Clip mode so a moved
        /// or resized rect keeps clipping correctly.
        /// </summary>
        private void RefreshOverflowClip()
        {
            if (subMeshRenderers.Count == 0) return;
            var valid = TryGetEffectiveClip(out var clip);
            if (valid == hasAppliedClip && (!valid || ClipApproximatelyEqual(clip, lastAppliedClip))) return;
            ApplyClipToRenderers(false);
        }

        private static bool ClipApproximatelyEqual(Rect a, Rect b) =>
            Mathf.Abs(a.xMin - b.xMin) < 0.01f && Mathf.Abs(a.yMin - b.yMin) < 0.01f &&
            Mathf.Abs(a.xMax - b.xMax) < 0.01f && Mathf.Abs(a.yMax - b.yMax) < 0.01f;

        private Rect lastAppliedClip;
        private bool hasAppliedClip;

        /// <summary>EDITOR/TEST: the effective overflow clip rect (root-canvas space), if clipping is active.</summary>
        internal bool TryGetEffectiveClipForTests(out Rect clip) => TryGetEffectiveClip(out clip);

        /// <summary>EDITOR/TEST: whether every active sub-mesh renderer currently has rect clipping enabled.</summary>
        internal bool AllSubMeshRenderersRectClippedForTests(out int activeCount)
        {
            activeCount = 0;
            var all = true;
            for (var i = 0; i < subMeshRenderers.Count; i++)
            {
                var r = subMeshRenderers[i].renderer;
                if (r == null || !r.gameObject.activeSelf) continue;
                activeCount++;
                if (!r.hasRectClipping) all = false;
            }
            return all && activeCount > 0;
        }

        /// <summary>Sets soft clipping edges for smooth mask transitions on all sub-mesh renderers.</summary>
        /// <inheritdoc/>
        public override void SetClipSoftness(Vector2 clipSoftness)
        {
            base.SetClipSoftness(clipSoftness);
            cachedClipSoftness = new Vector4(clipSoftness.x, clipSoftness.y, 0, 0);

            for (var i = 0; i < subMeshRenderers.Count; i++)
            {
                var r = subMeshRenderers[i].renderer;
                if (r != null) r.clippingSoftness = cachedClipSoftness;
            }
        }

        /// <summary>Applies visibility culling to all sub-mesh renderers based on clip rect.</summary>
        /// <inheritdoc/>
        public override void Cull(Rect clipRect, bool validRect)
        {
            base.Cull(clipRect, validRect);
            var cull = canvasRenderer != null && canvasRenderer.cull;

            for (var i = 0; i < subMeshRenderers.Count; i++)
            {
                var r = subMeshRenderers[i].renderer;
                if (r != null) r.cull = cull;
            }
        }

        /// <summary>Recalculates stencil masking, releasing cached stencil materials.</summary>
        /// <inheritdoc/>
        public override void RecalculateMasking()
        {
            base.RecalculateMasking();
            stencilDepthDirty = true;
            ReleaseSubMeshStencilMaterials();
            SetDirty(DirtyFlags.Material);
        }

        #endregion

        #region Sub-mesh Management

#if UNITY_EDITOR
        /// <summary>EDITOR/TEST: forces this component's unified-renderer mode.</summary>
        internal void SetUnifiedRendererModeForTests(UnifiedRendererMode mode) => unifiedRendererMode = mode;

        /// <summary>EDITOR/TEST: number of currently-active drawn sub-mesh CanvasRenderers.</summary>
        internal int ActiveSubMeshRendererCountForTests
        {
            get
            {
                int n = 0;
                for (int i = 0; i < subMeshRenderers.Count; i++)
                {
                    var r = subMeshRenderers[i].renderer;
                    if (r != null && r.gameObject.activeSelf && r.GetMesh() != null) n++;
                }
                return n;
            }
        }

        /// <summary>EDITOR/TEST: true if every active drawn renderer uses the UniText/Uber shader.</summary>
        internal bool ActiveRenderersUseUberShaderForTests()
        {
            bool any = false;
            for (int i = 0; i < subMeshRenderers.Count; i++)
            {
                var r = subMeshRenderers[i].renderer;
                if (r == null || !r.gameObject.activeSelf || r.GetMesh() == null) continue;
                any = true;
                var mat = r.GetMaterial(0);
                if (mat == null || mat.shader == null || mat.shader.name != "UniText/Uber") return false;
            }
            return any;
        }

        /// <summary>EDITOR/TEST: vertices actually submitted to the active drawn CanvasRenderers.</summary>
        internal List<Vector3> GetDrawnVerticesForTests()
        {
            var result = new List<Vector3>();
            for (int i = 0; i < subMeshRenderers.Count; i++)
            {
                var r = subMeshRenderers[i].renderer;
                if (r == null || !r.gameObject.activeSelf) continue;
                var m = r.GetMesh();
                if (m != null) result.AddRange(m.vertices);
            }
            return result;
        }

        /// <summary>EDITOR/TEST: shader names of each active drawn renderer's material[0] (diagnostics).</summary>
        internal List<string> GetActiveRendererShaderNamesForTests()
        {
            var names = new List<string>();
            for (int i = 0; i < subMeshRenderers.Count; i++)
            {
                var r = subMeshRenderers[i].renderer;
                if (r == null || !r.gameObject.activeSelf || r.GetMesh() == null) continue;
                var mat = r.GetMaterial(0);
                names.Add(mat == null ? "<null>" : (mat.shader == null ? "<null shader>" : mat.shader.name));
            }
            return names;
        }

        /// <summary>
        /// EDITOR/TEST: the merged unified-path render data this component produced on the last
        /// rebuild (source-side, independent of CanvasRenderer material read-back semantics). Each
        /// entry is one draw group; its material[0] shader is what will be drawn. Null/empty when the
        /// unified path did not run.
        /// </summary>
        internal List<string> GetUnifiedGroupShaderNamesForTests()
        {
            var names = new List<string>();
            if (unifiedRenderData == null) return names;
            foreach (var rd in unifiedRenderData)
            {
                var mat = rd.material;
                names.Add(mat == null ? "<null>" : (mat.shader == null ? "<null shader>" : mat.shader.name));
            }
            return names;
        }

        /// <summary>EDITOR/TEST: number of merged unified-path draw groups produced on the last rebuild.</summary>
        internal int UnifiedGroupCountForTests => unifiedRenderData?.Count ?? 0;

        /// <summary>EDITOR/TEST: number of DISTINCT per-span styles the span-style collector resolved on
        /// the last rebuild (local id 0 = base, so 1 means no distinct span styles). Proves per-span
        /// dedup / "a row per distinct span style".</summary>
        internal int SpanStyleLocalCountForTests => spanStyleCollector?.Count ?? 0;

        /// <summary>EDITOR/TEST: the per-vertex styleIdx (UV1.w) of every vertex submitted to the active
        /// unified-path draw groups. Distinct values across glyphs prove per-glyph style selection in
        /// a single renderer.</summary>
        internal List<int> GetUnifiedStyleIndicesForTests()
        {
            var result = new List<int>();
            if (unifiedRenderData == null) return result;
            var uv1 = new List<Vector4>();
            foreach (var rd in unifiedRenderData)
            {
                if (rd.mesh == null) continue;
                uv1.Clear(); rd.mesh.GetUVs(1, uv1);
                foreach (var v in uv1) result.Add((int)(v.w + 0.5f));
            }
            return result;
        }

        /// <summary>EDITOR/TEST: the (mesh, material, texture) tuples this component built for the
        /// ACTIVE path. For the legacy path the atlas is the per-entry texture (the component binds it
        /// via CanvasRenderer.SetTexture, not on the material); for the unified path the array is on
        /// the material and this texture is null.</summary>
        internal List<(Mesh mesh, Material mat, Texture tex)> GetDrawnMeshMaterialsForTests()
        {
            var result = new List<(Mesh, Material, Texture)>();
            var src = UseUnifiedRenderer ? unifiedRenderData : renderData;
            if (src == null) return result;
            foreach (var rd in src)
                if (rd.mesh != null && rd.mesh.vertexCount > 0)
                    result.Add((rd.mesh, rd.material, rd.texture));
            return result;
        }
#endif

        private void CollectExistingSubMeshRenderers()
        {
            subMeshRenderers.Clear();
            for (var i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (child.name.StartsWith("-_UTSM_-"))
                {
                    var r = child.GetComponent<CanvasRenderer>();
                    var rt = child.GetComponent<RectTransform>();
                    if (r != null) subMeshRenderers.Add(new SubMeshRenderer { renderer = r, rectTransform = rt });
                }
            }
        }

        private void UpdateSubMeshes()
        {
            UpdateSubMeshes(renderData);
        }

        private void UpdateSubMeshes(List<UniTextRenderData> data)
        {
            UniTextDebug.BeginSample("UniText.UpdateSubMeshes");

            var requiredCount = data.Count;
            var existingCount = subMeshRenderers.Count;

            var currentPivot = rectTransform.pivot;
            if (currentPivot != lastSyncedPivot)
            {
                lastSyncedPivot = currentPivot;
                for (var i = 0; i < existingCount; i++)
                    subMeshRenderers[i].rectTransform.pivot = currentPivot;
            }

            if (stencilDepthDirty)
            {
                cachedStencilDepth = 0;
                if (maskable)
                {
                    var rootCanvas = MaskUtilities.FindRootSortOverrideCanvas(transform);
                    cachedStencilDepth = MaskUtilities.GetStencilDepth(transform, rootCanvas);
                }
                stencilDepthDirty = false;
            }
            var stencilDepth = cachedStencilDepth;

            for (var i = requiredCount; i < existingCount; i++)
            {
                var r = subMeshRenderers[i].renderer;
                if (r != null) { r.Clear(); r.gameObject.SetActive(false); }
            }

            for (var i = 0; i < requiredCount; i++)
            {
                var pair = data[i];

                if (i < existingCount)
                {
                    var r = subMeshRenderers[i].renderer;
                    if (r != null)
                    {
                        if (!r.gameObject.activeSelf) r.gameObject.SetActive(true);
                        SetSubMeshRendererData(r, pair.mesh, pair.materials, pair.texture, i, stencilDepth);
                        continue;
                    }
                }

                var newR = CreateSubMeshRenderer(i, pair.mesh, pair.materials, pair.texture, stencilDepth);
                if (i < existingCount) subMeshRenderers[i] = newR;
                else subMeshRenderers.Add(newR);
            }

            RefreshOverflowClip();

            UniTextDebug.EndSample();
        }

        private void SetSubMeshRendererData(CanvasRenderer r, Mesh mesh, Material[] mats, Texture tex, int subMeshIndex, int stencilDepth)
        {
            if (mesh == null || mesh.vertexCount == 0) { r.Clear(); return; }

            r.SetMesh(mesh);

            var matCount = mats?.Length ?? 0;
            if (matCount == 0)
            {
                r.materialCount = 0;
                return;
            }

            r.materialCount = matCount;

            for (var i = 0; i < matCount; i++)
            {
                var mat = mats[i];
                var matToUse = mat;

                if (stencilDepth > 0 && mat != null)
                {
                    var stencilId = (1 << stencilDepth) - 1;
                    var stencilMat = StencilMaterial.Add(mat, stencilId, StencilOp.Keep, CompareFunction.Equal, ColorWriteMask.All, stencilId, 0);

                    var stencilIndex = subMeshIndex * 2 + i;
                    while (stencilMaterials.Count <= stencilIndex) stencilMaterials.Add(null);
                    if (stencilMaterials[stencilIndex] != null) StencilMaterial.Remove(stencilMaterials[stencilIndex]);

                    stencilMaterials[stencilIndex] = stencilMat;
                    matToUse = stencilMat;
                    if (UnifiedRenderBuilder.IsSharedMaterial(mat)) UnifiedRenderBuilder.TrackStencilCopy(mat, stencilMat);
                }
                
                r.SetMaterial(matToUse, i);
            }

            r.SetTexture(tex);
        }


        private SubMeshRenderer CreateSubMeshRenderer(int index, Mesh mesh, Material[] mats, Texture tex, int stencilDepth)
        {
            var go = new GameObject("-_UTSM_-") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.pivot = rectTransform.pivot;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;

            var r = go.AddComponent<CanvasRenderer>();
            SetSubMeshRendererData(r, mesh, mats, tex, index, stencilDepth);

            if (TryGetEffectiveClip(out var effectiveClip)) r.EnableRectClipping(effectiveClip);
            r.clippingSoftness = cachedClipSoftness;
            r.cull = subMeshRenderers.Count > 0 && subMeshRenderers[0].renderer != null && subMeshRenderers[0].renderer.cull;

            return new SubMeshRenderer { renderer = r, rectTransform = rt };
        }

        #endregion

        #region Cleanup

        private void ClearAllRenderers()
        {
            for (var i = 0; i < subMeshRenderers.Count; i++) subMeshRenderers[i].renderer?.Clear();
            // Renderers no longer hold the fingerprinted geometry — force the next build to re-upload.
            hasAppliedGeometry = false;
            lastAppliedGeometryFingerprint = 0;
        }

        private void ReleaseSubMeshStencilMaterials()
        {
            for (var i = 0; i < stencilMaterials.Count; i++)
            {
                if (stencilMaterials[i] != null)
                {
                    StencilMaterial.Remove(stencilMaterials[i]);
                    stencilMaterials[i] = null;
                }
            }
            stencilMaterials.Clear();
        }

        #endregion

    #if UNITY_EDITOR
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            for (var i = componentsBuffer.count - 1; i >= 0; i--)
            {
                var comp = componentsBuffer[i];
                if (comp == null || comp == this)
                {
                    if (comp != null)
                        comp.isRegisteredDirty = false;
                    componentsBuffer.SwapRemoveAt(i);
                }
            }
            
            UnregisterDirty(this);
            
            UnityEditor.EditorApplication.update += OnUpdate;

            void OnUpdate()
            {
                UnityEditor.EditorApplication.update -= OnUpdate;
                if(this ==  null) return;
                UnlistenModRegisterConfig();
                ListenModRegisterConfig();
                ReInitModifiers();
            }
        }
        
    #endif
    }

}
#pragma warning restore 618
