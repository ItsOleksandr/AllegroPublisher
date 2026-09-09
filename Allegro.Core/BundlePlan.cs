using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

/// <summary>
/// Works out which products should be sold as a multipack instead of individually, and what each
/// existing offer would have to become. Read-only: it resolves offers and computes the changes, but
/// applies nothing. The result is the list a human approves before anything is written.
///
/// A product qualifies when the supplier's minimum order quantity is above
/// <see cref="CSVOptions.BundleFromQuantity"/> - buying one unit is not possible, so listing one unit
/// is a lie. The whole pack becomes the unit of sale.
/// </summary>
public sealed class BundlePlan
{
    /// <summary>Allegro rejects offer titles longer than this.</summary>
    public const int MaxNameLength = 75;

    private readonly AllegroPublisher _publisher;

    public BundlePlan(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    /// <param name="Problem">Why this one cannot be converted, or null when it can.</param>
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
        var options = SaverExtensions.CSVOptions.Read();
        var products = SaverExtensions.Products.Read().Values.ToList();

        var candidates = SelectCandidates(products, options);
        log?.Invoke($"{products.Count} products parsed, {candidates.Count} qualify as bundles " +
                    $"(min order > {options.BundleFromQuantity}, in stock, not black listed).");

        // Raising the threshold un-qualifies products that are already packs on Allegro. They have to be
        // taken back to single units, or the CSV would price a pack of ten as though it were one item.
        var qualified = candidates.Select(p => p.EAN).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byEan = products.Where(p => !string.IsNullOrEmpty(p.EAN))
                            .GroupBy(p => p.EAN, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var reverts = SaverExtensions.Bundles.Read()
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

    /// <summary>
    /// The products worth converting. Deliberately reuses the ordinary CSV rules for everything except
    /// the min-order test, so a bundle can never sneak past a black list that would stop a single unit.
    /// </summary>
    private static List<ProductInfo> SelectCandidates(List<ProductInfo> products, CSVOptions options)
    {
        return products
            .Where(p => p.MinOrderQuantity > options.BundleFromQuantity)
            .Where(p => !string.IsNullOrWhiteSpace(p.EAN) && !p.EAN.Contains("—"))
            .Where(p => !p.CategoriesUrls.Any(url => options.CategoriesBlackList.Any(url.Contains)))
            .Where(p => !options.EansBlackList.Contains(p.EAN))
            .Where(p => p.Price >= options.MinimalPrice)
            // One whole pack must be available, or there is nothing to sell.
            .Where(p => p.Count >= p.MinOrderQuantity)
            .ToList();
    }

    private BundleChange BuildChange(ProductInfo product, OfferSnapshot? offer, CSVOptions options)
    {
        var pack = options.GetPackSize(product);
        var packPrice = options.GetOfferPrice(product);
        var newStock = options.GetOfferStock(product);
        var newName = BuildName(product.Name, pack);

        var problem = offer is null
            ? "no offer on Allegro - would have to be created from scratch"
            : offer.Quantity == pack
                ? "already a bundle of this size"
                : null;

        return new BundleChange(
            product.EAN, offer?.Id, pack, offer?.Name ?? product.Name, newName,
            product.Price, offer?.Price ?? 0m, packPrice, product.Count, newStock,
            offer?.Status ?? "", problem);
    }

    /// <summary>
    /// "Zestaw 10 szt. <name>", trimmed to Allegro's title limit. The prefix is what buyers scan for,
    /// so the original name gives way, not the prefix.
    /// </summary>
    public static string BuildName(string name, int pack)
    {
        var prefix = pack > 1 ? $"Zestaw {pack} szt. " : "";
        var room = MaxNameLength - prefix.Length;
        var clean = SanitizeName(name);
        var trimmed = clean.Length <= room ? clean : clean[..Math.Max(room, 0)].TrimEnd();
        return prefix + trimmed;
    }

    /// <summary>
    /// The supplier writes titles with typographic characters - 60×80, 1,33L – XJ4972, 5″ - that Allegro
    /// rejects outright ("Tytuł zawiera niedozwolone znaki"). Those names never passed Allegro validation,
    /// because they came from a scraped page, not from an existing offer. Fold the look-alikes onto their
    /// ASCII equivalents so the meaning survives, then drop anything still outside the safe set.
    /// Polish letters are kept - they are ordinary letters, not special characters.
    /// </summary>
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
                // Quotes survive the fold above but are not worth risking in a title: a stray inch mark
                // reads fine as nothing at all.
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

        // Folding and dropping leaves double spaces behind; Allegro trims titles anyway.
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // ------------------------------------------------------------------ offers

    public record OfferSnapshot(string Id, string Name, decimal Price, int Quantity, string Status);

    /// <summary>Looks up our offers by external.id == EAN, batched the way the publisher does it.</summary>
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
                    // The list endpoint does not expose productSet, so quantity is unknown here;
                    // the converter re-reads each offer in full before touching it.
                    0,
                    offer?["publication"]?["status"]?.GetValue<string>() ?? "");
            }
        }

        log?.Invoke($"Matched {result.Count} of {distinct.Count} EANs to existing offers.");
        return result;
    }

    // ------------------------------------------------------------------ report

    /// <summary>Prints a summary and writes the full plan to probe/bundle-plan.csv for review.</summary>
    public static void Report(List<BundleChange> changes, Action<string>? log = null)
    {
        var convertible = changes.Where(c => c.CanConvert).ToList();
        var blocked = changes.Where(c => !c.CanConvert).ToList();

        log?.Invoke("");
        log?.Invoke($"Can be converted in place : {convertible.Count}");
        foreach (var group in blocked.GroupBy(c => c.Problem))
        {
            log?.Invoke($"Blocked ({group.Count()}) : {group.Key}");
        }

        log?.Invoke("");
        foreach (var group in convertible.GroupBy(c => c.OfferStatus).OrderByDescending(g => g.Count()))
        {
            log?.Invoke($"  {group.Key,-10} {group.Count(),4} offers");
        }

        log?.Invoke("");
        log?.Invoke($"{"EAN",-15} {"pack",5} {"stock",6} {"price now",10} {"price as set",13}  name");
        foreach (var change in convertible.Take(25))
        {
            log?.Invoke($"{change.Ean,-15} {change.PackSize,5} {change.NewStock,6} " +
                        $"{change.OldOfferPrice,10:0.00} {change.NewOfferPrice,13:0.00}  {change.NewName}");
        }
        if (convertible.Count > 25)
        {
            log?.Invoke($"... and {convertible.Count - 25} more - see the CSV");
        }

        var path = Path.Combine(SaverExtensions.ResourceDirectory, "probe", "bundle-plan.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var csv = new StringBuilder();
        csv.AppendLine("EAN;OfferId;Status;Pack;SupplierUnitPrice;OldOfferPrice;NewOfferPrice;SupplierStock;NewStock;OldName;NewName;Problem");
        foreach (var c in changes)
        {
            csv.AppendLine(string.Join(";",
                c.Ean, c.OfferId, c.OfferStatus, c.PackSize,
                c.SupplierUnitPrice.ToString(CultureInfo.InvariantCulture),
                c.OldOfferPrice.ToString(CultureInfo.InvariantCulture),
                c.NewOfferPrice.ToString(CultureInfo.InvariantCulture),
                c.SupplierStock, c.NewStock,
                c.OldName.Replace(';', ','), c.NewName.Replace(';', ','), c.Problem));
        }
        File.WriteAllText(path, csv.ToString());

        log?.Invoke("");
        log?.Invoke($"Full plan written to {path}");
    }
}
