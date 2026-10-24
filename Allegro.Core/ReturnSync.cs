using System.Globalization;
using System.Text.Json.Nodes;

namespace Allegro.Core;

public class TrackedReturn
{
    public string Id { get; set; } = "";
    public string Reference { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string Buyer { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<ReturnItem> Items { get; set; } = new();
    public string Decision { get; set; } = "";
    public DateTime? DecidedAt { get; set; }
}

public class ReturnItem
{
    public string OfferId { get; set; } = "";
    public string Ean { get; set; } = "";
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public int Pack { get; set; } = 1;
    public string Reason { get; set; } = "";
    public int Accepted { get; set; }
}

public static class ReturnDecision
{
    public const string Pending = "";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public sealed class ReturnSync
{
    private static readonly object Gate = new();
    private const int PageSize = 100;

    private readonly AllegroPublisher _publisher;

    public ReturnSync(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    public static Dictionary<string, TrackedReturn> Returns()
    {
        lock (Gate)
        {
            return new Dictionary<string, TrackedReturn>(SaverExtensions.Returns.Read());
        }
    }

    public async Task<int> SyncAsync(Action<string>? log = null)
    {
        var known = Returns();
        var since = known.Count == 0 ? DateTime.UtcNow.AddDays(-30) : known.Values.Max(r => r.CreatedAt).ToUniversalTime().AddDays(-1);
        var orders = OrderSync.Orders();
        var bundles = SaverExtensions.Bundles.Read();
        var added = 0;

        for (int offset = 0; ; offset += PageSize)
        {
            var (status, body) = await _publisher.GetRawAsync(
                $"/order/customer-returns?limit={PageSize}&offset={offset}&createdAt.gte={Uri.EscapeDataString(since.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}");
            if (status is < 200 or >= 300)
            {
                throw new InvalidOperationException($"could not read customer returns ({status}): {(body.Length > 300 ? body[..300] : body)}");
            }

            var page = JsonNode.Parse(body)?["customerReturns"]?.AsArray() ?? new JsonArray();
            foreach (var node in page)
            {
                var id = node?["id"]?.ToString() ?? "";
                if (id.Length == 0 || known.ContainsKey(id))
                {
                    continue;
                }

                var orderId = node?["orderId"]?.ToString() ?? "";
                orders.TryGetValue(orderId, out var order);
                var tracked = new TrackedReturn
                {
                    Id = id,
                    Reference = node?["referenceNumber"]?.ToString() ?? "",
                    OrderId = orderId,
                    Buyer = node?["buyer"]?["login"]?.ToString() ?? node?["buyer"]?["email"]?.ToString() ?? "",
                    CreatedAt = DateTime.TryParse(node?["createdAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at)
                        ? at.ToLocalTime() : DateTime.Now,
                };

                foreach (var item in node?["items"]?.AsArray() ?? new JsonArray())
                {
                    var offerId = item?["offerId"]?.ToString() ?? "";
                    var line = order?.Lines.FirstOrDefault(l => l.OfferId == offerId);
                    var ean = line?.Ean ?? await ResolveEanAsync(offerId);
                    if (ean.Length == 0)
                    {
                        continue;
                    }
                    var quantity = item?["quantity"]?.GetValue<int>() ?? 0;
                    tracked.Items.Add(new ReturnItem
                    {
                        OfferId = offerId,
                        Ean = ean,
                        Name = item?["name"]?.ToString() ?? line?.Name ?? "",
                        Quantity = quantity,
                        Pack = line?.Pack ?? (bundles.TryGetValue(ean, out var listed) && listed > 1 ? listed : 1),
                        Reason = string.Join(": ", new[] { item?["reason"]?["type"]?.ToString(), item?["reason"]?["userComment"]?.ToString() }
                            .Where(s => !string.IsNullOrWhiteSpace(s))),
                        Accepted = quantity,
                    });
                }

                if (tracked.Items.Count == 0)
                {
                    continue;
                }

                lock (Gate)
                {
                    var all = SaverExtensions.Returns.Read();
                    all[id] = tracked;
                    SaverExtensions.Returns.Value = all;
                    SaverExtensions.Returns.Write();
                }
                known[id] = tracked;
                added++;
                log?.Invoke($"Customer return {tracked.Reference} for order {orderId[..Math.Min(8, orderId.Length)]}…: {tracked.Items.Sum(i => i.Quantity)} item(s) wait for a decision.");
            }

            if (page.Count < PageSize)
            {
                break;
            }
        }
        return added;
    }

    private async Task<string> ResolveEanAsync(string offerId)
    {
        if (offerId.Length == 0)
        {
            return "";
        }
        var (status, body) = await _publisher.GetRawAsync($"/sale/product-offers/{Uri.EscapeDataString(offerId)}");
        return status is >= 200 and < 300 ? JsonNode.Parse(body)?["external"]?["id"]?.ToString()?.Trim() ?? "" : "";
    }

    public static void Accept(string id, Dictionary<string, int> acceptedPerOffer, Action<string>? log = null)
    {
        lock (Gate)
        {
            var all = SaverExtensions.Returns.Read();
            if (!all.TryGetValue(id, out var tracked) || tracked.Decision != ReturnDecision.Pending)
            {
                return;
            }

            var total = 0;
            foreach (var item in tracked.Items)
            {
                var accepted = acceptedPerOffer.TryGetValue(item.OfferId, out var qty) ? Math.Clamp(qty, 0, item.Quantity) : 0;
                item.Accepted = accepted;
                var pieces = accepted * item.Pack;
                if (pieces > 0)
                {
                    OwnStock.Adjust(item.Ean, pieces, StockReason.Return, tracked.OrderId, $"customer return {tracked.Reference}".Trim());
                    total += pieces;
                }
            }

            tracked.Decision = total > 0 ? ReturnDecision.Accepted : ReturnDecision.Rejected;
            tracked.DecidedAt = DateTime.Now;
            SaverExtensions.Returns.Value = all;
            SaverExtensions.Returns.Write();
            log?.Invoke($"Customer return {tracked.Reference}: {total} pcs put on own stock.");
        }
    }

    public static void Reject(string id)
    {
        lock (Gate)
        {
            var all = SaverExtensions.Returns.Read();
            if (!all.TryGetValue(id, out var tracked) || tracked.Decision != ReturnDecision.Pending)
            {
                return;
            }
            foreach (var item in tracked.Items)
            {
                item.Accepted = 0;
            }
            tracked.Decision = ReturnDecision.Rejected;
            tracked.DecidedAt = DateTime.Now;
            SaverExtensions.Returns.Value = all;
            SaverExtensions.Returns.Write();
        }
    }
}
