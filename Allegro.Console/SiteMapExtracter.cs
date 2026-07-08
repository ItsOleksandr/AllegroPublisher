using System.Xml.Linq;
namespace Allegro.Console;

public class SiteMapExtracter
{
    public async Task<List<string>> ExtractFromUrls(params string[] urls)
    {
        List<string> mappedUrls = new List<string>();
        foreach (var url in urls)
        {
            mappedUrls.AddRange(await ExtractFromUrl(url));
        }
        var uniqueUrls = mappedUrls.Distinct().ToList(); 
        
        return uniqueUrls;
    }
    
    public async Task<string[]> ExtractFromUrl(string url)
    {
        using HttpClient client = new HttpClient();
        var xml = await client.GetStringAsync(url);
        XDocument doc = XDocument.Parse(xml);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var urls = doc.Descendants(ns + "url").Select(x => x.Element(ns + "loc")!.Value).ToArray();
        return urls;
    }
}