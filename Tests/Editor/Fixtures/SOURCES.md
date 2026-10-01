# Natural-text segmentation fixtures — sources & license

These plain-text files are article intros from Wikipedia, fetched at **pinned
revisions**, used only as test data to measure dictionary-segmentation quality
(precision / recall / F1) against ICU's word BreakIterator.

**License:** Wikipedia text is licensed **CC BY-SA 4.0**
(https://creativecommons.org/licenses/by-sa/4.0/). It is included here for
testing with attribution, per that license. It is NOT shipped in any build — it
lives only under `Tests/Editor/Fixtures/` and is compiled only into the EditMode
test assembly.

| File | Script | Article | Pinned revision (oldid) | Source URL |
|------|--------|---------|-------------------------|------------|
| `Thai.txt`    | Thai (th)    | ประเทศไทย        | 13305422 | [th.wikipedia.org](https://th.wikipedia.org/w/index.php?title=%E0%B8%9B%E0%B8%A3%E0%B8%B0%E0%B9%80%E0%B8%97%E0%B8%A8%E0%B9%84%E0%B8%97%E0%B8%A2&oldid=13305422) |
| `Lao.txt`     | Lao (lo)     | ປະເທດລາວ         | 131073   | [lo.wikipedia.org](https://lo.wikipedia.org/w/index.php?title=%E0%BA%9B%E0%BA%B0%E0%BB%80%E0%BA%97%E0%BA%94%E0%BA%A5%E0%BA%B2%E0%BA%A7&oldid=131073) |
| `Khmer.txt`   | Khmer (km)   | ភ្នំពេញ (Phnom Penh) | 330402   | [km.wikipedia.org](https://km.wikipedia.org/w/index.php?title=%E1%9E%97%E1%9F%92%E1%9E%93%E1%9F%86%E1%9E%96%E1%9F%81%E1%9E%89&oldid=330402) |
| `Myanmar.txt` | Myanmar (my) | မြန်မာနိုင်ငံ       | 1027754  | [my.wikipedia.org](https://my.wikipedia.org/w/index.php?title=%E1%80%99%E1%80%BC%E1%80%94%E1%80%BA%E1%80%99%E1%80%AC%E1%80%94%E1%80%AD%E1%80%AF%E1%80%84%E1%80%BA%E1%80%84%E1%80%B6&oldid=1027754) |

Fetched via the MediaWiki `action=query&prop=extracts&explaintext` API at the
pinned `revids`, then trimmed to ≤ ~3.5 KB. The Khmer country-article intro
extract was empty at the pinned revision, so the Phnom Penh article was used
instead (same script, comparable prose).

`NaturalSegmentationFixtures.cs` (in `Tests/Editor/`) is generated from these
texts: it embeds each text plus the SA-run ranges and the reference word
boundaries produced by **ICU4N 60.1.0-alpha.356** (ICU word BreakIterator),
restricted to positions interior to SA runs.

---

## Font fixture — `NotoSansThai-Regular.ttf`

Used by the real-layout test (`RealLayoutTests.cs`) to drive OpenGlyph's actual
shaping + line-breaking pipeline over Thai text with a Thai-capable font, so the
dictionary word boundaries can be checked against real wrap points. Like the text
fixtures it is **test data only**: it lives under `Tests/Editor/Fixtures/` and is
NOT shipped in any player build (that folder is compiled only into the EditMode
test assembly).

**License:** **SIL Open Font License, Version 1.1 (OFL-1.1)** —
https://scripts.sil.org/OFL. This is confirmed by the font's own `name` table:
name ID 13 (License Description) reads *"This Font Software is licensed under the
SIL Open Font License, Version 1.1"* and name ID 14 points to the OFL URL. The
OFL explicitly permits bundling the font with software, including for testing.

| File | Family / version | Copyright | Pinned source |
|------|------------------|-----------|---------------|
| `NotoSansThai-Regular.ttf` | Noto Sans Thai, Version 2.002 (`name` ID 3: `2.002;GOOG;NotoSansThai-Regular`) | 2022 The Noto Project Authors (https://github.com/notofonts/thai) | `google/fonts@64fcce14ed3e59cb68c3d225c016c738923ff0dc`, path `ofl/notosansthai/NotoSansThai[wdth,wght].ttf` (see the sibling `.pin` file) |

The pinned source in [google/fonts](https://github.com/google/fonts) is the
`wght,wdth` **variable** font under `ofl/` (Google Fonts' OFL tree). The committed
fixture is the **Regular static instance** of that family (the `wght=400`,
`wdth=100` default master), which is why its `name` ID 3 is `NotoSansThai-Regular`;
the upstream copyright, version and OFL licence are carried unchanged. The `.pin`
file records the exact upstream commit so the fixture's provenance is auditable.
No Khmer/Myanmar/Lao font fixture is bundled; the real-layout test only renders a
script for which a font is present (Thai), and skips the others cleanly.
