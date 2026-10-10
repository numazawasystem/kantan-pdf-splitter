"""Synthetic Office/Google-style regressions, not actual Word/Docs exports.
Development only: reportlab, pypdf, Pillow. Runtime remains PDFsharp only.
"""
from pathlib import Path
from io import BytesIO
import shutil
import reportlab
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.utils import ImageReader
from reportlab.lib.colors import Color
from PIL import Image, ImageDraw
from pypdf import PdfReader, PdfWriter
from pypdf.generic import DictionaryObject as D, ArrayObject as A, NameObject as N, NumberObject as I, FloatObject as F, TextStringObject as T, BooleanObject as B, DecodedStreamObject

root = Path(__file__).resolve().parents[1]
destination = root / "tests/KantanPdfSplitter.Tests/Fixtures"
destination.mkdir(parents=True, exist_ok=True)
fonts = Path(reportlab.__file__).parent / "fonts"
pdfmetrics.registerFont(TTFont("RegressionVera", str(fonts / "Vera.ttf")))
pdfmetrics.registerFont(TTFont("RegressionVeraBold", str(fonts / "VeraBd.ttf")))
shutil.copyfile(fonts / "bitstream-vera-license.txt", destination / "FONT-LICENSE.txt")
image = Image.new("RGB", (120, 60), "white")
draw = ImageDraw.Draw(image)
draw.rectangle((0, 0, 40, 59), fill="#17324D")
draw.rectangle((40, 0, 80, 59), fill="#1874E6")
draw.rectangle((80, 0, 119, 59), fill="#DC3545")

for case in ("office-like", "google-docs-like"):
    data = BytesIO()
    doc = canvas.Canvas(data, pageCompression=1, invariant=1)
    doc.setTitle(f"Synthetic {case} compatibility regression")
    for index, size in enumerate(((595, 842), (842, 595), (440, 660)), 1):
        doc.setPageSize(size)
        doc.setFont("RegressionVeraBold", 22)
        doc.drawString(36, size[1] - 55, f"{case} / page {index}")
        doc.setFont("RegressionVera", 12)
        doc.drawString(36, size[1] - 86, "Embedded TrueType text, image, links and transparency.")
        doc.drawString(36, size[1] - 108, f"PAGE-{index:03}  Cafe / Omega: \u03a9")
        doc.drawImage(ImageReader(image), 36, size[1] - 210, 120, 60)
        doc.saveState(); doc.setFillColorRGB(0.1, 0.4, 0.9); doc.setFillAlpha(0.35)
        doc.rect(60, size[1] - 210, 120, 50, fill=1, stroke=0); doc.restoreState()
        doc.setFont("RegressionVera", 12); doc.drawString(36, 72, "Web link / next section")
        doc.linkURL("https://example.com/pdf-regression", (36, 68, 92, 86), relative=0, thickness=1, color=Color(0.1, 0.3, 0.7))
        doc.showPage()
    doc.save()
    writer = PdfWriter(clone_from=PdfReader(data))
    writer.pdf_header = "%PDF-1.7"
    writer.add_metadata({"/Creator": "Synthetic regression fixture (reportlab + pypdf)", "/Author": "KantanPdfSplitter tests"})
    page_refs = [page.indirect_reference for page in writer.pages]
    writer.add_named_destination("section-two", 1)
    writer.add_named_destination("section-three", 2)
    if case == "google-docs-like":
        tree = writer._root_object["/Names"]["/Dests"]
        leaf = writer._add_object(D({N("/Names"): tree.pop(N("/Names"))}))
        tree[N("/Kids")] = A([leaf])
    parent = writer.add_outline_item("Overview", 0)
    writer.add_outline_item("Second section", 1, parent=parent)
    writer.add_outline_item("Third section", 2)
    structure = D({N("/Type"): N("/StructTreeRoot")})
    structure_ref = writer._add_object(structure)
    children = A(); nums = A()
    for index, page in enumerate(writer.pages):
        page[N("/Group")] = D({N("/Type"): N("/Group"), N("/S"): N("/Transparency"), N("/CS"): N("/DeviceRGB"), N("/I"): B(True)})
        page[N("/UserUnit")] = F(2 if index == 2 else 1)
        page[N("/Tabs")] = N("/S")
        page[N("/StructParents")] = I(index)
        if index == 1: page[N("/Rotate")] = I(90)
        stream = DecodedStreamObject()
        stream.set_data(b"/P <</MCID 0>> BDC\n" + page.get_contents().get_data() + b"\nEMC\n")
        page[N("/Contents")] = writer._add_object(stream.flate_encode())
        element = writer._add_object(D({N("/Type"): N("/StructElem"), N("/S"): N("/P"), N("/P"): structure_ref, N("/Pg"): page_refs[index], N("/K"): I(0)}))
        children.append(element); nums.extend([I(index), A([element])])
        link = D({N("/Type"): N("/Annot"), N("/Subtype"): N("/Link"), N("/Rect"): A([I(120), I(68), I(245), I(86)]), N("/Border"): A([I(0), I(0), I(1)]), N("/C"): A([F(0.8), F(0.1), F(0.1)]), N("/P"): page_refs[index]})
        if index == 0: link[N("/Dest")] = A([page_refs[1], N("/XYZ"), I(36), I(500), F(1.25)])
        elif index == 1: link[N("/A")] = D({N("/S"): N("/GoTo"), N("/D"): T("section-three")})
        else: link[N("/Dest")] = A([page_refs[2], N("/Fit")])
        page["/Annots"].append(writer._add_object(link))
        # An appearance stream checks that ordinary visible annotations remain vector data.
        appearance = DecodedStreamObject(); appearance.set_data(b"1 0.85 0 rg 0 0 90 18 re f\n")
        appearance.update({N("/Type"): N("/XObject"), N("/Subtype"): N("/Form"), N("/BBox"): A([I(0), I(0), I(90), I(18)]), N("/Resources"): D()})
        note = D({N("/Type"): N("/Annot"), N("/Subtype"): N("/Highlight"), N("/Rect"): A([I(36), I(40), I(126), I(58)]), N("/QuadPoints"): A([I(36), I(58), I(126), I(58), I(36), I(40), I(126), I(40)]), N("/P"): page_refs[index], N("/AP"): D({N("/N"): writer._add_object(appearance)})})
        page["/Annots"].append(writer._add_object(note))
    structure[N("/K")] = children
    structure[N("/ParentTree")] = writer._add_object(D({N("/Nums"): nums}))
    structure[N("/ParentTreeNextKey")] = I(3)
    writer._root_object[N("/StructTreeRoot")] = structure_ref
    writer._root_object[N("/MarkInfo")] = D({N("/Marked"): B(True)})
    writer._root_object[N("/Lang")] = T("en-US")
    writer.write(destination / f"{case}.pdf")
    print(f"Created {case}.pdf (synthetic, 3 pages)")
