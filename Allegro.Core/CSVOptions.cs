namespace Allegro.Core;

public class CSVOptions
{
    public int MinimalProductCount { get; set; } = 10;
    public decimal MultiplierPrice { get; set; } = 3m;
    public decimal MinimalPrice { get; set; } = 0m;
    public List<string> CategoriesBlackList { get; set; } = new List<string>();
    public List<string> EansBlackList { get; set; } = new List<string>();
}