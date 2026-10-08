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

        var sections = new JsonArray
        {
            new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["type"] = "TEXT", ["content"] = draft.Description }),
            },
        };
        foreach (var url in ReadImages(offer["description"]))
        {
            sections.Add(new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["type"] = "IMAGE", ["url"] = url }),
            });
        }

        var payload = new JsonObject
        {
            ["name"] = draft.Name,
            ["description"] = new JsonObject { ["sections"] = sections },
        };

        var (status, body) = await _publisher.SendJsonRawAsync(HttpMethod.Patch, $"/sale/product-offers/{draft.OfferId}",
            payload.ToJsonString());
        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"PATCH {status}: {ReadError(body)}");
        }
    }

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
