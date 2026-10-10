# PDF compatibility correction — 1.0.1

## Verified cause and limits of inference

v1.0.0 rejected any `/Annots` (including ordinary links), `/Outlines`, `/Names`, `/StructTreeRoot`, or unrecognized page key such as `/Group`, `/UserUnit` and `/Tabs`. This is verified from commit `26ed90a65418cd600a81bf4e40a32303ba451902`. Synthetic regression PDFs containing these features are accepted by 1.0.1. The user's actual PDF is unavailable, so the precise trigger, generator and successful handling of that file remain unverified. We infer that one or more of these ordinary features caused the report; we do not claim an actual Word/Docs reproduction.

## Retention policy

| Information | 1.0.1 behavior |
|---|---|
| Text, embedded fonts, images, vector content | Page Resources/Contents copied without rasterization. Byte/resource and pixel comparison tests. |
| Media/Crop/Bleed/Trim/Art boxes, rotation | Retained by PDFsharp. |
| Transparency group, page size unit | Supplemented with an owned deep object copy, including stream/color resources. |
| Web and mail URI links | Retained; no network connection is made by the splitter. |
| Direct, named and legacy page links | Resolved to the new local page object when the target is in the same output. Destination zoom/coordinates preserved. |
| Target outside this output, unresolved page link | Action removed; annotation border/appearance kept. Shown before splitting and acknowledgment required. |
| Bookmarks | Retain in-range destinations and hierarchy. Retain a heading when an in-range child remains; prune unavailable leaf destinations. Warn when splitting affects them. |
| Named destinations | In-range names retained in a new destination tree. |
| Ordinary text/markup/appearance annotations | Copied with owned resources and corrected page pointer. Fields/media/file attachments remain unsupported. |
| Title, author, subject, keywords, language and output color profiles | Retained. Creator remains the app name. |
| Reading structure/tags | Not reconstructed. Do not copy StructTreeRoot/MarkInfo; remove orphaned StructParent(s), use row tab order instead of structure-dependent tab order. Warn explicitly and require acknowledgment. Text/paint remain intact, but reading order and accessibility structure may change. |
| Initial view, document page labels/viewer preferences | Omitted with a preflight notice. XML metadata and PDF/A/UA conformance are not guaranteed. |

The in-memory source page's annotation entry is detached only while PDFsharp imports its content, then restored in `finally`. Original file bytes are read-only throughout. This avoids following original navigation references into unselected pages before a complete new page map exists. Links and bookmark trees are rebuilt against that map. Unknown/dangerous features produce a specific rejection cause; the UI shows the rejected filename with a reading-error state, not a normal unselected state.

Safety gates remain for encryption including empty opening passwords, signatures, input forms/Widgets, attachments, scripts/automatic actions, file-launch/remote-document actions, multimedia and unsupported layers/page settings. An explicitly empty AcroForm Fields array with no XFA/signature flags is treated as a harmless producer placeholder, while actual fields and incomplete form definitions are rejected. Page data that cannot be safely copied is rejected, rather than silently changed.

## Library comparison

PDFsharp 6.2.4 remains the pinned MIT managed dependency. Its page import already retains content/resources and page boxes; the missing `/Group` and `/UserUnit` and navigation mapping are handled locally. No native runtime dependency or distribution size increase is needed beyond the compatibility code.

qpdf 12.4.2 is Apache-2.0; its official Windows msvc64 archive is 28,183,147 bytes compressed (it contains tools/dependencies/development material, so this is not an exact minimal runtime increase). Adopting it adds native executable/DLL packaging, provenance/security updates, process/error/cancellation management and notices. qpdf's `--split-pages` also discards outlines and document-level data; `--pages` does not fully prune/rebuild all document-level structures. It would not alone solve the chosen link/tag policy. Because PDFsharp passes the regression/resource/render comparisons, a runtime switch is not justified for this patch.

Official sources checked:

- [PDFsharp MIT license](https://docs.pdfsharp.net/General/License/License.html)
- [PDFsharp 6.2.4 page import source](https://github.com/empira/PDFsharp/blob/v6.2.4/src/foundation/src/PDFsharp/src/PdfSharp/Pdf/PdfPages.cs)
- [qpdf page-selection limitations](https://qpdf.readthedocs.io/en/stable/cli.html#limitations)
- [qpdf Apache license](https://github.com/qpdf/qpdf/blob/v12.4.2/LICENSE.txt)
- [qpdf official 12.4.2 assets](https://github.com/qpdf/qpdf/releases/tag/v12.4.2)
