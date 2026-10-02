// OpenGlyph GlyphMeshPro — shared text surface.
//
// The common TMP-parity surface implemented by both GlyphMeshProUGUI (Canvas)
// and GlyphMeshPro (world-space MeshRenderer), so property-level code is written
// once and either component can be driven through the same interface.
// See Documentation/GlyphMeshPro-Parity.md.

using System.Text;
using UnityEngine;
using LightSide;

namespace OpenGlyph
{
    /// <summary>
    /// TMP-parity text surface shared by the Canvas and world-space GlyphMeshPro
    /// components. Member names mirror TextMeshPro.
    /// </summary>
    public interface IGlyphMeshText
    {
        string text { get; set; }
        void SetText(string sourceText);
        void SetText(StringBuilder sourceText);

        UniTextFont font { get; set; }
        float fontSize { get; set; }
        bool enableAutoSizing { get; set; }
        float fontSizeMin { get; set; }
        float fontSizeMax { get; set; }
        FontStyles fontStyle { get; set; }

        bool enableVertexGradient { get; set; }
        VertexGradient colorGradient { get; set; }

        TextAlignmentOptions alignment { get; set; }
        TextWrappingModes textWrappingMode { get; set; }
        TextOverflowModes overflowMode { get; set; }

        float characterSpacing { get; set; }
        float wordSpacing { get; set; }
        float lineSpacing { get; set; }
        float paragraphSpacing { get; set; }
        Vector4 margin { get; set; }

        bool richText { get; set; }
        bool isRightToLeftText { get; set; }

        int maxVisibleCharacters { get; set; }
        int maxVisibleWords { get; set; }
        int maxVisibleLines { get; set; }

        Vector2 GetPreferredValues();
        Vector2 GetPreferredValues(float width, float height);
        Vector2 GetRenderedValues();

        void ForceMeshUpdate(bool ignoreActiveState = false, bool forceTextReparsing = false);

        GlyphTextInfo textInfo { get; }
    }
}
