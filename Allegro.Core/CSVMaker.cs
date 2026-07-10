using System.Globalization;
using System.Text;

namespace Allegro.Core;

public static class CSVMaker
{
    public const string FileName = "products.csv"; 
    
    public static void MakeCSV(List<ProductInfo> products, CSVOptions options)
    {
        var filter = FilterProduct(options);
        var filteredProducts = products
            .Where(filter.Invoke)
            .ToList();
        filteredProducts.ForEach(x=>x.Price *= options.MultiplierPrice);
    
        var result = GetCSV(filteredProducts);
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
            stringBuilder.AppendLine(string.Join(";",product.EAN ,product.Count , (product.Price * 3).ToString(CultureInfo.InvariantCulture)));
        }
        return stringBuilder.ToString();
    }
}