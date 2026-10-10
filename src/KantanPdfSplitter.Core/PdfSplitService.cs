using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace KantanPdfSplitter.Core;

public sealed class PdfSplitService
{
    private int _busy;
    internal Action<int>? BeforePublish { get; init; }
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public Task<PdfInfo> InspectAsync(string path, CancellationToken token = default) => RunExclusive(() =>
    {
        path = Path.GetFullPath(path);
        using var source = OpenSource(path);
        using var doc = Read(source);
        PdfSafety.Validate(doc, token);
        source.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(source));
        token.ThrowIfCancellationRequested();
        return new PdfInfo(path, doc.PageCount, hash);
    }, token);

    public Task<IReadOnlyList<string>> SplitAsync(PdfInfo info, IReadOnlyList<PageRange> ranges, string destination,
        IProgress<SplitProgress>? progress = null, CancellationToken token = default) => RunExclusive<IReadOnlyList<string>>(() =>
    {
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(destination)) throw new PdfSplitException("保存先フォルダーがありません。選び直してください。");
        using var source = OpenSource(info.Path);
        var currentHash = Convert.ToHexString(SHA256.HashData(source));
        if (currentHash != info.Sha256) throw new PdfSplitException("選択後に元PDFが変更されました。もう一度PDFを選んでください。");
        source.Position = 0;
        using var doc = Read(source);
        PdfSafety.Validate(doc, token);
        ValidatePlan(info, ranges, doc.PageCount);
        var targets = ranges.Select(r => Path.Combine(destination, r.FileName)).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
            throw new PdfSplitException("出力ファイル名が重複しています。");
        foreach (var path in targets)
        {
            if (path.Equals(info.Path, StringComparison.OrdinalIgnoreCase) || File.Exists(path) || Directory.Exists(path))
                throw new PdfSplitException($"同じ名前のファイルまたはフォルダーがあります：{Path.GetFileName(path)}。別の保存先を選んでください。");
        }
        var stage = Path.Combine(destination, $".kantan-pdf-{Guid.NewGuid():N}");
        var published = new List<string>();
        var staged = new List<string>();
        try
        {
            // Creating the staging directory and a file proves write access without
            // modifying any pre-existing file. Stage stays on the target volume.
            Directory.CreateDirectory(stage);
            var probe = Path.Combine(stage, "write-check.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            for (var i = 0; i < ranges.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                using var output = new PdfDocument();
                output.Options.ColorMode = PdfColorMode.Undefined;
                output.Info.Creator = "かんたんPDF分割";
                for (var page = ranges[i].First; page <= ranges[i].Last; page++)
                {
                    token.ThrowIfCancellationRequested();
                    output.AddPage(doc.Pages[page - 1]);
                }
                var temporary = Path.Combine(stage, ranges[i].FileName);
                staged.Add(temporary);
                output.Save(temporary);
                // Reopen before publication; verify no unexpected missing pages.
                using var verify = PdfReader.Open(temporary, PdfDocumentOpenMode.Import);
                if (verify.PageCount != ranges[i].Count) throw new PdfSplitException("生成したPDFを確認できませんでした。");
                progress?.Report(new(i + 1, ranges.Count));
            }
            for (var i = 0; i < targets.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                BeforePublish?.Invoke(i);
                File.Move(staged[i], targets[i], overwrite: false);
                published.Add(targets[i]);
            }
            return published.AsReadOnly();
        }
        catch (Exception error)
        {
            var leftovers = new List<string>();
            foreach (var path in published)
                try { File.Delete(path); } catch (IOException) { leftovers.Add(path); } catch (UnauthorizedAccessException) { leftovers.Add(path); }
            if (leftovers.Count > 0)
                throw new PdfSplitException("処理を中止しましたが、一部の出力を削除できませんでした。保存先をご確認ください：" + string.Join("、", leftovers.Select(Path.GetFileName)), error);
            throw;
        }
        finally
        {
            foreach (var path in staged) TryDelete(path);
            TryDelete(Path.Combine(stage, "write-check.tmp"));
            try { if (Directory.Exists(stage)) Directory.Delete(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }, token);

    private static void ValidatePlan(PdfInfo info, IReadOnlyList<PageRange> ranges, int count)
    {
        var next = 1;
        foreach (var range in ranges)
        {
            if (range.First != next || range.Last < range.First || range.Last > count ||
                range.FileName != Path.GetFileName(range.FileName) || !range.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new PdfSplitException("分割範囲を確認できません。PDFを選び直してください。");
            next = range.Last + 1;
        }
        if (info.PageCount != count || next != count + 1) throw new PdfSplitException("分割範囲を確認できません。PDFを選び直してください。");
    }

    private async Task<T> RunExclusive<T>(Func<T> work, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new PdfSplitException("処理中です。完了するまでお待ちください。");
        try { return await Task.Run(work, token).ConfigureAwait(false); }
        catch (PdfSplitException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException e) { throw new PdfSplitException("ファイルを読むか保存する権限がありません。別の保存先を選んでください。", e); }
        catch (IOException e) { throw new PdfSplitException("ファイルを読むか保存できません。空き容量、保存先、他のアプリで開いていないかをご確認ください。", e); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new PdfSplitException("PDFを正しく読み取れませんでした。破損、保護、対応していない形式の可能性があります。", e); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static PdfDocument Read(Stream source)
    {
        try { return PdfReader.Open(source, PdfDocumentOpenMode.Import); }
        catch (PdfReaderException e) { throw new PdfSplitException("PDFを読み取れません。破損したPDFやパスワード付きPDFには対応していません。", e); }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
