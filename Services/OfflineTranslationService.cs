using System.Diagnostics;
using System.Text.Json;
using InkShelf.Models;

namespace InkShelf.Services;

public sealed class OfflineTranslationService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _engineRoot = Environment.GetEnvironmentVariable("INKSHELF_TRANSLATION_ROOT") ?? AppContext.BaseDirectory;
    private readonly string _cacheRoot;
    private Process? _worker;
    private StreamWriter? _input;
    private StreamReader? _output;
    private string _lastWorkerError = string.Empty;

    public OfflineTranslationService()
    {
        var dataRoot = Environment.GetEnvironmentVariable("INKSHELF_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkShelf");
        _cacheRoot = Path.Combine(dataRoot, "Translations");
        Directory.CreateDirectory(_cacheRoot);
    }

    public bool IsAvailable =>
        File.Exists(Path.Combine(_engineRoot, "TranslationRuntime", "python.exe")) &&
        File.Exists(Path.Combine(_engineRoot, "TranslationEngine", "worker.py")) &&
        File.Exists(Path.Combine(_engineRoot, "TranslationModels", "ready.json"));

    public async Task<PageTranslation> TranslatePageAsync(string bookId, int pageIndex, string imagePath, CancellationToken cancellationToken = default)
    {
        var cached = await LoadCachedAsync(bookId, pageIndex, cancellationToken);
        if (cached is not null) return cached;
        if (!IsAvailable) throw new InvalidOperationException("The offline translation runtime is not installed with this build.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cached = await LoadCachedAsync(bookId, pageIndex, cancellationToken);
            if (cached is not null) return cached;
            EnsureWorker();
            var id = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(new { id, imagePath });
            await _input!.WriteLineAsync(request.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var line = await _output!.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidOperationException(WorkerFailureMessage());
            using var response = JsonDocument.Parse(line);
            var root = response.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString() ?? WorkerFailureMessage());
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetString() != id)
                throw new InvalidOperationException("The offline translation worker returned an unexpected response.");
            var result = new PageTranslation
            {
                PageIndex = pageIndex,
                JapaneseText = root.GetProperty("japaneseText").GetString() ?? string.Empty,
                EnglishText = root.GetProperty("englishText").GetString() ?? string.Empty,
                TranslatedAt = DateTimeOffset.Now
            };
            await SaveCachedAsync(bookId, result, cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            StopWorker();
            throw;
        }
        catch
        {
            if (_worker?.HasExited != false) StopWorker();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearCachedPageAsync(string bookId, int pageIndex)
    {
        var path = CachePath(bookId, pageIndex);
        await Task.Run(() => { if (File.Exists(path)) File.Delete(path); });
    }

    private void EnsureWorker()
    {
        if (_worker is { HasExited: false } && _input is not null && _output is not null) return;
        StopWorker();
        var python = Path.Combine(_engineRoot, "TranslationRuntime", "python.exe");
        var script = Path.Combine(_engineRoot, "TranslationEngine", "worker.py");
        var start = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{script}\" \"{_engineRoot.TrimEnd(Path.DirectorySeparatorChar)}\"",
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
        _worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start the offline translation worker.");
        _worker.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) _lastWorkerError = args.Data; };
        _worker.BeginErrorReadLine();
        _input = _worker.StandardInput;
        _output = _worker.StandardOutput;
    }

    private string WorkerFailureMessage() => string.IsNullOrWhiteSpace(_lastWorkerError)
        ? "The offline translation worker stopped unexpectedly."
        : $"The offline translation worker stopped: {_lastWorkerError}";

    private async Task<PageTranslation?> LoadCachedAsync(string bookId, int pageIndex, CancellationToken cancellationToken)
    {
        var path = CachePath(bookId, pageIndex);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PageTranslation>(stream, cancellationToken: cancellationToken);
        }
        catch { return null; }
    }

    private async Task SaveCachedAsync(string bookId, PageTranslation translation, CancellationToken cancellationToken)
    {
        var path = CachePath(bookId, translation.PageIndex);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, translation, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        File.Move(temp, path, true);
    }

    private string CachePath(string bookId, int pageIndex) => Path.Combine(_cacheRoot, bookId, $"{pageIndex:D5}.json");

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
