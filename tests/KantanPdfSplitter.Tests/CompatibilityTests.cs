using System.IO;
using System.Security.Cryptography;
using KantanPdfSplitter.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Xunit;

namespace KantanPdfSplitter.Tests;

public sealed class CompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KantanCompatibility", Guid.NewGuid().ToString("N"));
    public CompatibilityTests() => Directory.CreateDirectory(_root);
    private string Output => Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
    private string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".pdf");
    private string Make(Action<PdfDocument> customize) => SplitTests.Fixture(Path.Combine(_root, "test.pdf"), 3, customize);

    [Theory] [InlineData("office-like", "2")] [InlineData("google-docs-like", "2")]
    [InlineData("office-like", "3")] [InlineData("google-docs-like", "3")]
    public async Task OfficeFeaturesPreserveContentAndNavigation(string name, string cuts)
    {
        var sourcePath = Fixture(name); var bytes = SHA256.HashData(File.ReadAllBytes(sourcePath));
        var service = new PdfSplitService(); var info = await service.InspectAsync(sourcePath);
        Assert.True(info.Compatibility.HasTags); Assert.True(info.Compatibility.HasBookmarks); Assert.True(info.Compatibility.HasNamedDestinations);
        var plan = SplitPlan.Create(info, false, cuts);
        Assert.NotEmpty(info.Compatibility.Warnings(plan));
        var paths = await service.SplitAsync(info, plan, Output, acknowledgeCompatibilityWarnings: true);
        using var source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
        for (var part = 0; part < paths.Count; part++)
        {
            using var result = PdfReader.Open(paths[part], PdfDocumentOpenMode.Import);
            var range = plan[part]; Assert.Equal(range.Count, result.PageCount);
            Assert.Equal(range.Count, result.Internals.GetAllObjects().OfType<PdfDictionary>().Count(o => PdfNavigation.Name(o.Elements["/Type"]) == "/Page"));
            Assert.False(result.Internals.Catalog.Elements.ContainsKey("/StructTreeRoot")); Assert.False(result.Internals.Catalog.Elements.ContainsKey("/MarkInfo"));
            Assert.Equal(source.Info.Title, result.Info.Title); Assert.Equal(source.Info.Author, result.Info.Author);
            for (var number = range.First; number <= range.Last; number++)
            {
                var before = source.Pages[number - 1]; var after = result.Pages[number - range.First];
                Assert.Equal(before.MediaBox, after.MediaBox); Assert.Equal(before.CropBox, after.CropBox); Assert.Equal(before.Rotate, after.Rotate);
                Assert.Equal(before.Elements.GetReal("/UserUnit"), after.Elements.GetReal("/UserUnit"));
                Assert.Equal(before.Contents.CreateSingleContent().Stream.UnfilteredValue, after.Contents.CreateSingleContent().Stream.UnfilteredValue);
                Assert.Equal(before.Elements.GetDictionary("/Group")!.Elements.GetName("/CS"), after.Elements.GetDictionary("/Group")!.Elements.GetName("/CS"));
                Assert.False(after.Elements.ContainsKey("/StructParents")); Assert.Equal("/R", after.Elements.GetName("/Tabs"));
                Assert.Equal(before.Resources.Elements.GetDictionary("/Font")!.Elements.Count, after.Resources.Elements.GetDictionary("/Font")!.Elements.Count);
                var annotations = after.Elements.GetArray("/Annots")!;
                Assert.Equal(before.Elements.GetArray("/Annots")!.Elements.Count, annotations.Elements.Count);
                var web = annotations.Elements.Select(PdfNavigation.Resolve).OfType<PdfDictionary>().First(a => a.Elements.GetDictionary("/A")?.Elements.GetName("/S") == "/URI");
                Assert.Equal("https://example.com/pdf-regression", web.Elements.GetDictionary("/A")!.Elements.GetString("/URI"));
                var internalLink = annotations.Elements.Select(PdfNavigation.Resolve).OfType<PdfDictionary>().First(a => a.Elements.GetName("/Subtype") == "/Link" && a != web);
                var destination = new PdfNavigation(source).LinkDestination(before.Elements.GetArray("/Annots")!.Elements.Select(PdfNavigation.Resolve).OfType<PdfDictionary>().First(a => a.Elements.GetName("/Subtype") == "/Link" && a.Elements.GetDictionary("/A")?.Elements.GetName("/S") != "/URI"))!;
                if (destination.Page >= range.First && destination.Page <= range.Last)
                {
                    var outputDestination = internalLink.Elements.GetArray("/Dest")!;
                    Assert.Same(result.Pages[destination.Page - range.First], PdfNavigation.Resolve(outputDestination.Elements[0]));
                }
                else { Assert.False(internalLink.Elements.ContainsKey("/Dest")); Assert.False(internalLink.Elements.ContainsKey("/A")); }
                var highlight = annotations.Elements.Select(PdfNavigation.Resolve).OfType<PdfDictionary>().Single(a => a.Elements.GetName("/Subtype") == "/Highlight");
                Assert.NotEmpty(highlight.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Stream.UnfilteredValue);
            }
            var bookmarks = new PdfNavigation(result).ReadBookmarks();
            foreach (var node in Flatten(bookmarks)) if (node.Destination != null) Assert.InRange(node.Destination.Page, 1, result.PageCount);
        }
        Assert.Equal(bytes, SHA256.HashData(File.ReadAllBytes(sourcePath))); Assert.Empty(Directory.GetDirectories(Output));
        var evidence = Environment.GetEnvironmentVariable("KANTAN_PDF_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
        {
            var target = Directory.CreateDirectory(Path.Combine(evidence, name + "-cuts-" + cuts)).FullName;
            File.Copy(sourcePath, Path.Combine(target, "source.pdf"), true);
            foreach (var path in paths) File.Copy(path, Path.Combine(target, Path.GetFileName(path)), true);
        }
    }
    private static IEnumerable<PdfBookmark> Flatten(IEnumerable<PdfBookmark> nodes)
    { foreach (var node in nodes) { yield return node; foreach (var child in Flatten(node.Children)) yield return child; } }

    [Theory] [InlineData("office-like")] [InlineData("google-docs-like")]
    public async Task EveryPageOfficeSplitting(string name)
    {
        var service = new PdfSplitService(); var info = await service.InspectAsync(Fixture(name));
        var plan = SplitPlan.Create(info, true, "");
        var paths = await service.SplitAsync(info, plan, Output, acknowledgeCompatibilityWarnings: true);
        foreach (var path in paths) { using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import); Assert.Equal(1, result.PageCount); }
        var evidence = Environment.GetEnvironmentVariable("KANTAN_PDF_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
        { var target = Directory.CreateDirectory(Path.Combine(evidence, name + "-every-page")).FullName; File.Copy(Fixture(name), Path.Combine(target, "source.pdf"), true); foreach (var path in paths) File.Copy(path, Path.Combine(target, Path.GetFileName(path)), true); }
    }
    [Fact] public async Task CompatibilityAcknowledgmentRequiredByCore()
    {
        var service = new PdfSplitService(); var info = await service.InspectAsync(Fixture("office-like"));
        var error = await Assert.ThrowsAsync<PdfSplitException>(() => service.SplitAsync(info, SplitPlan.Create(info, true, ""), Output));
        Assert.Contains("変更内容", error.Message); Assert.Empty(Directory.GetFileSystemEntries(Output));
    }
    [Theory] [InlineData("/Names")] [InlineData("/Outlines")] [InlineData("/StructTreeRoot")]
    public async Task NavigationAndTagsAloneAreNotRejected(string key)
    {
        var path = Make(d => d.Internals.Catalog.Elements[key] = new PdfDictionary(d));
        Assert.Equal(3, (await new PdfSplitService().InspectAsync(path)).PageCount);
    }
    [Fact] public async Task ExternalLinksAloneNeedNoLossWarning()
    {
        var path = Make(d => d.Pages[0].AddWebLink(new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, 30, 30)), "https://example.com"));
        var service = new PdfSplitService(); var info = await service.InspectAsync(path); var plan = SplitPlan.Create(info, true, "");
        Assert.Empty(info.Compatibility.Warnings(plan));
        Assert.Equal(3, (await service.SplitAsync(info, plan, Output)).Count);
    }
    [Fact] public async Task LegacyNamedDestinationRemapped()
    {
        var path = Make(d =>
        {
            var dest = new PdfArray(d); dest.Elements.Add(d.Pages[2].Reference!); dest.Elements.Add(new PdfName("/Fit"));
            var legacy = new PdfDictionary(d); legacy.Elements["/chapter"] = dest; d.Internals.Catalog.Elements["/Dests"] = legacy;
            var annotation = new PdfDictionary(d); annotation.Elements.SetName("/Subtype", "/Link"); annotation.Elements["/Rect"] = new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, 20, 20)); annotation.Elements.SetName("/Dest", "/chapter");
            d.Internals.AddObject(annotation); var annotations = new PdfArray(d); annotations.Elements.Add(annotation.Reference!); d.Pages[0].Elements["/Annots"] = annotations;
        });
        var service = new PdfSplitService(); var info = await service.InspectAsync(path);
        var paths = await service.SplitAsync(info, SplitPlan.Create(info, false, "3"), Output);
        using var result = PdfReader.Open(paths[0], PdfDocumentOpenMode.Import);
        var link = result.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!;
        Assert.Same(result.Pages[2], PdfNavigation.Resolve(link.Elements.GetArray("/Dest")!.Elements[0]));
    }
    [Theory] [InlineData("/AcroForm", "入力フォーム")] [InlineData("/Perms", "署名")]
    [InlineData("/OCProperties", "/OCProperties")]
    public async Task CriticalDocumentRejectionNamesCause(string key, string cause)
    {
        var path = Make(d => d.Internals.Catalog.Elements[key] = new PdfDictionary(d));
        var error = await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path)); Assert.Contains(cause, error.Message);
    }
    [Theory] [InlineData("/Widget", "入力フォーム")] [InlineData("/FileAttachment", "添付")]
    [InlineData("/RichMedia", "注釈")]
    public async Task CriticalAnnotationsStillRejected(string subtype, string cause)
    {
        var path = Make(d => { var a = new PdfDictionary(d); a.Elements.SetName("/Subtype", subtype); var array = new PdfArray(d); array.Elements.Add(a); d.Pages[0].Elements["/Annots"] = array; });
        var error = await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path)); Assert.Contains(cause, error.Message);
    }
    [Theory] [InlineData("/JavaScript", "スクリプト")] [InlineData("/Launch", "外部ファイル")]
    [InlineData("/GoToR", "外部ファイル")]
    public async Task ActiveActionsStillRejected(string kind, string cause)
    {
        var path = Make(d => { var a = new PdfDictionary(d); a.Elements.SetName("/S", kind); d.Internals.Catalog.Elements["/OpenAction"] = a; });
        var error = await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path)); Assert.Contains(cause, error.Message);
    }
    [Fact] public async Task InvalidUserUnitNamesCause()
    {
        var path = Make(d => d.Pages[0].Elements.SetReal("/UserUnit", -1));
        var error = await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path)); Assert.Contains("サイズ倍率", error.Message);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
