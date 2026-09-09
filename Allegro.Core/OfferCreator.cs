using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

public sealed class OfferCreator
{
    private readonly AllegroPublisher _publisher;

    public OfferCreator(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    private const string EanParameterId = "225693";

    public record Candidate(ProductInfo Product, string? ProductId, string? CategoryId, string? Problem)
    {
        public bool CanCreate => Problem is null;
    }

    public async Task<List<Candidate>> PlanAsync(Action<string>? log = null)
    {
        var options = SaverExtensions.ListingOptions.Read();
        var sellable = SaverExtensions.Products.Read().Values.Where(options.Includes).ToList();

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
}
