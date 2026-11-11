# Unicode 16.0.0 data

These data files and the official conformance fixture are from Unicode, Inc.,
https://www.unicode.org/Public/16.0.0/ucd/ . Retrieved 2026-10-09.
`SHA256.json` pins the exact local source bytes; regeneration verifies every hash.

| Local file | Relative upstream path |
|---|---|
| GraphemeBreakProperty.txt | auxiliary/GraphemeBreakProperty.txt |
| GraphemeBreakTest.txt | auxiliary/GraphemeBreakTest.txt |
| DerivedCoreProperties.txt | DerivedCoreProperties.txt |
| emoji-data.txt | emoji/emoji-data.txt |
| EastAsianWidth.txt | EastAsianWidth.txt |
| ReadMe.txt | ReadMe.txt |
| LICENSE.txt | https://www.unicode.org/license.txt (retrieval date above) |

The original copyright headers are retained. Unicode License V3 permits use,
modification and redistribution with its copyright/permission notice. Include
`LICENSE.txt` in binary distributions that contain these derived tables, as well
as the project's GPLv3 license. This license does not replace GPLv3 for TermLink code.

Run `scripts/Generate-Unicode16.py` using the pinned development Python to rebuild
the sorted, merged Rust lookup tables. It performs no network access. The UAX #29
algorithm is implemented by TermLink; no external terminal implementation is copied.
