using System.Globalization;
using System.Text;

namespace Allegro.Core;

public static class CSVMaker
{
    public const string FileName = "products.csv"; 
    
    public static void MakeCSV(List<ProductInfo> products, CSVOptions options)
    {
        var csvOptions = SaverExtensions.CSVOptions.Value;
        var filteredProducts = products
            .Where(x => !x.CategoriesUrls
                            .Any(categoryUrl => csvOptions.CategoriesBlackList
                                .Any(categoryUrl
                                    .Contains)) 
                        && x.Count >= csvOptions.MinimalProductCount
                        && x.Price >= csvOptions.MinimalPrice
                        && !x.EAN.Contains("—"))
            .ToList();
        filteredProducts.ForEach(x=>x.Price *= csvOptions.MultiplierPrice);
    
        var result = CSVMaker.GetCSV(filteredProducts);
        File.WriteAllText(Path.Combine(SaverExtensions.ResourceDirectory,FileName),result);
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