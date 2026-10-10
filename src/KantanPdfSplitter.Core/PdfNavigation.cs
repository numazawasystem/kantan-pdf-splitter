using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace KantanPdfSplitter.Core;

internal sealed record PdfDestination(int Page, PdfArray? Values);
internal sealed record PdfBookmark(PdfDictionary Source, PdfDestination? Destination, IReadOnlyList<PdfBookmark> Children);

internal sealed class PdfNavigation
{
    private readonly PdfDocument _doc;
    private readonly Dictionary<PdfObject, int> _pages = new(ReferenceEqualityComparer.Instance);
    public Dictionary<string, PdfItem> NamedDestinations { get; } = new(StringComparer.Ordinal);
    public bool HasUnresolvedBookmarks { get; private set; }
    public PdfNavigation(PdfDocument doc)
    {
        _doc = doc;
        for (var i = 0; i < doc.PageCount; i++) _pages[doc.Pages[i]] = i + 1;
        if (doc.Internals.Catalog.Elements.GetDictionary("/Dests") is { } legacy)
            foreach (var key in legacy.Elements.Keys) if (legacy.Elements[key] is { } value) NamedDestinations[key.TrimStart('/')] = value;
        if (doc.Internals.Catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests") is { } root)
        {
            var pending = new Stack<PdfDictionary>(); pending.Push(root);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            while (pending.TryPop(out var node))
            {
                if (!seen.Add(node)) continue;
                if (node.Elements.GetArray("/Names") is { } names)
                    for (var i = 0; i + 1 < names.Elements.Count; i += 2)
                        if (Resolve(names.Elements[i]) is PdfString text) NamedDestinations[text.Value] = names.Elements[i + 1];
                if (node.Elements.GetArray("/Kids") is { } kids)
                    foreach (var child in kids.Elements) if (Resolve(child) is PdfDictionary dictionary) pending.Push(dictionary);
            }
        }
    }
    public static PdfItem? Resolve(PdfItem? item)
    {
        for (var depth = 0; depth < 32 && item is PdfReference reference; depth++) item = reference.Value;
        return item is PdfReference ? null : item;
    }
    public static string Name(PdfItem? item) => Resolve(item) is PdfName name ? name.Value : "";
    public PdfDestination Destination(PdfItem? value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 32; depth++)
        {
            value = Resolve(value);
            if (value is PdfDictionary dictionary) { value = dictionary.Elements["/D"]; continue; }
            var key = value switch { PdfString text => text.Value, PdfName name => name.Value.TrimStart('/'), _ => null };
            if (key != null)
            {
                if (!seen.Add(key) || !NamedDestinations.TryGetValue(key, out value)) break;
                continue;
            }
            if (value is PdfArray { Elements.Count: > 1 } array)
            {
                var target = Resolve(array.Elements[0]);
                if (target is PdfObject obj && _pages.TryGetValue(obj, out var page)) return new(page, array);
                if (target is PdfInteger number && number.Value >= 0 && number.Value < _doc.PageCount) return new(number.Value + 1, array);
            }
            break;
        }
        return new(0, null);
    }
    public PdfDestination? LinkDestination(PdfDictionary annotation)
    {
        if (annotation.Elements["/Dest"] is { } direct) return Destination(direct);
        if (annotation.Elements.GetDictionary("/A") is not { } action) return null;
        var kind = Name(action.Elements["/S"]);
        return kind == "/URI" ? null : kind == "/GoTo" ? Destination(action.Elements["/D"]) : new(0, null);
    }
    public IReadOnlyList<PdfBookmark> ReadBookmarks()
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        return Read(_doc.Internals.Catalog.Elements.GetDictionary("/Outlines")?.Elements["/First"], 0);
        List<PdfBookmark> Read(PdfItem? first, int depth)
        {
            if (depth > 64) throw new PdfSplitException("しおりの階層が深すぎるため、読み取れません。");
            var result = new List<PdfBookmark>();
            for (var node = Resolve(first) as PdfDictionary; node != null; node = Resolve(node.Elements["/Next"]) as PdfDictionary)
            {
                if (!seen.Add(node)) throw new PdfSplitException("しおりの参照が循環しているため、読み取れません。");
                var destination = LinkDestination(node);
                if (destination?.Page == 0) HasUnresolvedBookmarks = true;
                result.Add(new(node, destination, Read(node.Elements["/First"], depth + 1)));
            }
            return result;
        }
    }
}
