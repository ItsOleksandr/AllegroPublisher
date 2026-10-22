using System.Globalization;
using System.Text.RegularExpressions;

namespace Allegro.Core;

public class OwnStockItem
{
    public string Ean { get; set; } = "";
    public int Quantity { get; set; }
    public decimal? UnitCost { get; set; }
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.MinValue;
}

public class StockMove
{
    public DateTime At { get; set; }
    public string Ean { get; set; } = "";
    public int Delta { get; set; }
    public int After { get; set; }
    public string Reason { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string Note { get; set; } = "";
}

public static class StockReason
{
    public const string Manual = "manual";
    public const string Order = "order";
    public const string Purchase = "purchase";
    public const string Return = "return";
    public const string Correction = "correction";
    public const string Rollback = "rollback";
}

public static class OwnStock
{
    private static readonly object Gate = new();

    public static Dictionary<string, OwnStockItem> Items()
    {
        lock (Gate)
        {
            return new Dictionary<string, OwnStockItem>(SaverExtensions.OwnStock.Read(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public static List<StockMove> Moves()
    {
        lock (Gate)
        {
            return SaverExtensions.StockMoves.Read().ToList();
        }
    }

    public static OwnStockItem Set(string ean, int quantity, string reason, string orderId = "", string note = "",
        decimal? unitCost = null, string? name = null)
    {
        lock (Gate)
        {
            var items = SaverExtensions.OwnStock.Read();
            var item = Get(items, ean);
            return Apply(items, item, quantity - item.Quantity, reason, orderId, note, unitCost, name);
        }
    }

    public static OwnStockItem Adjust(string ean, int delta, string reason, string orderId = "", string note = "",
        decimal? unitCost = null, string? name = null)
    {
        lock (Gate)
        {
            var items = SaverExtensions.OwnStock.Read();
            return Apply(items, Get(items, ean), delta, reason, orderId, note, unitCost, name);
        }
    }

    public static OwnStockItem Describe(string ean, decimal? unitCost, string? name, string? note)
    {
        lock (Gate)
        {
            var items = SaverExtensions.OwnStock.Read();
            var item = Get(items, ean);
            item.UnitCost = unitCost;
            if (name is not null)
            {
                item.Name = name.Trim();
            }
            if (note is not null)
            {
                item.Note = note.Trim();
            }
            SaverExtensions.OwnStock.Value = items;
            SaverExtensions.OwnStock.Write();
            return item;
        }
    }

    public static bool Remove(string ean)
    {
        lock (Gate)
        {
            var items = SaverExtensions.OwnStock.Read();
            if (!items.TryGetValue(ean.Trim(), out var item))
            {
                return false;
            }
            if (item.Quantity != 0)
            {
                Apply(items, item, -item.Quantity, StockReason.Correction, "", "removed from the stock list", null, null);
            }
            items.Remove(item.Ean);
            SaverExtensions.OwnStock.Value = items;
            SaverExtensions.OwnStock.Write();
            return true;
        }
    }

    public static StockMove? Undo()
    {
        lock (Gate)
        {
            var moves = SaverExtensions.StockMoves.Read();
            var last = moves.LastOrDefault();
            if (last is null || last.Reason == StockReason.Rollback || last.Delta == 0)
            {
                return null;
            }

            var items = SaverExtensions.OwnStock.Read();
            Apply(items, Get(items, last.Ean), -last.Delta, StockReason.Rollback, last.OrderId,
                $"undo of {last.Reason} {last.Delta:+#;-#;0} from {last.At:yyyy-MM-dd HH:mm}", null, null);
            return last;
        }
    }

    private static OwnStockItem Get(Dictionary<string, OwnStockItem> items, string ean)
    {
        ean = ean.Trim();
        if (!items.TryGetValue(ean, out var item))
        {
            item = new OwnStockItem { Ean = ean };
            items[ean] = item;
        }
        return item;
    }

    private static OwnStockItem Apply(Dictionary<string, OwnStockItem> items, OwnStockItem item, int delta, string reason,
        string orderId, string note, decimal? unitCost, string? name)
    {
        var target = item.Quantity + delta;
        if (target < 0)
        {
            note = $"{note} (would be {target}, clamped to 0)".Trim();
            delta = -item.Quantity;
            target = 0;
        }

        item.Quantity = target;
        item.UpdatedAt = DateTime.Now;
        if (unitCost is not null)
        {
            item.UnitCost = unitCost;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            item.Name = name.Trim();
        }

        var moves = SaverExtensions.StockMoves.Read();
        moves.Add(new StockMove
        {
            At = item.UpdatedAt,
            Ean = item.Ean,
            Delta = delta,
            After = item.Quantity,
            Reason = reason,
            OrderId = orderId,
            Note = note,
        });

        SaverExtensions.OwnStock.Value = items;
        SaverExtensions.OwnStock.Write();
        SaverExtensions.StockMoves.Value = moves;
        SaverExtensions.StockMoves.Write();
        return item;
    }

    public static bool IsValidEan(string code)
    {
        if (code.Length is not (8 or 13) || !code.All(char.IsDigit))
        {
            return false;
        }

        var sum = 0;
        for (int i = 0; i < code.Length - 1; i++)
        {
            var digit = code[i] - '0';
            var odd = (code.Length - 2 - i) % 2 == 0;
            sum += odd ? digit * 3 : digit;
        }
        return (10 - sum % 10) % 10 == code[^1] - '0';
    }

    public record ImportLine(int Number, string Raw, string Code, int Quantity, decimal? UnitCost, string? Error);

    public static List<ImportLine> ParseImport(string text)
    {
        var result = new List<ImportLine>();
        var number = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            number++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(new[] { ';', '\t' }, StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                parts = Regex.Split(line, @"[,\s]+");
            }

            var code = parts[0].Trim();
            if (number == 1 && !code.All(char.IsDigit) && parts.Length > 1 && !int.TryParse(parts[1], out _))
            {
                continue;
            }

            if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out var quantity) || quantity < 0)
            {
                result.Add(new ImportLine(number, line, code, 0, null, "quantity is missing or not a whole number"));
                continue;
            }

            decimal? cost = null;
            if (parts.Length > 2 && parts[2].Trim().Length > 0)
            {
                if (decimal.TryParse(parts[2].Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                {
                    cost = parsed;
                }
                else
                {
                    result.Add(new ImportLine(number, line, code, quantity, null, "unit cost is not a number"));
                    continue;
                }
            }

            result.Add(new ImportLine(number, line, code, quantity, cost, null));
        }
        return result;
    }
}
