using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Allegro.Core;

public class Saver<T> where T : class
{
    public T Value { get; set; } 

    public string FilePath { get; }

    public Saver(string fileName)
    {
        FilePath = Path.Combine(SaverExtensions.ResourceDirectory, fileName);
        Value = Read();
        
    }
    
    public void Write()
    {
        var content = JsonSerializer.Serialize(Value);
        File.WriteAllText(FilePath, content);
    }

    public T Read()
    {
        try
        {
            var content = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<T>(content) ?? throw new JsonException("Invalid JSON");
        }
        catch (Exception e) when (e is JsonException or FileNotFoundException)
        {
            Value = Activator.CreateInstance<T>();
            return Value;
        }
    }
}

public static class SaverExtensions
{
    public static string ResourceDirectory => _resourceDirectory.Value;
    private static readonly Lazy<string> _resourceDirectory = new Lazy<string>(() => 
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory) 
            .AddJsonFile("coresettings.json", optional: false, reloadOnChange: true)
            .Build();
        
        string? path = config.GetSection("BaseDirectory").Value;
        
        if (string.IsNullOrEmpty(path))
            throw new NullReferenceException(
                "ResourcePath is null or empty in appsettings.json. Please set it to a valid path.");
        
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        
        return path;
    });
    
    public static readonly Saver<List<string>> Urls = new Saver<List<string>>("urls.txt");
    public static readonly Saver<List<string>> UrlsBlackList = new Saver<List<string>>("urls_black_list.txt");
    public static readonly Saver<Dictionary<string,ProductInfo>> Products = new Saver<Dictionary<string,ProductInfo>>("products_dictionary.txt");
    public static readonly Saver<CSVOptions> CSVOptions = new Saver<CSVOptions>("csv_options.txt");
    public static readonly Saver<AllenetCreditails> Creaditails = new Saver<AllenetCreditails>("creditials.txt");
    public static readonly Saver<ParseResponse> LastParse = new Saver<ParseResponse>("last_parse.txt");
    public static readonly Saver<AllegroSettings> AllegroSettings = new Saver<AllegroSettings>("allegro_settings.txt");

}