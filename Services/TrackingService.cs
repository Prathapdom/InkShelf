using System.Text.Json;
using InkShelf.Models;

namespace InkShelf.Services;

public sealed class TrackingService
{
    private readonly string _root = Environment.GetEnvironmentVariable("INKSHELF_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkShelf");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<TrackerQueueItem> _queue = [];

    public AniListTracker AniList { get; } = new();
    public TrackingSettings Settings { get; private set; } = new();
    public string? ViewerName { get; private set; }
    private string SettingsFile => Path.Combine(_root, "tracking.json");
    private string QueueFile => Path.Combine(_root, "tracking-queue.json");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Settings = await LoadFileAsync(SettingsFile, new TrackingSettings());
        _queue = await LoadFileAsync(QueueFile, new List<TrackerQueueItem>());
        ViewerName = await AniList.RestoreSessionAsync();
    }

    public async Task<string> ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        Settings.AniListClientId = clientId.Trim();
        await SaveSettingsAsync();
        ViewerName = await AniList.AuthenticateAsync(Settings.AniListClientId, cancellationToken);
        return ViewerName;
    }

    public async Task DisconnectAsync()
    {
        await AniList.SignOutAsync();
        ViewerName = null;
    }

    public Task SaveSettingsAsync() => SaveFileAsync(SettingsFile, Settings);

    public async Task RemoveQueuedUpdatesAsync(string bookId)
    {
        await _gate.WaitAsync();
        try
        {
            _queue.RemoveAll(item => item.BookId == bookId);
            await SaveFileAsync(QueueFile, _queue);
        }
        finally { _gate.Release(); }
    }

    public async Task QueueProgressAsync(ComicBook book)
    {
        if (book.TrackerMediaId is null || book.ChapterEnd is not > 0) throw new InvalidOperationException("Link this comic and set its ending chapter first.");
        await _gate.WaitAsync();
        try
        {
            var existing = _queue.FirstOrDefault(q => q.BookId == book.Id && q.MediaId == book.TrackerMediaId);
            if (existing is null)
            {
                existing = new TrackerQueueItem { BookId = book.Id, MediaId = book.TrackerMediaId.Value };
                _queue.Add(existing);
            }
            existing.Chapter = Math.Max(existing.Chapter, book.ChapterEnd.Value);
            existing.Volume = Max(existing.Volume, book.VolumeNumber);
            existing.TotalChapters = book.TrackerTotalChapters;
            existing.QueuedAt = DateTimeOffset.Now;
            existing.LastError = string.Empty;
            book.TrackerSyncState = "Pending sync";
            await SaveFileAsync(QueueFile, _queue);
        }
        finally { _gate.Release(); }
    }

    public async Task<(int Synced, int Pending)> SyncPendingAsync(IReadOnlyCollection<ComicBook> books, CancellationToken cancellationToken = default)
    {
        if (!AniList.IsAuthenticated) throw new InvalidOperationException("Connect an AniList account first.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var synced = 0;
            foreach (var item in _queue.ToList())
            {
                try
                {
                    await AniList.UpdateProgressAsync(item.MediaId, item.Chapter, item.Volume, item.TotalChapters, cancellationToken);
                    _queue.Remove(item);
                    var book = books.FirstOrDefault(b => b.Id == item.BookId);
                    if (book is not null)
                    {
                        book.LastSyncedChapter = Math.Max(book.LastSyncedChapter, item.Chapter);
                        book.LastTrackerSyncAt = DateTimeOffset.Now;
                        book.TrackerSyncState = $"Chapter {item.Chapter} synced";
                    }
                    synced++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.Attempts++;
                    item.LastError = ex.Message;
                    var book = books.FirstOrDefault(b => b.Id == item.BookId);
                    if (book is not null) book.TrackerSyncState = "Pending sync";
                }
            }
            await SaveFileAsync(QueueFile, _queue);
            return (synced, _queue.Count);
        }
        finally { _gate.Release(); }
    }

    private static int? Max(int? left, int? right) => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private static async Task<T> LoadFileAsync<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream) ?? fallback;
        }
        catch { return fallback; }
    }

    private static async Task SaveFileAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true });
        File.Move(temp, path, true);
    }
}
