using System.Globalization;

namespace KantanPdfSplitter.Core;

public sealed class PdfSplitException(string message, Exception? inner = null) : Exception(message, inner);
public sealed record PdfInfo(string Path, int PageCount, string Sha256)
{
    public PdfCompatibility Compatibility { get; init; } = new();
}
public sealed record PdfPageLink(int SourcePage, int TargetPage);
public enum PdfNoticeSeverity { Information, Important }
public sealed record PdfNotice(string Code, PdfNoticeSeverity Severity, string Summary, string Details)
{
    public bool RequiresConfirmation => Severity == PdfNoticeSeverity.Important;
}
public sealed record PdfCompatibility
{
    public bool HasTags { get; init; }
    public bool HasBookmarks { get; init; }
    public bool HasNamedDestinations { get; init; }
    public bool HasUnresolvedBookmarks { get; init; }
    public bool HasInitialView { get; init; }
    public IReadOnlyList<string> InitialViewChanges { get; init; } = [];
    public IReadOnlyList<int> BookmarkTargetPages { get; init; } = [];
    public IReadOnlyList<int> NamedDestinationPages { get; init; } = [];
    public IReadOnlyList<PdfPageLink> PageLinks { get; init; } = [];
    public IReadOnlyList<PdfNotice> Notices(IReadOnlyList<PageRange> ranges)
    {
        var notices = new List<PdfNotice>();
        if (HasTags) notices.Add(new("reading-structure", PdfNoticeSeverity.Important,
            "読み上げ用の構造情報は引き継がれません。",
            "読み上げ順序や見出しなどの構造情報が失われます。文字・画像の見た目と文字検索は維持されます。読み上げに使う場合は、元のPDFも保管してください。"));
        if (BookmarkTargetPages.Concat(NamedDestinationPages).Any(page => page == 0 || ranges.Any(r => page < r.First || page > r.Last)))
            notices.Add(new("navigation", PdfNoticeSeverity.Information,
                "しおり・移動先は各ファイルのページに合わせて整理されます。",
                "しおりと移動先の名前は、各出力PDFに含まれるページのものを引き継ぎます。それ以外の移動先は省略します。子のしおりが残る見出しは維持します。"));
        if (PageLinks.Any(link => link.TargetPage == 0 || !ranges.Any(r => link.SourcePage >= r.First && link.SourcePage <= r.Last && link.TargetPage >= r.First && link.TargetPage <= r.Last)))
            notices.Add(new("page-links", PdfNoticeSeverity.Information,
                "一部のページ移動リンクが使えなくなります。",
                "別の出力PDFへ移動するリンクや、移動先を読み取れないリンクは、クリックして移動できなくなります。リンクの見た目は維持します。同じ出力内のページへのリンクとWeb・メールのリンクは維持します。"));
        if (HasUnresolvedBookmarks) notices.Add(new("unresolved-bookmarks", PdfNoticeSeverity.Information,
            "移動先が不明なしおりは引き継げない場合があります。", "移動先を読み取れないしおりは省略します。子のしおりが残る場合は、移動できない見出しとして維持します。"));
        if (HasInitialView) notices.Add(new("initial-view", PdfNoticeSeverity.Information,
            string.Join("・", InitialViewChanges) + "は引き継がれません。", string.Join("、", InitialViewChanges) + "は各出力PDFの標準設定になります。ページの内容・サイズ・向きは維持します。"));
        return notices.AsReadOnly();
    }
    public IReadOnlyList<string> Warnings(IReadOnlyList<PageRange> ranges) => Notices(ranges).Select(n => n.Details).ToArray();
}
public sealed record PageRange(int First, int Last, string FileName)
{
    public int Count => Last - First + 1;
    public string Description => First == Last ? $"{First}ページ" : $"{First}〜{Last}ページ";
}
public sealed record SplitProgress(int Completed, int Total);

public static class SplitPlan
{
    public static IReadOnlyList<PageRange> Create(PdfInfo info, bool everyPage, string cuts)
    {
        if (info.PageCount < 1) throw new PdfSplitException("ページのないPDFは分割できません。");
        var positions = new SortedSet<int>();
        if (everyPage)
        {
            for (var i = 1; i < info.PageCount; i++) positions.Add(i);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cuts)) throw new PdfSplitException("分割する位置を入力してください。例：5,12");
            foreach (var token in cuts.Split(','))
            {
                var value = token.Trim();
                if (value.Length == 0 || value.Any(c => c is < '0' or > '9') ||
                    !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var page))
                    throw new PdfSplitException("半角数字をカンマで区切って入力してください。例：5,12");
                if (page < 1 || page > info.PageCount) throw new PdfSplitException($"1〜{info.PageCount}のページ番号を入力してください。");
                if (!positions.Add(page)) throw new PdfSplitException($"{page}ページが重複しています。各番号を1回だけ入力してください。");
            }
            positions.Remove(info.PageCount);
        }
        positions.Add(info.PageCount);
        var digits = Math.Max(3, info.PageCount.ToString(CultureInfo.InvariantCulture).Length);
        var format = new string('0', digits);
        var stem = System.IO.Path.GetFileNameWithoutExtension(info.Path);
        var result = new List<PageRange>();
        var first = 1;
        foreach (var last in positions)
        {
            var suffix = everyPage ? first.ToString(format) : $"{first.ToString(format)}-{last.ToString(format)}";
            var name = $"{stem}_{suffix}.pdf";
            if (name.Length > 255) throw new PdfSplitException("元のファイル名が長すぎます。短い名前に変更してから選び直してください。");
            result.Add(new(first, last, name));
            first = last + 1;
        }
        return result.AsReadOnly();
    }
}
