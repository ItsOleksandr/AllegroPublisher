namespace Allegro.Core;

public class ParseResponse
{
    public List<string> BlackListUrls { get; set; } = new List<string>();
    public Dictionary<string, ProductInfo> Products { get; set; } = new Dictionary<string, ProductInfo>();
    public int CurrentIndexUrl { get; set; } = 0;
    public List<string> AllUrls { get; set; } = new List<string>();
}