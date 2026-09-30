namespace InkShelf.Models;

public sealed class WebSeriesInfo
{
    public string SeriesId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public DateTimeOffset RefreshedAt { get; set; } = DateTimeOffset.Now;
    public List<WebChapter> Chapters { get; set; } = [];
}

public sealed class WebChapter
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public string DateLabel => PublishedAt?.ToLocalTime().ToString("d MMM yyyy") ?? string.Empty;
}
