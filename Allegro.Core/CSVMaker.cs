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
            if (!isValid) productInfo.Count = 0;
            
            productInfo.Price *= options.MultiplierPrice;
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
                           && x.Count >= options.MinimalProductCount
                           && x.Price >= options.MinimalPrice
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