using System.Text.Json;
using Microsoft.Playwright;
using Allegro.Core;
using Microsoft.Extensions.Configuration;

namespace Allegro.Console;

public class ProductParcer
{
    public async Task<IBrowserContext> CreateBrowserContext(bool visible = false)
    {
        var playwright = await Playwright.CreateAsync();
        var args = new List<string>
        {
            "--disable-blink-features=AutomationControlled",
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-dev-shm-usage",
            "--disable-gpu",
            "--disable-software-rasterizer",
            "--disable-background-networking",

        };

        if (!visible)
        {
            args.Add("--headless=new");
        }
        
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
        var browser = await CreateBrowserContext(isUserStarts);
        var page = await browser.NewPageAsync();
        
        ProductExtracter extracter = new ProductExtracter(page);
        int failuresLogin = 0;
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
            catch (ParseProductException e)
            {
                System.Console.WriteLine($"Can`t parse product: {url} {e.Message}");
                continue;
            }
            catch (ParserException)
            {
                break;
            }
            catch (MemberAccessException m)
            {
                failuresLogin++;
                if (failuresLogin > 2)
                {
                    var config = new ConfigurationBuilder()
                        .SetBasePath(AppContext.BaseDirectory) 
                        .AddJsonFile("coresettings.json", optional: false, reloadOnChange: true)
                        .Build();
        
                    string? botToken = config.GetSection("Telegram").GetSection("BotToken").Value ?? throw new FormatException("No botToken in coresettings.json");
                    string? chatId = config.GetSection("Telegram").GetSection("ChatId").Value ?? throw new FormatException("No chatId in coresettings.json");
                    var telegram = new TelegramNotify();
                    await telegram.SendAsync(botToken, chatId, "Allegro parser:\n"+m);
                    throw;
                }
                else
                {
                    continue;
                }
            }
            catch (Exception e)
            {
                System.Console.WriteLine($"Unhandled exception: {e.Message}");
                if (isUserStarts) await Task.Delay(5000);
                continue;
            }
            if(product.Price < 0)
                if(SaverExtensions.Products.Value.TryGetValue(product.Url,out var savedProduct)) product.Price = savedProduct.Price;
            response.Products[product.Url] = product;
            SaverExtensions.LastParse.Value = response;
            SaverExtensions.LastParse.Write();
            System.Console.WriteLine($"Handled {response.CurrentIndexUrl} / {response.AllUrls.Count}\n");
        }
        await browser.CloseAsync();
        return response;
    }
}

