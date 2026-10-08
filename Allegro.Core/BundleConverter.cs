using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

public sealed class BundleConverter
{
    private readonly AllegroPublisher _publisher;

    public BundleConverter(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    public record Result(int Converted, int Failed, int Skipped);

    public async Task<Result> ConvertAsync(
        List<BundlePlan.BundleChange> plan, int? limit = null, Action<string>? log = null)
    {
        var todo = plan.Where(c => c.CanConvert).ToList();
        if (limit is not null)
        {
            todo = todo.Take(limit.Value).ToList();
        }

        int converted = 0, failed = 0, skipped = 0;
        var reasons = new Dictionary<string, int>();
        var registry = SaverExtensions.Bundles.Read();
        var drafts = SaverExtensions.ContentDrafts.Read();
        foreach (var change in todo)
        {
            try
            {
                var outcome = await ConvertOneAsync(change, registry, reasons, log);
                if (outcome is null)
                {
                    skipped++;
                }
                else if (outcome.Value)
                {
                    converted++;
                    if (drafts.TryGetValue(change.Ean, out var draft) && draft.Status == ContentDraftStatus.Applied
                                                                      && draft.Pack != change.PackSize)
                    {
                        draft.Status = ContentDraftStatus.Outdated;
                        log?.Invoke($"  {change.Ean}: its generated description was written for {draft.Pack} - regenerate it.");
                    }
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception e)
            {
                failed++;
                Record(reasons, e.Message);
                log?.Invoke($"  {change.Ean}: {e.Message}");
            }
        }

        SaverExtensions.Bundles.Value = registry;
        SaverExtensions.Bundles.Write();
        SaverExtensions.ContentDrafts.Value = drafts;
        SaverExtensions.ContentDrafts.Write();

        log?.Invoke("");
        log?.Invoke($"Done: {converted} converted, {skipped} skipped, {failed} failed. " +
                    $"{registry.Count} offers listed as packs.");
        foreach (var reason in reasons.OrderByDescending(r => r.Value))
        {
            log?.Invoke($"  {reason.Value,4}  {reason.Key}");
        }
        return new Result(converted, failed, skipped);
    }

    private async Task<bool?> ConvertOneAsync(
        BundlePlan.BundleChange change, Dictionary<string, int> registry,
        Dictionary<string, int> reasons, Action<string>? log)
    {
        var offerId = change.OfferId!;

        var (readStatus, readBody) = await _publisher.GetRawAsync($"/sale/product-offers/{offerId}");
        if (readStatus is < 200 or >= 300)
        {
            Record(reasons, $"read failed ({readStatus})");
            log?.Invoke($"  {change.Ean} offer {offerId}: read failed ({readStatus})");
            return false;
        }

        var offer = JsonNode.Parse(readBody)!;
        var entry = offer["productSet"]?.AsArray().FirstOrDefault()?.DeepClone().AsObject();
        if (entry is null)
        {
            Record(reasons, "no productSet");
            log?.Invoke($"  {change.Ean} offer {offerId}: no productSet - skipped");
            return false;
        }

        if (entry["quantity"]?["value"]?.GetValue<int>() == change.PackSize)
        {
            Remember(registry, change.Ean, change.PackSize);
            log?.Invoke($"  {change.Ean} offer {offerId}: already a pack of {change.PackSize}");
            return null;
        }

        var productId = entry["product"]?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(productId))
        {
            Record(reasons, "not linked to the catalogue");
            log?.Invoke($"  {change.Ean} offer {offerId}: not linked to the catalogue - skipped");
            return false;
        }

        entry["product"] = new JsonObject { ["id"] = productId };
        entry["quantity"] = new JsonObject { ["value"] = change.PackSize };
        foreach (var name in entry.Where(property => property.Value is null).Select(property => property.Key).ToList())
        {
            entry.Remove(name);
        }

        var payload = new JsonObject
        {
            ["name"] = change.NewName,
            ["productSet"] = new JsonArray(entry),
            ["sellingMode"] = new JsonObject
            {
                ["price"] = new JsonObject
                {
                    ["amount"] = change.NewOfferPrice.ToString("0.00", CultureInfo.InvariantCulture),
                    ["currency"] = _publisher.Settings.Currency,
                },
            },
            ["stock"] = new JsonObject { ["available"] = change.NewStock, ["unit"] = "UNIT" },
        };

        var (status, body) = await _publisher.SendJsonRawAsync(
            HttpMethod.Patch,
            $"/sale/product-offers/{offerId}",
            payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        if (status is < 200 or >= 300)
        {
            Record(reasons, $"PATCH {status}: {ReadErrorCode(body)}");
            log?.Invoke($"  {change.Ean} offer {offerId}: PATCH {status} - {Truncate(body, 400)}");
            return false;
        }

        Remember(registry, change.Ean, change.PackSize);
        var shape = change.PackSize > 1 ? $"pack {change.PackSize}" : "single units";
        log?.Invoke($"  {change.Ean} offer {offerId}: {shape}, " +
                    $"{change.OldOfferPrice:0.00} -> {change.NewOfferPrice:0.00} {_publisher.Settings.Currency}, " +
                    $"stock {change.NewStock} - {change.NewName}");
        return true;
    }

    private static void Remember(Dictionary<string, int> registry, string ean, int pack)
    {
        if (pack > 1)
        {
            registry[ean] = pack;
        }
        else
        {
            registry.Remove(ean);
        }
    }

    private static void Record(Dictionary<string, int> reasons, string reason) =>
        reasons[reason] = reasons.TryGetValue(reason, out var count) ? count + 1 : 1;

    private static string ReadErrorCode(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["errors"]?.AsArray().FirstOrDefault();
            return error?["code"]?.GetValue<string>() ?? "unknown";
        }
        catch (JsonException)
        {
            return "unparseable response";
        }
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length] + " ...";
}
