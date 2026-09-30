namespace InkShelf.Models;

public sealed class TrackingSettings
{
    public string AniListClientId { get; set; } = string.Empty;
    public bool AutoSyncOnCompletion { get; set; } = true;
}

public sealed class TrackerSearchResult
{
    public int MediaId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string NativeTitle { get; init; } = string.Empty;
    public int? Chapters { get; init; }
    public int? Volumes { get; init; }
    public string Status { get; init; } = string.Empty;

    public override string ToString()
    {
        var detail = Chapters is > 0 ? $" · {Chapters} chapters" : Volumes is > 0 ? $" · {Volumes} volumes" : string.Empty;
        return string.IsNullOrWhiteSpace(NativeTitle) || NativeTitle == Title ? Title + detail : $"{Title} ({NativeTitle}){detail}";
    }
}

public sealed class TrackerQueueItem
{
    public string BookId { get; set; } = string.Empty;
    public int MediaId { get; set; }
    public int Chapter { get; set; }
    public int? Volume { get; set; }
    public int? TotalChapters { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.Now;
    public int Attempts { get; set; }
    public string LastError { get; set; } = string.Empty;
}
