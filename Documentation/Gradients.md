# Radial and angular gradients

Gradients now come in three shapes: **linear** (along a direction), **radial** (out from a centre) and
**angular** (a conic sweep around a centre), on a span with `<gradient>` or on the whole text with
`UniText.GradientFill`.

![Linear, radial and angular gradients as span tags and as whole-text fills, including a two-line radial fill](../.github/assets/features/gradients.png)

## Span tag

Named gradients come from the `UniTextGradients` asset in `UniTextSettings` (as before):

| Markup | Shape |
|---|---|
| `<gradient=name>`, `<gradient=name,angle>` | linear, angle in degrees (0 = →, 90 = ↑), over the span's visual bounds |
| `<gradient=name,L>` | linear by character index (logical) |
| `<gradient=name,radial>` | radial from the centre of the span's bounds |
| `<gradient=name,radial,cx,cy,radius>` | centre normalised to the bounds (0..1), radius 1 = farthest corner |
| `<gradient=name,angular>` | angular, starting at 0° (→), counter-clockwise |
| `<gradient=name,angular,startDeg,cx,cy>` | start angle and centre |

`r` is an alias of `radial`, `conic` and `a` of `angular`. For a span over several lines the bounds are
the box around all its lines.

## Whole text: `UniText.GradientFill`

```csharp
using LightSide;

text.GradientFill = UniTextGradientFill.Radial(myGradient, new Vector2(0.5f, 0.5f), radius: 1f);
text.GradientFill = UniTextGradientFill.Angular(myGradient, new Vector2(0.5f, 0.5f), startAngleDeg: 90f);
text.GradientFill = UniTextGradientFill.Linear(myGradient, angleDeg: 0f);
text.GradientFill = UniTextGradientFill.None;
```

Inspector: **Effects > Gradient Fill**. The fill is the base colour of every glyph; `<color>` and
`<gradient>` spans override it on their range. Reassign the struct after changing the gradient's stops.

## Renderers and cost

Gradients are vertex colours: each glyph quad gets a colour at its 4 corners, computed once per mesh
rebuild (no per-frame cost). They render the same in the unified and the legacy renderer.

## Limitations

- One colour per quad corner: inside a large glyph the colour is interpolated linearly, so a radial
  centre inside one big letter is approximate.
- Angular: a glyph that straddles the start angle (the seam) is coloured from one side of the seam
  (corners are unwrapped around the quad centre) instead of smearing through the whole gradient.
- Gradient alpha is ignored; alpha follows the component colour (as for linear gradients).
- Colour emoji keep their own colours.

## Tests

`Tests/Editor/Wave2GradientTests.cs` (unified and legacy): `SpanRadial_DarkAtCentre_BrightAtEnds`
(black→white radial over 7 glyphs: per-glyph mean 216, 146, 81, 52, 83, 149, 218),
`SpanRadial_CentreAndRadiusParameters`, `SpanAngular_SweepsAroundTheCentre`,
`WholeText_RadialFill_AndSpanColourOverrides`, `WholeText_LinearAndAngularFill_AndOff`,
`SpanLinear_Unchanged`.
