using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

public sealed class BundlePlan
{
    public const int MaxNameLength = 75;

    private readonly AllegroPublisher _publisher;
    private Dictionary<string, int> _listedPacks = new(StringComparer.OrdinalIgnoreCase);

    public BundlePlan(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    public record BundleChange(
        string Ean,
        string? OfferId,
        int PackSize,
        string OldName,
        string NewName,
        decimal SupplierUnitPrice,
        decimal OldOfferPrice,
        decimal NewOfferPrice,
        int SupplierStock,
        int NewStock,
        string OfferStatus,
        string? Problem)
    {
        public bool CanConvert => Problem is null;
    }

    public async Task<List<BundleChange>> BuildAsync(Action<string>? log = null)
    {
        var options = SaverExtensions.ListingOptions.Read();
        var products = SaverExtensions.Products.Read().Values.ToList();

        var candidates = SelectCandidates(products, options);
        log?.Invoke($"{products.Count} products parsed, {candidates.Count} qualify as bundles " +
                    $"(min order > {options.BundleFromQuantity}, in stock, not black listed).");

        var qualified = candidates.Select(p => p.EAN).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byEan = products.Where(p => !string.IsNullOrEmpty(p.EAN))
                            .GroupBy(p => p.EAN, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _listedPacks = new Dictionary<string, int>(SaverExtensions.Bundles.Read(), StringComparer.OrdinalIgnoreCase);

        var reverts = _listedPacks
            .Where(entry => !qualified.Contains(entry.Key) && byEan.ContainsKey(entry.Key))
            .Select(entry => byEan[entry.Key])
            .ToList();
        if (reverts.Count > 0)
        {
            log?.Invoke($"{reverts.Count} offers are packs on Allegro but no longer qualify - back to single units.");
        }

        var offers = await ResolveOffersAsync(candidates.Concat(reverts).Select(p => p.EAN), log);

        var changes = new List<BundleChange>();
        foreach (var product in candidates.OrderByDescending(p => p.MinOrderQuantity).Concat(reverts))
        {
            offers.TryGetValue(product.EAN, out var offer);
            changes.Add(BuildChange(product, offer, options));
        }

        return changes;
    }

    private static List<ProductInfo> SelectCandidates(List<ProductInfo> products, ListingOptions options)
    {
        return products.Where(options.IsBundle).Where(options.Includes).ToList();
    }

    private BundleChange BuildChange(ProductInfo product, OfferSnapshot? offer, ListingOptions options)
    {
        var pack = options.GetPackSize(product);
        var packPrice = options.GetOfferPrice(product);
        var newStock = options.GetOfferStock(product);
        var newName = BuildName(product.Name, pack);

        var listedPack = offer is not null && _listedPacks.TryGetValue(product.EAN, out var known) ? known : 0;

        var problem = offer is null
            ? "no offer on Allegro - would have to be created from scratch"
            : listedPack == pack
                ? $"already a pack of {pack}"
                : null;

        return new BundleChange(
            product.EAN, offer?.Id, pack, offer?.Name ?? product.Name, newName,
            product.Price, offer?.Price ?? 0m, packPrice, product.Count, newStock,
            offer?.Status ?? "", problem);
    }

    public static string BuildName(string name, int pack)
    {
        var prefix = pack > 1 ? $"Zestaw {pack} szt. " : "";
        var room = MaxNameLength - prefix.Length;
        var clean = SanitizeName(name);
        var trimmed = clean.Length <= room ? clean : clean[..Math.Max(room, 0)].TrimEnd();
        return prefix + trimmed;
    }

    public static string SanitizeName(string name)
    {
        const string polish = "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ";
        const string punctuation = " .,-/()+%&:";

        var builder = new StringBuilder(name.Length);
        foreach (var raw in name)
        {
            var c = raw switch
            {
                '×' or '*' => 'x',        // 60×80 and 114*10*2 are dimensions
                '–' or '—' or '−' => '-',
                '”' or '“' or '„' or '″' => '"',
                '’' or '‘' or '′' => '\'',
                '\u00a0' => ' ',
                _ => raw,
            };

            if (char.IsLetterOrDigit(c) && c < 128 || polish.Contains(c) || punctuation.Contains(c))
            {
                builder.Append(c);
            }
            else if (c is '"' or '\'')
            {
                continue;
            }
            else if (!char.IsWhiteSpace(c))
            {
                continue;
            }
            else
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public record OfferSnapshot(string Id, string Name, decimal Price, string Status);

    public async Task<Dictionary<string, OfferSnapshot>> ResolveOffersAsync(IEnumerable<string> eans, Action<string>? log)
    {
        var result = new Dictionary<string, OfferSnapshot>(StringComparer.OrdinalIgnoreCase);
        var distinct = eans.Distinct().ToList();

        const int batchSize = 50;
        for (int i = 0; i < distinct.Count; i += batchSize)
        {
            var batch = distinct.Skip(i).Take(batchSize).ToList();
            var query = string.Join("&", batch.Select(e => $"external.id={Uri.EscapeDataString(e)}"));

            var (status, body) = await _publisher.GetRawAsync($"/sale/offers?limit=1000&{query}");
            if (status is < 200 or >= 300)
            {
                throw new InvalidOperationException($"Failed to list offers ({status}): {body}");
            }

            foreach (var offer in JsonNode.Parse(body)?["offers"]?.AsArray() ?? new JsonArray())
            {
                var ean = offer?["external"]?["id"]?.GetValue<string>();
                var id = offer?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(ean) || string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var amount = offer?["sellingMode"]?["price"]?["amount"]?.GetValue<string>();
                result[ean] = new OfferSnapshot(
                    id,
                    offer?["name"]?.GetValue<string>() ?? "",
                    decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) ? price : 0m,
                    offer?["publication"]?["status"]?.GetValue<string>() ?? "");
            }
        }

        log?.Invoke($"Matched {result.Count} of {distinct.Count} EANs to existing offers.");
        return result;
    }

}
