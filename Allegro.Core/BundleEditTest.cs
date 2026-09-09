using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

/// <summary>
/// Answers the one question no GET can: will Allegro let us turn an <b>existing</b> offer into a
/// multipack by raising <c>productSet[].quantity</c>, instead of creating a separate set offer and
/// ending the single-unit one?
///
/// If it does, the single-unit offer does not have to be ended at all - it becomes the set, keeping
/// its id, sales history and search position.
///
/// The test edits one live offer and puts it straight back. The price is multiplied by the pack size
/// in the same request, so that even if the revert fails nobody can buy ten units for the price of one.
/// Every request and response is written to <c>{ResourceDirectory}/probe/</c>, including a full backup
/// of the offer as it was before.
/// </summary>
public sealed class BundleEditTest
{
    private readonly AllegroPublisher _publisher;
    private readonly string _outputDirectory;

    public BundleEditTest(AllegroPublisher publisher)
    {
        _publisher = publisher;
        _outputDirectory = Path.Combine(SaverExtensions.ResourceDirectory, "probe");
    }

    public async Task RunAsync(string ean, int packSize, Action<string>? log = null)
    {
        Directory.CreateDirectory(_outputDirectory);

        var offerId = await FindOfferIdAsync(ean, log);
        if (offerId is null)
        {
            return;
        }

        var original = await ReadOfferAsync(offerId, "edit-1-before", log);
        if (original is null)
        {
            return;
        }

        var originalQuantity = ReadQuantity(original);
        var originalPrice = ReadPrice(original);
        log?.Invoke($"    name    : {original["name"]?.GetValue<string>()}");
        log?.Invoke($"    quantity: {originalQuantity?.ToString() ?? "(none)"}");
        log?.Invoke($"    price   : {originalPrice ?? "(none)"}");

        if (originalQuantity is null || originalPrice is null)
        {
            log?.Invoke("This offer has no productSet quantity or price - pick another EAN.");
            return;
        }

        // ------------------------------------------------------------ the edit

        log?.Invoke("");
        log?.Invoke($"--- 2. Raise quantity to {packSize} and the price with it ---");

        var packPrice = decimal.Parse(originalPrice, CultureInfo.InvariantCulture) * packSize;
        var applied = await PatchAsync(offerId, original, packSize, packPrice, "edit-2-patch", log);

        // ------------------------------------------------------- read it back

        if (applied)
        {
            log?.Invoke("");
            log?.Invoke("--- 3. Read the offer back ---");
            var after = await ReadOfferAsync(offerId, "edit-3-after", log);
            if (after is not null)
            {
                log?.Invoke($"    quantity is now: {ReadQuantity(after)?.ToString() ?? "(none)"}");
                log?.Invoke($"    price is now   : {ReadPrice(after) ?? "(none)"}");
            }
        }

        // ----------------------------------------------------------- put back

        log?.Invoke("");
        log?.Invoke("--- 4. Revert ---");

        var reverted = await PatchAsync(
            offerId, original, originalQuantity.Value,
            decimal.Parse(originalPrice, CultureInfo.InvariantCulture), "edit-4-revert", log);

        var final = await ReadOfferAsync(offerId, "edit-5-final", log);
        if (final is not null)
        {
            var quantity = ReadQuantity(final);
            var price = ReadPrice(final);
            log?.Invoke($"    quantity: {quantity?.ToString() ?? "(none)"}   price: {price ?? "(none)"}");

            var restored = quantity == originalQuantity && price == originalPrice;
            log?.Invoke(restored
                ? "    offer is back exactly as it was."
                : "    !!! OFFER NOT FULLY RESTORED - the backup is in probe/edit-1-before.json !!!");
        }
        else if (!reverted)
        {
            log?.Invoke("    !!! REVERT FAILED - restore by hand from probe/edit-1-before.json !!!");
        }
    }

    // ------------------------------------------------------------------ steps

    private async Task<string?> FindOfferIdAsync(string ean, Action<string>? log)
    {
        log?.Invoke($"--- 1. Find our offer for EAN {ean} ---");

        var (status, body) = await _publisher.GetRawAsync($"/sale/offers?external.id={Uri.EscapeDataString(ean)}");
        log?.Invoke($"  GET /sale/offers?external.id={ean} -> {status}");
        if (status is < 200 or >= 300)
        {
            log?.Invoke($"    {body}");
            return null;
        }

        var offers = JsonNode.Parse(body)?["offers"]?.AsArray();
        if (offers is null || offers.Count == 0)
        {
            log?.Invoke("    no offer with this external.id - is the product listed by this app?");
            return null;
        }

        var offerId = offers[0]?["id"]?.GetValue<string>();
        log?.Invoke($"    offer {offerId} ({offers[0]?["publication"]?["status"]})");
        return offerId;
    }

    private async Task<JsonNode?> ReadOfferAsync(string offerId, string dumpName, Action<string>? log)
    {
        var (status, body) = await _publisher.GetRawAsync($"/sale/product-offers/{offerId}");
        Dump(dumpName, body);
        if (status is < 200 or >= 300)
        {
            log?.Invoke($"  GET offer -> {status}: {Truncate(body, 400)}");
            return null;
        }
        return JsonNode.Parse(body);
    }

    /// <summary>
    /// Sends the smallest edit that could work: the original productSet with one number changed, plus
    /// the price. The productSet entry is copied from the live offer rather than rebuilt, so the GPSR
    /// fields (responsibleProducer, safetyInformation) survive the round trip - dropping those would
    /// break the offer far worse than a rejected quantity.
    /// </summary>
    private async Task<bool> PatchAsync(
        string offerId, JsonNode original, int quantity, decimal price, string dumpName, Action<string>? log)
    {
        var entry = original["productSet"]!.AsArray()[0]!.DeepClone().AsObject();

        // The read model carries fields the write model rejects; keep the id and the compliance data.
        var productId = entry["product"]?["id"]?.GetValue<string>();
        entry["product"] = new JsonObject { ["id"] = productId };
        entry["quantity"] = new JsonObject { ["value"] = quantity };
        foreach (var name in entry.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            entry.Remove(name);
        }

        var payload = new JsonObject
        {
            ["productSet"] = new JsonArray(entry),
            ["sellingMode"] = new JsonObject
            {
                ["price"] = new JsonObject
                {
                    ["amount"] = price.ToString("0.00", CultureInfo.InvariantCulture),
                    ["currency"] = _publisher.Settings.Currency,
                },
            },
        };

        var json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Dump($"{dumpName}-request", json);

        var (status, body) = await _publisher.SendJsonRawAsync(
            HttpMethod.Patch, $"/sale/product-offers/{offerId}", json);
        Dump($"{dumpName}-response", body);

        var ok = status is >= 200 and < 300;
        log?.Invoke($"  PATCH /sale/product-offers/{offerId} (quantity {quantity}, price {price:0.00}) -> {status}");
        if (!ok)
        {
            log?.Invoke($"    {Truncate(body, 800)}");
        }
        return ok;
    }

    // ----------------------------------------------------------------- helpers

    private static int? ReadQuantity(JsonNode offer) =>
        offer["productSet"]?.AsArray().FirstOrDefault()?["quantity"]?["value"]?.GetValue<int>();

    private static string? ReadPrice(JsonNode offer) =>
        offer["sellingMode"]?["price"]?["amount"]?.GetValue<string>();

    private void Dump(string name, string body)
    {
        var path = Path.Combine(_outputDirectory, $"{name}.json");
        try
        {
            using var document = JsonDocument.Parse(body);
            File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException)
        {
            File.WriteAllText(path, body);
        }
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length] + " ...";
}
