using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

public sealed class OfferCreator
{
    private const string EanParameterId = "225693";
    private const string ConditionParameterId = "11323";
    private const int MaxImages = 16;
    private const int LookupParallelism = 6;

    private readonly AllegroPublisher _publisher;

    public OfferCreator(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    public record Candidate(
        ProductInfo Product, string? ProductId, string? CategoryId, string? Problem,
        string Name = "", int Pack = 1, decimal Price = 0m, int Stock = 0)
    {
        public bool CanCreate => Problem is null;
    }

    public record Result(int Created, int Failed);

    public async Task<List<Candidate>> PlanAsync(Action<string>? log = null)
    {
        var options = SaverExtensions.ListingOptions.Read();
        var sellable = SaverExtensions.Products.Read().Values
            .Where(p => options.Includes(p) && !options.IsStale(p))
            .ToList();

        var known = await new BundlePlan(_publisher).ResolveOffersAsync(sellable.Select(p => p.EAN), log);
        var missing = sellable.Where(p => !known.ContainsKey(p.EAN)).ToList();

        var now = DateTime.Now;
        var cache = new ConcurrentDictionary<string, CardLookup>(SaverExtensions.CardLookups.Read());
        var cached = missing.Count(p => cache.TryGetValue(p.EAN, out var hit) && hit.IsFresh(now));
        log?.Invoke($"{missing.Count} products have no offer. {cached} catalogue cards known from earlier scans, " +
                    $"looking up {missing.Count - cached} ...");

        var resolved = new Candidate[missing.Count];
        using var gate = new SemaphoreSlim(LookupParallelism);
        await Task.WhenAll(missing.Select(async (product, index) =>
        {
            if (cache.TryGetValue(product.EAN, out var hit) && hit.IsFresh(now))
            {
                resolved[index] = new Candidate(product, hit.ProductId, hit.CategoryId, hit.Problem);
                return;
            }

            await gate.WaitAsync();
            try
            {
                var (candidate, cacheable) = await TryResolveCardAsync(product);
                resolved[index] = candidate;
                if (cacheable)
                {
                    cache[product.EAN] = new CardLookup
                    {
                        ProductId = candidate.ProductId,
                        CategoryId = candidate.CategoryId,
                        Problem = candidate.Problem,
                        CheckedAt = now,
                    };
                }
            }
            finally
            {
                gate.Release();
            }
        }));

        SaverExtensions.CardLookups.Value = cache
            .Where(entry => entry.Value.IsFresh(now))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        SaverExtensions.CardLookups.Write();

        var candidates = new List<Candidate>();
        foreach (var looked in resolved)
        {
            var candidate = looked;
            var product = candidate.Product;
            if (candidate.CanCreate)
            {
                var pack = options.GetPackSize(product);
                var name = BundlePlan.BuildName(product.Name, pack);
                candidate = candidate with
                {
                    Name = name,
                    Pack = pack,
                    Price = options.GetOfferPrice(product),
                    Stock = options.GetOfferStock(product),
                    Problem = IsValidName(name) ? null : "the supplier's name is too short for an Allegro title",
                };
            }
            candidates.Add(candidate);
        }

        return candidates;
    }

    public async Task<Result> CreateAsync(IEnumerable<Candidate> candidates, Action<string>? log = null)
    {
        var options = SaverExtensions.ListingOptions.Read();
        if (string.IsNullOrWhiteSpace(options.NewOfferTemplateId))
        {
            throw new InvalidOperationException("Set the template offer first: new offers copy its delivery, returns and GPSR settings.");
        }

        var template = await GetJsonAsync($"/sale/product-offers/{Uri.EscapeDataString(options.NewOfferTemplateId.Trim())}");
        var missing = MissingTemplateSettings(template);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"The template offer has no {string.Join(", ", missing)} - pick another one.");
        }

        var registry = SaverExtensions.Bundles.Read();

        int created = 0, failed = 0;
        foreach (var candidate in candidates.Where(c => c.CanCreate))
        {
            try
            {
                var card = await GetJsonAsync($"/sale/products/{candidate.ProductId}");
                var payload = BuildPayload(template, card, candidate, options);

                var (status, body) = await _publisher.SendJsonRawAsync(HttpMethod.Post, "/sale/product-offers", payload.ToJsonString());
                if (status is < 200 or >= 300)
                {
                    throw new InvalidOperationException($"POST {status}: {ReadErrors(body)}");
                }

                var response = JsonNode.Parse(body);
                var offerId = response?["id"]?.ToString() ?? "?";
                var state = response?["publication"]?["status"]?.ToString() ?? (status == 202 ? "processing" : "");
                var problems = ReadErrors(response?["validation"]?.ToJsonString() ?? "");

                if (candidate.Pack > 1)
                {
                    registry[candidate.Product.EAN] = candidate.Pack;
                }

                created++;
                log?.Invoke($"  {candidate.Product.EAN}: offer {offerId} ({state}) - {candidate.Name}, " +
                            $"{candidate.Price.ToString("0.00", CultureInfo.InvariantCulture)} {_publisher.Settings.Currency}, " +
                            $"stock {candidate.Stock}{(problems.Length > 0 ? $" - check: {problems}" : "")}");
            }
            catch (Exception e)
            {
                failed++;
                log?.Invoke($"  {candidate.Product.EAN}: {e.Message}");
            }
        }

        SaverExtensions.Bundles.Value = registry;
        SaverExtensions.Bundles.Write();
        log?.Invoke($"Created {created} offers, {failed} failed.");
        return new Result(created, failed);
    }

    private JsonObject BuildPayload(JsonNode template, JsonNode card, Candidate candidate, ListingOptions options)
    {
        var templateEntry = template["productSet"]?.AsArray().FirstOrDefault();

        var safety = card["productSafety"]?["safetyInformation"];
        var safetyType = safety?["type"]?.ToString();
        JsonNode safetyInformation = safetyType is "TEXT" or "ATTACHMENTS"
            ? safety!.DeepClone()
            : !string.IsNullOrWhiteSpace(options.NewOfferSafetyText)
                ? new JsonObject { ["type"] = "TEXT", ["description"] = options.NewOfferSafetyText.Trim() }
                : throw new InvalidOperationException("the catalogue card has no safety information and no fallback text is set");

        var entry = new JsonObject
        {
            ["product"] = new JsonObject { ["id"] = candidate.ProductId },
            ["quantity"] = new JsonObject { ["value"] = candidate.Pack },
            ["responsibleProducer"] = Copy(templateEntry?["responsibleProducer"]),
            ["responsiblePerson"] = Copy(templateEntry?["responsiblePerson"]),
            ["safetyInformation"] = safetyInformation,
            ["marketedBeforeGPSRObligation"] = false,
        };

        var images = (card["images"]?.AsArray() ?? new JsonArray())
            .Select(image => image?["url"]?.ToString())
            .Where(url => !string.IsNullOrEmpty(url))
            .Take(MaxImages)
            .Select(url => (JsonNode)JsonValue.Create(url)!)
            .ToArray();
        if (images.Length == 0)
        {
            throw new InvalidOperationException("the catalogue card has no images");
        }

        var payload = new JsonObject
        {
            ["name"] = candidate.Name,
            ["category"] = new JsonObject { ["id"] = candidate.CategoryId },
            ["productSet"] = new JsonArray(entry),
            ["parameters"] = new JsonArray(Condition(template)),
            ["images"] = new JsonArray(images),
            ["description"] = BuildDescription(card, candidate.Product),
            ["sellingMode"] = new JsonObject
            {
                ["format"] = "BUY_NOW",
                ["price"] = new JsonObject
                {
                    ["amount"] = candidate.Price.ToString("0.00", CultureInfo.InvariantCulture),
                    ["currency"] = _publisher.Settings.Currency,
                },
            },
            ["stock"] = new JsonObject { ["available"] = candidate.Stock, ["unit"] = "UNIT" },
            ["external"] = new JsonObject { ["id"] = candidate.Product.EAN },
            ["delivery"] = new JsonObject
            {
                ["shippingRates"] = Copy(template["delivery"]?["shippingRates"]),
                ["handlingTime"] = Copy(template["delivery"]?["handlingTime"]),
            },
            ["afterSalesServices"] = Copy(template["afterSalesServices"]),
            ["payments"] = Copy(template["payments"]),
            ["location"] = Copy(template["location"]),
            ["publication"] = new JsonObject { ["status"] = "ACTIVE" },
            ["language"] = "pl-PL",
        };

        return (JsonObject)WithoutNulls(payload)!;
    }

    private static JsonNode BuildDescription(JsonNode card, ProductInfo product)
    {
        var sections = card["description"]?["sections"]?.AsArray();
        if (sections is { Count: > 0 })
        {
            return new JsonObject { ["sections"] = sections.DeepClone() };
        }

        var lines = (product.Description ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var html = lines.Length == 0
            ? $"<p>{WebUtility.HtmlEncode(product.Name)}</p>"
            : string.Concat(lines.Select(line => $"<p>{WebUtility.HtmlEncode(line)}</p>"));
        return new JsonObject
        {
            ["sections"] = new JsonArray(new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["type"] = "TEXT", ["content"] = html }),
            }),
        };
    }

    private static JsonNode Condition(JsonNode template) =>
        (template["parameters"]?.AsArray() ?? new JsonArray())
            .FirstOrDefault(p => p?["id"]?.ToString() == ConditionParameterId)?.DeepClone()
        ?? new JsonObject { ["id"] = ConditionParameterId, ["valuesIds"] = new JsonArray("11323_1") };

    private static List<string> MissingTemplateSettings(JsonNode template)
    {
        var entry = template["productSet"]?.AsArray().FirstOrDefault();
        var checks = new (string Name, JsonNode? Value)[]
        {
            ("shipping rates", template["delivery"]?["shippingRates"]),
            ("return policy", template["afterSalesServices"]?["returnPolicy"]),
            ("implied warranty", template["afterSalesServices"]?["impliedWarranty"]),
            ("location", template["location"]),
            ("invoice setting", template["payments"]),
            ("GPSR responsible producer", entry?["responsibleProducer"]),
        };
        return checks.Where(c => c.Value is null).Select(c => c.Name).ToList();
    }

    private static JsonNode? Copy(JsonNode? node) => node?.DeepClone();

    private static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
                {
                    obj.Remove(key);
                }
                foreach (var property in obj.ToList())
                {
                    WithoutNulls(property.Value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    WithoutNulls(item);
                }
                break;
        }
        return node;
    }

    private static bool IsValidName(string name) =>
        name.Length >= 12 && name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;

    private async Task<JsonNode> GetJsonAsync(string path)
    {
        var (status, body) = await _publisher.GetRawAsync(path);
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"GET {path} failed ({status})");
        }
        return JsonNode.Parse(body) ?? throw new InvalidOperationException($"GET {path} returned nothing");
    }

    private static string ReadErrors(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }
        try
        {
            var errors = JsonNode.Parse(body)?["errors"]?.AsArray() ?? new JsonArray();
            return string.Join("; ", errors.Select(e => e?["userMessage"]?.ToString() ?? e?["message"]?.ToString()));
        }
        catch (JsonException)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }

    private async Task<(Candidate Candidate, bool Cacheable)> TryResolveCardAsync(ProductInfo product)
    {
        try
        {
            return await ResolveCardAsync(product);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (new Candidate(product, null, null, $"card lookup failed ({e.GetType().Name}) - scan again"), false);
        }
    }

    private async Task<(Candidate Candidate, bool Cacheable)> ResolveCardAsync(ProductInfo product)
    {
        var (status, body) = await _publisher.GetRawAsync(
            $"/sale/products?phrase={Uri.EscapeDataString(product.EAN)}&mode=GTIN");

        if (status is < 200 or >= 300)
        {
            return (new Candidate(product, null, null, $"card lookup failed ({status})"), false);
        }

        var products = JsonNode.Parse(body)?["products"]?.AsArray();
        if (products is null || products.Count == 0)
        {
            return (new Candidate(product, null, null, "no catalogue card for this EAN"), true);
        }

        var exact = products.Where(card => CardHasEan(card, product.EAN)).ToList();
        if (exact.Count == 0)
        {
            return (new Candidate(product, null, null, $"{products.Count} cards found, none carries this EAN"), true);
        }
        if (exact.Count > 1)
        {
            return (new Candidate(product, null, null, $"{exact.Count} cards carry this EAN - ambiguous"), true);
        }

        var card = exact[0];
        return (new Candidate(
            product,
            card?["id"]?.GetValue<string>(),
            card?["category"]?["id"]?.GetValue<string>(),
            null), true);
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
