using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

/// <summary>
/// Turns single-unit offers into multipacks in place, using the plan from <see cref="BundlePlan"/>.
///
/// In place matters: the offer keeps its id, its sales history and its position in search, and there
/// is no moment where a set exists alongside the single it replaces. The old individual listing does
/// not have to be ended - it <b>becomes</b> the set.
///
/// Quantity, price and stock move in one request, so the offer is never briefly sellable at ten units
/// for the price of one. Everything is written to <c>{ResourceDirectory}/probe/</c>: a backup of each
/// offer before the change, and a log of what was applied.
/// </summary>
public sealed class BundleConverter
{
    private readonly AllegroPublisher _publisher;
    private readonly string _backupDirectory;

    public BundleConverter(AllegroPublisher publisher)
    {
        _publisher = publisher;
        _backupDirectory = Path.Combine(SaverExtensions.ResourceDirectory, "probe", "converted");
    }

    public record Result(int Converted, int Failed, int Skipped);

    /// <param name="limit">Convert at most this many - used to try a single offer before the whole run.</param>
    public async Task<Result> ConvertAsync(
        List<BundlePlan.BundleChange> plan, int? limit = null, Action<string>? log = null)
    {
        Directory.CreateDirectory(_backupDirectory);

        var todo = plan.Where(c => c.CanConvert).ToList();
        if (limit is not null)
        {
            todo = todo.Take(limit.Value).ToList();
        }

        log?.Invoke($"Converting {todo.Count} offers. Backups go to {_backupDirectory}");
        log?.Invoke("");

        int converted = 0, failed = 0, skipped = 0;
        var reasons = new Dictionary<string, int>();
        var registry = SaverExtensions.Bundles.Read();
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
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception e)
            {
                // One bad offer must not cost us the rest of the run.
                failed++;
                Record(reasons, e.Message);
                log?.Invoke($"  {change.Ean}: {e.Message}");
            }
        }

        SaverExtensions.Bundles.Value = registry;
        SaverExtensions.Bundles.Write();

        log?.Invoke("");
        log?.Invoke($"Done: {converted} converted, {skipped} skipped, {failed} failed. " +
                    $"{registry.Count} offers listed as packs.");
        foreach (var reason in reasons.OrderByDescending(r => r.Value))
        {
            log?.Invoke($"  {reason.Value,4}  {reason.Key}");
        }
        return new Result(converted, failed, skipped);
    }

    /// <returns>true converted, false failed, null already a bundle of this size.</returns>
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

        File.WriteAllText(Path.Combine(_backupDirectory, $"{offerId}.json"), readBody);

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

        // Allegro will only count units for an offer that is linked to a catalogue product. Offers listed
        // without that link have no product id to send, and answer the PATCH with OfferWithoutProduct.
        // Attaching a card here would be a different, riskier change than the one that was approved, so
        // these are reported and left alone.
        var productId = entry["product"]?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(productId))
        {
            Record(reasons, "not linked to the catalogue");
            log?.Invoke($"  {change.Ean} offer {offerId}: not linked to the catalogue - skipped");
            return false;
        }

        // The read model carries fields the write model rejects, but the GPSR block (responsibleProducer,
        // safetyInformation) has to survive the round trip - losing it would break the offer far worse
        // than a rejected quantity. So: keep the entry as it came back, replace only what must change.
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

    /// <summary>Keeps the pack registry honest: a pack is remembered, a single unit is forgotten.</summary>
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

    /// <summary>Allegro's error code, so a hundred failures collapse into the handful of causes behind them.</summary>
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
