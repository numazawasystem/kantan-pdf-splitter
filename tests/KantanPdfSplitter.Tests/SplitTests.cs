using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KantanPdfSplitter.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace KantanPdfSplitter.Tests;

public sealed class SplitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KantanPdfTests", Guid.NewGuid().ToString("N"));
    public SplitTests() => Directory.CreateDirectory(_root);
    private string Output => Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;

    internal static string Fixture(string path, int pages, Action<PdfDocument>? customize = null)
    {
        using var doc = new PdfDocument();
        doc.Options.ColorMode = PdfColorMode.Undefined;
        for (var i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(400 + i); page.Height = XUnit.FromPoint(600 + i * 2);
            page.Rotate = (i % 4) * 90;
            page.CropBox = new PdfRectangle(new XRect(4, 5, 390 + i, 580 + i * 2));
            page.TrimBox = new PdfRectangle(new XRect(8, 9, 380, 570));
            var font = new PdfDictionary(doc);
            font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1"); font.Elements.SetName("/BaseFont", "/Helvetica");
            doc.Internals.AddObject(font);
            var fonts = new PdfDictionary(doc); fonts.Elements.SetReference("/F1", font);
            var resources = new PdfDictionary(doc); resources.Elements["/Font"] = fonts;
            var image = new PdfDictionary(doc);
            image.Elements.SetName("/Type", "/XObject"); image.Elements.SetName("/Subtype", "/Image");
            image.Elements.SetInteger("/Width", 2); image.Elements.SetInteger("/Height", 2);
            image.Elements.SetName("/ColorSpace", "/DeviceRGB"); image.Elements.SetInteger("/BitsPerComponent", 8);
            image.CreateStream([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]); doc.Internals.AddObject(image);
            var images = new PdfDictionary(doc); images.Elements.SetReference("/Im1", image); resources.Elements["/XObject"] = images;
            page.Elements["/Resources"] = resources;
            var content = new PdfDictionary(doc);
            content.CreateStream(Encoding.ASCII.GetBytes($"BT /F1 18 Tf 20 100 Td (PAGE-{i:000}) Tj ET\n0.1 0.3 0.8 rg 20 200 120 80 re f\nq 24 0 0 24 20 20 cm /Im1 Do Q\n"));
            doc.Internals.AddObject(content); page.Elements.SetReference("/Contents", content);
        }
        customize?.Invoke(doc);
        doc.Save(path);
        return path;
    }
    private string Make(int pages = 20, Action<PdfDocument>? customize = null, string name = "sample.pdf") => Fixture(Path.Combine(_root, name), pages, customize);

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(20)]
    public async Task EveryPagePreservesSourceAndPageObjects(int pages)
    {
        var path = Make(pages);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var service = new PdfSplitService(); var info = await service.InspectAsync(path);
        var plan = SplitPlan.Create(info, true, ""); var paths = await service.SplitAsync(info, plan, Output);
        Assert.Equal(pages, paths.Count);
        using var input = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        for (var i = 0; i < pages; i++)
        {
            using var output = PdfReader.Open(paths[i], PdfDocumentOpenMode.Import);
            Assert.Single(output.Pages.Cast<PdfPage>());
            Compare(input.Pages[i], output.Pages[0]);
            Assert.EndsWith($"_{i + 1:000}.pdf", paths[i]);
        }
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Empty(Directory.GetDirectories(Output));
        var evidence = Environment.GetEnvironmentVariable("KANTAN_PDF_EVIDENCE");
        if (pages == 20 && !string.IsNullOrEmpty(evidence))
        {
            Directory.CreateDirectory(evidence); File.Copy(path, Path.Combine(evidence, "source-20.pdf"), true);
            foreach (var output in paths) File.Copy(output, Path.Combine(evidence, Path.GetFileName(output)), true);
        }
    }
    private static void Compare(PdfPage a, PdfPage b)
    {
        Assert.Equal(a.MediaBox, b.MediaBox); Assert.Equal(a.CropBox, b.CropBox); Assert.Equal(a.TrimBox, b.TrimBox);
        Assert.Equal(a.Width.Point, b.Width.Point); Assert.Equal(a.Height.Point, b.Height.Point); Assert.Equal(a.Rotate, b.Rotate);
        Assert.Equal(a.Contents.CreateSingleContent().Stream.UnfilteredValue, b.Contents.CreateSingleContent().Stream.UnfilteredValue);
        var xObjects = b.Resources.Elements.GetDictionary("/XObject");
        Assert.NotNull(xObjects);
        Assert.Contains(xObjects.Elements.Values, item => item is PdfSharp.Pdf.Advanced.PdfReference r && r.Value is PdfDictionary d && d.Elements.GetName("/Subtype") == "/Image");
        var sourceImage = a.Resources.Elements.GetDictionary("/XObject")!.Elements.GetDictionary("/Im1")!;
        var outputImage = xObjects.Elements.GetDictionary("/Im1")!;
        Assert.Equal(sourceImage.Stream.UnfilteredValue, outputImage.Stream.UnfilteredValue);
        Assert.Equal(sourceImage.Elements.GetInteger("/Width"), outputImage.Elements.GetInteger("/Width"));
        Assert.Equal(sourceImage.Elements.GetInteger("/Height"), outputImage.Elements.GetInteger("/Height"));
        Assert.Equal(a.Resources.Elements.GetDictionary("/Font")!.Elements.GetDictionary("/F1")!.Elements.GetName("/BaseFont"),
            b.Resources.Elements.GetDictionary("/Font")!.Elements.GetDictionary("/F1")!.Elements.GetName("/BaseFont"));
    }

    [Theory]
    [InlineData("5", "1-5,6-20")]
    [InlineData("5,12", "1-5,6-12,13-20")]
    [InlineData("1", "1-1,2-20")]
    [InlineData("20", "1-20")]
    [InlineData("12,5,20", "1-5,6-12,13-20")]
    public async Task CutsCoverEveryPageInOrder(string cuts, string expected)
    {
        var path = Make(); var service = new PdfSplitService(); var info = await service.InspectAsync(path);
        var plan = SplitPlan.Create(info, false, cuts);
        Assert.Equal(expected, string.Join(",", plan.Select(r => $"{r.First}-{r.Last}")));
        var outputs = await service.SplitAsync(info, plan, Output);
        using var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var page = 0;
        foreach (var output in outputs)
        {
            using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            foreach (var result in doc.Pages) Compare(source.Pages[page++], result);
        }
        Assert.Equal(20, page);
    }

    [Theory]
    [InlineData("0")] [InlineData("21")] [InlineData("-1")] [InlineData("1a")] [InlineData("")]
    [InlineData(" ")] [InlineData("5,,12")] [InlineData("5,")] [InlineData("５")]
    [InlineData("1.5")] [InlineData("5;12")] [InlineData("5,5")] [InlineData("20,20")] [InlineData("999999999999")]
    public void InvalidCutsRejected(string value) => Assert.Throws<PdfSplitException>(() => SplitPlan.Create(new("test.pdf", 20, ""), false, value));

    [Fact] public void FourDigitNaturalOrder() { var plan = SplitPlan.Create(new("test.pdf", 1000, ""), true, ""); Assert.Equal("test_0001.pdf", plan[0].FileName); Assert.Equal("test_1000.pdf", plan[^1].FileName); }
    [Fact] public void SingleFinalPageNoEmptyRange() => Assert.Equal(1, Assert.Single(SplitPlan.Create(new("test.pdf", 1, ""), false, "1")).Count);
    [Fact] public async Task JapaneseAndLongPaths()
    {
        var dir = _root;
        for (var i = 0; i < 5; i++) dir = Directory.CreateDirectory(Path.Combine(dir, new string('あ', 42))).FullName;
        var path = Fixture(Path.Combine(dir, "日本語の資料.pdf"), 2); var service = new PdfSplitService(); var info = await service.InspectAsync(path);
        var outputs = await service.SplitAsync(info, SplitPlan.Create(info, true, ""), Output);
        Assert.Equal("日本語の資料_001.pdf", Path.GetFileName(outputs[0]));
    }
    [Fact] public async Task CollisionNeverOverwrites()
    {
        var s = new PdfSplitService(); var info = await s.InspectAsync(Make(2)); var path = Path.Combine(Output, "sample_001.pdf");
        await File.WriteAllTextAsync(path, "existing");
        await Assert.ThrowsAsync<PdfSplitException>(() => s.SplitAsync(info, SplitPlan.Create(info, true, ""), Output));
        Assert.Equal("existing", await File.ReadAllTextAsync(path)); Assert.Single(Directory.GetFiles(Output)); Assert.Empty(Directory.GetDirectories(Output));
    }
    [Fact] public async Task CollisionAtPublishRollsBackOnlyOurFiles()
    {
        var s = new PdfSplitService { BeforePublish = index => { if (index == 1) File.WriteAllText(Path.Combine(Output, "sample_002.pdf"), "race winner"); } };
        var info = await s.InspectAsync(Make(2));
        await Assert.ThrowsAsync<PdfSplitException>(() => s.SplitAsync(info, SplitPlan.Create(info, true, ""), Output));
        Assert.Equal("race winner", File.ReadAllText(Path.Combine(Output, "sample_002.pdf"))); Assert.Single(Directory.GetFiles(Output)); Assert.Empty(Directory.GetDirectories(Output));
    }
    [Fact] public async Task WriteDeniedAndCleanup()
    {
        var s = new PdfSplitService { BeforePublish = _ => throw new UnauthorizedAccessException("simulated filesystem denial") };
        var info = await s.InspectAsync(Make(2));
        var error = await Assert.ThrowsAsync<PdfSplitException>(() => s.SplitAsync(info, SplitPlan.Create(info, true, ""), Output));
        Assert.Contains("権限", error.Message); Assert.Empty(Directory.GetFileSystemEntries(Output));
    }
    [Fact] public async Task SourceReadLockRejected()
    {
        var path = Make(2); using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path));
    }
    [Fact] public async Task CleanupFailureReportsResidualStagingFolder()
    {
        FileStream? locked = null;
        var s = new PdfSplitService { BeforePublish = _ => { var stage = Directory.GetDirectories(Output).Single(); locked = new FileStream(Path.Combine(stage, "sample_001.pdf"), FileMode.Open, FileAccess.Read, FileShare.None); } };
        var info = await s.InspectAsync(Make(2));
        try
        {
            var error = await Assert.ThrowsAsync<PdfSplitException>(() => s.SplitAsync(info, SplitPlan.Create(info, true, ""), Output));
            Assert.Contains("一時ファイル", error.Message); Assert.Single(Directory.GetDirectories(Output));
            Assert.Empty(Directory.GetFiles(Output));
        }
        finally { locked?.Dispose(); }
    }
    [Fact] public async Task ChangedInputRejected()
    {
        var path = Make(2); var s = new PdfSplitService(); var info = await s.InspectAsync(path); Make(3);
        await Assert.ThrowsAsync<PdfSplitException>(() => s.SplitAsync(info, SplitPlan.Create(info, true, ""), Output)); Assert.Empty(Directory.GetFileSystemEntries(Output));
    }
    [Theory] [InlineData("not a PDF")] [InlineData("%PDF-1.7\n1 0 obj << /Type /Catalog >>")]
    public async Task CorruptRejected(string content) { var path = Path.Combine(_root, "bad.pdf"); File.WriteAllText(path, content); await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path)); }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task AllEncryptionRejected(bool openPassword)
    {
        var path = Make(2, d => { d.SecuritySettings.OwnerPassword = "owner"; d.SecuritySettings.PermitExtractContent = false; if (openPassword) d.SecuritySettings.UserPassword = "user"; });
        await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path));
    }
    [Theory]
    [InlineData(PdfSharp.Pdf.Security.PdfDefaultEncryption.V1)]
    [InlineData(PdfSharp.Pdf.Security.PdfDefaultEncryption.V2With128Bits)]
    [InlineData(PdfSharp.Pdf.Security.PdfDefaultEncryption.V4UsingAES)]
    [InlineData(PdfSharp.Pdf.Security.PdfDefaultEncryption.V5)]
    public async Task OwnerOnlyEncryptionRevisionsRejected(PdfSharp.Pdf.Security.PdfDefaultEncryption method)
    {
        var path = Make(1, d => { d.SecuritySettings.OwnerPassword = "owner"; d.SecurityHandler.SetEncryption(method); });
        await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path));
    }
    [Theory] [InlineData("/AcroForm")] [InlineData("/Perms")] [InlineData("/OCProperties")]
    public async Task SpecialCatalogRejected(string key)
    {
        var path = Make(1, d => d.Internals.Catalog.Elements[key] = new PdfDictionary(d));
        await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path));
    }
    [Theory] [InlineData("/B")] [InlineData("/VP")] [InlineData("/PresSteps")]
    public async Task UnsupportedPageInformationRejected(string key)
    {
        var path = Make(1, d => d.Pages[0].Elements[key] = new PdfArray(d));
        await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path));
    }
    [Fact] public async Task IndirectSignatureRejected()
    {
        var path = Make(1, d => { var sig = new PdfDictionary(d); sig.Elements.SetName("/Type", "/Sig"); sig.Elements["/ByteRange"] = new PdfArray(d); d.Internals.AddObject(sig); d.Info.Elements.SetReference("/TestSignature", sig); });
        await Assert.ThrowsAsync<PdfSplitException>(() => new PdfSplitService().InspectAsync(path));
    }
    [Fact] public async Task DoubleExecutionRejectedAndGateReleased()
    {
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s = new PdfSplitService { BeforePublish = _ => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); } };
        var info = await s.InspectAsync(Make(2)); var task = s.SplitAsync(info, SplitPlan.Create(info, true, ""), Output);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(s.IsBusy);
        try { await Assert.ThrowsAsync<PdfSplitException>(() => s.SplitAsync(info, SplitPlan.Create(info, true, ""), Output)); }
        finally { release.Set(); }
        await task; Assert.False(s.IsBusy); Assert.NotNull(await s.InspectAsync(info.Path));
    }
    [Fact] public async Task CancellationRollsBackPublishedFiles()
    {
        using var cts = new CancellationTokenSource(); var s = new PdfSplitService { BeforePublish = index => { if (index == 0) cts.Cancel(); } };
        var info = await s.InspectAsync(Make(2)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.SplitAsync(info, SplitPlan.Create(info, true, ""), Output, token: cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(Output)); Assert.False(s.IsBusy);
    }
    [Fact] public async Task InvalidPlanRejected()
    {
        var s = new PdfSplitService(); var info = await s.InspectAsync(Make(2));
        await Assert.ThrowsAsync<PdfSplitException>(() => s.SplitAsync(info, [new(1, 2, "../outside.pdf")], Output));
    }
    [Fact] public async Task DestinationMustExist()
    {
        var s = new PdfSplitService(); var info = await s.InspectAsync(Make(1));
        await Assert.ThrowsAsync<PdfSplitException>(() => s.SplitAsync(info, SplitPlan.Create(info, true, ""), Path.Combine(_root, "absent")));
    }
    [Fact] public void UiLayoutDropAndBusyContract()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var window = new MainWindow(); window.Show();
                var split = (Button)window.FindName("SplitButton"); Assert.False(split.IsEnabled);
                Assert.Equal("PDFが選択されていません", ((TextBlock)window.FindName("FileSummary")).Text);
                var task = window.LoadPdfAsync(Make(2));
                while (!task.IsCompleted) Pump(); task.GetAwaiter().GetResult();
                Assert.True(split.IsEnabled);
                ((RadioButton)window.FindName("AtPages")).IsChecked = true;
                ((TextBox)window.FindName("CutsBox")).Text = "1";
                Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("CutsPanel")).Visibility);
                foreach (var scale in new[] { 1.0, 1.25, 1.5 })
                {
                    window.Width = 1366 / scale - 80; window.Height = 768 / scale - 60; window.UpdateLayout();
                    Pump(); window.UpdateLayout();
                    var visual = (FrameworkElement)window.Content;
                    var buttonPosition = split.TransformToAncestor(visual).Transform(new Point());
                    Assert.True(buttonPosition.Y >= 0 && buttonPosition.Y + split.ActualHeight <= visual.ActualHeight);
                    var bitmap = new RenderTargetBitmap((int)(visual.ActualWidth * scale), (int)(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var evidence = Environment.GetEnvironmentVariable("KANTAN_UI_EVIDENCE");
                    if (!string.IsNullOrEmpty(evidence)) { Directory.CreateDirectory(evidence); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(evidence, $"ui-{scale:0.00}.png")); png.Save(file); }
                    Assert.True(split.ActualWidth > 300); Assert.True(((Button)window.FindName("DropArea")).AllowDrop);
                }
                split.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(split.IsEnabled); Assert.False(((StackPanel)window.FindName("InputPanel")).IsEnabled);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!split.IsEnabled && DateTime.UtcNow < deadline) Pump();
                Assert.True(split.IsEnabled);
                var rejected = Make(1, d => d.Internals.Catalog.Elements["/AcroForm"] = new PdfDictionary(d), "form.pdf");
                var rejectedTask = window.LoadPdfAsync(rejected); while (!rejectedTask.IsCompleted) Pump(); rejectedTask.GetAwaiter().GetResult();
                Assert.Contains("form.pdf", ((TextBlock)window.FindName("FileSummary")).Text);
                Assert.Contains("読み込みエラー", ((TextBlock)window.FindName("FileSummary")).Text);
                Assert.Contains("入力フォーム", ((TextBlock)window.FindName("StatusText")).Text); Assert.False(split.IsEnabled);
                ((RadioButton)window.FindName("EveryPage")).IsChecked = true;
                Assert.Contains("入力フォーム", ((TextBlock)window.FindName("StatusText")).Text);
                var compatibleTask = window.LoadPdfAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "office-like.pdf"));
                while (!compatibleTask.IsCompleted) Pump(); compatibleTask.GetAwaiter().GetResult();
                Assert.Equal(Visibility.Visible, ((Border)window.FindName("CompatibilityPanel")).Visibility);
                Assert.Contains("読み上げ", ((TextBlock)window.FindName("CompatibilityText")).Text); Assert.False(split.IsEnabled);
                var details = (Expander)window.FindName("CompatibilityDetails"); Assert.False(details.IsExpanded);
                var acknowledgment = (CheckBox)window.FindName("CompatibilityAcknowledgment");
                Assert.Equal(Visibility.Visible, acknowledgment.Visibility);
                Assert.DoesNotContain("しおり", ((TextBlock)window.FindName("CompatibilityText")).Text);
                Assert.Contains("しおり", ((TextBlock)window.FindName("CompatibilityDetailsText")).Text);
                acknowledgment.IsChecked = true; Assert.True(split.IsEnabled);
                ((RadioButton)window.FindName("AtPages")).IsChecked = true; Assert.False(split.IsEnabled); Assert.False(acknowledgment.IsChecked);
                ((TextBox)window.FindName("CutsBox")).Text = "2"; acknowledgment.IsChecked = true; Assert.True(split.IsEnabled);
                var uiEvidence = Environment.GetEnvironmentVariable("KANTAN_UI_EVIDENCE");
                void Capture(string name)
                {
                    if (string.IsNullOrEmpty(uiEvidence)) return;
                    window.Width = 900; window.Height = 720; Pump(); window.UpdateLayout();
                    ((ScrollViewer)window.FindName("InputScroll")).ScrollToBottom(); Pump(); window.UpdateLayout();
                    var visual = (FrameworkElement)window.Content; var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(uiEvidence, name + ".png")); png.Save(file);
                }
                acknowledgment.IsChecked = false; Capture("ui-important-collapsed");
                details.IsExpanded = true; Pump(); window.UpdateLayout(); Assert.False(split.IsEnabled);
                Capture("ui-important-expanded");
                // Expanding details does not count as acknowledging important information.
                acknowledgment.IsChecked = true; Assert.True(split.IsEnabled);
                var minorPath = Make(3, d => d.Pages[0].AddDocumentLink(new PdfRectangle(new XRect(0, 0, 30, 30)), 3), "minor-links.pdf");
                var minorTask = window.LoadPdfAsync(minorPath); while (!minorTask.IsCompleted) Pump(); minorTask.GetAwaiter().GetResult();
                Assert.True(split.IsEnabled); Assert.False(acknowledgment.IsChecked);
                Assert.Equal(Visibility.Collapsed, acknowledgment.Visibility); Assert.False(details.IsExpanded);
                Assert.Contains("リンク", ((TextBlock)window.FindName("CompatibilityText")).Text);
                Capture("ui-minor-collapsed");
                details.IsExpanded = true; Pump(); Assert.True(split.IsEnabled); Capture("ui-minor-expanded");
                foreach (var scale in new[] { 1.0, 1.25, 1.5 })
                {
                    window.Width = 1366 / scale - 80; window.Height = 768 / scale - 60; Pump(); window.UpdateLayout();
                    var visual = (FrameworkElement)window.Content;
                    var position = split.TransformToAncestor(visual).Transform(new Point());
                    Assert.True(position.Y + split.ActualHeight <= visual.ActualHeight); Assert.True(split.IsEnabled);
                    Assert.True(((Border)window.FindName("CompatibilityPanel")).ActualHeight < 280);
                }
                ((TextBox)window.FindName("CutsBox")).Text = "3";
                Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("CompatibilityPanel")).Visibility); Assert.True(split.IsEnabled);
                ((TextBox)window.FindName("CutsBox")).Text = "2";
                Assert.True(split.IsEnabled); Assert.Equal(Visibility.Collapsed, acknowledgment.Visibility);
                if (!string.IsNullOrEmpty(uiEvidence))
                {
                    File.Copy(minorPath, Path.Combine(uiEvidence, "qa-minor.pdf"), true);
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "office-like.pdf"), Path.Combine(uiEvidence, "qa-important.pdf"), true);
                }
                window.Close(); app.Shutdown();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (failure != null) throw new Exception("UI contract failed", failure);
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
