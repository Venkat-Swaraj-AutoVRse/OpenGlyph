# Content measurement

Three queries on `UniText` (and `GlyphMeshProUGUI`) tell you how much room a text needs, without
changing the component:

| Method | Returns |
|---|---|
| `GetMinContentWidth()` | the narrowest width the text can wrap to without breaking inside a word: its widest unbreakable segment |
| `GetMaxContentWidth()` | the width of its widest line between hard line breaks, i.e. the width at which nothing wraps (= `preferredWidth`) |
| `GetHeightForWidth(float width)` | its height when the component is `width` wide |

All three include `Padding` (and the TMP `margin` on GlyphMeshProUGUI): widths are outer widths, and
the `width` you pass to `GetHeightForWidth` is the outer width.

```csharp
var t = GetComponent<UniText>();
float narrowest = t.GetMinContentWidth();   // e.g. size a column so no word breaks
float widest    = t.GetMaxContentWidth();   // single-line width
float height    = t.GetHeightForWidth(320); // height of a 320-wide card
```

## Rules

- **Min-content** is measured the way the line breaker measures: a segment runs up to a line-break
  opportunity (UAX #14, including dictionary breaks for Thai/Lao/Khmer/Myanmar), and a breaking space at
  its end is counted because the breaker counts it (GlyphMeshProUGUI hangs trailing spaces like TMP, so
  there it is not counted). List margins at the start of a line are included. Laying the text out at
  exactly this width never breaks a word.
- Without word wrap, min-content equals max-content (lines break only at hard breaks).
- **Auto Size:** min-content is measured at `MinFontSize` (the text can shrink that far), max-content at
  `MaxFontSize`. `GetHeightForWidth` uses `MaxFontSize` with word wrap (there is no height to shrink
  into) and, without word wrap, the size that makes the widest line fit the width (with `AutoSizeStep`).
- Widths are rounded up to whole pixels, like `preferredWidth`.
- `GetHeightForWidth` uses the same line breaking, line heights, `<line-height>` modifiers, indents and
  edge trimming (`OverEdge`/`UnderEdge`) as the real layout. It equals `preferredHeight` of a component of
  that width.

## No side effects

The queries run the first pass (markup parse and shaping) if it has not run yet, exactly as the next
canvas update would. They never change the RectTransform, the current line breaks, glyph positions,
`preferredWidth`/`preferredHeight` or the mesh: line breaking for a query width runs into separate
scratch buffers.

## Layout groups: `ContentMinWidth`

`ILayoutElement.minWidth` is 0 by default, as before. Turn on **Content Min Width** (`ContentMinWidth =
true`) to report `GetMinContentWidth()` as `minWidth`, so a `HorizontalLayoutGroup` never squeezes the
text narrower than its widest word. `preferredWidth`/`preferredHeight` were already reported and now
include `Padding`.

## Tests

`Tests/Editor/Wave1MeasurementTests.cs`:

- `MinContentWidth_IsTheWidestUnbreakableSegment`: `a bb ccc extraordinarily` → the width of
  `extraordinarily`; `extraordinarily a bb` → the width of `extraordinarily ` (breaking space counted); laid
  out at that width the text has exactly 2 lines; padding 10 + 15 adds 25; Auto Size measures at
  `MinFontSize` 12; without wrap it is the widest hard line.
- `MaxContentWidth_IsTheWidestLineBetweenHardBreaks`: `short\nthe widest line here\nmid` → the width of the
  middle line, equal to `preferredWidth`; padding adds 12.
- `HeightForWidth_IsTheWrappedHeight_AndDoesNotTouchLayout`: `mmmm mmmm mmmm` at 36 px is ascender −
  descender = 49.032 for one line and 49.032 + 2 × 49.032 = 147.096 for three lines at the min-content
  width; equals `preferredHeight` of a component that wide; glyph positions, line count, rect and
  `preferredHeight` of the measured component are unchanged afterwards.
- `ContentMinWidth_FeedsILayoutElementMinWidth`.
