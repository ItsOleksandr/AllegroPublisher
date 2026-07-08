using System.Text.Json;
using Microsoft.Playwright;
using Allegro.Core;
namespace Allegro.Console;

public class ProductParcer
{
    public async Task<IBrowserContext> CreateBrowserContext(bool headless)
    {
        var playwright = await Playwright.CreateAsync();
        var args = new List<string> 
        { 
            "--disable-blink-features=AutomationControlled", 
            "--no-sandbox"
        };

        if (!headless)
        {
            args.Add("--remote-debugging-port=9222");
            args.Add("--remote-debugging-address=0.0.0.0");
        }
        string extensionPathVpn = Path.Combine(Directory.GetCurrentDirectory(),"Resources","nord_vpn");
        string extensionPathCaptcha = Path.Combine(Directory.GetCurrentDirectory(),"Resources","captcha");
        
        args.Add($"--disable-extensions-except={extensionPathVpn},{extensionPathCaptcha}");
        args.Add($"--load-extension={extensionPathVpn},{extensionPathCaptcha}");
        var browser = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(Directory.GetCurrentDirectory(),"Resources","PlaywrightData"), new BrowserTypeLaunchPersistentContextOptions()
        {
            Headless = headless,
            Args = args 
        });
        
        return browser;
    }
    
    public async Task<ParseResponse> Parse(string[] urls,bool isUserStarts,int startIndex = 0,bool loadLastSession = true)
    {
        var browser = await CreateBrowserContext(!isUserStarts);
        var page = await browser.NewPageAsync();
        
        ProductExtracter extracter = new ProductExtracter(page);
        string pathForSaveSession = Path.Combine(Directory.GetCurrentDirectory(),"Resources","last_parse.txt");
        var response = new ParseResponse();
        if (loadLastSession)
        {
            response = LoadSession(pathForSaveSession);
            startIndex = response.Products.Count-1;
        }
        
        for(int i = startIndex; i < urls.Length; i++)
        {
            var url = urls[i];
            ProductInfo product;
            System.Console.WriteLine($"Url: {url}\n");
            try
            {
                product = await extracter.Extract(url,isUserStarts);
            }
            catch (InvalidProductException e)
            {
                response.BlackListUrls.Add(url);
                System.Console.WriteLine($"Invalid product {e.Message}: {url}");
                SaveSession(pathForSaveSession, response);
                continue;
            }
            catch (ProductAlreadyHandledException)
            {
                continue;
            }
            catch (ParserException)
            {
                break;
            }
            
            response.Products[product.Url] = product;
            SaveSession(pathForSaveSession, response);
            System.Console.WriteLine($"Handled {i} / {urls.Length}\n");
        }
        await browser.CloseAsync();
        return response;
    }
    
    public void SaveSession(string path, ParseResponse response)
    {
        var content = JsonSerializer.Serialize(response);
        File.WriteAllText(path, content);
    }
    public ParseResponse LoadSession(string path)
    {
        var content = File.ReadAllText(path);
        var response = JsonSerializer.Deserialize<ParseResponse>(content);
        return response ?? new ParseResponse();
    }
}

public class ParseResponse
{
    public List<string> BlackListUrls { get; set; } = new List<string>();
    public Dictionary<string, ProductInfo> Products { get; set; } = new Dictionary<string, ProductInfo>();
}