namespace Allegro.Core;

public class ProductInfo
{
    public string Url { get; set; }
    public decimal Price { get; set; }
    public string Name { get; set; }
    public int Count { get; set; }
    public string EAN { get; set; }
    public string[] CategoriesUrls { get; set; } = Array.Empty<string>();
}