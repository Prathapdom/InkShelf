using System.Text.Json;

namespace InkShelf.Services;

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "System";
    public string Accent { get; set; } = "Coral";
    public string PageTurnAnimation { get; set; } = "Slide";
    public string WebChapterSort { get; set; } = "Newest";
}

public sealed class AppearanceService
{
    private readonly string _file;

    public AppearanceService()
    {
        var root = Environment.GetEnvironmentVariable("INKSHELF_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkShelf");
        Directory.CreateDirectory(root);
        _file = Path.Combine(root, "appearance.json");
    }

    public async Task<AppearanceSettings> LoadAsync()
    {
        if (!File.Exists(_file)) return new AppearanceSettings();
        try
        {
            await using var stream = File.OpenRead(_file);
            return await JsonSerializer.DeserializeAsync<AppearanceSettings>(stream) ?? new AppearanceSettings();
        }
        catch { return new AppearanceSettings(); }
    }

    public async Task SaveAsync(AppearanceSettings settings)
    {
        await using var stream = File.Create(_file);
        await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
    }
}
