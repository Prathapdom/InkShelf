using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using InkShelf.Models;

namespace InkShelf.Services;

public sealed class LibraryService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _root = Environment.GetEnvironmentVariable("INKSHELF_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkShelf");
    private string LibraryFile => Path.Combine(_root, "library.json");
    private string ComicsFolder => Path.Combine(_root, "Comics");
    private string CacheFolder => Path.Combine(_root, "Cache");

    public LibraryService()
    {
        Directory.CreateDirectory(ComicsFolder);
        Directory.CreateDirectory(CacheFolder);
    }

    public async Task<List<ComicBook>> LoadAsync()
    {
        if (!File.Exists(LibraryFile)) return [];
        try
        {
            await using var stream = File.OpenRead(LibraryFile);
            return await JsonSerializer.DeserializeAsync<List<ComicBook>>(stream) ?? [];
        }
        catch { return []; }
    }

    public async Task SaveAsync(IEnumerable<ComicBook> books)
    {
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root);
            var temp = LibraryFile + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, books, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temp, LibraryFile, true);
        }
        finally { _saveGate.Release(); }
    }

    public async Task<ComicBook> ImportAsync(string sourcePath)
    {
        await using var hashStream = File.OpenRead(sourcePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream)).ToLowerInvariant();
        var id = hash[..20];
        var managedPath = Path.Combine(ComicsFolder, id + ".cbz");
        if (!File.Exists(managedPath)) File.Copy(sourcePath, managedPath);

        using var archive = ZipFile.OpenRead(managedPath);
        var entries = GetImageEntries(archive);
        if (entries.Count == 0) throw new InvalidDataException("This CBZ does not contain any supported images.");

        var bookCache = Path.Combine(CacheFolder, id);
        Directory.CreateDirectory(bookCache);
        var coverPath = Path.Combine(bookCache, "cover" + Path.GetExtension(entries[0].Name).ToLowerInvariant());
        if (!File.Exists(coverPath))
        {
            await using var input = entries[0].Open();
            await using var output = File.Create(coverPath);
            await input.CopyToAsync(output);
        }

        var book = new ComicBook
        {
            Id = id,
            Title = CleanTitle(Path.GetFileNameWithoutExtension(sourcePath)),
            FilePath = managedPath,
            CoverPath = coverPath,
            PageCount = entries.Count
        };
        ApplyComicInfo(archive, book);
        return book;
    }

    public async Task<IReadOnlyList<string>> ExtractPagesAsync(ComicBook book)
    {
        var pagesFolder = Path.Combine(CacheFolder, book.Id, "pages");
        Directory.CreateDirectory(pagesFolder);
        using var archive = ZipFile.OpenRead(book.FilePath);
        var entries = GetImageEntries(archive);
        if (entries.Count == 0) throw new InvalidDataException("No readable pages were found.");

        long extractedBytes = 0;
        var pages = new List<string>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Length > 120 * 1024 * 1024) throw new InvalidDataException("A page is too large to extract safely.");
            extractedBytes += entry.Length;
            if (extractedBytes > 3L * 1024 * 1024 * 1024) throw new InvalidDataException("This comic is too large to extract safely.");
            var path = Path.Combine(pagesFolder, $"{i:D5}{Path.GetExtension(entry.Name).ToLowerInvariant()}");
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Length)
            {
                await using var input = entry.Open();
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            pages.Add(path);
        }
        return pages;
    }

    private static List<ZipArchiveEntry> GetImageEntries(ZipArchive archive) => archive.Entries
        .Where(e => e.Length > 0 && ImageExtensions.Contains(Path.GetExtension(e.Name)))
        .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
        .ToList();

    private static string CleanTitle(string value) => value.Replace('_', ' ').Replace('.', ' ').Trim();

    private static void ApplyComicInfo(ZipArchive archive, ComicBook book)
    {
        try
        {
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length > 1024 * 1024) return;
            using var stream = entry.Open();
            var document = XDocument.Load(stream, LoadOptions.None);
            var root = document.Root;
            if (root is null) return;
            var series = root.Element("Series")?.Value.Trim();
            var title = root.Element("Title")?.Value.Trim();
            var language = root.Element("LanguageISO")?.Value.Trim();
            if (!string.IsNullOrWhiteSpace(series)) book.SeriesTitle = series;
            if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(series)) book.Title = title;
            if (!string.IsNullOrWhiteSpace(language)) book.Language = language;
            if (TryPositiveInt(root.Element("Number")?.Value, out var chapter)) book.ChapterStart = book.ChapterEnd = chapter;
            if (TryPositiveInt(root.Element("Volume")?.Value, out var volume)) book.VolumeNumber = volume;
        }
        catch { }
    }

    private static bool TryPositiveInt(string? value, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var whole = value.Split('.', 2)[0];
        return int.TryParse(whole, out number) && number > 0;
    }
}
