using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Allegro.Core;

public sealed class ContentGenerator
{
    private const string EanParameterId = "225693";
    private const int Parallelism = 4;
    private const int Attempts = 3;

    private static readonly Regex PackPrefix = new(@"^Zestaw \d+ szt\. ", RegexOptions.Compiled);
    private static readonly Regex SupplierCode = new(@"\bXJ\d+\b|\b\d{5}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Number = new(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled);
    private static readonly Regex AnyTag = new(@"</?\s*([a-zA-Z][a-zA-Z0-9]*)[^>]*>", RegexOptions.Compiled);
    private static readonly Regex AllowedTag = new(@"<(/?)\s*(h1|h2|p|ul|ol|li|b)\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase) { "h1", "h2", "p", "ul", "ol", "li", "b" };

    private readonly AllegroPublisher _publisher;
    private readonly ConcurrentDictionary<string, string> _categoryNames = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public ContentGenerator(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    public record Candidate(string OfferId, string Ean, string Name, decimal Price, int Weakness);

    public async Task<List<Candidate>> PickWeakestAsync(int count, Action<string>? log = null)
    {
        var drafts = SaverExtensions.ContentDrafts.Read();
        var offers = await ListOwnOffersAsync();
        var picked = offers
            .Where(o => !drafts.ContainsKey(o.Ean))
            .OrderByDescending(o => o.Weakness)
            .ThenByDescending(o => o.Price)
            .Take(count)
            .ToList();

        log?.Invoke($"{offers.Count} active offers, {offers.Count(o => drafts.ContainsKey(o.Ean))} already have a draft. " +
                    $"Picked {picked.Count} with the weakest titles.");
        return picked;
    }

    public async Task<int> GenerateAsync(IReadOnlyCollection<Candidate> offers, Action<string>? log = null)
    {
        var ai = new AiClient();
        log?.Invoke($"Generating titles and descriptions for {offers.Count} offers with {ai.Model} ...");

        var drafts = SaverExtensions.ContentDrafts.Read();
        var supplier = SaverExtensions.Products.Read().Values
            .Where(p => !string.IsNullOrWhiteSpace(p.EAN))
            .GroupBy(p => p.EAN, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.LastUpdate).First(), StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(Parallelism);
        int done = 0, failed = 0;

        await Task.WhenAll(offers.Select(async offer =>
        {
            await gate.WaitAsync();
            try
            {
                var draft = await GenerateOneAsync(ai, offer.OfferId, offer.Ean, supplier.GetValueOrDefault(offer.Ean));
                await _saveLock.WaitAsync();
                try
                {
                    drafts[draft.Ean] = draft;
                    SaverExtensions.ContentDrafts.Value = drafts;
                    SaverExtensions.ContentDrafts.Write();
                }
                finally
                {
                    _saveLock.Release();
                }

                Interlocked.Increment(ref done);
                log?.Invoke($"  {draft.Ean}: {draft.Name}{(draft.Warning is null ? "" : $"  [check] {draft.Warning}")}");
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref failed);
                log?.Invoke($"  {offer.Ean} offer {offer.OfferId}: {e.Message}");
            }
            finally
            {
                gate.Release();
            }
        }));

        log?.Invoke($"Generation finished: {done} drafts, {failed} failed. " +
                    $"{ai.Calls} AI requests, tokens: {ai.InputTokens:N0} in, {ai.OutputTokens:N0} out ({ai.Model}).");
        return done;
    }

    public static int Weakness(string name)
    {
        var baseName = PackPrefix.Replace(name, "");
        var score = 0;
        if (SupplierCode.IsMatch(baseName))
        {
            score += 2;
        }

        var letters = baseName.Where(char.IsLetter).ToList();
        if (letters.Count > 0 && letters.Count(char.IsUpper) * 5 > letters.Count * 4)
        {
            score += 1;
        }

        if (baseName.Length < 32)
        {
            score += 2;
        }

        return score;
    }

    private async Task<List<Candidate>> ListOwnOffersAsync()
    {
        var result = new List<Candidate>();
        const int limit = 1000;
        for (int offset = 0; ; offset += limit)
        {
            var page = await GetJsonAsync($"/sale/offers?publication.status=ACTIVE&limit={limit}&offset={offset}");
            var offers = page["offers"]?.AsArray() ?? new JsonArray();
            foreach (var offer in offers)
            {
                var ean = offer?["external"]?["id"]?.GetValue<string>();
                var id = offer?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(ean) || string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var name = offer?["name"]?.GetValue<string>() ?? "";
                var amount = offer?["sellingMode"]?["price"]?["amount"]?.GetValue<string>();
                result.Add(new Candidate(id, ean, name,
                    decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) ? price : 0m,
                    Weakness(name)));
            }

            if (offers.Count < limit)
            {
                break;
            }
        }
        return result;
    }

    private record Source(
        string OfferId, string Ean, string Name, int Pack, string? CategoryName, string? CardName,
        List<string> Parameters, string Description, string CardDescription,
        string? SupplierName, string? SupplierDescription);

    private async Task<ContentDraft> GenerateOneAsync(AiClient ai, string offerId, string ean, ProductInfo? product)
    {
        var supplierDescription = product?.Description;
        if (string.IsNullOrWhiteSpace(supplierDescription) && !string.IsNullOrEmpty(product?.Url))
        {
            supplierDescription = await SupplierDescription.FetchAsync(product.Url);
        }

        var source = await ReadSourceAsync(offerId, ean) with
        {
            SupplierName = product?.Name,
            SupplierDescription = string.IsNullOrWhiteSpace(supplierDescription) ? null : supplierDescription,
        };
        var limit = BundlePlan.MaxNameLength - BundlePlan.BuildName("", source.Pack).Length;
        var system = BuildSystemPrompt(limit, source.Pack);
        var trustAllegro = source.SupplierName is null || CardConflicts(source).Count == 0;
        if (!trustAllegro && source.SupplierDescription is null)
        {
            throw new InvalidOperationException("the Allegro card disagrees with the supplier and the supplier's description " +
                                                "could not be read - skipped, try again later.");
        }
        var data = new Dictionary<string, object?>
        {
            ["DOSTAWCA - nazwa produktu"] = source.SupplierName ?? "(brak danych dostawcy)",
            ["DOSTAWCA - opis produktu"] = source.SupplierDescription ?? "(brak)",
            ["kategoria Allegro"] = source.CategoryName,
        };
        if (trustAllegro)
        {
            data["obecny tytuł oferty"] = source.Name;
            data["nazwa karty produktu w katalogu Allegro"] = source.CardName;
            data["parametry"] = source.Parameters;
            data["obecny opis (HTML)"] = HasText(source.Description) ? source.Description
                : HasText(source.CardDescription) ? source.CardDescription : "(brak opisu)";
        }
        else
        {
            data["uwaga"] = "Karta katalogu Allegro i obecny opis oferty opisują inny produkt niż dostawca, więc zostały pominięte. Pisz wyłącznie na podstawie danych dostawcy.";
        }
        var content = "Dane produktu:\n" + JsonSerializer.Serialize(data,
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        string? problem = null;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            var answer = await ai.CompleteAsync(system, content);
            var parsed = Parse(answer, limit, out problem);
            if (parsed is not null)
            {
                return new ContentDraft
                {
                    Ean = ean,
                    OfferId = offerId,
                    OldName = source.Name,
                    BaseName = parsed.Value.Title,
                    Name = BundlePlan.BuildName(parsed.Value.Title, source.Pack),
                    Description = parsed.Value.Description,
                    Pack = source.Pack,
                    Warning = FindWarning(source, parsed.Value.Title),
                    Status = ContentDraftStatus.Generated,
                    GeneratedAt = DateTime.Now,
                };
            }

            content += $"\n\nTwoja poprzednia odpowiedź:\n{answer}\n\nPopraw: {problem}. Zwróć cały JSON ponownie.";
        }

        throw new InvalidOperationException($"no valid answer after {Attempts} attempts: {problem}");
    }

    private async Task<Source> ReadSourceAsync(string offerId, string ean)
    {
        var offer = await GetJsonAsync($"/sale/product-offers/{offerId}");
        var entry = offer["productSet"]?.AsArray().FirstOrDefault();
        var productId = entry?["product"]?["id"]?.GetValue<string>();
        var pack = entry?["quantity"]?["value"]?.GetValue<int>() ?? 1;
        var card = productId is null ? null : await GetJsonAsync($"/sale/products/{productId}");

        var categoryId = offer["category"]?["id"]?.GetValue<string>() ?? card?["category"]?["id"]?.GetValue<string>();
        string? categoryName = null;
        if (!string.IsNullOrEmpty(categoryId) && !_categoryNames.TryGetValue(categoryId, out categoryName))
        {
            categoryName = (await GetJsonAsync($"/sale/categories/{categoryId}"))["name"]?.GetValue<string>() ?? "";
            _categoryNames[categoryId] = categoryName;
        }

        return new Source(
            offerId, ean,
            offer["name"]?.GetValue<string>() ?? "",
            Math.Max(pack, 1),
            categoryName,
            card?["name"]?.GetValue<string>(),
            ReadParameters(card),
            ReadDescription(offer["description"]),
            ReadDescription(card?["description"]),
            null,
            null);
    }

    private static List<string> ReadParameters(JsonNode? card)
    {
        var result = new List<string>();
        foreach (var parameter in card?["parameters"]?.AsArray() ?? new JsonArray())
        {
            var name = parameter?["name"]?.ToString();
            if (string.IsNullOrEmpty(name) || parameter?["id"]?.ToString() == EanParameterId)
            {
                continue;
            }

            var values = (parameter?["valuesLabels"]?.AsArray() ?? parameter?["values"]?.AsArray())
                ?.Select(v => v?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList()
                ?? new List<string?>();
            var range = parameter?["rangeValue"];
            if (values.Count == 0 && range is not null)
            {
                values.Add($"{range["from"]}-{range["to"]}");
            }
            if (values.Count == 0)
            {
                continue;
            }

            var unit = parameter?["unit"]?.ToString();
            result.Add($"{name}: {string.Join(", ", values)}{(string.IsNullOrEmpty(unit) ? "" : " " + unit)}");
        }
        return result;
    }

    private static string ReadDescription(JsonNode? description)
    {
        var parts = new List<string>();
        foreach (var section in description?["sections"]?.AsArray() ?? new JsonArray())
        {
            foreach (var item in section?["items"]?.AsArray() ?? new JsonArray())
            {
                if (item?["type"]?.ToString() == "TEXT")
                {
                    parts.Add(item["content"]?.ToString() ?? "");
                }
            }
        }
        return string.Join("\n", parts);
    }

    private static (string Title, string Description)? Parse(string answer, int limit, out string? problem)
    {
        var json = Regex.Replace(answer.Trim(), @"^```(?:json)?\s*|\s*```$", "");
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            problem = "odpowiedź nie jest poprawnym JSON-em";
            return null;
        }

        var title = BundlePlan.SanitizeName(node?["title"]?.ToString() ?? "");
        var description = node?["description"]?.ToString() ?? "";
        var badTags = AnyTag.Matches(description).Select(m => m.Groups[1].Value)
            .Where(tag => !AllowedTags.Contains(tag)).Distinct().ToList();

        problem = title.Length == 0 ? "brak tytułu"
            : title.Length > limit ? $"tytuł ma {title.Length} znaków, limit to {limit}"
            : badTags.Count > 0 ? $"niedozwolone tagi HTML: {string.Join(", ", badTags)}"
            : PlainText(description).Length < 200 ? "opis jest za krótki"
            : null;

        return problem is null
            ? (title, AllowedTag.Replace(description, m => $"<{m.Groups[1].Value}{m.Groups[2].Value.ToLowerInvariant()}>"))
            : null;
    }

    private static HashSet<string> CardNumbers(Source source) =>
        NumbersIn(string.Join(" ", source.CardName, string.Join(" ", source.Parameters), PlainText(source.CardDescription)));

    private static HashSet<string> OldTitleNumbers(Source source) =>
        NumbersIn(SupplierCode.Replace(PackPrefix.Replace(source.Name, ""), " "));

    private static List<string> CardConflicts(Source source)
    {
        var card = CardNumbers(source);
        var reference = source.SupplierName is null
            ? OldTitleNumbers(source)
            : NumbersIn(SupplierCode.Replace(source.SupplierName, " "));
        return reference.Where(n => n.Length >= 2 && !card.Contains(n)).ToList();
    }

    private static string? FindWarning(Source source, string title)
    {
        var conflicting = CardConflicts(source);
        if (conflicting.Count > 0)
        {
            return source.SupplierName is null
                ? $"The current title says {string.Join(", ", conflicting)}, the catalogue card does not - " +
                  "check that the offer is linked to the right card."
                : $"The supplier says {string.Join(", ", conflicting)}, the Allegro catalogue card does not - the text " +
                  "uses only the supplier's data, but the offer is probably linked to the wrong card.";
        }

        var oldNumbers = OldTitleNumbers(source);
        var evidence = CardNumbers(source);
        evidence.UnionWith(NumbersIn(SupplierCode.Replace(source.SupplierName ?? "", " ") + " " + source.SupplierDescription));
        evidence.UnionWith(NumbersIn(PlainText(source.Description)));
        var invented = NumbersIn(title).Where(n => !evidence.Contains(n) && !oldNumbers.Contains(n)).ToList();
        if (invented.Count > 0)
        {
            return $"The new title has {string.Join(", ", invented)}, which is not in the product data.";
        }

        return null;
    }

    private static HashSet<string> NumbersIn(string text) =>
        Number.Matches(text).Select(m => m.Value.Replace(',', '.')).ToHashSet();

    private static string PlainText(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(AnyTag.Replace(html, " ")), @"\s+", " ").Trim();

    private static bool HasText(string html) => PlainText(html).Length > 0;

    private async Task<JsonNode> GetJsonAsync(string path)
    {
        var (status, body) = await _publisher.GetRawAsync(path);
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"GET {path.Split('?')[0]} failed ({status})");
        }
        return JsonNode.Parse(body) ?? throw new InvalidOperationException($"GET {path.Split('?')[0]} returned nothing");
    }

    private static string BuildSystemPrompt(int limit, int pack)
    {
        var packRule = pack > 1
            ? $"""
               - To oferta ZESTAWU {pack} sztuk tego samego produktu. Tytuł zostanie poprzedzony prefiksem "Zestaw {pack} szt. ", więc NIE dodawaj liczby sztuk zestawu do tytułu. Na samym końcu opisu, po specyfikacji, dodaj sekcję <h2>Zawartość zestawu</h2> z jednym krótkim zdaniem, np. "Zestaw zawiera {pack} szt. lup metalowych 100 mm." - rzeczownik po "szt." zawsze w dopełniaczu liczby mnogiej ("{pack} szt. lup", "{pack} szt. prowadników", "{pack} szt. misek", "{pack} szt. szczypiec"). Co zawiera każda sztuka dopisz tylko wtedy, gdy oprócz samego produktu są w niej dodatkowe elementy (np. "każda z 2 kółkami, 2 uchwytami i kompletem śrub"); nie pisz "każda sztuka to jedna ...". Nie dopisuj, dla kogo jest zestaw ani że to sprzedaż hurtowa, i nie powtarzaj liczby sztuk zestawu w specyfikacji.
               """
            : "";

        return $$"""
                 Jesteś doświadczonym copywriterem e-commerce i specjalistą SEO od Allegro.pl. Piszesz tytuły i opisy ofert, które dobrze się wyszukują i przekonują do zakupu.
                 Piszesz po polsku, naturalnie i poprawnie, z polskimi znakami.

                 TYTUŁ - najważniejszy dla wyszukiwarki Allegro
                 - Wyszukiwarka Allegro dopasowuje oferty głównie po słowach z tytułu i parametrów, więc każde słowo w tytule powinno być słowem, które kupujący może wpisać.
                 - Długość: maksymalnie {{limit}} znaków (twardy limit, licz dokładnie), celuj w {{limit - 12}}–{{limit}} znaków.
                 - Kolejność: [rodzaj produktu - główna fraza] + [najważniejsza cecha wyróżniająca: rozmiar, pojemność, liczba elementów, moc] + [materiał lub kolor] + [zastosowanie: do czego, dla kogo, gdzie] + [synonim lub druga popularna nazwa produktu] + [marka lub model, jeśli są prawdziwe].
                 - Główną frazę pisz w mianowniku, tak jak wpisuje się ją w wyszukiwarkę ("pokrywka szklana", "lupa do czytania", "miska dla psa").
                 - Jeśli kupujący używają różnych nazw tego samego produktu, dodaj drugą nazwę (np. "prowadnik pchacz do rowerka", "kupozbieracz łopatka do odchodów", "lupa szkło powiększające").
                 - Liczby z jednostkami zapisuj tak jak w wyszukiwarce: "28 cm", "2 l", "100 mm", "12 szt." (liczba sztuk tylko gdy dotyczy samego produktu, np. 12 kamieni w opakowaniu).
                 - Tytuł musi być poprawny gramatycznie: materiał i inne cechy zapisuj jako przymiotniki uzgodnione z rzeczownikiem ("lupa metalowa", "szczypce stalowe", "kupozbieracz plastikowy"), nigdy jako gołe rzeczowniki wstawione w środek ("lupa 100 mm metal", "szczypce 25 cm stal").
                 - Tytuł ma brzmieć jak naturalna nazwa produktu z cechami, a nie lista słów kluczowych. Nie powtarzaj słowa ani jego rdzenia, nie dodawaj wypełniaczy ("wysokiej jakości", "nowy", "oryginalny", "praktyczny", "solidny", "zestaw" przy pojedynczym produkcie).
                 - Zwykła pisownia (wielka litera na początku i w nazwach własnych), bez CAPS LOCK.
                 - Tylko litery, cyfry, spacje i znaki . , - / ( ) + % & : (bez °, ×, cudzysłowów i innych symboli; temperaturę zapisz np. "300 st. C").
                 - Bez kodów magazynowych dostawcy (np. XJ4171, 06602), bez słów "brak", "inny", "bez marki".
                 - Bez słów promocyjnych i ocen ("najlepszy", "hit", "okazja", "super", "promocja"), bez wykrzykników i emoji.

                 OPIS - ma pomóc kupującemu zdecydować i wspierać pozycjonowanie
                 - Dozwolone są WYŁĄCZNIE tagi HTML: <h2>, <p>, <ul>, <ol>, <li>, <b>. Nie używaj <h1> (tytuł oferty jest już nagłówkiem strony). Żadnych innych tagów, atrybutów, stylów, linków ani emoji.
                 - Struktura w tej kolejności:
                   1. <h2> z główną frazą: rodzaj produktu + najważniejsza cecha.
                   2. <p> 2-3 zdania: czym jest produkt i do czego służy. Główna fraza naturalnie w pierwszym zdaniu, synonim wprowadzony poprawnie odmienionym zwrotem ("lupa, czyli szkło powiększające", "prowadnik, zwany też pchaczem").
                   3. <h2>Najważniejsze cechy</h2> i <ul> z 3-6 punktami w formie "<b>cecha</b> - konkretna korzyść", np. "<b>Szklana soczewka 100 mm</b> - duże pole powiększenia przy czytaniu drobnego druku". Korzyść musi wynikać wprost z cechy. Jeśli cecha nie daje oczywistej korzyści, podaj samą cechę bez dopisku - nigdy nie twórz sztucznych korzyści typu "wymiar pomocny przy ocenie rozmiaru", "pasuje do większości wnętrz", "estetyczne przechowywanie".
                   4. <h2>Zastosowanie</h2> i krótka lista <ul> lub akapit <p> - tylko zastosowania oczywiste dla tego rodzaju produktu. Pomiń tę sekcję, jeśli danych jest za mało.
                   5. <h2>Specyfikacja</h2> i <ul> w formie "Parametr: wartość" - wszystkie konkretne parametry z danych (wymiary, materiały poszczególnych części, kolor, pojemność, liczba elementów).
                 - Główną frazę użyj w całym opisie 2-3 razy, synonimy 1-2 razy - naturalnie, bez upychania słów kluczowych.
                 - Krótkie akapity (najwyżej 3 zdania), krótkie zdania, bez powtarzania tych samych informacji w kilku sekcjach.
                 - Zwykle 120-250 słów. Jeśli danych jest mało, napisz krótszy opis (minimum około 60 słów) zamiast dopisywać ogólniki.
                 - Konkretnie, bez lania wody i bez obietnic, których nie da się sprawdzić. Nie dopisuj od siebie ocen wyglądu, kształtu, jakości ani właściwości, których nie ma w danych (np. "solidny wygląd", "klasyczny kształt", "szerokie pole widzenia").
                 - Pomiń marketingowe zwroty dostawcy, które nie opisują konkretnej cechy (np. "idealny pomysł na prezent", "bardzo wygodna w użyciu", "idealnie nadaje się").
                 - Nie powołuj się na źródło ("według opisu dostawcy", "według producenta", "jak podaje sprzedawca") - fakt z danych podaj wprost, a twierdzenie, którego nie chcesz podać wprost, pomiń.
                 - Pomiń twierdzenia o zdrowiu i wpływie na organizm ludzi lub zwierząt (np. "wspiera stawy", "zdrowa postawa", "poprawia trawienie"), nawet jeśli są w danych.
                 - Materiał podawaj zawsze dla konkretnej części, której dotyczy (np. obudowa: metal, soczewka: szkło), nigdy jako materiał całego produktu, jeśli dane tego nie mówią.
                 - Informacja o sprzedaży hurtowej lub zestawie nie jest cechą produktu - nie umieszczaj jej w sekcji "Najważniejsze cechy".
                 - Używaj WYŁĄCZNIE faktów z dostarczonych danych (nazwy, parametry, dotychczasowy opis). Nie wymyślaj wymiarów, materiałów, certyfikatów, gwarancji ani funkcji.

                 ŹRÓDŁA DANYCH
                 - Dane oznaczone "DOSTAWCA" opisują fizyczny produkt, który wysyłamy kupującemu. Są najważniejsze.
                 - Karta katalogu Allegro, jej parametry i obecny opis oferty mogą zawierać błędy (np. inny rozmiar, inna marka, inny produkt). Używaj ich tylko wtedy, gdy nie są sprzeczne z danymi dostawcy.
                 - Jeśli dane dostawcy i Allegro są sprzeczne w jakiejkolwiek cesze (rozmiar, kolor, materiał, marka, liczba elementów), użyj wersji dostawcy.
                 - Kolor losowy: jeśli dane mówią, że produkt (lub jego część) jest w kilku wersjach kolorystycznych, "mix kolorów" albo że kolor jest wysyłany losowo, kupujący nie może wybrać koloru. Wtedy NIE wymieniaj żadnych kolorów - ani w tytule, ani w opisie - i nie zachęcaj do wyboru. W specyfikacji napisz tylko "Kolor: wysyłany losowo, bez możliwości wyboru" (lub np. "Kolor szpilek: wysyłany losowo, bez możliwości wyboru", jeśli dotyczy jednej części).
                 - "Wielokolorowy" oznacza produkt w wielu kolorach naraz, a nie losowy kolor. Nie dopisuj koloru części, o której dane nic nie mówią. Jeśli danych dostawcy brak, a pozostałe źródła są sprzeczne, pomiń sporną informację.
                 - Pomiń z opisu dostawcy informacje handlowe: ceny, ilość w kartonie, minimalne zamówienie, prośby o kontakt.
                 - Pomiń parametry techniczne bez wartości dla kupującego (kod taryfy celnej, "brak", "inny") oraz nazwy innych marek niż marka produktu.
                 {{packRule}}
                 Odpowiedz WYŁĄCZNIE poprawnym JSON-em, bez komentarzy i bez bloku kodu:
                 {"title": "...", "description": "..."}
                 """;
    }
}
