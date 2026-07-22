using System.Text.Json;
using Microsoft.Playwright;
using Allegro.Core;
namespace Allegro.Console;

public class ProductParcer
{
    public async Task<IBrowserContext> CreateBrowserContext()
    {
        var playwright = await Playwright.CreateAsync();
        var args = new List<string> 
        { 
            "--disable-blink-features=AutomationControlled", 
            "--no-sandbox"
        };
        
        string extensionPathVpn = Path.Combine(SaverExtensions.ResourceDirectory,"nord_vpn");
        string extensionPathCaptcha = Path.Combine(SaverExtensions.ResourceDirectory,"captcha");
        if (!Directory.Exists(extensionPathCaptcha))
        {
            System.Console.WriteLine($"Extension not found{extensionPathCaptcha}");
            extensionPathCaptcha = "";
        }

        if (!Directory.Exists(extensionPathVpn))
        {
            System.Console.WriteLine($"Extension not found{extensionPathVpn}");
            extensionPathVpn = "";
        }
        
        args.Add($"--disable-extensions-except={extensionPathVpn},{extensionPathCaptcha}");
        args.Add($"--load-extension={extensionPathVpn},{extensionPathCaptcha}");
        var browser = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(SaverExtensions.ResourceDirectory,"PlaywrightData"), new BrowserTypeLaunchPersistentContextOptions()
        {
            Headless = false,
            Args = args ,
            Env = new Dictionary<string, string>()
            {
                { "DISPLAY", ":99" }
            }
        });
        
        return browser;
    }

    public async Task<ParseResponse> NewParse(List<string> urls, bool isUserStarts,int startIndex = 0)
    {
        return await FinishParse(new ParseResponse() {AllUrls = urls,CurrentIndexUrl = startIndex},isUserStarts); 
    }
    
    /// <param name="response">Must be a new object as it uses a static set for <see cref="SaverExtensions.LastParse"/> and may throw exception when modifying a list</param>
    public async Task<ParseResponse> FinishParse(ParseResponse response,bool isUserStarts)
    {
        var browser = await CreateBrowserContext();
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*.{png,jpg,jpeg,gif,svg}", async route => await route.AbortAsync());
        await Task.Delay(2000);
        var pagesToDelete = browser.Pages.Where(x=> x != page).ToArray();
        foreach (IPage pageToDelete in pagesToDelete)
        {
            await pageToDelete.CloseAsync();
        }
        
        ProductExtracter extracter = new ProductExtracter(page);
        
        for(; response.CurrentIndexUrl < response.AllUrls.Count; response.CurrentIndexUrl++)
        {
            var url = response.AllUrls[response.CurrentIndexUrl];
            ProductInfo product;
            System.Console.WriteLine($"Url: {url}\n");
            try
            {
                product = await extracter.Extract(url, isUserStarts);
            }
            catch (InvalidProductException e)
            {
                response.BlackListUrls.Add(url);
                System.Console.WriteLine($"Invalid product {e.Message}: {url}");
                SaverExtensions.LastParse.Value = response;
                SaverExtensions.LastParse.Write();
                continue;
            }
            catch (ParserException)
            {
                break;
            }
            catch (Exception e)
            {
                System.Console.WriteLine($"Unhandled exception: {e.Message}");
                if (isUserStarts) await Task.Delay(5000);
                continue;
            }
            
            response.Products[product.Url] = product;
            SaverExtensions.LastParse.Value = response;
            SaverExtensions.LastParse.Write();
            System.Console.WriteLine($"Handled {response.CurrentIndexUrl} / {response.AllUrls.Count}\n");
        }
        await browser.CloseAsync();
        return response;
    }
}

