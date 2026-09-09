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

var probeArg = args.FirstOrDefault(x => x.StartsWith("--probe-bundle="));
if (probeArg is not null)
{
    var probeEan = probeArg["--probe-bundle=".Length..].Trim();
    var templateArg = args.FirstOrDefault(x => x.StartsWith("--template-offer="));
    var templateOfferId = templateArg?["--template-offer=".Length..].Trim();

    var probePublisher = new AllegroPublisher();
    if (!probePublisher.Settings.IsConnected)
    {
        Console.WriteLine("Allegro account is not connected. Starting device authorization ...");
        var probeAuth = await probePublisher.StartDeviceFlowAsync(Console.WriteLine);
        Console.WriteLine($"Open {probeAuth.VerificationUri} and confirm code {probeAuth.UserCode}");
        if (!await probePublisher.PollForTokenAsync(probeAuth, Console.WriteLine))
        {
            Console.WriteLine("Could not connect the Allegro account. Aborting probe.");
            return;
        }
    }

    Console.WriteLine($"Probing bundle feasibility for EAN {probeEan} (read-only) ...");
    Console.WriteLine();
    await new BundleProbe(probePublisher).RunAsync(probeEan, templateOfferId, Console.WriteLine);
    return;
}

var editArg = args.FirstOrDefault(x => x.StartsWith("--test-bundle-edit="));
if (editArg is not null)
{
    var parts = editArg["--test-bundle-edit=".Length..].Split(':');
    var editEan = parts[0].Trim();
    var packSize = parts.Length > 1 && int.TryParse(parts[1], out var parsedPack) ? parsedPack : 10;

    var editPublisher = new AllegroPublisher();
    if (!editPublisher.Settings.IsConnected)
    {
        Console.WriteLine("Allegro account is not connected. Connect it first.");
        return;
    }

    Console.WriteLine($"Testing in-place multipack edit for EAN {editEan}, pack of {packSize}.");
    Console.WriteLine("The offer is put back the way it was at the end of the run.");
    Console.WriteLine();
    await new BundleEditTest(editPublisher).RunAsync(editEan, packSize, Console.WriteLine);
    return;
}

if (args.Contains("--orphans"))
{
    // Read-only: says which live offers the next publish would take off sale, and why.
    var orphanPublisher = new AllegroPublisher();
    var orphans = await orphanPublisher.FindOrphanOffersAsync(Console.WriteLine);

    Console.WriteLine();
    foreach (var group in orphans.GroupBy(o => o.Reason.Split(':')[0]).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"  {group.Count(),4}  {group.Key}");
    }
    Console.WriteLine();
    Console.WriteLine($"{orphans.Count} active offers would be ended by the next publish. Nothing was changed.");
    return;
}

var getArg = args.FirstOrDefault(x => x.StartsWith("--api-get="));
if (getArg is not null)
{
    // Read-only escape hatch for inspecting the account while building the creation path.
    var getPublisher = new AllegroPublisher();
    var (getStatus, getBody) = await getPublisher.GetRawAsync(getArg["--api-get=".Length..]);
    Console.WriteLine($"{getStatus}");
    Console.WriteLine(getBody);
    return;
}

if (args.Contains("--create-offers-plan"))
{
    var creatorPublisher = new AllegroPublisher();
    Console.WriteLine("Resolving catalogue cards for unlisted products (read-only) ...");
    Console.WriteLine();
    var candidates = await new OfferCreator(creatorPublisher).PlanAsync(Console.WriteLine);
    OfferCreator.Report(candidates, Console.WriteLine);
    return;
}

if (args.Contains("--unlisted"))
{
    // Read-only: which sellable products have no offer at all. PublishAsync only ever updates offers
    // that already exist, so these are invisible on Allegro no matter how often the job runs.
    var unlistedPublisher = new AllegroPublisher();
    var unlistedOptions = SaverExtensions.CSVOptions.Read();
    var sellable = SaverExtensions.Products.Read().Values
        .Where(CSVMaker.FilterProduct(unlistedOptions))
        .ToList();

    var known = await new BundlePlan(unlistedPublisher)
        .ResolveOffersAsync(sellable.Select(p => p.EAN), Console.WriteLine);

    var missing = sellable.Where(p => !known.ContainsKey(p.EAN)).ToList();
    Console.WriteLine();
    Console.WriteLine($"{sellable.Count} products pass the CSV options, {missing.Count} have no offer on Allegro:");
    Console.WriteLine($"  {missing.Count(p => unlistedOptions.IsBundle(p)),4}  would be packs");
    Console.WriteLine($"  {missing.Count(p => !unlistedOptions.IsBundle(p)),4}  would be single units");
    Console.WriteLine();
    foreach (var product in missing.OrderByDescending(p => p.Price * p.Count).Take(10))
    {
        Console.WriteLine($"  {product.EAN}  {unlistedOptions.GetOfferPrice(product),9:0.00}  " +
                          $"stock {unlistedOptions.GetOfferStock(product),5}  {product.Name}");
    }
    return;
}

if (args.Contains("--make-csv"))
{
    // Rebuild products.csv from the catalogue already on disk - no parsing, no Allegro. Lets the
    // pricing and the bundle split be inspected before a run pushes any of it to the account.
    var storedProducts = SaverExtensions.Products.Read().Values.ToList();
    var csvOptions = SaverExtensions.CSVOptions.Read();
    CSVMaker.MakeCSV(storedProducts, csvOptions);

    var bundles = storedProducts.Count(p => csvOptions.IsBundle(p) && p.Count > 0);
    var singles = storedProducts.Count(p => !csvOptions.IsBundle(p) && p.Count > 0);
    var dropped = storedProducts.Count(p => p.Count == 0);
    Console.WriteLine($"{CSVMaker.FileName} rebuilt from {storedProducts.Count} products: " +
                      $"{bundles} packs, {singles} singles, {dropped} excluded (offer would be ended).");
    return;
}

if (args.Contains("--bundle-plan") || args.Contains("--convert-bundles"))
{
    var apply = args.Contains("--convert-bundles");
    var onlyArg = args.FirstOrDefault(x => x.StartsWith("--only="));
    var onlyEan = onlyArg?["--only=".Length..].Trim();
    var limitArg = args.FirstOrDefault(x => x.StartsWith("--limit="));
    int? convertLimit = limitArg is not null && int.TryParse(limitArg["--limit=".Length..], out var parsedLimit)
        ? parsedLimit
        : null;

    var planPublisher = new AllegroPublisher();
    if (!planPublisher.Settings.IsConnected)
    {
        Console.WriteLine("Allegro account is not connected. Connect it first.");
        return;
    }

    Console.WriteLine(apply ? "Building the plan, then applying it ..." : "Building the bundle conversion plan (read-only) ...");
    Console.WriteLine();
    var plan = await new BundlePlan(planPublisher).BuildAsync(Console.WriteLine);
    BundlePlan.Report(plan, Console.WriteLine);

    if (apply)
    {
        Console.WriteLine();
        var toApply = onlyEan is null ? plan : plan.Where(c => c.Ean == onlyEan).ToList();
        if (toApply.Count == 0)
        {
            Console.WriteLine($"EAN {onlyEan} is not in the plan.");
            return;
        }
        await new BundleConverter(planPublisher).ConvertAsync(toApply, convertLimit, Console.WriteLine);
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

// Pack size lives in two places - MinOrderQuantity here and productSet.quantity on Allegro - and the
// supplier moves its minimums. Reconciling before publishing keeps them from drifting apart, which
// would otherwise price and stock every pack against a size Allegro is no longer selling.
if (!args.Contains("--no-bundles"))
{
    var bundlePlan = await new BundlePlan(publisher).BuildAsync(Console.WriteLine);
    await new BundleConverter(publisher).ConvertAsync(bundlePlan, log: Console.WriteLine);
}

var updated = await publisher.PublishAsync(Console.WriteLine);
Console.WriteLine($"Publish finished: {updated} offers updated.");