namespace Allegro.Core;

public static class AutoContent
{
    public static async Task<ContentApplier.Result> RunAsync(AllegroPublisher publisher, int count, Action<string>? log = null)
    {
        var generator = new ContentGenerator(publisher);
        var picked = await generator.PickWeakestAsync(count, log);
        if (picked.Count == 0)
        {
            log?.Invoke("Every active offer already has a generated title and description.");
            return new ContentApplier.Result(0, 0);
        }

        var started = DateTime.Now;
        await generator.GenerateAsync(picked, log);

        var wanted = picked.Select(c => c.Ean).ToHashSet();
        var fresh = SaverExtensions.ContentDrafts.Read().Values
            .Where(d => wanted.Contains(d.Ean) && d.Status == ContentDraftStatus.Generated && d.GeneratedAt >= started)
            .ToList();
        var held = fresh.Where(d => d.Warning is not null).ToList();
        if (held.Count > 0)
        {
            log?.Invoke($"{held.Count} drafts disagree with the supplier's data and wait for review on the Titles & descriptions page.");
        }

        return await new ContentApplier(publisher).ApplyAsync(fresh.Where(d => d.Warning is null).Select(d => d.Ean), log);
    }
}
