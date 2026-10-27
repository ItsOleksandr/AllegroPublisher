using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

public sealed class AllegroMessaging
{
    private readonly AllegroPublisher _publisher;

    public AllegroMessaging(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    public async Task<JsonArray> ListThreadsAsync(int limit = 20, int offset = 0) =>
        (await GetAsync($"/messaging/threads?limit={limit}&offset={offset}"))?["threads"]?.AsArray() ?? new JsonArray();

    public async Task<JsonArray> ListMessagesAsync(string threadId, int limit = 20) =>
        (await GetAsync($"/messaging/threads/{Uri.EscapeDataString(threadId)}/messages?limit={limit}&offset=0"))?["messages"]?.AsArray() ?? new JsonArray();

    public async Task PostMessageAsync(string threadId, string text)
    {
        var body = JsonSerializer.Serialize(new { text, attachments = Array.Empty<object>() });
        var (status, response) = await _publisher.SendJsonRawAsync(HttpMethod.Post, $"/messaging/threads/{Uri.EscapeDataString(threadId)}/messages", body);
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"could not send the message ({status}): {Trim(response)}");
        }
    }

    public async Task MarkReadAsync(string threadId)
    {
        var (status, response) = await _publisher.SendJsonRawAsync(HttpMethod.Put, $"/messaging/threads/{Uri.EscapeDataString(threadId)}/read", JsonSerializer.Serialize(new { read = true }));
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"could not mark the thread as read ({status}): {Trim(response)}");
        }
    }

    public async Task<JsonArray> ListIssuesAsync(int limit = 100) =>
        (await GetAsync($"/sale/issues?limit={limit}&offset=0", AllegroPublisher.BetaMediaType))?["issues"]?.AsArray() ?? new JsonArray();

    public async Task<JsonArray> IssueChatAsync(string issueId) =>
        (await GetAsync($"/sale/issues/{Uri.EscapeDataString(issueId)}/chat", AllegroPublisher.BetaMediaType))?["messages"]?.AsArray() ?? new JsonArray();

    public async Task PostIssueMessageAsync(string issueId, string text)
    {
        var (status, response) = await _publisher.SendJsonRawAsync(HttpMethod.Post, $"/sale/issues/{Uri.EscapeDataString(issueId)}/message",
            JsonSerializer.Serialize(new { text }), AllegroPublisher.BetaMediaType);
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"could not answer the issue ({status}): {Trim(response)}");
        }
    }

    public async Task<JsonArray> BuyerOrdersAsync(string login, int limit = 10) =>
        string.IsNullOrWhiteSpace(login)
            ? new JsonArray()
            : (await GetAsync($"/order/checkout-forms?buyer.login={Uri.EscapeDataString(login)}&limit={limit}"))?["checkoutForms"]?.AsArray() ?? new JsonArray();

    public Task<JsonNode?> OrderAsync(string checkoutId) => TryGetAsync($"/order/checkout-forms/{Uri.EscapeDataString(checkoutId)}");

    public async Task<JsonArray> ShipmentsAsync(string checkoutId) =>
        (await TryGetAsync($"/order/checkout-forms/{Uri.EscapeDataString(checkoutId)}/shipments"))?["shipments"]?.AsArray() ?? new JsonArray();

    public Task<JsonNode?> OfferAsync(string offerId) => TryGetAsync($"/sale/product-offers/{Uri.EscapeDataString(offerId)}");

    private async Task<JsonNode?> GetAsync(string path, string? accept = null)
    {
        var (status, body) = await _publisher.GetRawAsync(path, accept);
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"GET {path.Split('?')[0]} failed ({status}): {Trim(body)}");
        }
        return JsonNode.Parse(body);
    }

    private async Task<JsonNode?> TryGetAsync(string path)
    {
        var (status, body) = await _publisher.GetRawAsync(path);
        return status is >= 200 and < 300 ? JsonNode.Parse(body) : null;
    }

    private static string Trim(string body) => body.Length > 300 ? body[..300] : body;
}
