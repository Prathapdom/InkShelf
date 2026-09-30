using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using InkShelf.Models;

namespace InkShelf.Services;

public sealed partial class WebSeriesService : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _cacheRoot;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public WebSeriesService()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("InkShelf/1.0 (+embedded-reader; Windows)");
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        var dataRoot = Environment.GetEnvironmentVariable("INKSHELF_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkShelf");
        _cacheRoot = Path.Combine(dataRoot, "WebSources");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<WebSeriesInfo> ImportAsync(string input, CancellationToken cancellationToken = default)
    {
        var uri = ValidateSeriesUri(input);
        var match = SeriesPathRegex().Match(uri.AbsolutePath);
        var seriesId = match.Groups["id"].Value;
        var html = await GetStringAsync(uri, cancellationToken);
        var title = MetaValue(html, "og:title");
        if (title.EndsWith(" | Weeb Central", StringComparison.OrdinalIgnoreCase))
            title = title[..^15].Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = WebUtility.UrlDecode(match.Groups["slug"].Value).Replace('-', ' ').Trim();
        var cover = MetaValue(html, "og:image");
        var chapters = await FetchChaptersAsync(seriesId, cancellationToken);
        var result = new WebSeriesInfo
        {
            SeriesId = seriesId,
            Title = WebUtility.HtmlDecode(title),
            SourceUrl = uri.ToString(),
            CoverUrl = cover,
            RefreshedAt = DateTimeOffset.Now,
            Chapters = chapters
        };
        await SaveCacheAsync(result, cancellationToken);
        return result;
    }

    public async Task<WebSeriesInfo> GetAsync(ComicBook book, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!book.IsWebSeries || string.IsNullOrWhiteSpace(book.SourceUrl))
            throw new InvalidOperationException("This library item is not a linked WeebCentral series.");
        var id = book.WebSeriesId ?? SeriesPathRegex().Match(new Uri(book.SourceUrl).AbsolutePath).Groups["id"].Value;
        var cached = await LoadCacheAsync(id, cancellationToken);
        if (!forceRefresh && cached is not null) return cached;
        try { return await ImportAsync(book.SourceUrl, cancellationToken); }
        catch when (cached is not null) { return cached; }
    }

    public static Uri ValidateSeriesUri(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Enter a complete HTTPS WeebCentral series link.");
        if (!string.Equals(uri.Host, "weebcentral.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "www.weebcentral.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only weebcentral.com series links are supported.");
        if (!SeriesPathRegex().IsMatch(uri.AbsolutePath))
            throw new ArgumentException("The link must point to a WeebCentral series page, not an individual chapter.");
        return new UriBuilder(uri) { Scheme = "https", Host = "weebcentral.com", Port = -1, Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    public static bool IsAllowedReaderUri(Uri? uri) => uri is not null &&
        (string.Equals(uri.Host, "weebcentral.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".weebcentral.com", StringComparison.OrdinalIgnoreCase));

    private async Task<List<WebChapter>> FetchChaptersAsync(string seriesId, CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://weebcentral.com/series/{seriesId}/full-chapter-list");
        var html = await GetStringAsync(uri, cancellationToken);
        var chapters = new List<WebChapter>();
        foreach (Match match in ChapterRegex().Matches(html))
        {
            var body = match.Groups["body"].Value;
            var labelMatch = ChapterLabelRegex().Match(body);
            if (!labelMatch.Success) continue;
            var id = match.Groups["id"].Value;
            var dateMatch = DateRegex().Match(body);
            DateTimeOffset? published = DateTimeOffset.TryParse(WebUtility.HtmlDecode(dateMatch.Groups["date"].Value), out var parsed) ? parsed : null;
            chapters.Add(new WebChapter
            {
                Id = id,
                Title = WebUtility.HtmlDecode(StripTags(labelMatch.Groups["label"].Value)).Trim(),
                Url = $"https://weebcentral.com/chapters/{id}",
                PublishedAt = published
            });
        }
        if (chapters.Count == 0) throw new InvalidDataException("WeebCentral returned no readable chapters for this series.");
        return chapters;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<WebSeriesInfo?> LoadCacheAsync(string id, CancellationToken cancellationToken)
    {
        var path = CachePath(id);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<WebSeriesInfo>(stream, cancellationToken: cancellationToken);
        }
        catch { return null; }
    }

    private async Task SaveCacheAsync(WebSeriesInfo info, CancellationToken cancellationToken)
    {
        var path = CachePath(info.SeriesId);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, info, _jsonOptions, cancellationToken);
        File.Move(temp, path, true);
    }

    private string CachePath(string id) => Path.Combine(_cacheRoot, $"{id}.json");
    private static string MetaValue(string html, string property)
    {
        var match = Regex.Match(html, $@"<meta\s+property=[""']{Regex.Escape(property)}[""']\s+content=[""'](?<value>.*?)[""']", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : string.Empty;
    }
    private static string StripTags(string value) => Regex.Replace(value, "<[^>]+>", string.Empty);

    [GeneratedRegex(@"^/series/(?<id>[0-9A-HJKMNP-TV-Z]{26})/(?<slug>[^/?#]+)/*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesPathRegex();
    [GeneratedRegex(@"<a\s+href=[""']/chapters/(?<id>[0-9A-HJKMNP-TV-Z]{26})[""'][^>]*>(?<body>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ChapterRegex();
    [GeneratedRegex(@"<span\s+class=[""'][^""']*[""'][^>]*>\s*(?<label>(?:Chapter|Episode)\s*[^<]+)\s*</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ChapterLabelRegex();
    [GeneratedRegex(@"<time[^>]+datetime=[""'](?<date>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex DateRegex();

    public void Dispose() => _client.Dispose();
}
