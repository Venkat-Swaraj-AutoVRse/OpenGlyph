using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// ScriptableObject container for font collections with fallback chain support.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first font in the list is the main font. Subsequent fonts serve as
    /// fallbacks for characters not found in the main font.
    /// </para>
    /// <para>
    /// Create via Assets menu: Create → UniText → Fonts
    /// </para>
    /// </remarks>
    /// <seealso cref="UniTextFont"/>
    /// <seealso cref="UniTextFontProvider"/>
    public class UniTextFontStack : ScriptableObject
    {
        /// <summary>List of fonts in fallback order. First font is the main font.</summary>
        public StyledList<UniTextFont> fonts = new();

        /// <summary>Optional fallback stack. Searched after this stack's own fonts.</summary>
        public UniTextFontStack fallbackStack;

        /// <summary>
        /// Optional font family driving the main slot. When set, a styled request (weight/width/style
        /// or <c>&lt;b&gt;</c>/<c>&lt;i&gt;</c> markup) selects the matching real face via
        /// <see cref="FontFamily.Resolve"/>; the family's faces also serve as the main font for plain
        /// codepoint fallback. Leave null to keep the classic <see cref="fonts"/>-list behaviour.
        /// </summary>
        public FontFamily family;

        /// <summary>Gets the main (primary) font, or null if the list is empty.</summary>
        public UniTextFont MainFont => fonts is { Count: > 0 } ? fonts[0]
            : (family != null && family.Count > 0 ? family.Match(FontStyleSpec.Normal, out _) : null);

        /// <summary>
        /// Resolves the real face for a styled run: if a <see cref="family"/> is set, selects the
        /// matching face (and reports whether synthetic bold/italic must still be layered on via
        /// <paramref name="how"/>, per CSS font-synthesis — synthesize only when no real face). With
        /// no family this returns <see cref="MainFont"/> and requests synthesis for the markup, exactly
        /// matching the pre-family behaviour.
        /// </summary>
        /// <param name="spec">Base style request.</param>
        /// <param name="markupBold">A <c>&lt;b&gt;</c> span covers the run.</param>
        /// <param name="markupItalic">An <c>&lt;i&gt;</c> span covers the run.</param>
        /// <param name="how">Output: exactness + synthetic-bold/italic flags.</param>
        public UniTextFont ResolveStyledFont(FontStyleSpec spec, bool markupBold, bool markupItalic, out FaceMatch how)
        {
            if (family != null && family.Count > 0)
                return family.Resolve(spec, markupBold, markupItalic, out how);

            // No family: keep legacy behaviour — the single main face, with synthesis requested for
            // whatever markup is present (there is no real bold/italic face to choose).
            how = new FaceMatch(false, markupBold || spec.weight >= FontStyleSpec.BoldWeight,
                markupItalic || spec.style != StyleAxis.Normal);
            return MainFont;
        }

        private UniTextFont[] resolvedFonts;

        /// <summary>
        /// Finds a font that can render the specified Unicode codepoint.
        /// </summary>
        /// <param name="unicode">Unicode codepoint to find a font for.</param>
        /// <param name="searched">Set of already-searched font IDs (to prevent loops).</param>
        /// <returns>Font that can render the codepoint, or null if none found.</returns>
        /// <remarks>
        /// Automatically checks emoji fonts for extended pictographic codepoints.
        /// </remarks>
        public UniTextFont FindFontForCodepoint(uint unicode, HashSet<int> searched = null)
        {
            resolvedFonts ??= BuildResolvedFonts();

            if (resolvedFonts.Length == 0)
                return null;

            searched ??= new HashSet<int>();

            if (UnicodeData.Provider.IsEmojiPresentation((int)unicode) && EmojiFont.IsAvailable)
            {
                var emojiFont = EmojiFont.Instance;
                if (searched.Add(EmojiFont.FontId))
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    return emojiFont;
#else
                    var glyphIndex = Shaper.GetGlyphIndex(emojiFont, unicode);
                    if (glyphIndex != 0) return emojiFont;
#endif
                }
            }

            for (var i = 0; i < resolvedFonts.Length; i++)
            {
                var font = resolvedFonts[i];

                if (!searched.Add(font.GetCachedInstanceId()))
                    continue;

                var glyphIndex = Shaper.GetGlyphIndex(font, unicode);
                if (glyphIndex != 0) return font;
            }

            return null;
        }

        private UniTextFont[] BuildResolvedFonts()
        {
            TryInit();
            var list = new List<UniTextFont>();
            var visitedStacks = new HashSet<UniTextFontStack>();
            CollectFonts(this, list, visitedStacks);
            return list.ToArray();
        }

        private static void CollectFonts(UniTextFontStack stack, List<UniTextFont> list, HashSet<UniTextFontStack> visited)
        {
            while (true)
            {
                if (stack == null || !visited.Add(stack)) return;

                for (int i = 0; i < stack.fonts.Count; i++)
                    if (stack.fonts[i] != null)
                        list.Add(stack.fonts[i]);

                // Family faces also participate in plain codepoint fallback (the regular member first).
                if (stack.family != null)
                    for (int i = 0; i < stack.family.faces.Count; i++)
                        if (stack.family.faces[i].font != null && !list.Contains(stack.family.faces[i].font))
                            list.Add(stack.family.faces[i].font);

                stack = stack.fallbackStack;
            }
        }

        internal event Action Changed;
        [NonSerialized] private bool isInitialized;
        
        private void TryInit()
        {
            if(isInitialized) return;

            isInitialized = true;
            
            for (var i = 0; i < fonts.Count; i++)
            {
                if (fonts[i] != null)
                    fonts[i].Changed += CallChanged;
            }

            if (fallbackStack != null)
                fallbackStack.Changed += CallChanged;
        }

        private void OnDisable()
        {
            DeInit();
        }

        private void OnDestroy()
        {
            DeInit();
        }

        private void DeInit()
        {
            isInitialized = false;
            for (var i = 0; i < fonts.Count; i++)
            {
                if (fonts[i] != null)
                    fonts[i].Changed -= CallChanged;
            }

            if (fallbackStack != null)
                fallbackStack.Changed -= CallChanged;
        }
        
#if UNITY_EDITOR

        private void OnValidate()
        {
            resolvedFonts = null;

            for (var i = 0; i < fonts.Count; i++)
            {
                if (fonts[i] == null) continue;
                fonts[i].Changed -= CallChanged;
                fonts[i].Changed += CallChanged;
            }

            if (fallbackStack != null)
            {
                fallbackStack.Changed -= CallChanged;
                fallbackStack.Changed += CallChanged;
            }

            CallChanged();
        }
    #endif
        
        private void CallChanged()
        {
            resolvedFonts = null;
            Changed?.Invoke();
        }
    }
}
