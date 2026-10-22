using System.Globalization;
using System.Text.Json.Nodes;

namespace Allegro.Core;

public class TrackedOrder
{
    public string Id { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public string Buyer { get; set; } = "";
    public string Status { get; set; } = OrderStatus.Open;
    public List<OrderLine> Lines { get; set; } = new();
    public string Note { get; set; } = "";
}

public class OrderLine
{
    public string Ean { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Packs { get; set; }
    public int Pack { get; set; } = 1;
    public int Need { get; set; }
    public int FromOwn { get; set; }
    public int ToBuy { get; set; }
    public DateTime? PurchasedAt { get; set; }
}

public static class OrderStatus
{
    public const string Open = "open";
    public const string Purchased = "purchased";
    public const string Cancelled = "cancelled";
}

public class OrderSyncState
{
    public string LastEventId { get; set; } = "";
    public DateTime LastSyncAt { get; set; } = DateTime.MinValue;
    public string LastError { get; set; } = "";
}

public record PurchaseRow(
    string Ean,
    string Name,
    ProductInfo? Product,
    int Need,
    int Moq,
    int Buy,
    int Leftover,
    decimal UnitPrice,
    List<string> OrderIds,
    bool SupplierHas)
{
    public decimal Total => UnitPrice * Buy;
}

public sealed class OrderSync
{
    private static readonly object Gate = new();
    private const int PageSize = 1000;

    private readonly AllegroPublisher _publisher;

    public OrderSync(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    public static OrderSyncState State() => SaverExtensions.OrderSync.Read();

    public static Dictionary<string, TrackedOrder> Orders()
    {
        lock (Gate)
        {
            return new Dictionary<string, TrackedOrder>(SaverExtensions.Orders.Read());
        }
    }

    public async Task<int> SyncAsync(Action<string>? log = null)
    {
        var state = SaverExtensions.OrderSync.Read();
        try
        {
            if (string.IsNullOrEmpty(state.LastEventId))
            {
                var (statsStatus, statsBody) = await _publisher.GetRawAsync("/order/event-stats");
                if (statsStatus is < 200 or >= 300)
                {
                    throw new InvalidOperationException($"could not read order events ({statsStatus}): {Trim(statsBody)}");
                }
                state.LastEventId = JsonNode.Parse(statsBody)?["latestEvent"]?["id"]?.ToString() ?? "";
                state.LastSyncAt = DateTime.Now;
                state.LastError = "";
                Save(state);
                log?.Invoke("Order sync started: orders placed from now on are tracked, earlier ones are not.");
                return 0;
            }

            var handled = 0;
            while (true)
            {
                var (status, body) = await _publisher.GetRawAsync($"/order/events?from={Uri.EscapeDataString(state.LastEventId)}&limit={PageSize}");
                if (status is < 200 or >= 300)
                {
                    throw new InvalidOperationException($"could not read order events ({status}): {Trim(body)}");
                }

                var events = JsonNode.Parse(body)?["events"]?.AsArray() ?? new JsonArray();
                if (events.Count == 0)
                {
                    break;
                }

                foreach (var e in events)
                {
                    var type = e?["type"]?.ToString() ?? "";
                    var checkoutId = e?["order"]?["checkoutForm"]?["id"]?.ToString() ?? "";
                    var eventId = e?["id"]?.ToString() ?? state.LastEventId;
                    if (checkoutId.Length > 0)
                    {
                        handled += await HandleEventAsync(type, checkoutId, log) ? 1 : 0;
                    }
                    state.LastEventId = eventId;
                    state.LastSyncAt = DateTime.Now;
                    state.LastError = "";
                    Save(state);
                }

                if (events.Count < PageSize)
                {
                    break;
                }
            }

            return handled;
        }
        catch (Exception e)
        {
            state.LastError = e.Message;
            state.LastSyncAt = DateTime.Now;
            Save(state);
            throw;
        }
    }

    private async Task<bool> HandleEventAsync(string type, string checkoutId, Action<string>? log)
    {
        switch (type)
        {
            case "READY_FOR_PROCESSING":
                return await ProcessAsync(checkoutId, log);
            case "BUYER_CANCELLED":
            case "AUTO_CANCELLED":
                return Cancel(checkoutId, type.ToLowerInvariant().Replace('_', ' '), log);
            case "FULFILLMENT_STATUS_CHANGED":
                var form = await ReadFormAsync(checkoutId);
                var fulfillment = form?["fulfillment"]?["status"]?.ToString() ?? "";
                return fulfillment.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) && Cancel(checkoutId, "cancelled by the seller", log);
            default:
                return false;
        }
    }

    private async Task<JsonNode?> ReadFormAsync(string checkoutId)
    {
        var (status, body) = await _publisher.GetRawAsync($"/order/checkout-forms/{Uri.EscapeDataString(checkoutId)}");
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"could not read order {checkoutId} ({status}): {Trim(body)}");
        }
        return JsonNode.Parse(body);
    }

    private async Task<bool> ProcessAsync(string checkoutId, Action<string>? log)
    {
        lock (Gate)
        {
            if (SaverExtensions.Orders.Read().ContainsKey(checkoutId))
            {
                return false;
            }
        }

        var form = await ReadFormAsync(checkoutId);
        if (form is null)
        {
            return false;
        }
        if ((form["status"]?.ToString() ?? "").Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var bundles = SaverExtensions.Bundles.Read();
        var order = new TrackedOrder
        {
            Id = checkoutId,
            OccurredAt = DateTime.TryParse(form["updatedAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at)
                ? at.ToLocalTime() : DateTime.Now,
            Buyer = form["buyer"]?["login"]?.ToString() ?? "",
        };

        lock (Gate)
        {
            var orders = SaverExtensions.Orders.Read();
            if (orders.ContainsKey(checkoutId))
            {
                return false;
            }

            var own = OwnStock.Items();
            foreach (var item in form["lineItems"]?.AsArray() ?? new JsonArray())
            {
                var ean = item?["offer"]?["external"]?["id"]?.ToString()?.Trim() ?? "";
                if (ean.Length == 0)
                {
                    continue;
                }

                var packs = item?["quantity"]?.GetValue<int>() ?? 0;
                var pack = bundles.TryGetValue(ean, out var listed) && listed > 1 ? listed : 1;
                var need = packs * pack;
                var available = own.TryGetValue(ean, out var stock) ? stock.Quantity : 0;
                var fromOwn = Math.Min(need, available);
                var line = new OrderLine
                {
                    Ean = ean,
                    OfferId = item?["offer"]?["id"]?.ToString() ?? "",
                    Name = item?["offer"]?["name"]?.ToString() ?? "",
                    Packs = packs,
                    Pack = pack,
                    Need = need,
                    FromOwn = fromOwn,
                    ToBuy = need - fromOwn,
                };
                if (fromOwn > 0)
                {
                    var left = OwnStock.Adjust(ean, -fromOwn, StockReason.Order, checkoutId, $"{line.Name}".Trim());
                    own[ean] = left;
                }
                order.Lines.Add(line);
            }

            if (order.Lines.Count == 0)
            {
                order.Note = "no line with our external id";
            }
            orders[checkoutId] = order;
            SaverExtensions.Orders.Value = orders;
            SaverExtensions.Orders.Write();
        }

        log?.Invoke($"Order {checkoutId} ({order.Buyer}): {order.Lines.Count} of our lines, " +
                    $"{order.Lines.Sum(l => l.FromOwn)} pcs taken from own stock, {order.Lines.Sum(l => l.ToBuy)} pcs to buy.");
        return true;
    }

    private static bool Cancel(string checkoutId, string why, Action<string>? log)
    {
        lock (Gate)
        {
            var orders = SaverExtensions.Orders.Read();
            if (!orders.TryGetValue(checkoutId, out var order) || order.Status == OrderStatus.Cancelled)
            {
                return false;
            }

            var returned = 0;
            foreach (var line in order.Lines)
            {
                var back = line.PurchasedAt is null ? line.FromOwn : line.Need;
                if (back > 0)
                {
                    OwnStock.Adjust(line.Ean, back, StockReason.Return, checkoutId,
                        line.PurchasedAt is null ? $"order {why}" : $"order {why} after purchase");
                    returned += back;
                }
            }

            order.Status = OrderStatus.Cancelled;
            order.Note = why;
            SaverExtensions.Orders.Value = orders;
            SaverExtensions.Orders.Write();
            log?.Invoke($"Order {checkoutId} {why}: {returned} pcs returned to own stock.");
            return true;
        }
    }

    public static List<PurchaseRow> PurchaseList()
    {
        var options = SaverExtensions.ListingOptions.Read();
        var products = StockPlanner.ProductsByEan(SaverExtensions.Products.Read().Values);
        var own = OwnStock.Items();
        var rows = new Dictionary<string, (int Need, List<string> Orders, string Name)>(StringComparer.OrdinalIgnoreCase);

        foreach (var order in Orders().Values.Where(o => o.Status == OrderStatus.Open).OrderBy(o => o.OccurredAt))
        {
            foreach (var line in order.Lines.Where(l => l.ToBuy > 0 && l.PurchasedAt is null))
            {
                var entry = rows.TryGetValue(line.Ean, out var found) ? found : (Need: 0, Orders: new List<string>(), Name: line.Name);
                entry.Need += line.ToBuy;
                entry.Orders.Add(order.Id);
                rows[line.Ean] = entry;
            }
        }

        var result = new List<PurchaseRow>();
        foreach (var (ean, entry) in rows)
        {
            products.TryGetValue(ean, out var product);
            var moq = Math.Max(product?.MinOrderQuantity ?? 1, 1);
            var buy = (entry.Need + moq - 1) / moq * moq;
            var price = product?.Price > 0 ? product.Price : own.TryGetValue(ean, out var stock) ? stock.UnitCost ?? 0m : 0m;
            result.Add(new PurchaseRow(ean, product?.Name ?? entry.Name, product, entry.Need, moq, buy, buy - entry.Need, price,
                entry.Orders.Distinct().ToList(), product is not null && StockPlanner.SupplierHas(product, options)));
        }
        return result.OrderBy(r => r.Name).ToList();
    }

    public record Bought(string Ean, int Quantity, decimal? UnitCost);

    public static int ConfirmPurchase(IEnumerable<Bought> bought, Action<string>? log = null)
    {
        var done = 0;
        lock (Gate)
        {
            var orders = SaverExtensions.Orders.Read();
            var now = DateTime.Now;
            foreach (var item in bought)
            {
                if (item.Quantity <= 0)
                {
                    continue;
                }

                var lines = orders.Values.Where(o => o.Status == OrderStatus.Open)
                    .SelectMany(o => o.Lines.Where(l => l.Ean.Equals(item.Ean, StringComparison.OrdinalIgnoreCase) && l.ToBuy > 0 && l.PurchasedAt is null))
                    .ToList();
                var need = lines.Sum(l => l.ToBuy);
                if (need == 0)
                {
                    continue;
                }

                OwnStock.Adjust(item.Ean, item.Quantity - need, StockReason.Purchase, "", $"bought {item.Quantity}, needed {need}", item.UnitCost);
                foreach (var line in lines)
                {
                    line.PurchasedAt = now;
                }
                done++;
                log?.Invoke($"{item.Ean}: bought {item.Quantity}, {need} go to orders, {item.Quantity - need:+#;-#;0} to own stock.");
            }

            foreach (var order in orders.Values.Where(o => o.Status == OrderStatus.Open))
            {
                if (order.Lines.All(l => l.ToBuy == 0 || l.PurchasedAt is not null))
                {
                    order.Status = OrderStatus.Purchased;
                }
            }
            SaverExtensions.Orders.Value = orders;
            SaverExtensions.Orders.Write();
        }
        return done;
    }

    public static DateTime NextTrip(DateTime from)
    {
        var day = from.Date;
        while (day.DayOfWeek is not (DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday) || (day == from.Date && from.Hour >= 12))
        {
            day = day.AddDays(1);
        }
        return day;
    }

    private static void Save(OrderSyncState state)
    {
        SaverExtensions.OrderSync.Value = state;
        SaverExtensions.OrderSync.Write();
    }

    private static string Trim(string body) => body.Length > 300 ? body[..300] : body;
}
