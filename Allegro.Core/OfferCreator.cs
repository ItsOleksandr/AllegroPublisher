using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

/// <summary>
/// Lists products that have no offer at all. <see cref="AllegroPublisher.PublishAsync"/> only ever
/// updates offers that already exist, so a product the supplier added after the storefront was built
/// stays invisible however often the job runs.
///
/// A new offer is assembled from three sources, none of which we have to author:
/// <list type="bullet">
/// <item>the <b>catalogue card</b>, found by EAN, supplies the category, its required parameters and the images;</item>
/// <item>an <b>existing offer of ours</b> supplies the account-level blocks - delivery rates, return policy,
/// implied warranty, location, payment settings - which are identical across every offer we list;</item>
/// <item>the <b>parsed product</b> supplies name, price and stock, packed or single per the CSV options.</item>
/// </list>
///
/// <see cref="PlanAsync"/> resolves everything and reports, without writing. <see cref="CreateAsync"/>
/// creates the offers as drafts, never published, so a human decides what goes on sale.
/// </summary>
public sealed class OfferCreator
{
    private readonly AllegroPublisher _publisher;

    public OfferCreator(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    /// <summary>Allegro's parameter id for the EAN on a catalogue card - used to confirm an exact match.</summary>
    private const string EanParameterId = "225693";

    public record Candidate(ProductInfo Product, string? ProductId, string? CategoryId, string? Problem)
    {
        public bool CanCreate => Problem is null;
    }

    // ------------------------------------------------------------------- plan

    /// <summary>Read-only. Resolves a catalogue card for every unlisted product and reports what is missing.</summary>
    public async Task<List<Candidate>> PlanAsync(Action<string>? log = null)
    {
        var options = SaverExtensions.CSVOptions.Read();
        var sellable = SaverExtensions.Products.Read().Values.Where(CSVMaker.FilterProduct(options)).ToList();

        var known = await new BundlePlan(_publisher).ResolveOffersAsync(sellable.Select(p => p.EAN), log);
        var missing = sellable.Where(p => !known.ContainsKey(p.EAN)).ToList();

        log?.Invoke($"{missing.Count} products have no offer. Looking each one up in the catalogue ...");

        var candidates = new List<Candidate>();
        foreach (var product in missing)
        {
            candidates.Add(await ResolveCardAsync(product));
        }

        return candidates;
    }

    /// <summary>
    /// Finds the one catalogue card that is unambiguously this product. Allegro's search is a phrase
    /// search even in GTIN mode and readily returns several cards, so a card only counts when it carries
    /// our exact EAN. Guessing between near-matches would attach an offer to the wrong product, which is
    /// worse than not listing it.
    /// </summary>
    private async Task<Candidate> ResolveCardAsync(ProductInfo product)
    {
        var (status, body) = await _publisher.GetRawAsync(
            $"/sale/products?phrase={Uri.EscapeDataString(product.EAN)}&mode=GTIN");

        if (status is < 200 or >= 300)
        {
            return new Candidate(product, null, null, $"card lookup failed ({status})");
        }

        var products = JsonNode.Parse(body)?["products"]?.AsArray();
        if (products is null || products.Count == 0)
        {
            return new Candidate(product, null, null, "no catalogue card for this EAN");
        }

        var exact = products.Where(card => CardHasEan(card, product.EAN)).ToList();
        if (exact.Count == 0)
        {
            return new Candidate(product, null, null, $"{products.Count} cards found, none carries this EAN");
        }
        if (exact.Count > 1)
        {
            return new Candidate(product, null, null, $"{exact.Count} cards carry this EAN - ambiguous");
        }

        var card = exact[0];
        return new Candidate(
            product,
            card?["id"]?.GetValue<string>(),
            card?["category"]?["id"]?.GetValue<string>(),
            null);
    }

    private static bool CardHasEan(JsonNode? card, string ean)
    {
        var parameters = card?["parameters"]?.AsArray();
        if (parameters is null)
        {
            return false;
        }

        foreach (var parameter in parameters)
        {
            if (parameter?["id"]?.GetValue<string>() != EanParameterId)
            {
                continue;
            }
            var values = parameter?["values"]?.AsArray();
            if (values is not null && values.Any(v => v?.GetValue<string>() == ean))
            {
                return true;
            }
        }
        return false;
    }

    public static void Report(List<Candidate> candidates, Action<string>? log = null)
    {
        var ready = candidates.Where(c => c.CanCreate).ToList();
        var options = SaverExtensions.CSVOptions.Read();

        log?.Invoke("");
        log?.Invoke($"Ready to create : {ready.Count}");
        foreach (var group in candidates.Where(c => !c.CanCreate).GroupBy(c => c.Problem).OrderByDescending(g => g.Count()))
        {
            log?.Invoke($"Blocked ({group.Count(),3})  : {group.Key}");
        }

        log?.Invoke("");
        foreach (var candidate in ready.Take(10))
        {
            var pack = options.GetPackSize(candidate.Product);
            log?.Invoke($"  {candidate.Product.EAN}  card {candidate.ProductId}  cat {candidate.CategoryId}  " +
                        $"{options.GetOfferPrice(candidate.Product),9:0.00}  " +
                        $"{(pack > 1 ? $"pack {pack}" : "single")}");
        }
    }
}
