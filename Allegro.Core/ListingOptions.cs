namespace Allegro.Core;

public class ListingOptions
{
    public int MinimalProductCount { get; set; } = 10;
    public decimal MinimalPrice { get; set; } = 0m;
    public int BundleFromQuantity { get; set; } = 5;
    public int StaleAfterHours { get; set; } = 48;
    public bool AddOwnStockToSupplier { get; set; } = false;
    public string NewOfferTemplateId { get; set; } = "";
    public bool AutoCreateOffers { get; set; } = true;
    public bool AiCheckNewOffers { get; set; } = true;
    public string ForbiddenGoods { get; set; } = DefaultForbiddenGoods;

    public const string DefaultForbiddenGoods =
        "- elektronika i sprzęt elektryczny: wszystko, co działa z sieci, na baterie, akumulator lub USB " +
        "(np. latarki, lampki, oświetlenie LED, girlandy, detektory, kamery, zegarki i zabawki na baterie, wentylatory);\n" +
        "- oświetlenie każdego rodzaju, także świecące elementy i produkty z diodami LED;\n" +
        "- telefony, komputery i akcesoria z elektroniką (ładowarki, kable, powerbanki, słuchawki, głośniki);\n" +
        "- baterie i akumulatory.";
    public int AutoContentPerRun { get; set; } = 50;
    public string NewOfferSafetyText { get; set; } = "";

    public const string StandardSafetyText =
        "Informacje dotyczące bezpieczeństwa zgodnie z Rozporządzeniem (UE) 2023/988 w sprawie ogólnego bezpieczeństwa produktów (GPSR):\n" +
        "- Przed użyciem zapoznaj się z opisem produktu i używaj go wyłącznie zgodnie z przeznaczeniem.\n" +
        "- Przed każdym użyciem sprawdź, czy produkt nie jest uszkodzony. Nie używaj produktu uszkodzonego ani niekompletnego.\n" +
        "- Przechowuj produkt i opakowanie poza zasięgiem małych dzieci. Drobne elementy oraz folia mogą stwarzać ryzyko zadławienia lub uduszenia.\n" +
        "- Produkt nie jest zabawką, chyba że opis stanowi inaczej. Jeśli korzystają z niego dzieci, powinno to odbywać się pod nadzorem osoby dorosłej.\n" +
        "- Zachowaj ostrożność przy elementach ostrych, gorących lub ruchomych.\n" +
        "- Przechowuj produkt w suchym miejscu, z dala od źródeł ciepła i otwartego ognia, chyba że jest do tego przeznaczony.\n" +
        "- Czyść produkt odpowiednio do materiału, z którego jest wykonany. Nie używaj żrących środków czyszczących.\n" +
        "- Zużyty produkt i opakowanie przekaż do odpowiedniego punktu zbiórki odpadów.";
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

    public bool IsBlackListed(ProductInfo product) =>
        product.CategoriesUrls.Any(url => CategoriesBlackList.Any(url.Contains))
        || EansBlackList.Contains(product.EAN);

    public bool Includes(ProductInfo product) =>
        !IsBlackListed(product)
        && !string.IsNullOrWhiteSpace(product.EAN)
        && !product.EAN.Contains("—")
        && product.Price >= MinimalPrice
        && product.Count >= MinimalProductCount
        && GetOfferStock(product) >= 1;

    public bool IsStale(ProductInfo product) =>
        StaleAfterHours > 0 && product.LastUpdate < DateTime.Now.AddHours(-StaleAfterHours);

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

    public decimal GetOfferPrice(ProductInfo product, int pack) => GetOfferPrice(product, pack, product.Price);

    public decimal GetOfferPrice(ProductInfo product, int pack, decimal unitPrice)
    {
        var cost = unitPrice * pack;

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
