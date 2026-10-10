using PdfSharp.Pdf;

namespace KantanPdfSplitter.Core;

internal static class PdfSafety
{
    private static readonly HashSet<string> PageKeys = ["/Type", "/Parent", "/Resources", "/Contents", "/MediaBox", "/CropBox", "/Rotate", "/BleedBox", "/TrimBox", "/ArtBox", "/Annots", "/Group", "/UserUnit", "/Tabs", "/StructParents", "/Metadata", "/PieceInfo", "/LastModified", "/ID", "/Dur", "/Trans"];
    private static readonly HashSet<string> OrdinaryAnnotations = ["/Link", "/Text", "/Highlight", "/Underline", "/StrikeOut", "/Squiggly", "/Ink", "/Square", "/Circle", "/Line", "/PolyLine", "/Polygon", "/FreeText", "/Stamp", "/Caret", "/Popup"];

    public static PdfCompatibility Validate(PdfDocument doc, CancellationToken token)
    {
        if (doc.SecuritySettings.IsEncrypted || doc.SecurityHandler.Elements.ContainsKey("/Filter")) Reject("パスワードまたは利用制限が設定されています。保護のないPDFを選んでください。");
        if (doc.PageCount < 1) Reject("ページがありません。");
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.ContainsKey("/AcroForm")) Reject("入力フォームが含まれています。入力欄の値や動作を安全に引き継げないため分割できません。");
        if (catalog.Elements.ContainsKey("/Perms")) Reject("電子署名または証明用の情報が含まれています。分割後に署名の有効性を維持できません。");
        foreach (var key in new[] { "/OCProperties", "/Collection", "/Requirements", "/Extensions", "/Threads" })
            if (catalog.Elements.ContainsKey(key)) Reject($"対応していない文書設定（{key}）が含まれています。");
        var navigation = new PdfNavigation(doc);
        var links = new List<PdfPageLink>();
        for (var index = 0; index < doc.PageCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = doc.Pages[index];
            var unknown = page.Elements.Keys.FirstOrDefault(key => !PageKeys.Contains(key));
            if (unknown != null) Reject($"{index + 1}ページに対応していないページ設定（{unknown}）があります。");
            if (page.Width.Point <= 0 || page.Height.Point <= 0 || page.Rotate % 90 != 0) Reject($"{index + 1}ページのサイズまたは向きが不正です。");
            if (page.Elements.ContainsKey("/UserUnit") && (page.Elements.GetReal("/UserUnit") <= 0 || page.Elements.GetReal("/UserUnit") > 75000)) Reject($"{index + 1}ページのサイズ倍率が不正です。");
            var annotations = page.Elements.GetArray("/Annots");
            if (annotations == null) continue;
            foreach (var item in annotations.Elements)
            {
                if (PdfNavigation.Resolve(item) is not PdfDictionary annotation) Reject($"{index + 1}ページの注釈を読み取れません。");
                else
                {
                    var subtype = PdfNavigation.Name(annotation.Elements["/Subtype"]);
                    if (subtype == "/Widget") Reject($"{index + 1}ページに入力フォームの欄があります。");
                    if (subtype == "/FileAttachment") Reject($"{index + 1}ページに添付ファイルがあります。");
                    if (!OrdinaryAnnotations.Contains(subtype)) Reject($"{index + 1}ページに対応していない注釈（{subtype}）があります。");
                    if (subtype == "/Link" && navigation.LinkDestination(annotation) is { } destination) links.Add(new(index + 1, destination.Page));
                }
            }
        }
        var pending = new Stack<PdfItem>(doc.Internals.GetAllObjects());
        var visited = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(item)) continue;
            item = PdfNavigation.Resolve(item)!;
            if (item is PdfDictionary dictionary)
            {
                if (dictionary.Elements.ContainsKey("/ByteRange") || PdfNavigation.Name(dictionary.Elements["/Type"]) == "/Sig" || PdfNavigation.Name(dictionary.Elements["/FT"]) == "/Sig") Reject("電子署名が含まれています。分割後に署名の有効性を維持できません。");
                if (dictionary.Elements.ContainsKey("/XFA") || dictionary.Elements.ContainsKey("/FT")) Reject("入力フォームが含まれています。");
                if (dictionary.Elements.Keys.Any(key => key is "/EF" or "/EmbeddedFiles" or "/AF") || PdfNavigation.Name(dictionary.Elements["/Type"]) == "/EmbeddedFile") Reject("添付ファイルが含まれています。添付を引き継げないため分割できません。");
                if (dictionary.Elements.Keys.Any(key => key is "/JS" or "/JavaScript" or "/AA") || PdfNavigation.Name(dictionary.Elements["/S"]) == "/JavaScript") Reject("自動実行する操作やスクリプトが含まれています。");
                var action = PdfNavigation.Name(dictionary.Elements["/S"]);
                if (action is "/Launch" or "/GoToR" or "/SubmitForm" or "/ImportData" or "/Rendition" or "/GoToE") Reject($"外部ファイルやプログラムを操作する設定（{action}）があります。");
                if (dictionary.Elements.ContainsKey("/RichMediaContent")) Reject("動画などの特殊な再生情報が含まれています。");
                if (action == "/URI")
                {
                    var uri = dictionary.Elements.GetString("/URI");
                    if (!Uri.TryCreate(uri, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https" or "mailto")) Reject("対応していない種類の外部リンクがあります。Webページとメールのリンクに対応しています。");
                }
                foreach (var child in dictionary.Elements.Values) if (child != null) pending.Push(child);
            }
            else if (item is PdfArray array) foreach (var child in array.Elements) pending.Push(child);
        }
        var bookmarks = navigation.ReadBookmarks();
        return new PdfCompatibility
        {
            HasTags = catalog.Elements.ContainsKey("/StructTreeRoot") || catalog.Elements.GetDictionary("/MarkInfo")?.Elements.GetBoolean("/Marked") == true || doc.Pages.Cast<PdfPage>().Any(p => p.Elements.ContainsKey("/StructParents")),
            HasBookmarks = bookmarks.Count > 0, HasNamedDestinations = navigation.NamedDestinations.Count > 0,
            HasUnresolvedBookmarks = navigation.HasUnresolvedBookmarks,
            HasInitialView = catalog.Elements.ContainsKey("/OpenAction") || catalog.Elements.ContainsKey("/PageLabels") || catalog.Elements.ContainsKey("/ViewerPreferences"),
            PageLinks = links.AsReadOnly()
        };
    }
    private static void Reject(string reason) => throw new PdfSplitException("このPDFは分割できません。" + reason);
}
