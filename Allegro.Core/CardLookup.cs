namespace Allegro.Core;

public class CardLookup
{
    public string? ProductId { get; set; }
    public string? CategoryId { get; set; }
    public string? Problem { get; set; }
    public DateTime CheckedAt { get; set; }

    public bool IsFresh(DateTime now) =>
        now - CheckedAt < (Problem is null ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1));
}
