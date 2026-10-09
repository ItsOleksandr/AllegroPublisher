using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegro.Core;

public sealed class ContentApplier
{
    private readonly AllegroPublisher _publisher;

    public ContentApplier(AllegroPublisher publisher)
    {
        _publisher = publisher;
    }

    public record Result(int Applied, int Failed);

    public async Task<Result> ApplyAsync(IEnumerable<string> eans, Action<string>? log = null)
    {
        var drafts = SaverExtensions.ContentDrafts.Read();
        var todo = eans.Distinct()
            .Select(ean => drafts.GetValueOrDefault(ean))
            .Where(d => d is { Status: ContentDraftStatus.Generated })
            .ToList();

        int applied = 0, failed = 0;
        foreach (var draft in todo)
        {
            try
            {
                await ApplyOneAsync(draft!);
                draft!.Status = ContentDraftStatus.Applied;
                draft.AppliedAt = DateTime.Now;
                draft.Error = null;
                applied++;
                log?.Invoke($"  {draft.Ean} offer {draft.OfferId}: {draft.Name}");
            }
            catch (Exception e)
            {
                draft!.Error = e.Message;
                failed++;
                log?.Invoke($"  {draft.Ean} offer {draft.OfferId}: {e.Message}");
            }
        }

        SaverExtensions.ContentDrafts.Value = drafts;
        SaverExtensions.ContentDrafts.Write();
        log?.Invoke($"Applied {applied} titles and descriptions, {failed} failed.");
        return new Result(applied, failed);
    }

    public static void SetStatus(IEnumerable<string> eans, ContentDraftStatus status)
    {
        var drafts = SaverExtensions.ContentDrafts.Read();
        foreach (var ean in eans)
        {
            if (drafts.TryGetValue(ean, out var draft))
            {
                draft.Status = status;
            }
        }
        SaverExtensions.ContentDrafts.Value = drafts;
        SaverExtensions.ContentDrafts.Write();
    }

    private async Task ApplyOneAsync(ContentDraft draft)
    {
        var (readStatus, readBody) = await _publisher.GetRawAsync($"/sale/product-offers/{draft.OfferId}");
        if (readStatus is < 200 or >= 300)
        {
            throw new InvalidOperationException($"read failed ({readStatus})");
        }

        var offer = JsonNode.Parse(readBody)!;
        var currentName = offer["name"]?.GetValue<string>() ?? "";
        if (currentName != draft.OldName && currentName != draft.Name)
        {
            throw new InvalidOperationException("the title was changed on Allegro after generation - regenerate this draft");
        }

        var pack = offer["productSet"]?.AsArray().FirstOrDefault()?["quantity"]?["value"]?.GetValue<int>() ?? 1;
        if (Math.Max(pack, 1) != draft.Pack)
        {
            throw new InvalidOperationException($"the offer is now a pack of {pack}, the draft was written for {draft.Pack} - regenerate it");
        }

        draft.OriginalDescriptionJson ??= offer["description"]?.ToJsonString();

        var payload = new JsonObject
        {
            ["name"] = draft.Name,
            ["description"] = new JsonObject { ["sections"] = BuildSections(draft, offer) },
        };

        var (status, body) = await _publisher.SendJsonRawAsync(HttpMethod.Patch, $"/sale/product-offers/{draft.OfferId}",
            payload.ToJsonString());
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"PATCH {status}: {ReadError(body)}");
        }
    }

    public static JsonArray BuildSections(ContentDraft draft, JsonNode offer)
    {
        var blocks = draft.Blocks.Count > 0 ? draft.Blocks : new List<string> { draft.Description };
        var gallery = (offer["images"]?.AsArray() ?? new JsonArray())
            .Select(image => image is JsonObject ? image["url"]?.ToString() : image?.ToString())
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => url!)
            .Distinct()
            .ToList();
        var ownImages = ReadImages(offer["description"]).Where(url => !gallery.Contains(url)).Distinct().ToList();

        var slots = blocks.Count(TakesPhoto);
        var photos = new Queue<string>(gallery.Count > slots ? gallery.Skip(1) : gallery);

        var sections = new JsonArray();
        var photoOnRight = true;
        foreach (var block in blocks)
        {
            var text = new JsonObject { ["type"] = "TEXT", ["content"] = block };
            if (TakesPhoto(block) && photos.Count > 0)
            {
                var image = new JsonObject { ["type"] = "IMAGE", ["url"] = photos.Dequeue() };
                sections.Add(new JsonObject
                {
                    ["items"] = photoOnRight ? new JsonArray(text, image) : new JsonArray(image, text),
                });
                photoOnRight = !photoOnRight;
            }
            else
            {
                sections.Add(new JsonObject { ["items"] = new JsonArray(text) });
            }
        }

        foreach (var url in ownImages)
        {
            sections.Add(new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["type"] = "IMAGE", ["url"] = url }),
            });
        }
        return sections;
    }

    private static bool TakesPhoto(string block) =>
        !NoPhotoHeadings.Any(heading => block.Contains(heading, StringComparison.OrdinalIgnoreCase))
        && PlainLength(block) <= 900;

    private static readonly string[] NoPhotoHeadings = { ">Specyfikacja<", ">Najczęstsze pytania<", ">Zawartość zestawu<" };

    private static int PlainLength(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "").Trim().Length;

    private static IEnumerable<string> ReadImages(JsonNode? description)
    {
        foreach (var section in description?["sections"]?.AsArray() ?? new JsonArray())
        {
            foreach (var item in section?["items"]?.AsArray() ?? new JsonArray())
            {
                var url = item?["url"]?.ToString();
                if (item?["type"]?.ToString() == "IMAGE" && !string.IsNullOrEmpty(url))
                {
                    yield return url;
                }
            }
        }
    }

    private static string ReadError(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["errors"]?.AsArray().FirstOrDefault();
            return error?["userMessage"]?.ToString() ?? error?["message"]?.ToString() ?? body;
        }
        catch (JsonException)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }
}
