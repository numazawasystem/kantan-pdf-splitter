# Compatibility regression fixtures

`office-like.pdf` and `google-docs-like.pdf` are **synthetic** regressions generated with `scripts/Generate-CompatibilityFixtures.py` using reportlab 4.4.9, pypdf 6.10.0 and Pillow 12.3.0. They are not actual Word/Google Docs exports and do not reproduce the user's unavailable problem file.

Both contain 3 mixed-size/rotation pages, embedded subset TrueType fonts and Unicode text, image XObjects, vector transparency, page `/Group` and `/UserUnit`, ordinary visible annotations, URI/direct/named GoTo links, nested bookmarks and tagged structure. The Google-style case uses a nested destination name tree. These ordinary feature combinations were rejected by v1.0.0. Font embedding uses the redistributable Bitstream Vera fonts bundled with reportlab; full terms are in `FONT-LICENSE.txt`.

Tests compare all page content, geometry and navigation. Exported evidence is independently checked for font/image/resource data and rendered pixels using pypdf/PDFium in CI and Poppler locally. Fixture files and Python tooling are development-only and are not installed with the product.
