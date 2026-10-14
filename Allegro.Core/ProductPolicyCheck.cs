using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Allegro.Core;

public class ProductCheck
{
    public bool Allowed { get; set; }
    public string Reason { get; set; } = "";
    public string RulesStamp { get; set; } = "";
    public DateTime CheckedAt { get; set; }
}

public sealed class ProductPolicyCheck
{
    private const int BatchSize = 15;
    private const int DescriptionChars = 600;

    public record Item(ProductInfo Product, string? AllegroCategory);

    public static string Stamp(string rules) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rules.Trim())))[..16];

    public async Task<Dictionary<string, ProductCheck>> CheckAsync(IReadOnlyCollection<Item> items, string rules, Action<string>? log = null)
    {
        var stamp = Stamp(rules);
        var cache = SaverExtensions.ProductChecks.Read();
        var todo = items
            .Where(i => !cache.TryGetValue(i.Product.EAN, out var hit) || hit.RulesStamp != stamp)
            .GroupBy(i => i.Product.EAN)
            .Select(g => g.First())
            .ToList();

        if (todo.Count > 0)
        {
            log?.Invoke($"AI check of {todo.Count} products against the list of forbidden goods ...");
            var ai = new AiClient();
            foreach (var batch in todo.Chunk(BatchSize))
            {
                try
                {
                    foreach (var (ean, check) in await CheckBatchAsync(ai, batch, rules, stamp))
                    {
                        cache[ean] = check;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log?.Invoke($"  AI check of {batch.Length} products failed: {e.Message} - they are not listed this time.");
                }
            }

            SaverExtensions.ProductChecks.Value = cache;
            SaverExtensions.ProductChecks.Write();
            log?.Invoke($"AI check done: {ai.Calls} requests, tokens {ai.InputTokens:N0} in, {ai.OutputTokens:N0} out.");
        }

        return items
            .Select(i => i.Product.EAN)
            .Distinct()
            .Where(ean => cache.TryGetValue(ean, out var hit) && hit.RulesStamp == stamp)
            .ToDictionary(ean => ean, ean => cache[ean]);
    }

    private static async Task<Dictionary<string, ProductCheck>> CheckBatchAsync(AiClient ai, Item[] batch, string rules, string stamp)
    {
        var products = new JsonArray(batch.Select(i => (JsonNode)new JsonObject
        {
            ["ean"] = i.Product.EAN,
            ["name"] = i.Product.Name,
            ["supplierCategories"] = new JsonArray(i.Product.CategoriesUrls
                .Select(url => (JsonNode)JsonValue.Create(CategorySlug(url))!).ToArray()),
            ["allegroCategory"] = i.AllegroCategory,
            ["description"] = Shorten(i.Product.Description),
        }).ToArray());

        var system = $$"""
                       You decide whether a shop may list a product on Allegro. The shop must NOT list these goods:

                       {{rules.Trim()}}

                       Judge each product by its name, description and categories. A product belongs to a forbidden group if it
                       has the property described there, even when the name does not say so directly (for example anything that
                       lights up, needs batteries or is charged). When the data does not let you rule a forbidden group out, mark
                       the product as not allowed. Do not forbid anything for reasons not on the list.

                       Answer ONLY with JSON, no comments and no code block, one entry per product:
                       [{"ean": "...", "allowed": true, "reason": "..."}]
                       "reason" is one short sentence in Polish; for a forbidden product name the forbidden group.
                       """;

        var answer = await ai.CompleteAsync(system, products.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

        var json = Regex.Replace(answer.Trim(), @"^```(?:json)?\s*|\s*```$", "");
        var wanted = batch.Select(i => i.Product.EAN).ToHashSet();
        var result = new Dictionary<string, ProductCheck>();
        foreach (var entry in JsonNode.Parse(json)?.AsArray() ?? new JsonArray())
        {
            var ean = entry?["ean"]?.ToString();
            var allowed = entry?["allowed"];
            if (ean is null || !wanted.Contains(ean) || allowed is null)
            {
                continue;
            }
            result[ean] = new ProductCheck
            {
                Allowed = allowed.GetValue<bool>(),
                Reason = entry?["reason"]?.ToString() ?? "",
                RulesStamp = stamp,
                CheckedAt = DateTime.Now,
            };
        }
        return result;
    }

    private static string CategorySlug(string url) =>
        Regex.Replace(url, @"^https?://[^/]+/(kategoria-produktu/)?", "").Trim('/');

    private static string Shorten(string? text)
    {
        var clean = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return clean.Length <= DescriptionChars ? clean : clean[..DescriptionChars] + " ...";
    }
}
