using System.Globalization;
using System.Text.RegularExpressions;

namespace Allegro.Core;

public class WorkingDay
{
    public int Start { get; set; }
    public int End { get; set; }
    public bool Enabled => End > Start;
}

public class AutoReplySettings
{
    public bool Enabled { get; set; } = false;
    public bool Level1 { get; set; } = true;
    public bool Level2 { get; set; } = true;
    public bool Level3 { get; set; } = true;
    public bool TopicAvailability { get; set; } = true;
    public bool TopicShipping { get; set; } = true;
    public bool TopicParcel { get; set; } = true;
    public bool TopicInvoice { get; set; } = true;
    public bool TopicDescription { get; set; } = true;
    public bool HandleIssues { get; set; } = true;
    public bool NotifyTelegram { get; set; } = true;
    public string TelegramChatId { get; set; } = "348337220";
    public string SellerName { get; set; } = "MaxiGroup";
    public string Signature { get; set; } = "Pozdrawiamy, MaxiGroup";
    public string AckTemplate { get; set; } = "Dzień dobry, dziękujemy za wiadomość. Odpowiemy najpóźniej {termin}.";
    public string IssueAckTemplate { get; set; } = "Dzień dobry, przyjęliśmy zgłoszenie. Odpowiemy najpóźniej {termin}.";
    public string InvoiceText { get; set; } = "Tak, wystawiamy fakturę (sprzedawca zwolniony z VAT, art. 113). Prosimy o podanie danych do faktury w formularzu zamówienia lub w wiadomości.";
    public double ConfidenceThreshold { get; set; } = 0.85;
    public int ReplyWithinHours { get; set; } = 3;
    public List<int> ShipDays { get; set; } = new() { 1, 3, 5 };
    public bool TestMode { get; set; }
    public int RateLimitMinutes { get; set; } = 10;
    public string ClassifierModel { get; set; } = "claude-haiku-5-5";
    public string DraftModel { get; set; } = "claude-sonnet-5-5";
    public string AdminUrl { get; set; } = "http://31.3.218.113:5101";
    public List<WorkingDay> Week { get; set; } = new()
    {
        new WorkingDay { Start = 9, End = 18 },
        new WorkingDay { Start = 9, End = 18 },
        new WorkingDay { Start = 9, End = 18 },
        new WorkingDay { Start = 9, End = 18 },
        new WorkingDay { Start = 9, End = 18 },
        new WorkingDay { Start = 10, End = 14 },
        new WorkingDay { Start = 0, End = 0 },
    };
    public DateTime LastRunAt { get; set; } = DateTime.MinValue;
    public string LastError { get; set; } = "";

    public bool InTestMode() => TestMode;

    public bool ShipsOn(DayOfWeek day) => ShipDays.Contains((int)day);

    public string ShipDaysPolish()
    {
        string[] names = { "niedziele", "poniedziałki", "wtorki", "środy", "czwartki", "piątki", "soboty" };
        var days = ShipDays.Where(d => d is >= 0 and <= 6).OrderBy(d => d == 0 ? 7 : d).Select(d => names[d]).ToList();
        return days.Count == 0 ? "" : days.Count == 1 ? days[0] : string.Join(", ", days.Take(days.Count - 1)) + " i " + days[^1];
    }

    public WorkingDay Day(DayOfWeek day) => Week[((int)day + 6) % 7];
}

public static class MessageTopic
{
    public const string Availability = "availability";
    public const string Shipping = "shipping";
    public const string Parcel = "parcel";
    public const string Invoice = "invoice";
    public const string Description = "description";
    public const string AddressChange = "address_change";
    public const string Other = "other";

    public static readonly string[] All = { Availability, Shipping, Parcel, Invoice, Description, AddressChange, Other };
}

public static class ThreadStatus
{
    public const string New = "new";
    public const string Auto = "auto";
    public const string Draft = "draft";
    public const string Sent = "sent";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
}

public class MessageThread
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "thread";
    public string Buyer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string OfferName { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string LastBuyerMessageId { get; set; } = "";
    public string BuyerText { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime SeenAt { get; set; }
    public DateTime? AckSentAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public DateTime? LastAutoAt { get; set; }
    public string Status { get; set; } = ThreadStatus.New;
    public string Topic { get; set; } = "";
    public double Confidence { get; set; }
    public int Level { get; set; }
    public string Draft { get; set; } = "";
    public string DraftReason { get; set; } = "";
    public string Context { get; set; } = "";
    public DateTime? Reminder18At { get; set; }
    public DateTime? Reminder22At { get; set; }
    public string Error { get; set; } = "";

    public bool IsIssue => Kind == "issue";
    public bool NeedsAnswer => AnsweredAt is null && Status != ThreadStatus.Rejected;
}

public class MessageLogEntry
{
    public DateTime At { get; set; }
    public string ThreadId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Buyer { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Sender { get; set; } = "";
    public int Level { get; set; }
    public string Topic { get; set; } = "";
    public string Text { get; set; } = "";
}

public static class AutoReplyStore
{
    private static readonly object Gate = new();

    public static AutoReplySettings Settings() => SaverExtensions.AutoReply.Read();

    public static void Save(AutoReplySettings settings)
    {
        SaverExtensions.AutoReply.Value = settings;
        SaverExtensions.AutoReply.Write();
    }

    public static Dictionary<string, MessageThread> Threads()
    {
        lock (Gate)
        {
            return new Dictionary<string, MessageThread>(SaverExtensions.MessageThreads.Read());
        }
    }

    public static MessageThread? Get(string id)
    {
        lock (Gate)
        {
            return SaverExtensions.MessageThreads.Read().TryGetValue(id, out var thread) ? thread : null;
        }
    }

    public static void Put(MessageThread thread)
    {
        lock (Gate)
        {
            var all = SaverExtensions.MessageThreads.Read();
            all[thread.Id] = thread;
            SaverExtensions.MessageThreads.Value = all;
            SaverExtensions.MessageThreads.Write();
        }
    }

    public static List<MessageLogEntry> Log()
    {
        lock (Gate)
        {
            return SaverExtensions.MessageLog.Read().ToList();
        }
    }

    public static void Log(MessageThread thread, string direction, string sender, string text, int level = 0)
    {
        lock (Gate)
        {
            var all = SaverExtensions.MessageLog.Read();
            all.Add(new MessageLogEntry
            {
                At = DateTime.Now,
                ThreadId = thread.Id,
                Kind = thread.Kind,
                Buyer = thread.Buyer,
                Direction = direction,
                Sender = sender,
                Level = level,
                Topic = thread.Topic,
                Text = text,
            });
            if (all.Count > 5000)
            {
                all.RemoveRange(0, all.Count - 5000);
            }
            SaverExtensions.MessageLog.Value = all;
            SaverExtensions.MessageLog.Write();
        }
    }
}

public static class WorkingHours
{
    private static readonly string[] PolishDays = { "niedzieli", "poniedziałku", "wtorku", "środy", "czwartku", "piątku", "soboty" };

    public static DateTime Deadline(AutoReplySettings settings, DateTime received)
    {
        var hours = Math.Max(settings.ReplyWithinHours, 1);
        var cursor = received;
        for (int i = 0; i < 14; i++)
        {
            var day = settings.Day(cursor.DayOfWeek);
            var start = cursor.Date.AddHours(day.Start);
            var end = cursor.Date.AddHours(day.End);
            if (day.Enabled && cursor < end)
            {
                var from = cursor < start ? start : cursor;
                var deadline = from.AddHours(hours);
                return Cap(deadline > end ? NextStart(settings, cursor.Date.AddDays(1)).AddHours(hours) : deadline, received);
            }
            cursor = cursor.Date.AddDays(1);
        }
        return Cap(received.AddHours(hours), received);
    }

    private static DateTime NextStart(AutoReplySettings settings, DateTime date)
    {
        for (int i = 0; i < 14; i++)
        {
            var day = settings.Day(date.DayOfWeek);
            if (day.Enabled)
            {
                return date.Date.AddHours(day.Start);
            }
            date = date.AddDays(1);
        }
        return date;
    }

    private static DateTime Cap(DateTime deadline, DateTime received)
    {
        var latest = received.AddHours(23);
        return deadline > latest ? latest : deadline;
    }

    public static string Describe(DateTime deadline, DateTime now)
    {
        var time = deadline.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (deadline.Date == now.Date)
        {
            return $"dziś do {time}";
        }
        return $"do {PolishDays[(int)deadline.DayOfWeek]} do {time}";
    }
}

public static class ReplyFilter
{
    private static readonly Regex Url = new(@"(https?://|www\.|\b[a-z0-9-]+\.(pl|com|eu|net|org|info)\b(/|\b))", RegexOptions.IgnoreCase);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.IgnoreCase);
    private static readonly Regex Phone = new(@"(?<!\d)(\+?\d[\d \-]{7,14}\d)(?!\d)");
    private static readonly Regex Money = new(@"(\d+(?:[.,]\d{1,2})?)\s*(zł|pln)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Forbidden = new(@"allenett|hurtowni|dropship|rabat|zni[żz]k|zwrot\w*\s+pieni|zwrot\w*\s+[śs]rodk|rekompensat|odszkodowan", RegexOptions.IgnoreCase);

    public static string? Problem(string text, IEnumerable<string> allowedNumbers, IEnumerable<decimal> allowedAmounts)
    {
        if (Url.IsMatch(text))
        {
            return "contains a link";
        }
        if (Email.IsMatch(text))
        {
            return "contains an e-mail address";
        }

        var allowed = allowedNumbers.Select(n => Regex.Replace(n, @"\D", "")).Where(n => n.Length > 0).ToHashSet();
        foreach (Match m in Phone.Matches(text))
        {
            var digits = Regex.Replace(m.Value, @"\D", "");
            if (!allowed.Any(a => a.Contains(digits) || digits.Contains(a)))
            {
                return "contains a phone-like number";
            }
        }

        var amounts = allowedAmounts.ToHashSet();
        foreach (Match m in Money.Matches(text))
        {
            if (decimal.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                && !amounts.Contains(amount))
            {
                return $"mentions an amount ({m.Value}) not found in the offer or order";
            }
        }

        var forbidden = Forbidden.Match(text);
        if (forbidden.Success)
        {
            return $"contains a forbidden word ({forbidden.Value})";
        }
        return null;
    }
}
