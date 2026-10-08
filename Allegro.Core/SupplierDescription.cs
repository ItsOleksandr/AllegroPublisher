using System.Net;
using System.Text.RegularExpressions;

namespace Allegro.Core;

public static class SupplierDescription
{
    private const int MaxLength = 4000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly Regex Boilerplate = new(
        @"^(Cena dotyczy|Pamiętaj drogi kliencie|KARTON SZTUK|Informacja o sztucznej inteligencji|Treści, opisy i zdjęcia wygenerowane)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Panel = new(
        @"<div[^>]*id=""tab-description""[^>]*>(.*?)</div>\s*(?:<div[^>]*class=""[^""]*woocommerce-Tabs-panel|</div>)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineBreak = new(@"<br\s*/?>|</p>|</li>|</h\d>|</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);

    public static string Clean(string text)
    {
        var lines = text.Split('\n')
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length > 0 && !Boilerplate.IsMatch(line));
        var description = string.Join("\n", lines);
        return description.Length > MaxLength ? description[..MaxLength] : description;
    }

    public static async Task<string> FetchAsync(string productUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, productUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return "";
            }

            var match = Panel.Match(await response.Content.ReadAsStringAsync());
            if (!match.Success)
            {
                return "";
            }

            var text = WebUtility.HtmlDecode(Tag.Replace(LineBreak.Replace(match.Groups[1].Value, "\n"), ""));
            return Clean(text);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return "";
        }
    }
}
