namespace Allegro.Core;

public class CSVOptions
{
    public int MinimalProductCount { get; set; } = 10;
    public decimal MinimalPrice { get; set; } = 0m;
    public int BundleFromQuantity { get; set; } = 5;
    public List<string> CategoriesBlackList { get; set; } = new List<string>();
    public List<string> EansBlackList { get; set; } = new List<string>();
    
    public List<PriceTier> PriceMultipliers { get; set; } = new()
    {
        new PriceTier { MaxPrice = 10m, Multiplier = 1.5m },
        new PriceTier { MaxPrice = 20m, Multiplier = 1.2m },
    };
    public decimal DefaultMultiplier { get; set; } = 3m;

    /// <summary>A product the supplier will not sell singly, so we sell the whole pack as one offer.</summary>
    public bool IsBundle(ProductInfo product) =>
        BundleFromQuantity > 0 && product.MinOrderQuantity > BundleFromQuantity;

    /// <summary>How many units one offer contains: the whole minimum order, or 1 for a normal product.</summary>
    public int GetPackSize(ProductInfo product) => IsBundle(product) ? product.MinOrderQuantity : 1;

    /// <summary>
    /// What one offer costs the buyer. For a pack the tier is chosen by the cost of the <b>whole pack</b>
    /// rather than of a single item: the steep multipliers on cheap goods exist to cover the fixed cost
    /// of one shipment, and a pack ships once no matter how many items it holds. Marking up per unit
    /// would charge that shipment 500 times over.
    /// </summary>
    public decimal GetOfferPrice(ProductInfo product)
    {
        var cost = product.Price * GetPackSize(product);
        return Math.Round(cost * GetMultiplier(cost), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Whole packs available - the leftover units cannot be sold on their own.</summary>
    public int GetOfferStock(ProductInfo product) => product.Count / GetPackSize(product);

    public decimal GetMultiplier(decimal price)
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
// 5904734418597 - 2.5zl * 10 , 5904734423737 - 14.5 * 3
public class PriceTier
{
    public decimal MaxPrice { get; set; }
    public decimal Multiplier { get; set; }
}
