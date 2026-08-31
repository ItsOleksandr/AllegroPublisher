using System.Text.Json;
using Allegro.Core;
using Allegro.Console;

bool isVisible = args.Contains("--visible");

var testUrlArg = args.FirstOrDefault(x => x.StartsWith("--test-url="));
if (testUrlArg is not null)
{
    var url = testUrlArg["--test-url=".Length..].Trim();
    Console.WriteLine($"Test parsing: {url}");

    var testParcer = new ProductParcer();
    var testBrowser = await testParcer.CreateBrowserContext(isVisible);
    var testPage = await testBrowser.NewPageAsync();
    try
    {
        var product = await new ProductExtracter(testPage).Extract(url);
        Console.WriteLine("=== PARSED OK ===");
        Console.WriteLine(JsonSerializer.Serialize(product, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception e)
    {
        Console.WriteLine($"=== PARSE FAILED: {e.GetType().Name}: {e.Message} ===");
    }
    finally
    {
        await testBrowser.CloseAsync();
    }
    return;
}

if (args.Contains("--configure-browser"))
{
    Console.WriteLine("Starting browser ...");
    ProductParcer productParcerConfigure = new ProductParcer();
    var configureBrowser = await productParcerConfigure.CreateBrowserContext(isVisible);
    await configureBrowser.NewPageAsync();
    Console.WriteLine("Browser started");
    await Task.Delay(-1);
    await configureBrowser.CloseAsync();
    return;
}


bool loadLastSession = args.Contains("--load_last_session");

ProductParcer productParcer = new ProductParcer();
Task<ParseResponse> taskParsing;
if (loadLastSession)
{
    taskParsing = productParcer.FinishParse(SaverExtensions.LastParse.Read(), isVisible);
}
else
{
    SiteMapExtracter siteMapExtracter = new SiteMapExtracter();
    var urls = await siteMapExtracter.ExtractFromUrls("https://allenett.pl/product-sitemap1.xml","https://allenett.pl/product-sitemap2.xml","https://allenett.pl/product-sitemap3.xml","https://allenett.pl/product-sitemap4.xml");
    urls.AddRange(SaverExtensions.Products.Value.Values.Select(x => x.Url).ToList());
    urls = urls.Distinct().ToList();
    
    string? startIndexArg = args.FirstOrDefault(x => x.StartsWith("--start-index="));
    int startIndex = int.TryParse(startIndexArg?.Split('=')[1], out int index) ? index : 0;
    
    taskParsing = productParcer.NewParse(urls, isVisible,startIndex);
}

ParseResponse responseParsing = await taskParsing;

Console.WriteLine($"Black urls:{responseParsing.BlackListUrls.Count}\nProducts:{responseParsing.Products.Count}");

foreach (var product in responseParsing.Products.Values)
{
    SaverExtensions.Products.Value[product.Url] = product;
}
SaverExtensions.Products.Write();

CSVMaker.MakeCSV(SaverExtensions.Products.Read().Values.ToList(),SaverExtensions.CSVOptions.Value);

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