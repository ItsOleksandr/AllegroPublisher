namespace Allegro.Core;

public class ContentDraft
{
    public string Ean { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string OldName { get; set; } = "";
    public string BaseName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? OriginalDescriptionJson { get; set; }
    public int Pack { get; set; } = 1;
    public string? Warning { get; set; }
    public string? Error { get; set; }
    public ContentDraftStatus Status { get; set; }
    public DateTime GeneratedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
}

public enum ContentDraftStatus
{
    Generated,
    Applied,
    Rejected,
    Outdated,
}
