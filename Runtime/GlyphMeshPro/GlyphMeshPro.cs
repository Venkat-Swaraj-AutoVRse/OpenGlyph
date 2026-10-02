// OpenGlyph GlyphMeshPro — world-space (MeshRenderer) text component.
//
// ROUND 1 = DESIGN + STUB. This component carries the full TMP-parity surface
// (IGlyphMeshText) and its serialized fields, but the headless engine host and
// world-space mesh emission are Round 2 (bodies marked // TODO R2). The design
// is documented in Documentation/GlyphMeshPro-Parity.md.
//
// Target: Quest / world-space text without a Canvas. Unlike GlyphMeshProUGUI
// (which subclasses UniText : MaskableGraphic and renders through a
// CanvasRenderer), GlyphMeshPro renders into a MeshFilter.sharedMesh via a
// MeshRenderer, driving the SAME shaping/layout engine through a headless host
// (no CanvasRenderer). The two share IGlyphMeshText so property code is common.

using System.Text;
using UnityEngine;
using LightSide;

namespace OpenGlyph
{
    /// <summary>
    /// World-space text renderer with a TextMeshPro-parity API, backed by the
    /// OpenGlyph engine. <b>Round 1: design + stub.</b> Properties store state and
    /// are wired to a headless engine host in Round 2; mesh emission is Round 2.
    /// </summary>
    [AddComponentMenu("")] // not yet surfaced in the GameObject menu (stub)
    [RequireComponent(typeof(MeshRenderer))]
    [RequireComponent(typeof(MeshFilter))]
    [DisallowMultipleComponent]
    public class GlyphMeshPro : MonoBehaviour, IGlyphMeshText
    {
        [TextArea(2, 6)] [SerializeField] private string m_text = string.Empty;
        [SerializeField] private UniTextFont m_font;
        [SerializeField] private float m_fontSize = 36f;
        [SerializeField] private bool m_enableAutoSizing;
        [SerializeField] private float m_fontSizeMin = 10f;
        [SerializeField] private float m_fontSizeMax = 72f;
        [SerializeField] private FontStyles m_fontStyle = FontStyles.Normal;
        [SerializeField] private Color m_color = Color.white;
        [SerializeField] private bool m_enableVertexGradient;
        [SerializeField] private VertexGradient m_colorGradient = new VertexGradient(Color.white);
        [SerializeField] private TextAlignmentOptions m_alignment = TextAlignmentOptions.TopLeft;
        [SerializeField] private TextWrappingModes m_textWrappingMode = TextWrappingModes.Normal;
        [SerializeField] private TextOverflowModes m_overflowMode = TextOverflowModes.Overflow;
        [SerializeField] private float m_characterSpacing;
        [SerializeField] private float m_wordSpacing;
        [SerializeField] private float m_lineSpacing;
        [SerializeField] private float m_paragraphSpacing;
        [SerializeField] private Vector4 m_margin = Vector4.zero;
        [SerializeField] private bool m_richText = true;
        [SerializeField] private bool m_isRightToLeftText;
        [SerializeField] private int m_maxVisibleCharacters = int.MaxValue;
        [SerializeField] private int m_maxVisibleWords = int.MaxValue;
        [SerializeField] private int m_maxVisibleLines = int.MaxValue;

        private readonly GlyphTextInfo m_textInfo = new GlyphTextInfo();

        /// <summary>Marks the mesh dirty so the next build re-emits geometry. Round 2 wires this
        /// to the headless engine host; in Round 1 it only flags the component.</summary>
        private void SetMeshDirty()
        {
            // TODO R2: queue a rebuild on the headless engine host, then push the
            // resulting vertex/index buffers into GetComponent<MeshFilter>().sharedMesh.
        }

        public string text { get => m_text; set { m_text = value; SetMeshDirty(); } }
        public void SetText(string sourceText) => text = sourceText;
        public void SetText(StringBuilder sourceText) => text = sourceText?.ToString() ?? string.Empty;

        public UniTextFont font { get => m_font; set { m_font = value; SetMeshDirty(); } }
        public float fontSize { get => m_fontSize; set { m_fontSize = value; SetMeshDirty(); } }
        public bool enableAutoSizing { get => m_enableAutoSizing; set { m_enableAutoSizing = value; SetMeshDirty(); } }
        public float fontSizeMin { get => m_fontSizeMin; set { m_fontSizeMin = value; SetMeshDirty(); } }
        public float fontSizeMax { get => m_fontSizeMax; set { m_fontSizeMax = value; SetMeshDirty(); } }
        public FontStyles fontStyle { get => m_fontStyle; set { m_fontStyle = value; SetMeshDirty(); } }

        public Color color { get => m_color; set { m_color = value; SetMeshDirty(); } }
        public bool enableVertexGradient { get => m_enableVertexGradient; set { m_enableVertexGradient = value; SetMeshDirty(); } }
        public VertexGradient colorGradient { get => m_colorGradient; set { m_colorGradient = value; SetMeshDirty(); } }

        public TextAlignmentOptions alignment { get => m_alignment; set { m_alignment = value; SetMeshDirty(); } }
        public TextWrappingModes textWrappingMode { get => m_textWrappingMode; set { m_textWrappingMode = value; SetMeshDirty(); } }
        public TextOverflowModes overflowMode { get => m_overflowMode; set { m_overflowMode = value; SetMeshDirty(); } }

        public float characterSpacing { get => m_characterSpacing; set { m_characterSpacing = value; SetMeshDirty(); } }
        public float wordSpacing { get => m_wordSpacing; set { m_wordSpacing = value; SetMeshDirty(); } }
        public float lineSpacing { get => m_lineSpacing; set { m_lineSpacing = value; SetMeshDirty(); } }
        public float paragraphSpacing { get => m_paragraphSpacing; set { m_paragraphSpacing = value; SetMeshDirty(); } }
        public Vector4 margin { get => m_margin; set { m_margin = value; SetMeshDirty(); } }

        public bool richText { get => m_richText; set { m_richText = value; SetMeshDirty(); } }
        public bool isRightToLeftText { get => m_isRightToLeftText; set { m_isRightToLeftText = value; SetMeshDirty(); } }

        public int maxVisibleCharacters { get => m_maxVisibleCharacters; set { m_maxVisibleCharacters = value; SetMeshDirty(); } }
        public int maxVisibleWords { get => m_maxVisibleWords; set { m_maxVisibleWords = value; SetMeshDirty(); } }
        public int maxVisibleLines { get => m_maxVisibleLines; set { m_maxVisibleLines = value; SetMeshDirty(); } }

        // TODO R2: implement over the headless engine host. Returns zero in Round 1.
        public Vector2 GetPreferredValues() => Vector2.zero;
        public Vector2 GetPreferredValues(float width, float height) => Vector2.zero;
        public Vector2 GetRenderedValues() => Vector2.zero;

        // TODO R2: trigger a headless rebuild + MeshFilter.sharedMesh push.
        public void ForceMeshUpdate(bool ignoreActiveState = false, bool forceTextReparsing = false) => SetMeshDirty();

        // TODO R2: populate from the headless engine's positioned glyphs.
        public GlyphTextInfo textInfo => m_textInfo;
    }
}
