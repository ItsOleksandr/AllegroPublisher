namespace Allegro.Core;

public record PlannedListing(
    ProductInfo Product,
    string Ean,
    int Count,
    decimal Price,
    int Pack,
    int SupplierCount,
    int OwnUnits,
    bool FromOwnStock,
    string? Warning);

public static class StockPlanner
{
    public static PlannedListing Plan(ProductInfo product, ListingOptions options, bool stale, OwnStockItem? own, int? packOverride = null)
    {
        var pack = Math.Max(packOverride ?? options.GetPackSize(product), 1);
        var supplier = options.Includes(product) && !stale ? options.GetOfferStock(product, pack) : 0;
        var ownUnits = own?.Quantity ?? 0;
        var ownPacks = ownUnits / pack;
        var ean = product.EAN;

        if (options.IsBlackListed(product))
        {
            return new PlannedListing(product, ean, 0, options.GetOfferPrice(product, pack), pack, supplier, ownUnits, false,
                ownUnits > 0 ? "black listed, own stock is not sold" : null);
        }

        if (supplier > 0)
        {
            var extra = options.AddOwnStockToSupplier ? ownPacks : 0;
            return new PlannedListing(product, ean, supplier + extra, options.GetOfferPrice(product, pack), pack, supplier, ownUnits, false, null);
        }

        if (ownPacks > 0)
        {
            var unit = own?.UnitCost is > 0 ? own.UnitCost.Value : product.Price;
            if (unit <= 0)
            {
                return new PlannedListing(product, ean, 0, 0m, pack, 0, ownUnits, true, "own stock without a unit cost and no supplier price");
            }
            var price = options.SellOffMultiplier > 0
                ? Math.Round(unit * pack * options.SellOffMultiplier, 2, MidpointRounding.AwayFromZero)
                : options.GetOfferPrice(product, pack, unit);
            return new PlannedListing(product, ean, ownPacks, price, pack, 0, ownUnits, true, null);
        }

        var warning = ownUnits > 0 && pack > 1 ? $"own stock {ownUnits} is less than one pack of {pack}" : null;
        return new PlannedListing(product, ean, 0, options.GetOfferPrice(product, pack), pack, 0, ownUnits, false, warning);
    }

    public static ProductInfo Placeholder(OwnStockItem own) => new()
    {
        Url = "",
        EAN = own.Ean,
        Name = own.Name,
        Price = own.UnitCost ?? 0m,
        Count = 0,
        MinOrderQuantity = 0,
        LastUpdate = own.UpdatedAt,
    };

    public static Dictionary<string, ProductInfo> ProductsByEan(IEnumerable<ProductInfo> products) =>
        products.Where(p => !string.IsNullOrWhiteSpace(p.EAN) && !p.EAN.Contains('—'))
            .GroupBy(p => p.EAN.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.LastUpdate).First(), StringComparer.OrdinalIgnoreCase);

    public static bool SupplierHas(ProductInfo product, ListingOptions options) =>
        product.Count > 0 && !options.IsStale(product);
}
