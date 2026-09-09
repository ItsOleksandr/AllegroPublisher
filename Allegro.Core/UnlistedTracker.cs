namespace Allegro.Core;

public sealed class UnlistedTracker
{
    private readonly Saver<List<string>> _unlisted = new("unlisted.txt");
    private readonly Saver<List<string>> _known = new("known_unlisted.txt");

    public IReadOnlyList<string> Unlisted => _unlisted.Read();

    public IReadOnlyList<string> New
    {
        get
        {
            var known = _known.Read().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _unlisted.Read().Where(ean => !known.Contains(ean)).ToList();
        }
    }

    public int Count => Unlisted.Count;

    public int NewCount => New.Count;

    public void Record(IEnumerable<string> eans)
    {
        var current = eans.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _unlisted.Value = current;
        _unlisted.Write();

        var stillUnlisted = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _known.Value = _known.Read().Where(stillUnlisted.Contains).ToList();
        _known.Write();
    }

    public void MarkImported()
    {
        var known = _known.Read().ToHashSet(StringComparer.OrdinalIgnoreCase);
        known.UnionWith(_unlisted.Read());
        _known.Value = known.ToList();
        _known.Write();
    }

    public List<ProductInfo> NewProducts()
    {
        var wanted = New.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return SaverExtensions.Products.Read().Values
            .Where(p => !string.IsNullOrWhiteSpace(p.EAN) && wanted.Contains(p.EAN))
            .OrderByDescending(p => p.Price * p.Count)
            .ToList();
    }
}
