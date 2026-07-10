using Allegro.Core;
using Allegro.Console;

if (args.Contains("--configure-browser"))
{
    Console.WriteLine("Starting browser ...");
    ProductParcer productParcerConfigure = new ProductParcer();
    var configureBrowser = await productParcerConfigure.CreateBrowserContext();
    await configureBrowser.NewPageAsync();
    Console.WriteLine("Browser started");
    await Task.Delay(-1);
    await configureBrowser.CloseAsync();
    return;
}

string? startIndexArg = args.FirstOrDefault(x => x.StartsWith("--start-index="));
int startIndex = int.TryParse(startIndexArg?.Split('=')[1], out int index) ? index : 0;
bool isUser = args.Contains("--mode=manual");
bool needParse = args.Contains("--mode-xml=new_parse");
bool loadLastSession = args.Contains("--mode-xml=load_last_session");

List<string> urls;
if (needParse)
{
    Console.WriteLine("Start parsing products urls ...");
    SiteMapExtracter siteMapExtracter = new SiteMapExtracter();
    urls = await siteMapExtracter.ExtractFromUrls("https://allenett.pl/product-sitemap1.xml","https://allenett.pl/product-sitemap2.xml","https://allenett.pl/product-sitemap3.xml","https://allenett.pl/product-sitemap4.xml");
    if(!isUser) urls.RemoveAll(x=>SaverExtensions.UrlsBlackList.Value.Contains(x));
}
else
{
    urls = SaverExtensions.Urls.Value;
}

ProductParcer productParcer = new ProductParcer();
var taskParsing = loadLastSession
    ? productParcer.FinishParse(SaverExtensions.LastParse.Read(), isUser)
    : productParcer.NewParse(urls, isUser,startIndex);
ParseResponse responseParsing = await taskParsing;

Console.WriteLine($"Black urls:{responseParsing.BlackListUrls.Count}\nProducts:{responseParsing.Products.Count}");

foreach (var product in responseParsing.Products.Values)
{
    SaverExtensions.Products.Value[product.Url] = product;
}
SaverExtensions.Products.Write();
    
var blackList = SaverExtensions.UrlsBlackList.Value;
blackList.AddRange(responseParsing.BlackListUrls);
blackList = blackList.Distinct().ToList();
SaverExtensions.UrlsBlackList.Value = blackList;
SaverExtensions.UrlsBlackList.Write();
    
urls.RemoveAll(x => responseParsing.BlackListUrls.Contains(x));
SaverExtensions.Urls.Value = urls;
SaverExtensions.Urls.Write();

CSVMaker.MakeCSV(responseParsing.Products.Values.ToList(),SaverExtensions.CSVOptions.Value);

var publisher = new AllegroPublisher();

if (!publisher.Settings.IsConnected)
{
    Console.WriteLine("Allegro account is not connected. Starting device authorization ...");
    var auth = await publisher.StartDeviceFlowAsync(Console.WriteLine);
    Console.WriteLine($"Open {auth.VerificationUri} and confirm code {auth.UserCode}");
    if (!await publisher.PollForTokenAsync(auth, Console.WriteLine))
    {
        Console.WriteLine("Could not connect the Allegro account. Aborting publish.");
        return;
    }
}

var updated = await publisher.PublishAsync(Console.WriteLine);
Console.WriteLine($"Publish finished: {updated} offers updated.");