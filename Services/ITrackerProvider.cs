using InkShelf.Models;

namespace InkShelf.Services;

public interface ITrackerProvider
{
    string Name { get; }
    bool IsAuthenticated { get; }
    Task<string?> RestoreSessionAsync();
    Task<string> AuthenticateAsync(string clientId, CancellationToken cancellationToken);
    Task SignOutAsync();
    Task<IReadOnlyList<TrackerSearchResult>> SearchAsync(string title, CancellationToken cancellationToken = default);
    Task UpdateProgressAsync(int mediaId, int chapter, int? volume, int? totalChapters, CancellationToken cancellationToken = default);
}
