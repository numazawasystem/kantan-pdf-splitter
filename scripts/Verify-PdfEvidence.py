"""Optional independent QA: pip install pypdf pillow; requires Poppler pdftoppm.
Usage: python Verify-PdfEvidence.py evidence_dir pdftoppm.exe report.json
Checks every generated fixture page through a parser and renderer independent
of PDFsharp. Test evidence is exported by the Windows CI tests.
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from PIL import Image, ImageChops
from pypdf import PdfReader

evidence, renderer, report_path = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3])
source = PdfReader(evidence / "source-20.pdf")
records = []
with tempfile.TemporaryDirectory(prefix="KantanPdfVisual-") as temporary:
    temp = Path(temporary)
    for index, page in enumerate(source.pages, 1):
        output_path = evidence / f"sample_{index:03}.pdf"
        output = PdfReader(output_path)
        assert len(output.pages) == 1
        result = output.pages[0]
        assert page.get_contents().get_data() == result.get_contents().get_data()
        assert page.extract_text() == result.extract_text()
        assert result.extract_text().strip() == f"PAGE-{index:03}"
        for box in ("mediabox", "cropbox", "trimbox", "bleedbox", "artbox"):
            assert list(getattr(page, box)) == list(getattr(result, box))
        assert page.rotation == result.rotation
        assert page["/Resources"]["/XObject"]["/Im1"].get_data() == result["/Resources"]["/XObject"]["/Im1"].get_data()
        for file, number, prefix in ((evidence / "source-20.pdf", index, "input"), (output_path, 1, "output")):
            subprocess.run([renderer, "-f", str(number), "-l", str(number), "-cropbox", "-scale-to", "700", "-png", "-singlefile", str(file), str(temp / prefix)], check=True, capture_output=True)
        with Image.open(temp / "input.png") as before, Image.open(temp / "output.png") as after:
            assert before.size == after.size
            assert ImageChops.difference(before.convert("RGB"), after.convert("RGB")).getbbox() is None
        records.append({"source_page": index, "output": output_path.name, "text_content_resources_boxes_rotation": "PASS", "render_pixel_difference": 0})
report = {"verdict": "PASS", "pages": len(records), "independent_tools": ["pypdf", "Poppler", "Pillow"], "results": records}
report_path.parent.mkdir(parents=True, exist_ok=True)
report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"PASS: {len(records)} pages, exact content and image data; render pixel difference zero.")
