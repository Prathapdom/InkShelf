using System.Diagnostics;
using System.Text.Json;
using InkShelf.Models;

namespace InkShelf.Services;

public sealed class BubbleZoomService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _engineRoot = Environment.GetEnvironmentVariable("INKSHELF_TRANSLATION_ROOT") ?? AppContext.BaseDirectory;
    private readonly string _cacheRoot;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private Process? _worker;
    private StreamWriter? _input;
    private StreamReader? _output;
    private string _lastError = string.Empty;

    public BubbleZoomService()
    {
        var dataRoot = Environment.GetEnvironmentVariable("INKSHELF_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkShelf");
        _cacheRoot = Path.Combine(dataRoot, "SmartZoom");
        Directory.CreateDirectory(_cacheRoot);
    }

    public bool IsAvailable =>
        File.Exists(Path.Combine(_engineRoot, "TranslationRuntime", "python.exe")) &&
        File.Exists(Path.Combine(_engineRoot, "TranslationEngine", "bubble_worker.py"));

    public async Task<SpeechBubblePage?> GetCachedPageAsync(string bookId, int pageIndex, CancellationToken cancellationToken = default)
    {
        var path = AnalysisPath(bookId, pageIndex);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var cached = await JsonSerializer.DeserializeAsync<SpeechBubblePage>(stream, _jsonOptions, cancellationToken);
            return cached is { Version: >= 2 } ? cached : null;
        }
        catch { return null; }
    }

    public async Task<SpeechBubblePage> AnalyzePageAsync(string bookId, int pageIndex, string imagePath, bool rtl, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force)
        {
            var cached = await GetCachedPageAsync(bookId, pageIndex, cancellationToken);
            if (cached is not null) return cached;
        }
        if (!IsAvailable) throw new InvalidOperationException("The offline Smart Zoom analyzer is missing from this build.");

        using var response = await SendAsync(new { kind = "analyze", pageIndex, imagePath, rtl }, cancellationToken);
        var result = response.RootElement.GetProperty("result").Deserialize<SpeechBubblePage>(_jsonOptions)
            ?? throw new InvalidOperationException("Smart Zoom returned an invalid page analysis.");
        var path = AnalysisPath(bookId, pageIndex);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, result, _jsonOptions, cancellationToken);
        File.Move(temp, path, true);
        return result;
    }

    public async Task<string> GetCropAsync(string bookId, int pageIndex, string imagePath, SpeechBubbleRegion bubble, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_cacheRoot, bookId, "Crops", $"{pageIndex:D5}-{bubble.Order:D3}.jpg");
        if (File.Exists(path)) return path;
        using var response = await SendAsync(new
        {
            kind = "crop",
            imagePath,
            outputPath = path,
            x = bubble.X,
            y = bubble.Y,
            width = bubble.Width,
            height = bubble.Height
        }, cancellationToken);
        return response.RootElement.GetProperty("result").GetProperty("outputPath").GetString() ?? path;
    }

    public async Task<string> GetPanelCropAsync(string bookId, int pageIndex, string imagePath, ComicPanelRegion panel, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_cacheRoot, bookId, "PanelCrops", $"{pageIndex:D5}-{panel.Order:D3}.jpg");
        if (File.Exists(path)) return path;
        using var response = await SendAsync(new
        {
            kind = "crop",
            imagePath,
            outputPath = path,
            x = panel.X,
            y = panel.Y,
            width = panel.Width,
            height = panel.Height
        }, cancellationToken);
        return response.RootElement.GetProperty("result").GetProperty("outputPath").GetString() ?? path;
    }

    private async Task<JsonDocument> SendAsync(object payload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureWorker();
            var id = Guid.NewGuid().ToString("N");
            var values = JsonSerializer.SerializeToElement(payload);
            var request = new Dictionary<string, object?> { ["id"] = id };
            foreach (var property in values.EnumerateObject()) request[property.Name] = property.Value.Clone();
            await _input!.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var line = await _output!.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidOperationException(WorkerFailureMessage());
            var response = JsonDocument.Parse(line);
            if (response.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.GetString() ?? WorkerFailureMessage();
                response.Dispose();
                throw new InvalidOperationException(message);
            }
            if (response.RootElement.GetProperty("id").GetString() != id)
            {
                response.Dispose();
                throw new InvalidOperationException("Smart Zoom returned an unexpected response.");
            }
            return response;
        }
        catch (OperationCanceledException) { StopWorker(); throw; }
        catch { if (_worker?.HasExited != false) StopWorker(); throw; }
        finally { _gate.Release(); }
    }

    private void EnsureWorker()
    {
        if (_worker is { HasExited: false } && _input is not null && _output is not null) return;
        StopWorker();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(_engineRoot, "TranslationRuntime", "python.exe"),
            Arguments = $"\"{Path.Combine(_engineRoot, "TranslationEngine", "bubble_worker.py")}\"",
            WorkingDirectory = _engineRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        _worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Smart Zoom analyzer.");
        _worker.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) _lastError = args.Data; };
        _worker.BeginErrorReadLine();
        _input = _worker.StandardInput;
        _output = _worker.StandardOutput;
    }

    private string WorkerFailureMessage() => string.IsNullOrWhiteSpace(_lastError)
        ? "The Smart Zoom analyzer stopped unexpectedly."
        : $"The Smart Zoom analyzer stopped: {_lastError}";

    private string AnalysisPath(string bookId, int pageIndex) => Path.Combine(_cacheRoot, bookId, $"{pageIndex:D5}.json");

    private void StopWorker()
    {
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { if (_worker is { HasExited: false }) _worker.Kill(true); } catch { }
        try { _worker?.Dispose(); } catch { }
        _worker = null;
        _input = null;
        _output = null;
    }

    public void Dispose()
    {
        StopWorker();
        _gate.Dispose();
    }
}
