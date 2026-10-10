using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace KantanPdfSplitter.Core;

internal sealed class PdfPageImporter(PdfDocument source, PdfDocument output, PageRange range, CancellationToken token)
{
    private readonly Dictionary<PdfObject, PdfObject> _copies = new(ReferenceEqualityComparer.Instance);
    private readonly PdfNavigation _navigation = new(source);
    private readonly List<(PdfDictionary Source, PdfDictionary Output, PdfPage Page)> _annotations = [];

    public void Import()
    {
        for (var number = range.First; number <= range.Last; number++)
        {
            token.ThrowIfCancellationRequested();
            var page = source.Pages[number - 1];
            var annotations = page.Elements["/Annots"];
            // PDFsharp otherwise imports navigation references before the selected
            // page map exists. Temporarily detach only from the in-memory source.
            page.Elements.Remove("/Annots");
            try { _copies[page] = output.AddPage(page); }
            finally { if (annotations != null) page.Elements["/Annots"] = annotations; }
        }
        for (var number = range.First; number <= range.Last; number++)
        {
            var original = source.Pages[number - 1]; var target = (PdfPage)_copies[original];
            foreach (var key in new[] { "/Group", "/UserUnit", "/Dur", "/Trans", "/ID", "/LastModified" })
                if (original.Elements[key] is { } value) target.Elements[key] = Copy(value);
            // /Tabs /S depends on the discarded structure tree. Row order is a
            // valid fallback; page paint/content coordinates are untouched.
            if (original.Elements["/Tabs"] is { } tabs) target.Elements["/Tabs"] = PdfNavigation.Name(tabs) == "/S" ? new PdfName("/R") : Copy(tabs);
            if (original.Elements.GetArray("/Annots") is not { } annotations) continue;
            var result = new PdfArray(output);
            foreach (var item in annotations.Elements)
            {
                var annotation = (PdfDictionary)PdfNavigation.Resolve(item)!;
                var clone = new PdfDictionary(output); output.Internals.AddObject(clone);
                _copies[annotation] = clone;
                _annotations.Add((annotation, clone, target)); result.Elements.Add(clone.Reference!);
            }
            target.Elements["/Annots"] = result;
        }
        foreach (var (original, target, page) in _annotations)
        {
            token.ThrowIfCancellationRequested();
            foreach (var key in original.Elements.Keys)
                if (key is not ("/P" or "/StructParent" or "/Dest" or "/A") && original.Elements[key] is { } value) target.Elements[key] = Copy(value);
            target.Elements.SetReference("/P", page);
            if (_navigation.LinkDestination(original) is { } destination)
            {
                // A link outside this output becomes inert. Keep its annotation
                // appearance/border rather than changing what the page looks like.
                if (LocalDestination(destination) is { } local) target.Elements["/Dest"] = local;
            }
            else if (original.Elements.GetDictionary("/A") is { } action && PdfNavigation.Name(action.Elements["/S"]) == "/URI") target.Elements["/A"] = Copy(action);
        }
        PreserveNames(); PreserveBookmarks();
        foreach (var key in new[] { "/Lang", "/OutputIntents" })
            if (source.Internals.Catalog.Elements[key] is { } value) output.Internals.Catalog.Elements[key] = Copy(value);
        output.Info.Title = source.Info.Title; output.Info.Author = source.Info.Author;
        output.Info.Subject = source.Info.Subject; output.Info.Keywords = source.Info.Keywords;
        RemoveOrphanedStructureIds();
    }
    private PdfArray? LocalDestination(PdfDestination destination)
    {
        if (destination.Page < range.First || destination.Page > range.Last || destination.Values == null) return null;
        var array = new PdfArray(output);
        array.Elements.Add(((PdfPage)_copies[source.Pages[destination.Page - 1]]).Reference!);
        foreach (var item in destination.Values.Elements.Skip(1)) array.Elements.Add(Copy(item));
        return array;
    }
    private void PreserveNames()
    {
        var pairs = new PdfArray(output);
        foreach (var (name, value) in _navigation.NamedDestinations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (LocalDestination(_navigation.Destination(value)) is { } destination)
            { pairs.Elements.Add(new PdfString(name)); pairs.Elements.Add(destination); }
        if (pairs.Elements.Count == 0) return;
        var tree = new PdfDictionary(output); tree.Elements["/Names"] = pairs;
        var names = new PdfDictionary(output); names.Elements["/Dests"] = tree;
        output.Internals.Catalog.Elements["/Names"] = names;
    }
    private void PreserveBookmarks()
    {
        var root = new PdfDictionary(output); output.Internals.AddObject(root); root.Elements.SetName("/Type", "/Outlines");
        var count = Write(_navigation.ReadBookmarks(), root);
        if (count > 0) { root.Elements.SetInteger("/Count", count); output.Internals.Catalog.Elements.SetReference("/Outlines", root); }
        int Write(IReadOnlyList<PdfBookmark> bookmarks, PdfDictionary parent)
        {
            var kept = new List<PdfDictionary>(); var total = 0;
            foreach (var bookmark in bookmarks)
            {
                token.ThrowIfCancellationRequested();
                var node = new PdfDictionary(output); output.Internals.AddObject(node);
                var children = Write(bookmark.Children, node);
                var destination = bookmark.Destination == null ? null : LocalDestination(bookmark.Destination);
                var web = bookmark.Source.Elements.GetDictionary("/A") is { } action && PdfNavigation.Name(action.Elements["/S"]) == "/URI" ? action : null;
                if (destination == null && web == null && children == 0) continue;
                foreach (var key in new[] { "/Title", "/C", "/F" }) if (bookmark.Source.Elements[key] is { } value) node.Elements[key] = Copy(value);
                node.Elements.SetReference("/Parent", parent);
                if (destination != null) node.Elements["/Dest"] = destination;
                else if (web != null) node.Elements["/A"] = Copy(web);
                if (children > 0) node.Elements.SetInteger("/Count", bookmark.Source.Elements.GetInteger("/Count") < 0 ? -children : children);
                if (kept.Count > 0) { node.Elements.SetReference("/Prev", kept[^1]); kept[^1].Elements.SetReference("/Next", node); }
                kept.Add(node); total += 1 + children;
            }
            if (kept.Count > 0) { parent.Elements.SetReference("/First", kept[0]); parent.Elements.SetReference("/Last", kept[^1]); }
            return total;
        }
    }
    private PdfItem Copy(PdfItem item, int depth = 0)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 256) throw new PdfSplitException("ページ情報の参照が深すぎるため、安全にコピーできません。");
        if (item is PdfReference reference) return ((PdfObject)Copy(reference.Value, depth + 1)).Reference!;
        if (item is not PdfObject obj) return item.Clone();
        if (_copies.TryGetValue(obj, out var known)) return known;
        if (obj is PdfDictionary page && PdfNavigation.Name(page.Elements["/Type"]) is "/Page" or "/Pages")
            throw new PdfSplitException("注釈やページ設定が、分割先に含まれないページを参照しています。このPDFの関連情報を安全にコピーできません。");
        PdfObject clone = obj switch { PdfDictionary => new PdfDictionary(output), PdfArray => new PdfArray(output), _ => obj.Clone() };
        _copies[obj] = clone;
        if (obj.Reference != null) output.Internals.AddObject(clone);
        if (obj is PdfDictionary dictionary && clone is PdfDictionary target)
        {
            foreach (var key in dictionary.Elements.Keys) if (dictionary.Elements[key] is { } value) target.Elements[key] = Copy(value, depth + 1);
            if (dictionary.Stream != null) target.CreateStream(dictionary.Stream.Value.ToArray());
        }
        else if (obj is PdfArray array && clone is PdfArray result)
            foreach (var value in array.Elements) result.Elements.Add(Copy(value, depth + 1));
        return clone;
    }
    private void RemoveOrphanedStructureIds()
    {
        var pending = new Stack<PdfItem>(output.Internals.GetAllObjects());
        var seen = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var item))
        {
            if (!seen.Add(item)) continue;
            item = PdfNavigation.Resolve(item)!;
            if (item is PdfDictionary dictionary)
            {
                dictionary.Elements.Remove("/StructParent"); dictionary.Elements.Remove("/StructParents");
                foreach (var child in dictionary.Elements.Values) if (child != null) pending.Push(child);
            }
            else if (item is PdfArray array) foreach (var child in array.Elements) pending.Push(child);
        }
    }
}
