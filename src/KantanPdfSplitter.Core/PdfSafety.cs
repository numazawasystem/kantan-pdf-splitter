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
        if (catalog.Elements["/AcroForm"] is { } formItem)
        {
            if (PdfNavigation.Resolve(formItem) is not PdfDictionary form || PdfNavigation.Resolve(form.Elements["/Fields"]) is not PdfArray fields)
                Reject("入力フォームの定義を読み取れません。安全に分割できません。");
            else if (fields.Elements.Count > 0 || form.Elements.ContainsKey("/XFA") || form.Elements.GetInteger("/SigFlags") != 0)
                Reject("入力フォームまたは署名用の欄が含まれています。入力欄の値や動作を安全に引き継げないため分割できません。");
            // An explicit empty Fields array is a harmless producer placeholder,
            // not an interactive form. It is not copied to the output.
        }
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
            if (page.Elements["/Group"] is { } group && PdfNavigation.Resolve(group) is not PdfDictionary) Reject($"{index + 1}ページの透明度設定を読み取れません。");
            if (page.Elements["/Annots"] is { } annots && PdfNavigation.Resolve(annots) is not PdfArray) Reject($"{index + 1}ページの注釈情報を読み取れません。");
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
        var hasStructureIds = false;
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(item)) continue;
            item = PdfNavigation.Resolve(item)!;
            if (item is PdfDictionary dictionary)
            {
                hasStructureIds |= dictionary.Elements.ContainsKey("/StructParent") || dictionary.Elements.ContainsKey("/StructParents");
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
        var initialViewChanges = new List<string>();
        if (HasData(catalog.Elements["/OpenAction"])) initialViewChanges.Add("開いたときの移動先");
        if (HasCustomPageLabels(catalog.Elements["/PageLabels"])) initialViewChanges.Add("ページ番号の表示");
        if (HasNonDefaultViewerPreferences(catalog.Elements["/ViewerPreferences"])) initialViewChanges.Add("表示・印刷の設定");
        return new PdfCompatibility
        {
            // An empty producer placeholder / declaration has no reading structure to lose.
            // Keep the important notice for any structure payload or association, including
            // orphaned page/annotation associations and malformed nonempty structure data.
            HasTags = hasStructureIds || HasData(catalog.Elements["/StructTreeRoot"], ignoreStructureType: true),
            HasBookmarks = bookmarks.Count > 0, HasNamedDestinations = navigation.NamedDestinations.Count > 0,
            HasUnresolvedBookmarks = navigation.HasUnresolvedBookmarks,
            BookmarkTargetPages = Flatten(bookmarks).Where(b => b.Destination?.Page > 0).Select(b => b.Destination!.Page).ToArray(),
            NamedDestinationPages = navigation.NamedDestinations.Values.Select(v => navigation.Destination(v).Page).ToArray(),
            HasInitialView = initialViewChanges.Count > 0, InitialViewChanges = initialViewChanges.AsReadOnly(),
            PageLinks = links.AsReadOnly()
        };
    }
    private static IEnumerable<PdfBookmark> Flatten(IEnumerable<PdfBookmark> bookmarks)
    { foreach (var bookmark in bookmarks) { yield return bookmark; foreach (var child in Flatten(bookmark.Children)) yield return child; } }

    private static bool HasData(PdfItem? item, bool ignoreStructureType = false)
    {
        var pending = new Stack<PdfItem>(); if (item != null) pending.Push(item);
        var visited = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var next))
        {
            next = PdfNavigation.Resolve(next)!;
            if (next == null || !visited.Add(next) || next is PdfNull) continue;
            if (next is PdfDictionary dictionary)
            {
                if (dictionary.Stream is { Value.Length: > 0 }) return true;
                foreach (var key in dictionary.Elements.Keys)
                    if (!(ignoreStructureType && key is "/Type" or "/ParentTreeNextKey") && dictionary.Elements[key] is { } value) pending.Push(value);
            }
            else if (next is PdfArray array) foreach (var value in array.Elements) pending.Push(value);
            else return true;
        }
        return false;
    }
    private static bool HasCustomPageLabels(PdfItem? item)
    {
        if (PdfNavigation.Resolve(item) is not PdfDictionary root) return HasData(item);
        var pending = new Stack<PdfDictionary>(); pending.Push(root);
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node)) continue;
            if (node.Elements.GetArray("/Nums") is { } nums)
                for (var i = 0; i < nums.Elements.Count; i += 2)
                {
                    if (i + 1 >= nums.Elements.Count || PdfNavigation.Resolve(nums.Elements[i]) is not PdfInteger { Value: 0 } ||
                        PdfNavigation.Resolve(nums.Elements[i + 1]) is not PdfDictionary label) return true;
                    if (label.Elements.GetName("/S") != "/D" || label.Elements.GetString("/P").Length > 0 ||
                        (label.Elements.ContainsKey("/St") && label.Elements.GetInteger("/St") != 1) ||
                        label.Elements.Keys.Any(k => k is not ("/S" or "/P" or "/St"))) return true;
                }
            if (node.Elements.GetArray("/Kids") is { } kids)
                foreach (var child in kids.Elements)
                    if (PdfNavigation.Resolve(child) is PdfDictionary dictionary) pending.Push(dictionary); else if (HasData(child)) return true;
            if (node.Elements.Keys.Any(k => k is not ("/Nums" or "/Kids" or "/Limits"))) return true;
        }
        return false;
    }
    private static bool HasNonDefaultViewerPreferences(PdfItem? item)
    {
        if (PdfNavigation.Resolve(item) is not PdfDictionary prefs) return HasData(item);
        foreach (var key in prefs.Elements.Keys)
        {
            var value = PdfNavigation.Resolve(prefs.Elements[key]);
            if (value is PdfNull) continue;
            if (key is "/HideToolbar" or "/HideMenubar" or "/HideWindowUI" or "/FitWindow" or "/CenterWindow" or "/DisplayDocTitle" or "/PickTrayByPDFSize")
            { if (value is PdfBoolean { Value: false }) continue; }
            if ((key == "/Direction" && PdfNavigation.Name(value) == "/L2R") ||
                (key == "/NonFullScreenPageMode" && PdfNavigation.Name(value) == "/UseNone") ||
                (key == "/PrintScaling" && PdfNavigation.Name(value) == "/AppDefault") ||
                (key is "/ViewArea" or "/ViewClip" or "/PrintArea" or "/PrintClip" && PdfNavigation.Name(value) == "/CropBox") ||
                (key == "/NumCopies" && value is PdfInteger { Value: 1 })) continue;
            if (HasData(value)) return true;
        }
        return false;
    }
    private static void Reject(string reason) => throw new PdfSplitException("このPDFは分割できません。" + reason);
}
