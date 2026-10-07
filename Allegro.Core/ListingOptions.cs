namespace Allegro.Core;

public class ListingOptions
{
    public int MinimalProductCount { get; set; } = 10;
    public decimal MinimalPrice { get; set; } = 0m;
    public int BundleFromQuantity { get; set; } = 5;
    public List<PackTier> PackDivisors { get; set; } = new()
    {
        new PackTier { MaxMinOrder = 10, Divisor = 1 },
        new PackTier { MaxMinOrder = 50, Divisor = 5 },
    };
    public int PackDefaultDivisor { get; set; } = 10;
    public List<string> CategoriesBlackList { get; set; } = new List<string>();
    public List<string> EansBlackList { get; set; } = new List<string>();

    public List<PriceTier> PriceMultipliers { get; set; } = new()
    {
        new PriceTier { MaxPrice = 10m, Multiplier = 1.5m },
        new PriceTier { MaxPrice = 20m, Multiplier = 1.2m },
    };
    public decimal DefaultMultiplier { get; set; } = 3m;

    public List<PriceTier> BundlePriceMultipliers { get; set; } = new();
    public decimal BundleDefaultMultiplier { get; set; } = 3m;

    public bool Includes(ProductInfo product) =>
        !product.CategoriesUrls.Any(url => CategoriesBlackList.Any(url.Contains))
        && !EansBlackList.Contains(product.EAN)
        && !string.IsNullOrWhiteSpace(product.EAN)
        && !product.EAN.Contains("—")
        && product.Price >= MinimalPrice
        && product.Count >= MinimalProductCount
        && GetOfferStock(product) >= 1;

    public bool IsBundle(ProductInfo product) =>
        BundleFromQuantity > 0 && product.MinOrderQuantity > BundleFromQuantity;

    public int GetPackSize(ProductInfo product)
    {
        if (!IsBundle(product))
        {
            return 1;
        }

        var minOrder = product.MinOrderQuantity;
        var divisor = GetPackDivisor(minOrder);
        return divisor > 1 && minOrder % divisor == 0 ? minOrder / divisor : minOrder;
    }

    public int GetPackDivisor(int minOrder)
    {
        foreach (var tier in PackDivisors.OrderBy(t => t.MaxMinOrder))
        {
            if (minOrder <= tier.MaxMinOrder)
            {
                return tier.Divisor;
            }
        }
        return PackDefaultDivisor;
    }

    public decimal GetOfferPrice(ProductInfo product) => GetOfferPrice(product, GetPackSize(product));

    public decimal GetOfferPrice(ProductInfo product, int pack)
    {
        var cost = product.Price * pack;

        var asBundle = pack == GetPackSize(product) ? IsBundle(product) : pack > 1;
        var markup = asBundle
            ? GetMultiplier(cost, BundlePriceMultipliers, BundleDefaultMultiplier)
            : GetMultiplier(cost, PriceMultipliers, DefaultMultiplier);

        return Math.Round(cost * markup, 2, MidpointRounding.AwayFromZero);
    }

    public int GetOfferStock(ProductInfo product) => GetOfferStock(product, GetPackSize(product));

    public int GetOfferStock(ProductInfo product, int pack) => product.Count / Math.Max(pack, 1);

    private static decimal GetMultiplier(decimal price, List<PriceTier> tiers, decimal fallback)
    {
        foreach (var tier in tiers.OrderBy(t => t.MaxPrice))
        {
            if (price <= tier.MaxPrice)
            {
                return tier.Multiplier;
            }
        }
        return fallback;
    }
}
public class PriceTier
{
    public decimal MaxPrice { get; set; }
    public decimal Multiplier { get; set; }
}

public class PackTier
{
    public int MaxMinOrder { get; set; }
    public int Divisor { get; set; }
}
