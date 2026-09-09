namespace Allegro.Core;

public class ListingOptions
{
    public int MinimalProductCount { get; set; } = 10;
    public decimal MinimalPrice { get; set; } = 0m;
    public int BundleFromQuantity { get; set; } = 5;
    public decimal BundleMultiplier { get; set; } = 1m;
    public List<string> CategoriesBlackList { get; set; } = new List<string>();
    public List<string> EansBlackList { get; set; } = new List<string>();
    
    public List<PriceTier> PriceMultipliers { get; set; } = new()
    {
        new PriceTier { MaxPrice = 10m, Multiplier = 1.5m },
        new PriceTier { MaxPrice = 20m, Multiplier = 1.2m },
    };
    public decimal DefaultMultiplier { get; set; } = 3m;

    public bool Includes(ProductInfo product) =>
        !product.CategoriesUrls.Any(url => CategoriesBlackList.Any(url.Contains))
        && !EansBlackList.Contains(product.EAN)
        && !string.IsNullOrWhiteSpace(product.EAN)
        && !product.EAN.Contains("—")
        && product.Count >= MinimalProductCount
        && product.Price >= MinimalPrice
        && (!IsBundle(product) || product.Count >= product.MinOrderQuantity);

    public bool IsBundle(ProductInfo product) =>
        BundleFromQuantity > 0 && product.MinOrderQuantity > BundleFromQuantity;

    public int GetPackSize(ProductInfo product) => IsBundle(product) ? product.MinOrderQuantity : 1;

    public decimal GetOfferPrice(ProductInfo product)
    {
        var cost = product.Price * GetPackSize(product);
        var price = cost * GetMultiplier(cost);

        if (IsBundle(product) && BundleMultiplier > 0m)
        {
            price *= BundleMultiplier;
        }

        return Math.Round(price, 2, MidpointRounding.AwayFromZero);
    }

    public int GetOfferStock(ProductInfo product) => product.Count / GetPackSize(product);

    private decimal GetMultiplier(decimal price)
    {
        foreach (var tier in PriceMultipliers.OrderBy(t => t.MaxPrice))
        {
            if (price <= tier.MaxPrice)
            {
                return tier.Multiplier;
            }
        }
        return DefaultMultiplier;
    }
}
public class PriceTier
{
    public decimal MaxPrice { get; set; }
    public decimal Multiplier { get; set; }
}
