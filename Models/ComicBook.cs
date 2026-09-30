using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InkShelf.Models;

public sealed class ComicBook : INotifyPropertyChanged
{
    private int _currentPage;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled";
    public string FilePath { get; set; } = string.Empty;
    public string CoverPath { get; set; } = string.Empty;
    public string? SeriesTitle { get; set; }
    public string? Language { get; set; }
    public int PageCount { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastOpenedAt { get; set; }
    public bool IsFavorite { get; set; }
    public string? TrackerProvider { get; set; }
    public int? TrackerMediaId { get; set; }
    public string? TrackerTitle { get; set; }
    public int? ChapterStart { get; set; }
    public int? ChapterEnd { get; set; }
    public int? VolumeNumber { get; set; }
    public int? TrackerTotalChapters { get; set; }
    public int LastSyncedChapter { get; set; }
    public DateTimeOffset? LastTrackerSyncAt { get; set; }
    public string TrackerSyncState { get; set; } = string.Empty;
    public string TranslationPreference { get; set; } = "Ask";
    public string SourceType { get; set; } = "Local";
    public string? SourceUrl { get; set; }
    public string? WebSeriesId { get; set; }
    public string? LastWebChapterUrl { get; set; }
    public string? LastWebChapterTitle { get; set; }
    public DateTimeOffset? LastWebRefreshAt { get; set; }

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == value) return;
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressLabel));
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    [JsonIgnore] public double Progress => PageCount <= 0 ? 0 : Math.Clamp((CurrentPage + 1d) / PageCount * 100d, 0, 100);
    [JsonIgnore] public bool IsWebSeries => string.Equals(SourceType, "WeebCentral", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string ProgressLabel => IsWebSeries
        ? CurrentPage <= 0 ? $"{PageCount} chapters · online" : $"Chapter {Math.Min(CurrentPage + 1, PageCount)} of {PageCount}"
        : CurrentPage <= 0 ? $"{PageCount} pages" : $"Page {Math.Min(CurrentPage + 1, PageCount)} of {PageCount}";
    [JsonIgnore] public string ActionLabel => IsWebSeries
        ? string.IsNullOrWhiteSpace(LastWebChapterTitle) ? "Browse chapters" : $"Continue · {LastWebChapterTitle}"
        : CurrentPage <= 0 ? "Start reading" : Progress >= 99.9 ? "Read again" : "Continue";
    [JsonIgnore] public string TrackingLabel => TrackerMediaId is null
        ? IsWebSeries ? "WeebCentral · Online" : string.Empty
        : string.IsNullOrWhiteSpace(TrackerSyncState) ? $"AniList · {TrackerTitle}" : $"AniList · {TrackerSyncState}";
    [JsonIgnore] public BitmapImage? CoverImage { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
