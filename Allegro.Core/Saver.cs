using System.Text.Json;

namespace Allegro.Core;

public class Saver<T> where T : class
{
    public T Value { get; set; } 

    public string _filePath { get; }

    public Saver(string fileName)
    {
        _filePath = Path.Combine(Directory.GetCurrentDirectory(), "Resources", fileName);
        Value = Read();
        
    }
    
    public void Write()
    {
        try
        {
            var content = JsonSerializer.Serialize(Value);
            File.WriteAllText(_filePath, content);
        }
        catch(DirectoryNotFoundException)
        {
            Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "Resources"));
            Write();
        }
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
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "Resources"));
            return Read();
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
    
    
}