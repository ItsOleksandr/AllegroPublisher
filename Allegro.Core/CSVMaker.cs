using System.Globalization;
using System.Text;

namespace Allegro.Core;

public static class CSVMaker
{
    public const string FileName = "products.csv"; 
    
    public static void MakeCSV(List<ProductInfo> products, CSVOptions options)
    {
        var filter = FilterProduct(options);
        foreach (ProductInfo productInfo in products)
        {
            var isValid = filter.Invoke(productInfo);

            // Both are pack-aware: a bundled product is priced and counted per pack, a normal one per unit.
            productInfo.Price = options.GetOfferPrice(productInfo);
            productInfo.Count = isValid ? options.GetOfferStock(productInfo) : 0;
        }
    
        var result = GetCSV(products);
        File.WriteAllText(Path.Combine(SaverExtensions.ResourceDirectory,FileName),result);
    }

    public static Func<ProductInfo, bool> FilterProduct(CSVOptions options)
    {
        return x => !x.CategoriesUrls
                               .Any(categoryUrl => options.CategoriesBlackList
                                   .Any(categoryUrl
                                       .Contains))
                           && !options.EansBlackList.Contains(x.EAN)
                           && x.Count >= options.MinimalProductCount
                           && x.Price >= options.MinimalPrice
                           // A high minimum order is not a reason to drop a product - it is the reason
                           // to sell it as a pack, which only needs one whole pack to be in stock.
                           && (!options.IsBundle(x) || x.Count >= x.MinOrderQuantity)
                           && !x.EAN.Contains("—");
    }
    
    private static string GetCSV(List<ProductInfo> products)
    {
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.AppendLine("EAN;Liczba;Cena");

        foreach (var product in products)
        {
            stringBuilder.AppendLine(string.Join(";",product.EAN ,product.Count , product.Price.ToString(CultureInfo.InvariantCulture)));
        }
        return stringBuilder.ToString();
    }
}