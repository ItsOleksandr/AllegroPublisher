using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Allegro.Core;

public sealed class AutoReplyEngine
{
    private const int MaxMessageAgeHours = 48;
    private readonly AllegroMessaging _api;
    private AiClient? _ai;

    public AutoReplyEngine(AllegroPublisher publisher)
    {
        _api = new AllegroMessaging(publisher);
    }

    public record RunResult(int Incoming, int Auto, int Drafts, int Acks, List<string> Errors);

    private sealed class Counters
    {
        public int Incoming, Auto, Drafts, Acks;
    }

    private sealed class ReplyContext
    {
        public string OfferName = "";
        public int? OfferStock;
        public decimal? OfferPrice;
        public int HandlingDays = 2;
        public string Description = "";
        public string Parameters = "";
        public string OrderId = "";
        public string OrderStatus = "";
        public DateTime? BoughtAt;
        public DateTime? PaidAt;
        public bool Shipped;
        public string Waybill = "";
        public string Carrier = "";
        public DateTime? ShippedAt;
        public decimal? OrderTotal;
        public string OrderItems = "";
        public List<string> AllowedNumbers = new();
        public List<decimal> AllowedAmounts = new();

        public bool HasOffer => OfferName.Length > 0;
        public bool HasOrder => OrderId.Length > 0;

        public string Summary()
        {
            var parts = new List<string>();
            if (HasOffer)
            {
                parts.Add($"offer: {OfferName}, stock {OfferStock?.ToString() ?? "?"}, ships in {HandlingDays} day(s)");
            }
            if (HasOrder)
            {
                parts.Add($"order {OrderId[..Math.Min(8, OrderId.Length)]}…: {OrderStatus}" +
                          (Shipped ? $", sent {ShippedAt:dd.MM} {Carrier} {Waybill}" : ", not sent yet"));
            }
            return string.Join(" · ", parts);
        }

        public string ShipDaysText = "";

        public string ForAi()
        {
            var sb = new System.Text.StringBuilder();
            if (ShipDaysText.Length > 0)
            {
                sb.AppendLine($"DNI WYSYŁKI: paczki nadajemy w {ShipDaysText} (po zaksięgowaniu wpłaty, w najbliższy taki dzień).");
            }
            if (HasOffer)
            {
                sb.AppendLine($"OFERTA: {OfferName}");
                sb.AppendLine($"Dostępna ilość: {OfferStock?.ToString() ?? "brak danych"}");
                sb.AppendLine($"Cena: {OfferPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? "brak danych"} zł");
                sb.AppendLine($"Czas wysyłki: {HandlingDays} dni roboczych");
                if (Parameters.Length > 0)
                {
                    sb.AppendLine($"Parametry: {Parameters}");
                }
                if (Description.Length > 0)
                {
                    sb.AppendLine($"Opis: {Description}");
                }
            }
            else
            {
                sb.AppendLine("OFERTA: wiadomość nie dotyczy konkretnej oferty");
            }
            if (HasOrder)
            {
                sb.AppendLine($"ZAMÓWIENIE: {OrderId}, status {OrderStatus}, pozycje: {OrderItems}");
                sb.AppendLine($"Kupione: {BoughtAt:yyyy-MM-dd HH:mm}, opłacone: {(PaidAt is null ? "nie" : PaidAt.Value.ToString("yyyy-MM-dd HH:mm"))}");
                sb.AppendLine(Shipped
                    ? $"Wysłane: {ShippedAt:yyyy-MM-dd}, przewoźnik {Carrier}, numer {Waybill}"
                    : "Wysłane: jeszcze nie");
            }
            else
            {
                sb.AppendLine("ZAMÓWIENIE: brak zamówienia tego kupującego");
            }
            return sb.ToString();
        }
    }

    public async Task<RunResult> RunAsync(Action<string>? log = null)
    {
        var settings = AutoReplyStore.Settings();
        var counters = new Counters();
        var errors = new List<string>();
        if (!settings.Enabled)
        {
            return new RunResult(0, 0, 0, 0, errors);
        }

        try
        {
            await ScanThreadsAsync(settings, counters, log);
        }
        catch (Exception e)
        {
            errors.Add(Hint("messages", e.Message));
        }

        if (settings.HandleIssues)
        {
            try
            {
                await ScanIssuesAsync(settings, counters, log);
            }
            catch (Exception e)
            {
                errors.Add(Hint("issues", e.Message));
            }
        }

        try
        {
            await RemindAsync(settings, log);
        }
        catch (Exception e)
        {
            errors.Add($"reminders: {e.Message}");
        }

        settings = AutoReplyStore.Settings();
        settings.LastRunAt = DateTime.Now;
        settings.LastError = string.Join(" · ", errors);
        AutoReplyStore.Save(settings);
        return new RunResult(counters.Incoming, counters.Auto, counters.Drafts, counters.Acks, errors);
    }

    private static string Hint(string what, string message) =>
        message.Contains("403") || message.Contains("401")
            ? $"{what}: {message} - the Allegro app needs the messaging scope (allegro:api:messaging) and, for issues, allegro:api:disputes; reconnect the account after enabling them"
            : $"{what}: {message}";

    private async Task ScanThreadsAsync(AutoReplySettings settings, Counters counters, Action<string>? log)
    {
        var threads = new List<JsonNode?>();
        for (int offset = 0; offset < 60; offset += 20)
        {
            var page = await _api.ListThreadsAsync(20, offset);
            threads.AddRange(page);
            if (page.Count < 20)
            {
                break;
            }
        }
        var cutoff = DateTime.Now.AddHours(-MaxMessageAgeHours);
        foreach (var node in threads)
        {
            var id = node?["id"]?.ToString() ?? "";
            if (id.Length == 0)
            {
                continue;
            }
            var lastAt = ParseDate(node?["lastMessageDateTime"]?.ToString()) ?? DateTime.Now;
            var tracked = AutoReplyStore.Get(id);
            if (tracked is not null && lastAt <= tracked.SeenAt)
            {
                continue;
            }
            if (tracked is null && lastAt < cutoff)
            {
                AutoReplyStore.Put(new MessageThread { Id = id, Kind = "thread", SeenAt = lastAt, Buyer = node?["interlocutor"]?["login"]?.ToString() ?? "", Status = ThreadStatus.Rejected, AnsweredAt = lastAt });
                continue;
            }

            var messages = await _api.ListMessagesAsync(id, 10);
            var latest = messages
                .Select(m => (Node: m, At: ParseDate(m?["createdAt"]?.ToString()) ?? DateTime.MinValue))
                .OrderByDescending(m => m.At)
                .FirstOrDefault();
            var thread = tracked ?? new MessageThread { Id = id, Kind = "thread" };
            thread.SeenAt = lastAt;
            thread.Buyer = node?["interlocutor"]?["login"]?.ToString() ?? thread.Buyer;

            if (latest.Node is null || latest.Node["author"]?["isInterlocutor"]?.GetValue<bool>() != true)
            {
                AutoReplyStore.Put(thread);
                continue;
            }

            var messageId = latest.Node["id"]?.ToString() ?? "";
            if (messageId == thread.LastBuyerMessageId)
            {
                AutoReplyStore.Put(thread);
                continue;
            }

            thread.LastBuyerMessageId = messageId;
            thread.BuyerText = latest.Node["text"]?.ToString() ?? "";
            thread.Subject = latest.Node["subject"]?.ToString() ?? thread.Subject;
            thread.OfferId = latest.Node["relatesTo"]?["offer"]?["id"]?.ToString() ?? thread.OfferId;
            thread.OrderId = latest.Node["relatesTo"]?["order"]?["id"]?.ToString() ?? thread.OrderId;
            thread.ReceivedAt = latest.At;
            thread.AnsweredAt = null;
            thread.Status = ThreadStatus.New;
            thread.Draft = "";
            thread.DraftReason = "";
            thread.Error = "";
            thread.Reminder18At = null;
            thread.Reminder22At = null;
            counters.Incoming++;
            AutoReplyStore.Log(thread, "in", "buyer", thread.BuyerText);
            log?.Invoke($"New message from {thread.Buyer} ({id[..Math.Min(8, id.Length)]}…): {Shorten(thread.BuyerText, 80)}");

            await ProcessAsync(thread, settings, counters, log);
        }
    }

    private async Task ScanIssuesAsync(AutoReplySettings settings, Counters counters, Action<string>? log)
    {
        var issues = await _api.ListIssuesAsync(100);
        foreach (var node in issues)
        {
            var id = node?["id"]?.ToString() ?? "";
            var status = node?["status"]?.ToString() ?? "";
            if (id.Length == 0 || status.Contains("CLOSED", StringComparison.OrdinalIgnoreCase) || status.Contains("ENDED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tracked = AutoReplyStore.Get("issue:" + id);
            var issueAt = ParseDate(node?["updatedAt"]?.ToString() ?? node?["createdAt"]?.ToString());
            if (tracked is null && issueAt is not null && issueAt < DateTime.Now.AddHours(-MaxMessageAgeHours))
            {
                continue;
            }
            var chat = await _api.IssueChatAsync(id);
            var latest = chat
                .Where(m => (m?["author"]?["role"]?.ToString() ?? "").Equals("BUYER", StringComparison.OrdinalIgnoreCase))
                .Select(m => (Node: m, At: ParseDate(m?["createdAt"]?.ToString()) ?? DateTime.MinValue))
                .OrderByDescending(m => m.At)
                .FirstOrDefault();
            if (latest.Node is null)
            {
                continue;
            }

            var messageId = latest.Node["id"]?.ToString() ?? latest.At.ToString("O");
            if (tracked is not null && tracked.LastBuyerMessageId == messageId)
            {
                continue;
            }

            var thread = tracked ?? new MessageThread { Id = "issue:" + id, Kind = "issue" };
            thread.SeenAt = DateTime.Now;
            thread.Buyer = node?["buyer"]?["login"]?.ToString() ?? latest.Node["author"]?["login"]?.ToString() ?? thread.Buyer;
            thread.Subject = $"{node?["type"]?.ToString()} {node?["subject"]?.ToString()}".Trim();
            thread.OrderId = node?["order"]?["id"]?.ToString() ?? node?["checkoutForm"]?["id"]?.ToString() ?? thread.OrderId;
            thread.LastBuyerMessageId = messageId;
            thread.BuyerText = latest.Node["text"]?.ToString() ?? latest.Node["message"]?.ToString() ?? "";
            thread.ReceivedAt = latest.At == DateTime.MinValue ? DateTime.Now : latest.At;
            thread.AnsweredAt = null;
            thread.Status = ThreadStatus.New;
            thread.Draft = "";
            thread.DraftReason = "";
            thread.Error = "";
            thread.Reminder18At = null;
            thread.Reminder22At = null;
            counters.Incoming++;
            AutoReplyStore.Log(thread, "in", "buyer", thread.BuyerText);
            log?.Invoke($"New issue message from {thread.Buyer} ({id}): {Shorten(thread.BuyerText, 80)}");

            await ProcessAsync(thread, settings, counters, log);
        }
    }

    private async Task ProcessAsync(MessageThread thread, AutoReplySettings settings, Counters counters, Action<string>? log)
    {
        var now = DateTime.Now;
        ReplyContext ctx;
        try
        {
            ctx = await BuildContextAsync(thread);
            ctx.ShipDaysText = settings.ShipDaysPolish();
            thread.OfferName = ctx.OfferName;
            if (thread.OrderId.Length == 0)
            {
                thread.OrderId = ctx.OrderId;
            }
            thread.Context = ctx.Summary();
        }
        catch (Exception e)
        {
            ctx = new ReplyContext();
            thread.Error = $"context: {e.Message}";
        }

        try
        {
            var (topic, confidence) = thread.IsIssue ? (MessageTopic.Other, 1.0) : await ClassifyAsync(thread, ctx, settings);
            thread.Topic = topic;
            thread.Confidence = confidence;
        }
        catch (Exception e)
        {
            thread.Topic = MessageTopic.Other;
            thread.Confidence = 0;
            thread.Error = AppendError(thread.Error, $"classifier: {e.Message}");
        }

        string? answer = null;
        string reason = "";
        if (!thread.IsIssue && settings.Level2 && TopicEnabled(settings, thread.Topic) && thread.Confidence >= settings.ConfidenceThreshold)
        {
            try
            {
                (answer, reason) = await ComposeAsync(thread, ctx, settings);
            }
            catch (Exception e)
            {
                reason = $"compose: {e.Message}";
            }
            if (answer is not null)
            {
                var problem = ReplyFilter.Problem(answer, ctx.AllowedNumbers, ctx.AllowedAmounts);
                if (problem is not null)
                {
                    reason = $"blocked by the filter: {problem}";
                    thread.Draft = answer;
                    answer = null;
                }
            }
        }
        else if (!thread.IsIssue)
        {
            reason = !settings.Level2 ? "level 2 is off"
                : !TopicEnabled(settings, thread.Topic) ? $"topic '{thread.Topic}' is not answered automatically"
                : $"confidence {thread.Confidence:0.00} below {settings.ConfidenceThreshold:0.00}";
        }

        if (answer is not null && !settings.InTestMode())
        {
            if (await TrySendAsync(thread, answer, settings, 2, "auto", log))
            {
                thread.AnsweredAt = DateTime.Now;
                thread.Status = ThreadStatus.Auto;
                thread.Level = 2;
                counters.Auto++;
                try
                {
                    await _api.MarkReadAsync(thread.Id);
                }
                catch (Exception)
                {
                }
                AutoReplyStore.Put(thread);
                return;
            }
        }

        thread.Level = 3;
        if (answer is not null)
        {
            thread.Draft = answer;
            thread.DraftReason = "test mode: the automatic answer waits for your approval";
        }
        else if (settings.Level3)
        {
            if (thread.Draft.Length == 0)
            {
                try
                {
                    thread.Draft = await DraftAsync(thread, ctx, settings);
                    var problem = ReplyFilter.Problem(thread.Draft, ctx.AllowedNumbers, ctx.AllowedAmounts);
                    thread.DraftReason = problem is null ? reason : $"{reason}; the draft itself was flagged: {problem}";
                }
                catch (Exception e)
                {
                    thread.DraftReason = reason;
                    thread.Error = AppendError(thread.Error, $"draft: {e.Message}");
                }
            }
            else
            {
                thread.DraftReason = reason;
            }
        }
        else
        {
            thread.DraftReason = reason;
        }
        thread.Status = ThreadStatus.Draft;
        counters.Drafts++;

        if (settings.Level1 && (thread.AckSentAt is null || thread.AckSentAt < now.AddHours(-24)))
        {
            var deadline = WorkingHours.Deadline(settings, thread.ReceivedAt);
            var template = thread.IsIssue ? settings.IssueAckTemplate : settings.AckTemplate;
            var ack = template.Replace("{termin}", WorkingHours.Describe(deadline, now));
            if (await TrySendAsync(thread, ack, settings, 1, "auto", log))
            {
                thread.AckSentAt = DateTime.Now;
                counters.Acks++;
            }
        }

        AutoReplyStore.Put(thread);
        await NotifyAsync(settings, thread, log);
    }

    private async Task<bool> TrySendAsync(MessageThread thread, string text, AutoReplySettings settings, int level, string sender, Action<string>? log)
    {
        if (thread.LastAutoAt is not null && thread.LastAutoAt > DateTime.Now.AddMinutes(-settings.RateLimitMinutes))
        {
            thread.Error = AppendError(thread.Error, "rate limit: an automatic message went out less than 10 minutes ago");
            return false;
        }

        var full = settings.Signature.Length > 0 ? $"{text.Trim()}\n\n{settings.Signature}" : text.Trim();
        try
        {
            await SendAsync(thread, full);
            thread.LastAutoAt = DateTime.Now;
            AutoReplyStore.Log(thread, "out", sender, full, level);
            log?.Invoke($"Level {level} message sent to {thread.Buyer}: {Shorten(text, 80)}");
            return true;
        }
        catch (Exception e)
        {
            thread.Error = AppendError(thread.Error, $"send: {e.Message}");
            log?.Invoke($"Could not send to {thread.Buyer}: {e.Message}");
            return false;
        }
    }

    public async Task SendAsync(MessageThread thread, string text)
    {
        if (thread.IsIssue)
        {
            await _api.PostIssueMessageAsync(thread.Id["issue:".Length..], text);
        }
        else
        {
            await _api.PostMessageAsync(thread.Id, text);
        }
    }

    public async Task SendDraftAsync(string threadId, string text, Action<string>? log = null)
    {
        var thread = AutoReplyStore.Get(threadId) ?? throw new InvalidOperationException("the message is no longer tracked");
        var settings = AutoReplyStore.Settings();
        var full = settings.Signature.Length > 0 && !text.Contains(settings.Signature) ? $"{text.Trim()}\n\n{settings.Signature}" : text.Trim();
        await SendAsync(thread, full);
        thread.Draft = text;
        thread.AnsweredAt = DateTime.Now;
        thread.Status = ThreadStatus.Sent;
        thread.Error = "";
        AutoReplyStore.Put(thread);
        AutoReplyStore.Log(thread, "out", "seller", full, 3);
        try
        {
            if (!thread.IsIssue)
            {
                await _api.MarkReadAsync(thread.Id);
            }
        }
        catch (Exception)
        {
        }
        log?.Invoke($"Seller's answer sent to {thread.Buyer}.");
    }

    public static void Reject(string threadId)
    {
        var thread = AutoReplyStore.Get(threadId);
        if (thread is null)
        {
            return;
        }
        thread.Status = ThreadStatus.Rejected;
        thread.AnsweredAt = DateTime.Now;
        AutoReplyStore.Put(thread);
        AutoReplyStore.Log(thread, "note", "seller", "draft rejected, answered outside the program");
    }

    private async Task RemindAsync(AutoReplySettings settings, Action<string>? log)
    {
        if (!settings.NotifyTelegram)
        {
            return;
        }
        var now = DateTime.Now;
        foreach (var thread in AutoReplyStore.Threads().Values.Where(t => t.NeedsAnswer && t.Status != ThreadStatus.New))
        {
            var age = now - thread.ReceivedAt;
            if (age > TimeSpan.FromHours(22) && thread.Reminder22At is null)
            {
                thread.Reminder22At = now;
                AutoReplyStore.Put(thread);
                await TelegramAsync(settings, $"#AllegroApp PILNE: wiadomość od {thread.Buyer} czeka {age.TotalHours:0}h bez odpowiedzi!\n{Shorten(thread.BuyerText, 200)}\n{settings.AdminUrl.TrimEnd('/')}/messages", log);
            }
            else if (age > TimeSpan.FromHours(18) && thread.Reminder18At is null)
            {
                thread.Reminder18At = now;
                AutoReplyStore.Put(thread);
                await TelegramAsync(settings, $"#AllegroApp Przypomnienie: wiadomość od {thread.Buyer} czeka {age.TotalHours:0}h bez odpowiedzi.\n{Shorten(thread.BuyerText, 200)}\n{settings.AdminUrl.TrimEnd('/')}/messages", log);
            }
        }
    }

    private static async Task NotifyAsync(AutoReplySettings settings, MessageThread thread, Action<string>? log)
    {
        if (!settings.NotifyTelegram)
        {
            return;
        }
        var what = thread.IsIssue ? "Nowa reklamacja/dyskusja" : "Nowa wiadomość";
        var offer = thread.OfferName.Length > 0 ? $"\nOferta: {thread.OfferName}" : "";
        await TelegramAsync(settings, $"#AllegroApp {what} od {thread.Buyer}{offer}\n{Shorten(thread.BuyerText, 300)}\n\nSzkic odpowiedzi czeka na akceptację: {settings.AdminUrl.TrimEnd('/')}/messages", log);
    }

    public static async Task TelegramAsync(AutoReplySettings settings, string text, Action<string>? log)
    {
        try
        {
            if (!await new TelegramNotify().SendToAsync(settings.TelegramChatId, text))
            {
                log?.Invoke("Telegram notification failed.");
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"Telegram notification failed: {e.Message}");
        }
    }

    private static bool TopicEnabled(AutoReplySettings settings, string topic) => topic switch
    {
        MessageTopic.Availability => settings.TopicAvailability,
        MessageTopic.Shipping => settings.TopicShipping,
        MessageTopic.Parcel => settings.TopicParcel,
        MessageTopic.Invoice => settings.TopicInvoice,
        MessageTopic.Description => settings.TopicDescription,
        _ => false,
    };

    private async Task<ReplyContext> BuildContextAsync(MessageThread thread)
    {
        var ctx = new ReplyContext();
        if (thread.OfferId.Length > 0)
        {
            var offer = await _api.OfferAsync(thread.OfferId);
            if (offer is not null)
            {
                ctx.OfferName = offer["name"]?.ToString() ?? "";
                ctx.OfferStock = offer["stock"]?["available"]?.GetValue<int>();
                ctx.OfferPrice = ParseAmount(offer["sellingMode"]?["price"]?["amount"]?.ToString());
                ctx.HandlingDays = HandlingDays(offer["delivery"]?["handlingTime"]?.ToString());
                ctx.Description = DescriptionText(offer["description"]);
                ctx.Parameters = ParametersText(offer);
                ctx.AllowedNumbers.Add(thread.OfferId);
                if (ctx.OfferPrice is { } price)
                {
                    ctx.AllowedAmounts.Add(price);
                }
            }
        }

        JsonNode? order = null;
        if (thread.OrderId.Length > 0)
        {
            order = await _api.OrderAsync(thread.OrderId);
        }
        if (order is null && thread.Buyer.Length > 0)
        {
            var orders = await _api.BuyerOrdersAsync(thread.Buyer, 10);
            order = orders
                .Where(o => o is not null)
                .OrderByDescending(o => thread.OfferId.Length > 0 && (o!["lineItems"]?.AsArray() ?? new JsonArray()).Any(li => li?["offer"]?["id"]?.ToString() == thread.OfferId))
                .ThenByDescending(o => ParseDate(o!["updatedAt"]?.ToString()) ?? DateTime.MinValue)
                .FirstOrDefault();
        }

        if (order is not null)
        {
            ctx.OrderId = order["id"]?.ToString() ?? "";
            ctx.OrderStatus = order["fulfillment"]?["status"]?.ToString() ?? order["status"]?.ToString() ?? "";
            var items = order["lineItems"]?.AsArray() ?? new JsonArray();
            ctx.BoughtAt = items.Select(li => ParseDate(li?["boughtAt"]?.ToString())).Where(d => d is not null).Min();
            ctx.PaidAt = ParseDate(order["payment"]?["finishedAt"]?.ToString());
            ctx.OrderTotal = ParseAmount(order["summary"]?["totalToPay"]?["amount"]?.ToString());
            ctx.OrderItems = string.Join("; ", items.Select(li => $"{li?["offer"]?["name"]} × {li?["quantity"]}"));
            ctx.AllowedNumbers.Add(ctx.OrderId);
            if (ctx.OrderTotal is { } total)
            {
                ctx.AllowedAmounts.Add(total);
            }
            if (ctx.OfferName.Length == 0 && items.Count == 1)
            {
                ctx.OfferName = items[0]?["offer"]?["name"]?.ToString() ?? "";
            }

            var shipments = await _api.ShipmentsAsync(ctx.OrderId);
            var shipment = shipments.OrderByDescending(s => ParseDate(s?["createdAt"]?.ToString()) ?? DateTime.MinValue).FirstOrDefault();
            if (shipment is not null)
            {
                ctx.Shipped = true;
                ctx.Waybill = shipment["waybill"]?.ToString() ?? "";
                ctx.Carrier = shipment["carrierName"]?.ToString() ?? shipment["carrierId"]?.ToString() ?? "";
                ctx.ShippedAt = ParseDate(shipment["createdAt"]?.ToString());
                ctx.AllowedNumbers.Add(ctx.Waybill);
            }
            else if (ctx.OrderStatus.Equals("SENT", StringComparison.OrdinalIgnoreCase) || ctx.OrderStatus.Equals("PICKED_UP", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Shipped = true;
            }
        }
        return ctx;
    }

    private async Task<(string Topic, double Confidence)> ClassifyAsync(MessageThread thread, ReplyContext ctx, AutoReplySettings settings)
    {
        var system = """
                     You classify a buyer's message sent to a seller on Allegro (Polish marketplace). Pick exactly one topic:
                     - availability: is the product available / how many pieces are there
                     - shipping: when will the order be shipped / how long is dispatch
                     - parcel: where is the parcel / tracking number / which courier
                     - invoice: invoice (faktura) questions
                     - description: a fact that the offer description or parameters answer directly (quantity in a set, size, material, colour)
                     - address_change: the buyer wants to change the delivery address or details before shipping
                     - other: anything else (compatibility, "will it fit", technical advice, bargaining, complaints, returns, damage, several questions at once)
                     If the message mixes topics or you are unsure, choose "other" with low confidence.
                     Answer ONLY with JSON: {"topic": "...", "confidence": 0.0-1.0}
                     """;
        var content = $"Wiadomość kupującego:\n{thread.BuyerText}\n\nKontekst:\n{(ctx.HasOffer ? $"oferta: {ctx.OfferName}" : "bez oferty")}; {(ctx.HasOrder ? $"kupujący ma zamówienie ({ctx.OrderStatus}, {(ctx.Shipped ? "wysłane" : "niewysłane")})" : "brak zamówienia")}";
        var answer = await Ai().CompleteAsync(system, content, settings.ClassifierModel, 256);
        var node = JsonNode.Parse(StripFence(answer));
        var topic = node?["topic"]?.ToString()?.Trim().ToLowerInvariant() ?? MessageTopic.Other;
        var confidence = node?["confidence"]?.GetValue<double>() ?? 0;
        return (MessageTopic.All.Contains(topic) ? topic : MessageTopic.Other, Math.Clamp(confidence, 0, 1));
    }

    private async Task<(string? Answer, string Reason)> ComposeAsync(MessageThread thread, ReplyContext ctx, AutoReplySettings settings)
    {
        switch (thread.Topic)
        {
            case MessageTopic.Availability:
                if (!ctx.HasOffer || ctx.OfferStock is null)
                {
                    return (null, "the message does not point to an offer with a known quantity");
                }
                return (ctx.OfferStock > 0
                    ? $"Dzień dobry, tak, produkt „{ctx.OfferName}” jest dostępny – mamy {ctx.OfferStock} szt. Paczki nadajemy w {settings.ShipDaysPolish()}."
                    : $"Dzień dobry, niestety produkt „{ctx.OfferName}” jest obecnie niedostępny.", "");

            case MessageTopic.Shipping:
                if (ctx.HasOrder && ctx.Shipped)
                {
                    return ParcelAnswer(ctx, settings);
                }
                if (ctx.HasOrder)
                {
                    var date = DispatchDate(ctx.PaidAt ?? ctx.BoughtAt ?? DateTime.Now, ctx.HandlingDays, settings);
                    return (ctx.PaidAt is null
                        ? $"Dzień dobry, zamówienie wyślemy w najbliższy dzień wysyłki po zaksięgowaniu wpłaty – paczki nadajemy w {settings.ShipDaysPolish()}."
                        : $"Dzień dobry, zamówienie wyślemy {PolishOn(date)} {date:dd.MM.yyyy} – paczki nadajemy w {settings.ShipDaysPolish()}.", "");
                }
                if (ctx.HasOffer)
                {
                    return ($"Dzień dobry, paczki nadajemy w {settings.ShipDaysPolish()}, w najbliższy taki dzień po zaksięgowaniu wpłaty.", "");
                }
                return (null, "no order and no offer to read the dispatch time from");

            case MessageTopic.Parcel:
                if (!ctx.HasOrder)
                {
                    return (null, "no order found for this buyer");
                }
                return ParcelAnswer(ctx, settings);

            case MessageTopic.Invoice:
                return (settings.InvoiceText.Length > 0 ? $"Dzień dobry, {LowerFirst(settings.InvoiceText)}" : null, "no invoice text is set");

            case MessageTopic.Description:
                if (!ctx.HasOffer || (ctx.Description.Length == 0 && ctx.Parameters.Length == 0))
                {
                    return (null, "the offer has no description to quote");
                }
                var quote = await QuoteAsync(thread, ctx, settings);
                if (quote is null)
                {
                    return (null, "the description does not answer the question word for word");
                }
                return ($"Dzień dobry, zgodnie z opisem oferty: „{quote}”.", "");

            default:
                return (null, "needs the seller");
        }
    }

    private static (string? Answer, string Reason) ParcelAnswer(ReplyContext ctx, AutoReplySettings settings)
    {
        if (ctx.Shipped && ctx.Waybill.Length > 0)
        {
            var carrier = ctx.Carrier.Length > 0 ? $" ({ctx.Carrier})" : "";
            return ($"Dzień dobry, paczka została nadana {ctx.ShippedAt:dd.MM.yyyy}, numer przesyłki: {ctx.Waybill}{carrier}.", "");
        }
        if (ctx.Shipped)
        {
            return (null, "the order is marked as sent but Allegro has no tracking number");
        }
        var date = DispatchDate(ctx.PaidAt ?? ctx.BoughtAt ?? DateTime.Now, ctx.HandlingDays, settings);
        return (ctx.PaidAt is null
            ? $"Dzień dobry, zamówienie nie zostało jeszcze nadane – wyślemy je w najbliższy dzień wysyłki po zaksięgowaniu wpłaty (paczki nadajemy w {settings.ShipDaysPolish()})."
            : $"Dzień dobry, zamówienie nie zostało jeszcze nadane – wyślemy je {PolishOn(date)} {date:dd.MM.yyyy} (paczki nadajemy w {settings.ShipDaysPolish()}).", "");
    }

    private static string PolishOn(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "w poniedziałek",
        DayOfWeek.Tuesday => "we wtorek",
        DayOfWeek.Wednesday => "w środę",
        DayOfWeek.Thursday => "w czwartek",
        DayOfWeek.Friday => "w piątek",
        DayOfWeek.Saturday => "w sobotę",
        _ => "w niedzielę",
    };

    private async Task<string?> QuoteAsync(MessageThread thread, ReplyContext ctx, AutoReplySettings settings)
    {
        var system = """
                     The buyer asks about a fact. You get the offer description and parameters. Find ONE sentence or fragment,
                     copied word for word from the description or parameters, that answers the question directly.
                     Answer ONLY with JSON: {"found": true, "quote": "..."} or {"found": false}.
                     Never paraphrase and never add anything that is not in the text.
                     """;
        var content = $"Pytanie: {thread.BuyerText}\n\nParametry: {ctx.Parameters}\n\nOpis: {Shorten(ctx.Description, 6000)}";
        var answer = await Ai().CompleteAsync(system, content, settings.ClassifierModel, 512);
        var node = JsonNode.Parse(StripFence(answer));
        if (node?["found"]?.GetValue<bool>() != true)
        {
            return null;
        }
        var quote = node["quote"]?.ToString()?.Trim() ?? "";
        if (quote.Length < 3 || quote.Length > 300)
        {
            return null;
        }
        var source = Normalize(ctx.Description + " " + ctx.Parameters);
        return source.Contains(Normalize(quote), StringComparison.OrdinalIgnoreCase) ? quote : null;
    }

    private async Task<string> DraftAsync(MessageThread thread, ReplyContext ctx, AutoReplySettings settings)
    {
        var system = $"""
                      Piszesz odpowiedzi do kupujących na Allegro w imieniu sprzedawcy „{settings.SellerName}”. Zasady:
                      - odpowiadaj po polsku, uprzejmie i krótko (do 600 znaków), zaczynaj od „Dzień dobry”;
                      - korzystaj WYŁĄCZNIE z faktów podanych w danych oferty i zamówienia poniżej; jeśli czegoś tam nie ma, napisz „sprawdzimy i odpowiemy”, nie zgaduj;
                      - nie obiecuj rabatów, zwrotu pieniędzy, rekompensat ani terminów innych niż podane;
                      - nie podawaj telefonów, adresów e-mail ani linków, nie proponuj kontaktu ani transakcji poza Allegro;
                      - nie wspominaj o hurtowni, dropshippingu ani cenach zakupu;
                      - {(thread.IsIssue ? "to jest reklamacja lub dyskusja: nie rozstrzygaj sprawy, potwierdź przyjęcie i zapowiedz odpowiedź sprzedawcy;" : "jeśli pytanie dotyczy zgodności lub dopasowania, odpowiadaj tylko tym, co wynika z parametrów;")}
                      - nie dodawaj podpisu, zostanie dodany automatycznie.
                      Odpowiedz samym tekstem wiadomości, bez komentarzy.
                      """;
        var content = $"WIADOMOŚĆ KUPUJĄCEGO ({thread.Buyer}):\n{thread.BuyerText}\n\nDANE:\n{ctx.ForAi()}";
        var draft = await Ai().CompleteAsync(system, content, settings.DraftModel, 1024);
        return draft.Trim();
    }

    private AiClient Ai() => _ai ??= new AiClient();

    private static string Days(int days) => days switch
    {
        1 => "1 dnia roboczego",
        _ => $"{days} dni roboczych",
    };

    public static DateTime DispatchDate(DateTime from, int handlingDays, AutoReplySettings? settings = null)
    {
        var date = from.Date;
        if (settings is not null && settings.ShipDays.Count > 0)
        {
            for (int i = 1; i <= 7; i++)
            {
                if (settings.ShipsOn(date.AddDays(i).DayOfWeek))
                {
                    return date.AddDays(i);
                }
            }
        }
        var left = Math.Max(handlingDays, 1);
        while (left > 0)
        {
            date = date.AddDays(1);
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                left--;
            }
        }
        return date;
    }

    private static int HandlingDays(string? handlingTime)
    {
        if (string.IsNullOrEmpty(handlingTime))
        {
            return 2;
        }
        var hours = Regex.Match(handlingTime, @"PT(\d+)H");
        if (hours.Success)
        {
            return Math.Max(1, (int)Math.Ceiling(int.Parse(hours.Groups[1].Value) / 24.0));
        }
        var days = Regex.Match(handlingTime, @"P(\d+)D");
        return days.Success ? Math.Max(1, int.Parse(days.Groups[1].Value)) : 2;
    }

    private static string DescriptionText(JsonNode? description)
    {
        var parts = new List<string>();
        foreach (var section in description?["sections"]?.AsArray() ?? new JsonArray())
        {
            foreach (var item in section?["items"]?.AsArray() ?? new JsonArray())
            {
                if (item?["type"]?.ToString() == "TEXT")
                {
                    var html = item["content"]?.ToString() ?? "";
                    parts.Add(WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"</(p|li|h\d)>", "\n"), "<[^>]+>", " ")));
                }
            }
        }
        return Regex.Replace(string.Join("\n", parts), @"[ \t]+", " ").Trim();
    }

    private static string ParametersText(JsonNode offer)
    {
        var parts = new List<string>();
        foreach (var product in offer["productSet"]?.AsArray() ?? new JsonArray())
        {
            foreach (var parameter in product?["product"]?["parameters"]?.AsArray() ?? new JsonArray())
            {
                var name = parameter?["name"]?.ToString();
                var values = parameter?["values"]?.AsArray()?.Select(v => v?.ToString()).Where(v => !string.IsNullOrEmpty(v)).ToList() ?? new List<string?>();
                if (!string.IsNullOrEmpty(name) && values.Count > 0)
                {
                    parts.Add($"{name}: {string.Join(", ", values)}");
                }
            }
        }
        return string.Join("; ", parts);
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Replace("„", "\"").Replace("”", "\"").Trim();

    private static string StripFence(string answer) => Regex.Replace(answer.Trim(), @"^```(?:json)?\s*|\s*```$", "");

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string AppendError(string existing, string error) => existing.Length == 0 ? error : $"{existing}; {error}";

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date) ? date.ToLocalTime() : null;

    private static decimal? ParseAmount(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;

    private static string Shorten(string text, int max)
    {
        var clean = Regex.Replace(text, @"\s+", " ").Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }
}
