"""Independent PDF regression QA (development only, not shipped).
Usage: python Verify-PdfEvidence.py evidence_dir pdftoppm.exe|pdfium report.json
Compares normal and synthetic Office/Google-style split evidence. Checks text,
font/image/resource bytes, boxes, units, rotation, annotation appearance and links
in addition to rendering with an engine independent of PDFsharp.
"""
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from PIL import Image, ImageChops
from pypdf import PdfReader
from pypdf.generic import DictionaryObject, ArrayObject, StreamObject, IndirectObject

evidence, renderer, report_path = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3])
records = []

def normalize(value, ancestors=frozenset()):
    if isinstance(value, IndirectObject): value = value.get_object()
    if id(value) in ancestors: return "cycle"
    seen = ancestors | {id(value)}
    if isinstance(value, StreamObject):
        return {"data_sha256": hashlib.sha256(value.get_data()).hexdigest(), "properties": {str(k): normalize(v, seen) for k, v in value.items() if k not in ("/Length", "/Filter", "/DecodeParms", "/StructParent", "/StructParents")}}
    if isinstance(value, DictionaryObject): return {str(k): normalize(v, seen) for k, v in value.items() if k not in ("/StructParent", "/StructParents")}
    if isinstance(value, ArrayObject): return [normalize(v, seen) for v in value]
    if value is None or str(value) == "NullObject": return None
    return str(value)

def destination(reader, annotation):
    value = annotation.get("/Dest")
    if value is None:
        action = annotation.get("/A", {}).get_object() if annotation.get("/A") else {}
        if action.get("/S") != "/GoTo": return None
        value = action.get("/D")
    if isinstance(value, IndirectObject): value = value.get_object()
    if isinstance(value, str):
        entry = reader.named_destinations.get(value.lstrip("/"))
        if entry is None: return (-1, None)
        value = entry.dest_array
    if not isinstance(value, (list, ArrayObject)) or not value: return (-1, None)
    target = value[0]
    for number, page in enumerate(reader.pages):
        if target == page.indirect_reference: return (number, normalize(value[1:]))
    if isinstance(target, int) and 0 <= target < len(reader.pages): return (target, normalize(value[1:]))
    return (-1, None)

def render(file, number, prefix, temp):
    if renderer == "pdfium":
        import pypdfium2
        document = pypdfium2.PdfDocument(file)
        try:
            page = document.get_page(number - 1)
            try:
                bitmap = page.render(scale=700 / max(page.get_size()))
                try: bitmap.to_pil().convert("RGB").save(temp / (prefix + ".png"))
                finally: bitmap.close()
            finally: page.close()
        finally: document.close()
    else:
        subprocess.run([renderer, "-f", str(number), "-l", str(number), "-cropbox", "-scale-to", "700", "-png", "-singlefile", str(file), str(temp / prefix)], check=True, capture_output=True)

cases = [(evidence / "source-20.pdf", sorted(evidence.glob("sample_*.pdf")))]
cases += [(folder / "source.pdf", sorted(p for p in folder.glob("*.pdf") if p.name != "source.pdf")) for folder in sorted(evidence.iterdir()) if folder.is_dir() and (folder / "source.pdf").exists()]
assert len(cases) >= 7, "Office/Google regression evidence must actually be present"
with tempfile.TemporaryDirectory(prefix="KantanPdfVisual-") as temporary:
    temp = Path(temporary)
    for source_path, outputs in cases:
        source = PdfReader(source_path)
        source_index = 0
        for output_path in outputs:
            output = PdfReader(output_path)
            first = source_index
            last = first + len(output.pages) - 1
            for number, result in enumerate(output.pages, 1):
                page = source.pages[source_index]
                assert page.get_contents().get_data() == result.get_contents().get_data(), (source_path, source_index, "content")
                assert page.extract_text() == result.extract_text(), (source_path, source_index, "text")
                for box in ("mediabox", "cropbox", "trimbox", "bleedbox", "artbox"):
                    assert list(getattr(page, box)) == list(getattr(result, box)), (source_path, source_index, box)
                assert page.rotation == result.rotation
                assert page.get("/UserUnit", 1) == result.get("/UserUnit", 1)
                assert normalize(page["/Resources"]) == normalize(result["/Resources"]), (source_path, source_index, "font/image/resource data")
                assert normalize(page.get("/Group")) == normalize(result.get("/Group"))
                before_annotations = [item.get_object() for item in page.get("/Annots", [])]
                after_annotations = [item.get_object() for item in result.get("/Annots", [])]
                assert len(before_annotations) == len(after_annotations)
                for before, after in zip(before_annotations, after_annotations):
                    appearance = lambda item: {k: normalize(v) for k, v in item.items() if k not in ("/P", "/Dest", "/A", "/StructParent")}
                    assert appearance(before) == appearance(after), (source_path, source_index, "annotation appearance")
                    if before.get("/A") and before["/A"].get("/S") == "/URI": assert normalize(before["/A"]) == normalize(after["/A"])
                    jump = destination(source, before)
                    if jump:
                        if first <= jump[0] <= last: assert destination(output, after) == (jump[0] - first, jump[1])
                        else: assert after.get("/Dest") is None and after.get("/A") is None
                render(source_path, source_index + 1, "input", temp)
                render(output_path, number, "output", temp)
                with Image.open(temp / "input.png") as before, Image.open(temp / "output.png") as after:
                    assert before.size == after.size
                    assert ImageChops.difference(before.convert("RGB"), after.convert("RGB")).getbbox() is None, (source_path, source_index, "render")
                records.append({"case": source_path.parent.name, "source_page": source_index + 1, "output": output_path.name, "text_fonts_images_boxes_links": "PASS", "render_pixel_difference": 0})
                source_index += 1
        assert source_index == len(source.pages), "missing/reordered page"
report = {"verdict": "PASS", "cases": len(cases), "page_comparisons": len(records), "independent_tools": ["pypdf", "PDFium" if renderer == "pdfium" else "Poppler", "Pillow"], "results": records}
report_path.parent.mkdir(parents=True, exist_ok=True)
report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"PASS: {len(cases)} cases / {len(records)} page comparisons; exact text/resources/navigation; render pixel difference zero.")
