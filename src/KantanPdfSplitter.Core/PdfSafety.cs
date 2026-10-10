using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace KantanPdfSplitter.Core;

internal static class PdfSafety
{
    private const string Unsupported = "このPDFには、署名・入力欄・注釈・添付・しおり・特殊な表示設定など、保持できない情報が含まれています。初版では分割できません。電子署名の有効性は分割後に維持されません。";
    // PDFsharp 6.2.4 ImportExternalPage copies these content and geometry keys.
    private static readonly HashSet<string> AllowedPageKeys = ["/Type", "/Parent", "/Resources", "/Contents", "/MediaBox", "/CropBox", "/Rotate", "/BleedBox", "/TrimBox", "/ArtBox"];
    private static readonly HashSet<string> UnsupportedCatalogKeys = ["/AcroForm", "/Perms", "/Names", "/Outlines", "/OpenAction", "/AA", "/OCProperties", "/StructTreeRoot", "/Collection", "/AF", "/Requirements", "/Extensions", "/Threads"];
    private static readonly HashSet<string> UnsafeKeys = ["/ByteRange", "/JavaScript", "/JS", "/EmbeddedFiles", "/EF", "/XFA", "/RichMediaContent", "/AA", "/AF", "/Annots"];

    public static void Validate(PdfDocument doc, CancellationToken token)
    {
        // PdfReader resets active encryption after decryption, so IsEncrypted
        // alone misses owner-only protection. The parsed original handler keeps
        // its /Filter entry, including when the opening password is empty.
        if (doc.SecuritySettings.IsEncrypted || doc.SecurityHandler.Elements.ContainsKey("/Filter"))
            throw new PdfSplitException("パスワードや利用制限が設定されたPDFには対応していません。保護のないPDFを選んでください。");
        if (doc.PageCount < 1) throw new PdfSplitException("ページのないPDFは分割できません。");
        if (doc.Internals.Catalog.Elements.Keys.Any(UnsupportedCatalogKeys.Contains)) throw new PdfSplitException(Unsupported);
        foreach (var page in doc.Pages)
        {
            token.ThrowIfCancellationRequested();
            if (page.Elements.Keys.Any(key => !AllowedPageKeys.Contains(key))) throw new PdfSplitException(Unsupported);
            if (page.Width.Point <= 0 || page.Height.Point <= 0 || page.Rotate % 90 != 0)
                throw new PdfSplitException("ページのサイズや向きを読み取れません。PDFを確認してください。");
        }
        // Inspect both indirect objects and nested direct objects. Resolve names via
        // the parser, so escaped keys and compressed object streams are covered.
        var pending = new Stack<PdfItem>(doc.Internals.GetAllObjects());
        var visited = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(item)) continue;
            if (item is PdfReference reference) { pending.Push(reference.Value); continue; }
            if (item is PdfDictionary dictionary)
            {
                if (dictionary.Elements.Keys.Any(UnsafeKeys.Contains)) throw new PdfSplitException(Unsupported);
                foreach (var key in new[] { "/Type", "/FT", "/S" })
                {
                    if (dictionary.Elements[key] is PdfName name && name.Value is "/Sig" or "/EmbeddedFile" or "/JavaScript" or "/Launch" or "/GoToR")
                        throw new PdfSplitException(Unsupported);
                }
                foreach (var child in dictionary.Elements.Values) if (child != null) pending.Push(child);
            }
            else if (item is PdfArray array)
                foreach (var child in array.Elements) pending.Push(child);
        }
    }
}

