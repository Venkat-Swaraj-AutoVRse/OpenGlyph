# Padding

`Padding` is space between the RectTransform's edges and the text, like CSS padding: the text is laid
out in the rect inset by it.

![Padding: the same paragraph in two boxes of the same size, without padding and with padding 24, 12, 24, 12; the box shows the RectTransform and the inner line the content area](../.github/assets/features/padding.png)

```csharp
// x = left, y = top, z = right, w = bottom (pixels in the RectTransform's space)
card.Padding = new Vector4(24, 12, 24, 12);
```

Inspector: **Layout > Padding (L, T, R, B)**. Negative values are clamped to 0.

## What it affects

- **Layout and wrapping:** lines wrap at the inner width; alignment (left/centre/right, top/middle/bottom)
  is relative to the inner box. Auto Size fits the inner box.
- **Preferred size:** `preferredWidth` and `preferredHeight` include left + right and top + bottom, so a
  `ContentSizeFitter` or layout group sizes the rect around text plus padding. The content measurement
  methods include it too ([ContentMeasurement.md](ContentMeasurement.md)).
- **Overflow Clip:** with `Overflow = Clip`, glyphs are clipped to the inner box, so text never draws into
  the padding.
- **Hit testing:** `HitTest`, link clicks and hover use the glyphs where they are drawn (shifted by the
  padding); a point in the padding is not over any glyph.

## Padding and GlyphMeshProUGUI `margin`

GlyphMeshProUGUI's TMP `margin` uses the same mechanism (it insets the text area and positive margins
add to the preferred size). On a GlyphMeshProUGUI, `Padding` adds to `margin`. The difference: `margin`
may be negative (TMP allows the text area to grow), padding may not; and Clip uses the padded box, while
`margin` does not change the clip rect (as before).

## Tests

`Tests/Editor/Wave1PaddingTests.cs::Padding_InsetsLayoutPreferredSizeClipAndHitTest`, padding
(30, 10, 20, 5) on a 400 × 200 rect: preferred width +50 and height +15; mesh moves 30 px right and
10 px down; wrapping in a 260-wide rect with 60 left padding equals a 200-wide rect without padding
(same lines and positions); `HitTest` finds the first glyph where it is drawn and nothing in the padding;
the Clip rect is 350 × 185; negative values clamp to 0.
