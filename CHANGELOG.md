# Changelog

All notable changes to UniText will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### World link feedback

- **`WorldTextHighlighter`**: hover box and click flash for links and interactive ranges on world-space
  text (`UniTextWorld`, world `GlyphMeshPro`), drawn by a MeshRenderer below the glyphs; the default for
  world text (a Canvas keeps `DefaultTextHighlighter`). Tests: `Wave3WorldTextTests.World_LinkHover_*`,
  `World_LinkClick_FlashesTheLink`.

### OpenGlyph 1.1.0-preview

Open-source (MIT) clean-room fork of UniText 1.0. This preview integrates phase 0
through phase 1c.

#### Changed
- **Unified renderer is now the project default** (`UniTextSettings.useUnifiedRenderer = true`, shipped asset and runtime default). Opt out per component with `UnifiedRenderer = ForceOff` or project-wide in settings. Test runs: `UNITEXT_FORCE_UNIFIED=1` / `UNITEXT_FORCE_LEGACY=1` pin each mode.

#### Added
- **Wave 5: built-in on-screen keyboard for VR, and default markup.**
  - **`UniTextKeyboard`** (`Runtime/Keyboard/`): an on-screen keyboard made of OpenGlyph text, with `UniTextWorld` keys (one BoxCollider each) in world space or `UniText` keys on a Canvas. It types into the focused `UniTextInputField` through `ProcessText` / `ProcessKey`, and opens through the field's `IInputFieldTouchKeyboard` seam (`UniTextKeyboardTouchAdapter`).
    - **Layouts as data** (`UniTextKeyboardLayout`: ScriptableObject or JSON). Built in: QWERTY (Shift, caps lock, a numbers & symbols page), Email (`@ . .com _`), Numeric (Integer / Decimal / PIN fields), Hindi Devanagari InScript-lite, and Arabic. A globe key cycles the text layouts.
    - **Keys.** Backspace repeats on hold; Enter submits, or inserts a newline in multi-line fields; Space, ← →, Hide, page and layout keys. The icons are vector shapes.
    - **Interaction.** Standard EventSystem pointer events (XRI ray / poke, mouse, touch); `GetKeyAt`, `PressKey` / `ReleaseKey` for custom interaction; `onKeyPressed` / `KeyPressed` for audio and haptics. Hover and press change vertex colours only. A key preview popup is shown, except in password fields.
    - **Placement.** Below the field (tilted), at a transform, or following the head (lazy follow); `Show(field)` / `Hide()`.
    - **Cost.** All labels share one material, and the backgrounds are one mesh. 0 B per idle frame. The full QWERTY page draws as +4 draw calls in Play Mode (Built-in pipeline, Editor).
  - **`UniTextInputField.SoftKeyboard`** (`Auto` / `System` / `BuiltIn` / `None`) with `BuiltInKeyboard`. `Auto` uses `TouchScreenKeyboard` where it works, the built-in keyboard when an XR device is active (`InputFieldPlatform.XRActive`: Quest under OpenXR reports the system keyboard visible but shows nothing), and none on desktop. A keyboard is created on demand if the scene has none (`UniTextKeyboard.ForField`). `IInputFieldFocusKeeper`: a press on the keyboard keeps the field focused. New asmdef version define `OPENGLYPH_VR` (`com.unity.modules.vr`).
  - **`UniText.RegisterDefaultMarkup()`** (`UniTextDefaultMarkup`): registers the common tags (`b i u s color size cspace line-height line-spacing link sup sub gradient outline outline2 underlay glow innershadow dilate softness style mark smallcaps upper uppercase lowercase voffset align indent line-indent nobr noparse lang feature`) and skips tags already parsed. Also in the component context menu.
  - **`UniTextSettings.DefaultMarkupOnNewComponents`** (default **off**, so behaviour is unchanged): components without rules of their own parse the default set. The rules are added at runtime only, never serialized. GlyphMeshPro components are not affected.
  - Menus: **GameObject > 3D Object > OpenGlyph > UniText Keyboard (World)**, **GameObject > UI > OpenGlyph > UniText - Keyboard**.
  - Docs: new `Documentation/VRKeyboard.md`. In `InputField.md`, the claim that the Meta system keyboard appears through `TouchScreenKeyboard` on Quest was removed, and the on-screen keyboard modes are documented. The Meta keyboard needs the Meta XR SDK's Virtual Keyboard, which is not included. Doc snippets that used tags on a plain `UniText` / `UniTextWorld` now call `RegisterDefaultMarkup()`.
  - Tests: `Wave5KeyboardTests` (15), `Wave5MarkupTests` (4), `Wave5KeyboardPlayTests` (1).
- **Wave 4: editable text (input field), Canvas and world space.**
  - **`UniTextInputField`** (`Runtime/Input/`): a `Selectable` that edits a `UniText` (Canvas) or a `UniTextWorld` (world space, no Canvas).
    - **Caret.** The caret map is built from the engine layout (`TextCaretMap`). The caret, selection and deletion work on grapheme clusters. The caret is a BiDi visual caret: (position, affinity), with Left/Right in visual order. Ligatures are split into equal caret stops.
    - **Navigation.** UAX #29 word moves and word delete (`TextSegments`), line and document Home/End, Up/Down/PageUp/PageDown with column memory.
    - **Pointer.** Click, Shift+click, double-click word, triple-click line, and drag selection. On a Canvas through the screen point; in world space through the EventSystem raycast hit point (PhysicsRaycaster, XRI TrackedDevicePhysicsRaycaster) or `SetCaretFromRay` / `SetCaretFromWorldPoint`.
    - **Editing.** Insert, Backspace/Delete, cut/copy/paste through a replaceable `IInputFieldClipboard` (system or `InputFieldMemoryClipboard`). Paste is sanitised: control characters removed, newlines only in multi-line fields, `MaxPasteLength` and the character limit applied without splitting a grapheme. Undo/redo coalesces typing per word (`TextEditHistory`).
    - **IME.** The composition is shown inline with an underline; commit and cancel are supported.
    - **Content types.** Standard, Autocorrected, Integer, Decimal, Alphanumeric, Name, Email, Password, Pin and Custom, with uGUI/TMP semantics (`InputFieldValidation`). Line types SingleLine, MultiLineSubmit and MultiLineNewline. Read-only. Password masking is one character per grapheme.
    - **Markup.** Shown literally by default (a NoParse wrapper or the GlyphMeshPro `richText` flag); with `RichText` on, the caret maps the visible text back to the source.
    - **Caret look.** Bar, block or underline; blink rate, width and colour; a custom caret object.
    - **Scrolling.** Single-line fields scroll horizontally and multi-line fields vertically. Clipping is a RectMask2D on a Canvas, and an object-space clip on world text (`UniText.SetInputClip`).
    - **Events.** UnityEvents `onValueChanged`, `onEndEdit`, `onSubmit`, `onSelect`, `onDeselect`, `onTextSelection`, `onEndTextSelection`, and `onValidateInput`, plus C# events. Tab navigation, and EventSystem select/deselect/submit.
    - **Allocation.** A focused, idle field allocates nothing per frame.
  - **Input backends** (`InputFieldKeyboardSources`):
    - Input System (`Keyboard.onTextInput`, `onIMECompositionChange`, key states). Compiled with `OPENGLYPH_INPUTSYSTEM`, an asmdef version define on `com.unity.inputsystem`, and used when the project's Active Input Handling includes it.
    - The legacy Input Manager otherwise.
    - Key repeat for held keys.
    - The `IInputFieldKeyboardSource` seam for custom or in-VR keyboards.
  - **Touch / system keyboard** (`IInputFieldTouchKeyboard`, default `SystemTouchKeyboard` over `TouchScreenKeyboard`):
    - Opens on focus with the keyboard type, multiline, secure, placeholder and limit of the content type.
    - Mirrors the keyboard's text and selection.
    - Done submits, Cancel restores. `HideMobileInput`, `HideSoftKeyboard`.
    - On Meta Quest this API shows the system keyboard overlay.
    - `SimulatedTouchKeyboard` runs the flow in the Editor.
  - **`OpenGlyph.GlyphMeshProInputField`**: the `TMP_InputField` API over `UniTextInputField`, with TMP member names, nested enums, character-index positions and `ProcessEvent(Event)`. Created with `CreateGlyphMeshPro`. Gaps are listed in `GlyphMeshPro-Parity.md`.
  - **`UniTextSelectableText`**: read-only select and copy on any `UniText`, over its visible text.
  - **`UniText` additions:**
    - The `LayoutApplied` event, raised after each rebuild and when the text is cleared.
    - `TextAreaRect`.
    - An internal world input clip.
  - **Factories and menus:** `UniTextInputField.Create(parent, world)`; **GameObject > UI > OpenGlyph > UniText - Input Field / GlyphMeshPro - Input Field** and **GameObject > 3D Object > OpenGlyph > UniText Input Field (World)**. The menus create an EventSystem with the Input System UI module when that is active.
  - Docs: `Documentation/InputField.md`. Tests: `Tests/Editor/Wave4{Caret,Editing,Field,Backend}Tests.cs`.
- **Wave 3: world-space text without a Canvas.**
  - **`UniTextWorld`** component (`Runtime/World/UniTextWorld.cs`): UniText drawn by the `MeshFilter` / `MeshRenderer` on its GameObject. Same engine, markup, modifiers, animations and Auto Size; output identical to UniText on a World Space Canvas (glyphs, vertices, pixels). Options `Lighting` (Unlit / Lit), `DepthWrite`, `DoubleSided`, `Collider`, `SortingLayerID` / `SortingOrder`, `WorldSize`; Scene-view rect gizmo; inspector = UniText inspector + World Rendering; **GameObject > 3D Object > OpenGlyph** menu items.
  - **`OpenGlyph.GlyphMeshPro`** (TMP `TextMeshPro` counterpart) is no longer a stub: it is the `GlyphMeshProUGUI` adapter drawn through a MeshRenderer, plus `sortingLayerID`, `sortingOrder`, `renderer`, `mesh`.
  - World output in `UniText` (`UniText_World.cs`, `RendersToMeshRenderer`): the draw-group meshes are combined into one component-owned mesh (one sub-mesh per draw group, bounds follow animated glyphs). Unified renderer: one shared material per atlas format and option set (`UniTextWorldMaterials`), no per-renderer state, SRP-Batcher compatible. Legacy renderer: font materials with per-page property blocks. World text is laid out without a Canvas layout pass.
  - **`UniText/World/Uber`** shader (URP `UniversalForward` + Built-in `ForwardBase` SubShaders, no pipeline include): unlit or wrapped-Lambert lit (main light + ambient), depth write with alpha clip, cull mode, fog, stereo instancing, object-space overflow clip. The Uber program moved to `Shaders/UniText_UberCore.cginc`, shared with `UniText/Uber` (output unchanged). Added to the build processor's Always-Included Shaders.
  - **World-space pointer events:** `HitTestPointer(PointerEventData)` uses the raycast's 3D hit for world text (PhysicsRaycaster, XRI TrackedDevicePhysicsRaycaster); `HitTestWorld`, `HitTestRay`, `ClickAtWorldPoint`; an auto-sized, unsaved BoxCollider while the text is interactive (`WorldTextCollider`). Assembly define `OPENGLYPH_PHYSICS` from `com.unity.modules.physics`.
  - Docs: `Documentation/WorldText.md`. Tests: `Tests/Editor/Wave3{WorldText,WorldRender,WorldPerf}Tests.cs`, `Tests/Runtime/Wave3WorldLabelsPlayTests.cs`.
- **Wave 2: animation, reveal, effects.**
  - **Reveal / typewriter:** `UniTextReveal` component (by grapheme cluster, word or line; speed, delay, per-unit fade and slide, easing; `RevealStarted` / `UnitRevealed(int)` / `RevealCompleted` as C# and Unity events; Play / Pause / Skip / Restart / `SetPlaybackTime`); `<pause=seconds>` tag (`PauseParseRule` + `RevealPauseModifier`, adds no character). Logical order, so RTL and bidirectional text reveal in reading order; composes with GlyphMeshProUGUI `maxVisibleCharacters`. Docs: `Documentation/TextReveal.md`.
  - **Span animations:** `<wave> <bounce> <pulse> <shake> <fade> <rainbow>` (`TextAnimationModifier` + `WaveParseRule` … `BounceParseRule`, attribute-style parameters `<wave amp=4 freq=2>`; `TextAnimationModifier.RegisterAll`). Shared clock `UniTextAnimationClock` (manual time for tests/capture), per component `AnimationTimeScale`, `AnimationUnscaledTime`, `AnimationPhase`. Docs: `Documentation/TextAnimations.md`.
  - **Vertex-effect pass** (`IUniTextVertexEffect`, `UniText.AddVertexEffect`, `UniTextVertexEffectContext`): a component with effects captures its final meshes once per rebuild and, per frame, uploads only edited positions and colours to its own mesh, from `Canvas.willRenderCanvases`. No per-frame shaping, layout, mesh generation or managed allocation; components without effects are not hooked. Unified and legacy renderer. Engine: `UniTextMeshGenerator.currentGlyphVertexStart`, `OnGlyphBase`; `UnifiedRenderBuilder` records merged-vertex source ranges.
  - **Glow, inner shadow, second outline:** `<glow=#RRGGBBAA,size,softness,intensity>`, `<innershadow=#RRGGBBAA,x,y,dilate,softness>`, `<outline2=#RRGGBBAA,width,softness>` (`SpanStyleModifier.Kind.Glow / InnerShadow / Outline2`, `GlowParseRule`, `InnerShadowParseRule`, `Outline2ParseRule`) and new `GlyphStyle` / `UniTextStyle` fields (`outline2*`, `innerShadow*`); rendered by `UniText/Uber` (style table now 12 columns; effect parameters fetched per vertex). `UniText/Mobile/SDF` and `UniText/Mobile/MSDF` gained a `GLOW_ON` glow (material inspector Glow panel). The appearance shim now maps `UNDERLAY_INNER` to the inner shadow (it was drawn as an outer drop shadow in the unified renderer). Docs: `Documentation/GlowAndShadows.md`.
  - **Radial and angular gradients:** `<gradient=name,radial[,cx,cy,radius]>`, `<gradient=name,angular[,startDeg,cx,cy]>`, and whole-text `UniText.GradientFill` (`UniTextGradientFill`: Linear / Radial / Angular) that `<color>` / `<gradient>` spans override; shared `GradientFrame`. Vertex colours, both renderers. Docs: `Documentation/Gradients.md`.
  - Unified renderer: vertex tint now applies to face, outlines and underlay but not to the new glow (same output as before for existing styles).
  - Tests: `Tests/Editor/Wave2{Reveal,Animation,Effect,Gradient}Tests.cs`.
- **Wave 1: typography and integration.**
  - **OpenType features:** `UniText.FontFeatures` (e.g. `tnum`, `onum`, `smcp`, `ss01`, `cv01`, `liga=0`, `-kern`) and the `<feature=…>` tag (`FeatureParseRule` + `FeatureModifier`), passed to HarfBuzz with the span's cluster range; spans override the component list; parser `OpenTypeFeatures`. Docs: `Documentation/OpenTypeFeatures.md`.
  - **Language-aware shaping:** `UniText.Language` (BCP 47) and the `<lang=…>` tag (`LanguageParseRule` + `LanguageModifier`) set the HarfBuzz buffer language (`locl` and other language systems) and pick the CJK system font face per component and per span (ja / ko / zh-Hans / zh-Hant / zh-HK; new Traditional-Chinese face group, `msjh.ttc` on Windows). Unset = previous behaviour. Native: new export `ut_hb_buffer_set_language`; every runtime binary in `Plugins/` rebuilt by CI (118 `ut_*` symbols; provenance in `NativeSource~/tests/BINARIES.md`). `Shaper.SupportsLanguage` reports an older native library. Docs: `Documentation/LanguageShaping.md`.
  - **Content measurement:** `GetMinContentWidth()`, `GetMaxContentWidth()`, `GetHeightForWidth(float)` on UniText (padding/margins and Auto Size min/max included; no change to the rect or the current layout); opt-in `ContentMinWidth` reports min-content as `ILayoutElement.minWidth`. Docs: `Documentation/ContentMeasurement.md`.
  - **Auto Size fit steps:** `UniText.AutoSizeStep` (0 = continuous, the default): the fitted size is the largest multiple of the step that fits. Docs: `Documentation/AutoSizeSteps.md`.
  - **Padding:** `UniText.Padding` (left, top, right, bottom) insets layout and wrapping, adds to the preferred size, insets the Clip rect and link hit testing; on GlyphMeshProUGUI it adds to `margin`. Docs: `Documentation/Padding.md`.
  - **Unity Localization:** optional assembly `OpenGlyph.Localization` (compiles only with `com.unity.localization` ≥ 1.0.0) with `LocalizeUniText`: a `LocalizedString` drives `Text`, the selected locale drives `Language`. Docs: `Documentation/Localization.md`.
  - Tests: `Tests/Editor/Wave1{Feature,Language,Measurement,AutoSizeStep,Padding}Tests.cs`, `Tests/Editor/Localization/LocalizeUniTextTests.cs`.
- **GlyphMeshProUGUI TMP parity, round 3.** The properties that were only stored now work with TMP units and semantics, update at runtime and serialize: `characterSpacing`, `wordSpacing`, `lineSpacing` (negative allowed), `paragraphSpacing` (em/100), `margin`, `richText` (off shows tags literally), `enableVertexGradient`/`colorGradient` (4-corner, multiplied with the colour), `maxVisibleCharacters`/`Words`/`Lines` (hidden glyphs emit no geometry; changing them never reshapes or re-lays out). New: `FontStyles.SmallCaps` (OpenType `smcp` or synthetic 0.8x capitals), `FontStyles.Highlight` + `<mark=#RRGGBBAA>` (box behind the run, both renderers), tags `<nobr> <noparse> <align> <indent> <line-indent> <font="Name">` (`UniTextFontRegistry` lookup), overflow ScrollRect (= Overflow, no warning), Page (`pageToDisplay`) and Linked (`linkedTextComponent`, `firstVisibleCharacter`). Engine: TMP spacing inputs on `TextProcessor`, `UniText.LayoutMargins`, `UniTextMeshGenerator.ClusterHidden`, ranged HarfBuzz features, per-line alignment and indent jumps; components dirtied during mesh apply are processed in the same frame. Tests: `GlyphMeshProParity3Tests`. Fixed during the demo render: a component created with `maxVisibleCharacters = 0` could reveal stale glyph geometry from the pooled glyph cache (`MaxVisibleCharacters_StartingAtZero_RevealsTheRightGlyphs`). Visibility now counts characters exactly like TMP (spaces, newlines and combining marks; Latin `liga`/`clig` off as with TMP's default font features; whitespace is never `isVisible`), and a space at a line end no longer forces a wrap in GlyphMeshPro.
- **Physical alignment for GlyphMeshPro (B23)**: `GlyphMeshProUGUI` Left/Right now hug the rect's physical left/right edge for RTL paragraphs, like TMP (`LayoutSettings.physicalAlignment` / `TextProcessSettings.PhysicalAlignment`). Plain `UniText` is unchanged: `HorizontalAlignment.Left/Right` are the paragraph's start/end edge (now documented as such).
- **System font fallback for any script**: code points of any script the font stack does not cover (Tamil, Bengali, Georgian, Ethiopic, Tibetan, ...) now use an installed system font found by script (Android `fonts.xml`, per-platform name hints, `NotoSans<Script>` files, then a bounded cmap scan). It logs one warning per script per session, naming the font used or saying it will render as missing glyphs. See `Documentation/SystemFontFallback.md`.
- **System CJK font fallback** (`SystemFontFallback`, `UniTextSettings.useSystemFontFallback`, default on): CJK code points not covered by the font stack lazily use the OS CJK font. TTC face index is now honoured by `CreateFontAsset(..., faceIndex)` and shaping. See `Documentation/SystemFontFallback.md`.
- **Phase 0 — Native MSDF pipeline.** Clean-room native font core
  (`unitext_native`) built from source for all platforms (Windows, macOS, Linux,
  Android, iOS, tvOS, WebGL), FreeType outline extraction, and a from-scratch
  multi-channel signed distance field (MSDF) generator (`Runtime/FontCore/Msdf/*`)
  with edge colouring and error correction, validated against msdfgen for parity.
- **Phase 1a — MSDF integration & artifact tests.** MSDF glyph generation wired
  into the atlas pipeline with exact-distance, real-glyph, real-export and visual
  artifact test coverage, plus the DLL smoke tests.
- **Phase 1b — Dictionary word segmentation.** Opt-in dictionary segmenter
  (`DictionarySegmenter`, `DictionaryTrie`, `SegmentationScript`) for the
  space-less complex-context scripts Thai, Lao, Khmer and Myanmar — a faithful
  re-implementation of ICU dictionary break iteration, with every emitted boundary
  filtered to a UAX #29 grapheme-cluster boundary. Line breaking wraps such runs at
  real word boundaries. Dictionaries ship as `.bytes` assets outside any `Resources`
  folder, so a project that assigns none pays zero cost. Includes `DictGen` tooling
  and natural-text F1 accuracy fixtures.
- **Phase 1c — Pixel fonts + Smooth/Mono rendering.** Pixel/bitmap-strike font
  detection (`PixelFontDetection`, native pixel-grid and EBDT/CBDT/sbix strike
  analysis) and a Pixel-Perfect rendering path (`MonoGlyphRenderer`,
  `SmoothGlyphRenderer`, `FreeTypeOutlineGridProvider`) that rasterises detected
  pixel fonts in 1-bit Mono at integer multiples of the native em into a
  point-filtered, mip-free atlas, snapping glyph quads and the text origin to the
  device pixel grid. Adds **Smooth** (anti-aliased grayscale bitmap) and **Mono**
  (1-bit) render modes for ordinary vector fonts, selectable per font asset.

#### Changed
- **Shared glyph-atlas memory budgets are disabled at runtime (temporary).** The per-array
  page/byte budgets and the global byte budget on `UniTextSettings`
  (`SharedAtlasPageBudget`, `SharedAtlasByteBudgetPerArray`, `SharedAtlasByteBudgetGlobal`)
  are **no longer enforced**: `SharedGlyphAtlas` reads every budget as `0` (unbounded)
  regardless of the configured values, so the atlas never evicts through the runtime.
  They were disabled because the eviction machinery only evicts refcount-0 cells on an
  LRU clock, but the render path never calls `Acquire`/`Release` or `BeginFrame` — so a
  non-zero budget could evict **on-screen** glyphs and draw wrong text. The eviction code
  and its unit tests are retained; the serialized settings fields are kept (existing assets
  load unchanged) and marked *"not active yet"* in their tooltips, and a single warning is
  logged once per session if a non-zero budget is set. See
  `Documentation/Design/MemoryBudgets.md` for the full rationale and the conditions to
  re-enable.

#### Known limitations
- **MSDF 'W' junction residual.** A small corner-rounding residual remains at the
  'W' apex. This is inherent to the algorithm, not a bug: msdfgen v1.12 (85e8b3d)
  reproduces it identically (20 offenders / 0.356 @ 280,368). 'W' is gated against
  the msdfgen baseline; the other eight reference glyphs keep an absolute 0.25 bound.
- **Lao segmentation F1 target is 0.90**, deliberately lower than the other scripts.
  This reflects a disagreement between ICU's current `laodict` and ICU's own word
  `BreakIterator` (which caps any faithful re-implementation at ~F1 0.92), not an
  OpenGlyph quality regression; against the ICU-60-vintage `laodict` the same code
  reaches F1 0.9985.
- **Real-layout wrap tests** cover Thai, Khmer and Myanmar with bundled OFL Noto Sans
  fixtures (test data only, not shipped). Lao has no real-layout test yet.

## [1.0.0] - TBD

### Fixed
- Restore Unity 6.6 compilation by implementing unlimited UI layout maximum sizes and using the current object identity API on Unity 6.2+, while retaining Unity 2021.3 compatibility.

### Added
- Initial release
