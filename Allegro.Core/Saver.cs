using System.Text.Json;

namespace Allegro.Core;

public class Saver<T> where T : class
{
    public T Value { get; set; } 

    public string _filePath { get; }

    public Saver(string fileName)
    {
        _filePath = Path.Combine(SaverExtensions.ResourceDirectory, fileName);
        Value = Read();
        
    }
    
    public void Write()
    {
        var content = JsonSerializer.Serialize(Value);
        File.WriteAllText(_filePath, content);
    }

    public T Read()
    {
        try
        {
            var content = File.ReadAllText(_filePath);
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
    public static readonly Saver<List<string>> Urls = new Saver<List<string>>("urls.txt");
    public static readonly Saver<List<string>> UrlsBlackList = new Saver<List<string>>("urls_black_list.txt");
    public static readonly Saver<Dictionary<string,ProductInfo>> Products = new Saver<Dictionary<string,ProductInfo>>("products_dictionary.txt");
    public static readonly Saver<CSVOptions> CSVOptions = new Saver<CSVOptions>("csv_options.txt");
    public static readonly Saver<AllenetCreditails> Creaditails = new Saver<AllenetCreditails>("creditials.txt");
    public static readonly Saver<ParseResponse> LastParse = new Saver<ParseResponse>("last_parse.txt");

    public static readonly string ResourceDirectory;

    static SaverExtensions()
    {
        string resourceDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Resources");
        ResourceDirectory = resourceDirectory;
        if (!Directory.Exists(resourceDirectory))
        {
            Directory.CreateDirectory(resourceDirectory);
        }
    }
}