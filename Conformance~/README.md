# OpenGlyph Unicode conformance data

Official Unicode Character Database test files used by `Tests/Editor/Conformance/*`
(NUnit category `Conformance`). The folder name ends in `~`, so Unity does not import it.

Unicode version: **17.0.0** (matches `Resources/UnicodeData.bytes`).

| File | Source | Standard |
|---|---|---|
| `ucd-17.0.0/BidiTest.txt` | https://www.unicode.org/Public/17.0.0/ucd/BidiTest.txt | UAX #9 |
| `ucd-17.0.0/BidiCharacterTest.txt` | https://www.unicode.org/Public/17.0.0/ucd/BidiCharacterTest.txt | UAX #9 |
| `ucd-17.0.0/LineBreakTest.txt` | https://www.unicode.org/Public/17.0.0/ucd/auxiliary/LineBreakTest.txt | UAX #14 |
| `ucd-17.0.0/GraphemeBreakTest.txt` | https://www.unicode.org/Public/17.0.0/ucd/auxiliary/GraphemeBreakTest.txt | UAX #29 |
| `ucd-17.0.0/Scripts.txt` | https://www.unicode.org/Public/17.0.0/ucd/Scripts.txt | UAX #24 |
| `ucd-17.0.0/ScriptExtensions.txt` | https://www.unicode.org/Public/17.0.0/ucd/ScriptExtensions.txt | UAX #24 |
| `ucd-17.0.0/PropertyValueAliases.txt` | https://www.unicode.org/Public/17.0.0/ucd/PropertyValueAliases.txt | maps 4-letter script codes (ScriptExtensions.txt) to long names |

Machine-readable results of the last run are written to `results/<standard>.json`.

Run: Test Runner (EditMode) with category `Conformance`, or filter `LightSide.Tests.Conformance`.

## License

The data files are redistributed unmodified under the Unicode License v3
(https://www.unicode.org/license.txt, reproduced below).

---

UNICODE LICENSE V3

COPYRIGHT AND PERMISSION NOTICE

Copyright © 1991-2026 Unicode, Inc.

NOTICE TO USER: Carefully read the following legal agreement. BY
DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

Permission is hereby granted, free of charge, to any person obtaining a
copy of data files and any associated documentation (the "Data Files") or
software and any associated documentation (the "Software") to deal in the
Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell
copies of the Data Files or Software, and to permit persons to whom the
Data Files or Software are furnished to do so, provided that either (a)
this copyright and permission notice appear with all copies of the Data
Files or Software, or (b) this copyright and permission notice appear in
associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
THIRD PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall
not be used in advertising or otherwise to promote the sale, use or other
dealings in these Data Files or Software without prior written
authorization of the copyright holder.
