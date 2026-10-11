using System.IO;
using KantanPdfSplitter.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace KantanPdfSplitter.Tests;

public sealed class NoticeTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "KantanNotices", Guid.NewGuid().ToString("N"))).FullName;
    private string Make(Action<PdfDocument> customize) => SplitTests.Fixture(Path.Combine(_root, "notice.pdf"), 3, customize);
    private string Output => Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
    private static IReadOnlyList<PdfNotice> Notices(PdfInfo info, string cuts = "") => info.Compatibility.Notices(SplitPlan.Create(info, cuts.Length == 0, cuts));

    [Theory] [InlineData("/StructTreeRoot")] [InlineData("/Outlines")] [InlineData("/Names")]
    [InlineData("/ViewerPreferences")] [InlineData("/PageLabels")] [InlineData("/OpenAction")]
    public async Task EmptyProducerPlaceholdersHaveNoImpact(string key)
    {
        var info = await new PdfSplitService().InspectAsync(Make(d => d.Internals.Catalog.Elements[key] = new PdfDictionary(d)));
        Assert.Empty(Notices(info));
        Assert.Equal(3, (await new PdfSplitService().SplitAsync(info, SplitPlan.Create(info, true, ""), Output)).Count);
    }
    [Fact] public async Task EmptyTagDeclarationIsNotReadingStructure()
    {
        var info = await new PdfSplitService().InspectAsync(Make(d =>
        {
            var root = new PdfDictionary(d); root.Elements.SetName("/Type", "/StructTreeRoot"); root.Elements["/K"] = new PdfArray(d);
            root.Elements["/ParentTree"] = new PdfDictionary(d); d.Internals.Catalog.Elements["/StructTreeRoot"] = root;
            root.Elements.SetInteger("/ParentTreeNextKey", 0);
            var mark = new PdfDictionary(d); mark.Elements.SetBoolean("/Marked", true); d.Internals.Catalog.Elements["/MarkInfo"] = mark;
        }));
        Assert.False(info.Compatibility.HasTags); Assert.Empty(Notices(info));
    }
    [Theory] [InlineData("structure")] [InlineData("page-association")] [InlineData("annotation-association")]
    public async Task ReadingStructureLossAlwaysRequiresConfirmation(string kind)
    {
        var path = Make(d =>
        {
            if (kind == "structure") { var root = new PdfDictionary(d); root.Elements["/K"] = new PdfInteger(0); d.Internals.Catalog.Elements["/StructTreeRoot"] = root; }
            else if (kind == "page-association") d.Pages[0].Elements.SetInteger("/StructParents", 0);
            else { var a = new PdfDictionary(d); a.Elements.SetName("/Subtype", "/Text"); a.Elements.SetInteger("/StructParent", 0); var annots = new PdfArray(d); annots.Elements.Add(a); d.Pages[0].Elements["/Annots"] = annots; }
        });
        var service = new PdfSplitService(); var info = await service.InspectAsync(path);
        var notice = Assert.Single(Notices(info)); Assert.Equal("reading-structure", notice.Code); Assert.True(notice.RequiresConfirmation);
        Assert.Contains("読み上げ", notice.Summary); Assert.True(notice.Summary.Length < 35);
        // Revalidation must enforce the important gate even if a caller changes the UI assessment.
        var forged = info with { Compatibility = new PdfCompatibility() };
        await Assert.ThrowsAsync<PdfSplitException>(() => service.SplitAsync(forged, SplitPlan.Create(info, true, ""), Output));
        Assert.Empty(Directory.GetFileSystemEntries(Output));
        Assert.Equal(3, (await service.SplitAsync(info, SplitPlan.Create(info, true, ""), Output, acknowledgeCompatibilityWarnings: true)).Count);
    }
    [Theory] [InlineData("link")] [InlineData("bookmark")] [InlineData("named")]
    public async Task NavigationNoticeDependsOnActualOutputRangesAndNeverBlocks(string kind)
    {
        var path = Make(d =>
        {
            if (kind == "link") d.Pages[0].AddDocumentLink(new PdfRectangle(new XRect(0, 0, 30, 30)), 2);
            if (kind == "bookmark") d.Outlines.Add("Chapter", d.Pages[1], true);
            if (kind == "named") { var dest = new PdfArray(d); dest.Elements.Add(d.Pages[1].Reference!); dest.Elements.Add(new PdfName("/Fit")); var names = new PdfDictionary(d); names.Elements["/chapter"] = dest; d.Internals.Catalog.Elements["/Dests"] = names; }
        });
        var service = new PdfSplitService(); var info = await service.InspectAsync(path);
        Assert.Empty(Notices(info, "3")); // Entire document retained in one output.
        var notice = Assert.Single(Notices(info)); Assert.Equal(PdfNoticeSeverity.Information, notice.Severity); Assert.False(notice.RequiresConfirmation);
        var plan = SplitPlan.Create(info, true, ""); var outputs = await service.SplitAsync(info, plan, Output);
        Assert.Equal(3, outputs.Count); foreach (var output in outputs) { using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import); Assert.Equal(1, pdf.PageCount); }
        if (kind == "link")
        {
            Assert.Empty(Notices(info, "2")); // Source and target remain together, even with two outputs.
            using var result = PdfReader.Open(outputs[0], PdfDocumentOpenMode.Import);
            Assert.False(result.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!.Elements.ContainsKey("/Dest"));
        }
    }
    [Fact] public async Task SelfPageLinksRemainUsableWithoutNotice()
    {
        var service = new PdfSplitService(); var info = await service.InspectAsync(Make(d => d.Pages[0].AddDocumentLink(new PdfRectangle(new XRect(0, 0, 30, 30)), 1)));
        Assert.Empty(Notices(info));
        var outputs = await service.SplitAsync(info, SplitPlan.Create(info, true, ""), Output);
        using var pdf = PdfReader.Open(outputs[0], PdfDocumentOpenMode.Import);
        Assert.Equal(1, new PdfNavigation(pdf).LinkDestination(pdf.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!)!.Page);
    }
    [Theory] [InlineData("link")] [InlineData("bookmark")] [InlineData("named")]
    public async Task UnresolvedNavigationIsReportedEvenForOneOutput(string kind)
    {
        var info = await new PdfSplitService().InspectAsync(Make(d =>
        {
            if (kind == "named") { var dests = new PdfDictionary(d); dests.Elements["/missing"] = new PdfString("undefined"); d.Internals.Catalog.Elements["/Dests"] = dests; }
            else if (kind == "bookmark") { var outline = d.Outlines.Add("Missing", d.Pages[0], true); outline.Elements["/Dest"] = new PdfString("undefined"); }
            else { var a = new PdfDictionary(d); a.Elements.SetName("/Subtype", "/Link"); a.Elements["/Rect"] = new PdfRectangle(new XRect(0, 0, 20, 20)); a.Elements["/Dest"] = new PdfString("undefined"); var annots = new PdfArray(d); annots.Elements.Add(a); d.Pages[0].Elements["/Annots"] = annots; }
        }));
        var notice = Assert.Single(Notices(info, "3")); Assert.False(notice.RequiresConfirmation);
        Assert.Equal(kind == "link" ? "page-links" : kind == "bookmark" ? "unresolved-bookmarks" : "navigation", notice.Code);
    }
    [Theory] [InlineData("default-labels")] [InlineData("default-viewer")]
    public async Task DefaultDisplaySettingsNeedNoNotice(string kind)
    {
        var info = await new PdfSplitService().InspectAsync(Make(d =>
        {
            if (kind == "default-labels") SetLabels(d, "/D");
            else { var prefs = new PdfDictionary(d); prefs.Elements.SetBoolean("/HideToolbar", false); prefs.Elements.SetName("/Direction", "/L2R"); prefs.Elements.SetName("/PrintScaling", "/AppDefault"); d.Internals.Catalog.Elements["/ViewerPreferences"] = prefs; }
        }));
        Assert.Empty(Notices(info));
    }
    [Theory] [InlineData("labels", "ページ番号の表示")] [InlineData("viewer", "表示・印刷の設定")] [InlineData("opening", "開いたときの移動先")]
    public async Task ChangedDisplaySettingIsNamedPreciselyAndDoesNotBlock(string kind, string summary)
    {
        var service = new PdfSplitService(); var info = await service.InspectAsync(Make(d =>
        {
            if (kind == "labels") SetLabels(d, "/r");
            if (kind == "viewer") { var prefs = new PdfDictionary(d); prefs.Elements.SetBoolean("/HideToolbar", true); d.Internals.Catalog.Elements["/ViewerPreferences"] = prefs; }
            if (kind == "opening") { var dest = new PdfArray(d); dest.Elements.Add(d.Pages[1].Reference!); dest.Elements.Add(new PdfName("/Fit")); d.Internals.Catalog.Elements["/OpenAction"] = dest; }
        }));
        var notice = Assert.Single(Notices(info)); Assert.Equal(summary + "は引き継がれません。", notice.Summary); Assert.False(notice.RequiresConfirmation);
        Assert.Equal(3, (await service.SplitAsync(info, SplitPlan.Create(info, true, ""), Output)).Count);
    }
    private static void SetLabels(PdfDocument d, string style)
    {
        var label = new PdfDictionary(d); label.Elements.SetName("/S", style);
        var nums = new PdfArray(d); nums.Elements.Add(new PdfInteger(0)); nums.Elements.Add(label);
        var labels = new PdfDictionary(d); labels.Elements["/Nums"] = nums; d.Internals.Catalog.Elements["/PageLabels"] = labels;
    }
    public void Dispose() => Directory.Delete(_root, true);
}
