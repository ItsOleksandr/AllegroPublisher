using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Allegro.Core;

public class BaseLinkerSettings
{
    public bool Enabled { get; set; } = false;
    public string Token { get; set; } = "";
    public string NoteField { get; set; } = "admin_comments";
    public int ShipStatusId { get; set; } = 0;
    public string ShipStatusName { get; set; } = "";
    public int BuyStatusId { get; set; } = 0;
    public string BuyStatusName { get; set; } = "";
    public DateTime LastSyncAt { get; set; } = DateTime.MinValue;
    public string LastError { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);
}

public record BaseLinkerStatus(int Id, string Name);

public sealed class BaseLinkerClient
{
    private const string Endpoint = "https://api.baselinker.com/connector.php";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _token;

    public BaseLinkerClient(string token)
    {
        _token = token;
    }

    public async Task<JsonNode> CallAsync(string method, object parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["method"] = method,
                ["parameters"] = JsonSerializer.Serialize(parameters),
            }),
        };
        request.Headers.Add("X-BLToken", _token);

        var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"BaseLinker {method} failed ({(int)response.StatusCode}): {Trim(body)}");
        }

        var node = JsonNode.Parse(body) ?? throw new InvalidOperationException($"BaseLinker {method} returned nothing");
        if (!string.Equals(node["status"]?.ToString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"BaseLinker {method}: {node["error_code"]?.ToString()} {node["error_message"]?.ToString()}".Trim());
        }
        return node;
    }

    public async Task<List<BaseLinkerStatus>> GetStatusesAsync()
    {
        var node = await CallAsync("getOrderStatusList", new { });
        return (node["statuses"]?.AsArray() ?? new JsonArray())
            .Select(s => new BaseLinkerStatus(s?["id"]?.GetValue<int>() ?? 0, s?["name"]?.ToString() ?? ""))
            .Where(s => s.Id > 0)
            .ToList();
    }

    private static string Trim(string body) => body.Length > 300 ? body[..300] : body;
}

public sealed class BaseLinkerSync
{
    private static readonly object Gate = new();
    private const int PageSize = 100;

    public static BaseLinkerSettings Settings() => SaverExtensions.BaseLinker.Read();

    public static void Save(BaseLinkerSettings settings)
    {
        SaverExtensions.BaseLinker.Value = settings;
        SaverExtensions.BaseLinker.Write();
    }

    public async Task<int> SyncAsync(Action<string>? log = null)
    {
        var settings = Settings();
        if (!settings.Enabled || !settings.IsConfigured)
        {
            return 0;
        }

        try
        {
            var pending = OrderSync.Orders().Values
                .Where(o => o.Status != OrderStatus.Cancelled && o.BaseLinkerMarkedAt is null && o.Lines.Count > 0)
                .OrderBy(o => o.OccurredAt)
                .ToList();
            if (pending.Count == 0)
            {
                settings.LastSyncAt = DateTime.Now;
                settings.LastError = "";
                Save(settings);
                return 0;
            }

            var client = new BaseLinkerClient(settings.Token);
            var since = pending.Min(o => o.OccurredAt).AddDays(-1);
            var byExternal = await LoadOrdersAsync(client, since);
            log?.Invoke($"BaseLinker: {byExternal.Count} orders since {since:yyyy-MM-dd}, {pending.Count} of ours wait for a note.");

            var products = StockPlanner.ProductsByEan(SaverExtensions.Products.Read().Values);
            var marked = 0;
            foreach (var order in pending)
            {
                if (!byExternal.TryGetValue(order.Id, out var blOrder))
                {
                    continue;
                }

                var blOrderId = blOrder["order_id"]?.GetValue<int>() ?? 0;
                var note = BuildNote(order, products);
                var covered = order.Lines.All(l => l.ToBuy == 0);

                await client.CallAsync("setOrderFields", new Dictionary<string, object>
                {
                    ["order_id"] = blOrderId,
                    [settings.NoteField] = MergeNote(blOrder[settings.NoteField]?.ToString() ?? "", note),
                });

                var statusChanged = false;
                if (covered && settings.ShipStatusId > 0 && (blOrder["order_status_id"]?.GetValue<int>() ?? 0) != settings.ShipStatusId)
                {
                    await client.CallAsync("setOrderStatus", new { order_id = blOrderId, status_id = settings.ShipStatusId });
                    statusChanged = true;
                }

                lock (Gate)
                {
                    var orders = SaverExtensions.Orders.Read();
                    if (orders.TryGetValue(order.Id, out var stored))
                    {
                        stored.BaseLinkerOrderId = blOrderId.ToString();
                        stored.BaseLinkerMarkedAt = DateTime.Now;
                        stored.BaseLinkerNote = note + (statusChanged ? $" → {settings.ShipStatusName}" : "");
                        SaverExtensions.Orders.Value = orders;
                        SaverExtensions.Orders.Write();
                    }
                }

                marked++;
                log?.Invoke($"BaseLinker order {blOrderId} (Allegro {order.Id[..8]}…): {note}{(statusChanged ? $" - status set to {settings.ShipStatusName}" : "")}");
            }

            settings.LastSyncAt = DateTime.Now;
            settings.LastError = "";
            Save(settings);
            return marked;
        }
        catch (Exception e)
        {
            settings.LastSyncAt = DateTime.Now;
            settings.LastError = e.Message;
            Save(settings);
            throw;
        }
    }

    private static async Task<Dictionary<string, JsonNode>> LoadOrdersAsync(BaseLinkerClient client, DateTime since)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var dateFrom = new DateTimeOffset(since).ToUnixTimeSeconds();
        var idFrom = 0;
        while (true)
        {
            var parameters = new Dictionary<string, object>
            {
                ["date_from"] = dateFrom,
                ["get_unconfirmed_orders"] = true,
            };
            if (idFrom > 0)
            {
                parameters["id_from"] = idFrom;
            }

            var node = await client.CallAsync("getOrders", parameters);
            var orders = node["orders"]?.AsArray() ?? new JsonArray();
            foreach (var order in orders)
            {
                var external = order?["external_order_id"]?.ToString() ?? "";
                if (external.Length > 0 && order is not null)
                {
                    result[external] = order;
                }
                var id = order?["order_id"]?.GetValue<int>() ?? 0;
                idFrom = Math.Max(idFrom, id + 1);
            }

            if (orders.Count < PageSize)
            {
                break;
            }
        }
        return result;
    }

    public static string BuildNote(TrackedOrder order, Dictionary<string, ProductInfo> products)
    {
        var fromStock = new List<string>();
        var toBuy = new List<string>();
        foreach (var line in order.Lines)
        {
            products.TryGetValue(line.Ean, out var product);
            var code = Code(product?.Name ?? line.Name, line.Ean);
            if (line.FromOwn > 0)
            {
                fromStock.Add($"{code} ×{line.FromOwn}");
            }
            if (line.ToBuy > 0)
            {
                var moq = Math.Max(product?.MinOrderQuantity ?? 1, 1);
                var buy = (line.ToBuy + moq - 1) / moq * moq;
                toBuy.Add(moq > 1 ? $"{code} ×{buy} (allenett, min {moq})" : $"{code} ×{buy} (allenett)");
            }
        }

        var parts = new List<string>();
        if (fromStock.Count > 0)
        {
            parts.Add("MAGAZYN: " + string.Join(", ", fromStock));
        }
        if (toBuy.Count > 0)
        {
            parts.Add("KUPIĆ: " + string.Join(", ", toBuy));
        }
        return string.Join(" | ", parts);
    }

    private static string MergeNote(string existing, string note)
    {
        var kept = Regex.Replace(existing, @"\s*(MAGAZYN|KUPIĆ):.*$", "", RegexOptions.Singleline).Trim();
        return kept.Length == 0 ? note : $"{kept}\n{note}";
    }

    private static string Code(string name, string ean)
    {
        var last = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return Regex.IsMatch(last, @"^[A-Za-z]{1,3}\d{2,6}[A-Za-z]?$") ? last.ToUpperInvariant() : ean;
    }
}
