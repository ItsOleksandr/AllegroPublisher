using System.Text.Json;

namespace Allegro.Core;

/// <summary>
/// Read-only reconnaissance for listing a product as a multipack ("zestaw 10 szt.").
///
/// Answers three questions without changing anything on the account:
/// <list type="number">
/// <item>Does the EAN resolve to a product card, and in which category?</item>
/// <item>What does that category require of an offer, and is there any pack-size parameter?</item>
/// <item>What does a complete, working offer of ours actually look like — the JSON we would
/// clone instead of authoring an offer template from scratch?</item>
/// </list>
///
/// Every response is written verbatim to <c>{ResourceDirectory}/probe/</c>. Nothing is created,
/// updated or published: this issues GET requests only.
/// </summary>
public sealed class BundleProbe
{
    private readonly AllegroPublisher _publisher;
    private readonly string _outputDirectory;

    public BundleProbe(AllegroPublisher publisher)
    {
        _publisher = publisher;
        _outputDirectory = Path.Combine(SaverExtensions.ResourceDirectory, "probe");
    }

    /// <summary>Words that would betray a pack-size / multipack parameter in a category or product.</summary>
    private static readonly string[] PackHints =
        { "sztuk", "zestaw", "opakowan", "pakiet", "multipak", "multipack", "liczba w" };

    /// <param name="ean">A real EAN from the parsed catalogue — ideally one whose min order quantity is high.</param>
    /// <param name="templateOfferId">Offer to dump as the clone source. Defaults to the first active offer found.</param>
    public async Task RunAsync(string ean, string? templateOfferId = null, Action<string>? log = null)
    {
        Directory.CreateDirectory(_outputDirectory);
        log?.Invoke($"Probe output: {_outputDirectory}");
        log?.Invoke("");

        var productId = await ProbeProductAsync(ean, log);
        var categoryId = productId.CategoryId;

        if (productId.Id is not null)
        {
            await ProbeProductDetailAsync(productId.Id, log);
        }

        if (categoryId is not null)
        {
            await ProbeCategoryAsync(categoryId, log);
        }

        await ProbeTemplateOfferAsync(templateOfferId, log);

        log?.Invoke("");
        log?.Invoke("Probe finished. Nothing on the account was changed.");
    }

    // ------------------------------------------------------------------ steps

    private record ProductMatch(string? Id, string? CategoryId);

    /// <summary>Resolves the EAN to a product card. Allegro has changed the search parameters over
    /// time, so try the documented forms in order and keep the first that answers.</summary>
    private async Task<ProductMatch> ProbeProductAsync(string ean, Action<string>? log)
    {
        log?.Invoke($"--- 1. Resolve EAN {ean} to a product card ---");

        string[] candidates =
        {
            $"/sale/products?phrase={Uri.EscapeDataString(ean)}&mode=GTIN",
            $"/sale/products?phrase={Uri.EscapeDataString(ean)}",
        };

        foreach (var path in candidates)
        {
            var result = await GetAsync(path, "1-product-search", log);
            if (!result.Ok)
            {
                continue;
            }

            using var document = JsonDocument.Parse(result.Body);
            if (!document.RootElement.TryGetProperty("products", out var products)
                || products.GetArrayLength() == 0)
            {
                log?.Invoke("    no products matched this EAN");
                continue;
            }

            var product = products[0];
            var id = ReadString(product, "id");
            var name = ReadString(product, "name");
            string? categoryId = null;
            if (product.TryGetProperty("category", out var category))
            {
                categoryId = ReadString(category, "id");
            }

            log?.Invoke($"    matched {products.GetArrayLength()} product(s)");
            log?.Invoke($"    product id  : {id}");
            log?.Invoke($"    product name: {name}");
            log?.Invoke($"    category id : {categoryId}");
            return new ProductMatch(id, categoryId);
        }

        log?.Invoke("    could not resolve this EAN to a product card - try another EAN.");
        return new ProductMatch(null, null);
    }

    /// <summary>The product card itself: what it already supplies, and what an offer must still add.</summary>
    private async Task ProbeProductDetailAsync(string productId, Action<string>? log)
    {
        log?.Invoke("");
        log?.Invoke("--- 2. Product card detail ---");

        var result = await GetAsync($"/sale/products/{Uri.EscapeDataString(productId)}", "2-product", log);
        if (!result.Ok)
        {
            return;
        }

        using var document = JsonDocument.Parse(result.Body);
        var root = document.RootElement;

        if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            log?.Invoke($"    card carries {parameters.GetArrayLength()} parameters (these come free with the link)");
        }
        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            log?.Invoke($"    card carries {images.GetArrayLength()} images");
        }
        if (root.TryGetProperty("offerRequirements", out var requirements))
        {
            log?.Invoke($"    offerRequirements: {requirements.GetRawText()}");
        }

        ReportPackHints(result.Body, log);
    }

    /// <summary>What the category demands of an offer, and whether a pack-size parameter exists there.</summary>
    private async Task ProbeCategoryAsync(string categoryId, Action<string>? log)
    {
        log?.Invoke("");
        log?.Invoke("--- 3. Category rules ---");

        await GetAsync($"/sale/categories/{Uri.EscapeDataString(categoryId)}", "3-category", log);

        var result = await GetAsync(
            $"/sale/categories/{Uri.EscapeDataString(categoryId)}/parameters", "3-category-parameters", log);
        if (!result.Ok)
        {
            return;
        }

        using var document = JsonDocument.Parse(result.Body);
        if (!document.RootElement.TryGetProperty("parameters", out var parameters))
        {
            return;
        }

        var required = new List<string>();
        var packRelated = new List<string>();
        foreach (var parameter in parameters.EnumerateArray())
        {
            var name = ReadString(parameter, "name") ?? "";
            var id = ReadString(parameter, "id") ?? "";
            var isRequired = parameter.TryGetProperty("required", out var flag) && flag.ValueKind == JsonValueKind.True;

            if (isRequired)
            {
                required.Add($"{name} (id {id})");
            }
            if (PackHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            {
                packRelated.Add($"{name} (id {id}){(isRequired ? " [required]" : "")}");
            }
        }

        log?.Invoke($"    {required.Count} required offer parameters:");
        foreach (var name in required)
        {
            log?.Invoke($"      - {name}");
        }

        log?.Invoke(packRelated.Count == 0
            ? "    no pack-size parameter in this category"
            : $"    pack-size candidates ({packRelated.Count}):");
        foreach (var name in packRelated)
        {
            log?.Invoke($"      - {name}");
        }
    }

    /// <summary>
    /// Dumps one of our own complete offers. This is the answer to "I don't have the data for a
    /// template": delivery, afterSalesServices, location, payments and the rest get copied from here.
    /// Also reports whether any existing offer already uses a productSet quantity above 1.
    /// </summary>
    private async Task ProbeTemplateOfferAsync(string? templateOfferId, Action<string>? log)
    {
        log?.Invoke("");
        log?.Invoke("--- 4. Clone source: one of our own offers, in full ---");

        if (templateOfferId is null)
        {
            var list = await GetAsync("/sale/offers?publication.status=ACTIVE&limit=20", "4-offers-list", log);
            if (!list.Ok)
            {
                return;
            }

            using var document = JsonDocument.Parse(list.Body);
            if (!document.RootElement.TryGetProperty("offers", out var offers) || offers.GetArrayLength() == 0)
            {
                log?.Invoke("    no active offers to clone from");
                return;
            }

            templateOfferId = ReadString(offers[0], "id");
            log?.Invoke($"    using offer {templateOfferId} - \"{ReadString(offers[0], "name")}\"");
        }

        if (templateOfferId is null)
        {
            return;
        }

        var result = await GetAsync(
            $"/sale/product-offers/{Uri.EscapeDataString(templateOfferId)}", "4-template-offer", log);
        if (!result.Ok)
        {
            return;
        }

        using var offerDocument = JsonDocument.Parse(result.Body);
        var root = offerDocument.RootElement;

        log?.Invoke("    top-level blocks present in a working offer:");
        foreach (var property in root.EnumerateObject())
        {
            log?.Invoke($"      - {property.Name}");
        }

        if (root.TryGetProperty("productSet", out var productSet) && productSet.ValueKind == JsonValueKind.Array)
        {
            log?.Invoke($"    productSet: {productSet.GetRawText()}");
            log?.Invoke("    ^ this is the shape a multipack has to fit into - look for a quantity field");
        }
    }

    // ----------------------------------------------------------------- plumbing

    private record ApiResult(bool Ok, string Body);

    /// <summary>GET one endpoint, dump the raw body, and never throw - a failing step must not
    /// cost us the rest of the reconnaissance.</summary>
    private async Task<ApiResult> GetAsync(string path, string dumpName, Action<string>? log)
    {
        var (status, body) = await _publisher.GetRawAsync(path);
        var file = Path.Combine(_outputDirectory, $"{dumpName}.json");
        await File.WriteAllTextAsync(file, Prettify(body));

        var ok = status is >= 200 and < 300;
        log?.Invoke($"  GET {path} -> {status}  ({Path.GetFileName(file)})");
        if (!ok)
        {
            log?.Invoke($"    {Truncate(body, 300)}");
        }
        return new ApiResult(ok, body);
    }

    private void ReportPackHints(string body, Action<string>? log)
    {
        var hits = PackHints.Where(hint => body.Contains(hint, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count > 0)
        {
            log?.Invoke($"    pack-related wording found in the response: {string.Join(", ", hits)}");
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Prettify(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length] + " ...";
}
