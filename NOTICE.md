# OpenGlyph

OpenGlyph is an independent, MIT-licensed fork of UniText 1.0 (MIT) by Light Side LLC.
It is not affiliated with or endorsed by Light Side LLC. "UniText" is their product name.

## Clean-room policy

OpenGlyph is built only from:

- the MIT-licensed UniText 1.0 source,
- public specifications (Unicode UAX/UTS, OpenType, FreeType, HarfBuzz, Blend2D docs),
- contributors' own original work.

Contributors must NOT copy code, shaders, documentation text, images or other material
from UniText 2.0 / Platinum or any other PolyForm-Noncommercial-licensed LightSide release.

## Compatibility

The UPM package ID is `com.openglyph.text`. The C# namespaces and assembly names
(`LightSide.UniText*`) are unchanged for now so existing code keeps compiling; they will be
renamed in a later, versioned breaking release.
