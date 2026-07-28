namespace Allegro.Core;

public class CSVOptions
{
    public int MinimalProductCount { get; set; } = 10;
    public decimal MinimalPrice { get; set; } = 0m;
    public int MaxMinOrderQuantity { get; set; } = 10;
    public List<string> CategoriesBlackList { get; set; } = new List<string>();
    public List<string> EansBlackList { get; set; } = new List<string>();
    
    public List<PriceTier> PriceMultipliers { get; set; } = new()
    {
        new PriceTier { MaxPrice = 10m, Multiplier = 1.5m },
        new PriceTier { MaxPrice = 20m, Multiplier = 1.2m },
    };
    public decimal DefaultMultiplier { get; set; } = 3m;

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
