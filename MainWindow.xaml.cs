using System.Collections.ObjectModel;
using InkShelf.Models;
using InkShelf.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;

namespace InkShelf;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly LibraryService _libraryService = new();
    private readonly AppearanceService _appearanceService = new();
    private readonly TrackingService _trackingService = new();
    private readonly OfflineTranslationService _translationService = new();
    private readonly BubbleZoomService _bubbleZoomService = new();
    private readonly WebSeriesService _webSeriesService = new();
    private readonly ObservableCollection<ComicBook> _books = [];
    private readonly ObservableCollection<ComicBook> _visibleBooks = [];
    private ComicBook? _activeBook;
    private ComicBook? _continueBook;
    private IReadOnlyList<string> _pages = [];
    private int _pageIndex;
    private string _filter = "all";
    private bool _updatingReader;
    private bool _isFullScreen;
    private bool _isPageAnimating;
    private bool _updatingAppearance = true;
    private bool _updatingTranslationToggle;
    private CancellationTokenSource? _translationCancellation;
    private CancellationTokenSource? _bubbleAnalysisCancellation;
    private SpeechBubblePage? _currentBubblePage;
    private int _bubblePageIndex;
    private int _bubbleIndex;
    private bool _smartZoomEnabled;
    private bool _isAnalyzingBubbles;
    private SpeechBubblePage? _guidedPage;
    private int _guidedPanelIndex;
    private bool _guidedViewEnabled;
    private bool _guidedTransition;
    private ComicBook? _activeWebBook;
    private WebSeriesInfo? _activeWebSeries;
    private bool _webReaderInitialized;
    private AppearanceSettings _appearance = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        LibraryGrid.ItemsSource = _visibleBooks;
        RootGrid.KeyDown += RootGrid_KeyDown;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1280, 820));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RootGrid.SizeChanged += (_, _) => UpdateReaderCaptionInset();
        UpdateReaderCaptionInset();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateTitleBarColors();
            if (_webReaderInitialized) _ = WebReader.ExecuteScriptAsync(GetWebReaderModeScript());
        };
        Closed += (_, _) =>
        {
            _translationCancellation?.Cancel();
            _translationCancellation?.Dispose();
            _translationService.Dispose();
            _bubbleAnalysisCancellation?.Cancel();
            _bubbleAnalysisCancellation?.Dispose();
            _bubbleZoomService.Dispose();
            _webSeriesService.Dispose();
        };

        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        _appearance = await _appearanceService.LoadAsync();
        await _trackingService.InitializeAsync();
        TrackingClientIdBox.Text = _trackingService.Settings.AniListClientId;
        AutoSyncToggle.IsOn = _trackingService.Settings.AutoSyncOnCompletion;
        UpdateTrackingAccountUi();
        ThemeBox.SelectedIndex = _appearance.Theme switch { "Light" => 1, "Dark" => 2, "Midnight" => 3, _ => 0 };
        AccentBox.SelectedIndex = _appearance.Accent switch { "Ocean" => 1, "Emerald" => 2, "Violet" => 3, _ => 0 };
        PageAnimationBox.SelectedIndex = _appearance.PageTurnAnimation switch { "None" => 0, "Fade" => 1, "PageTurn" => 3, _ => 2 };
        WebChapterSortBox.SelectedIndex = _appearance.WebChapterSort == "Oldest" ? 1 : 0;
        ApplyAppearance();
        _updatingAppearance = false;
        var loaded = await _libraryService.LoadAsync();
        foreach (var book in loaded.Where(b => b.IsWebSeries || File.Exists(b.FilePath)))
        {
            LoadCover(book);
            _books.Add(book);
        }
        RefreshLibrary();
    }

    private async void Appearance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAppearance || ThemeBox.SelectedItem is not ComboBoxItem theme || AccentBox.SelectedItem is not ComboBoxItem accent) return;
        _appearance.Theme = theme.Tag?.ToString() ?? "System";
        _appearance.Accent = accent.Tag?.ToString() ?? "Coral";
        ApplyAppearance();
        await _appearanceService.SaveAsync(_appearance);
    }

    private void ApplyAppearance()
    {
        ApplyDarkPalette(_appearance.Theme == "Midnight");
        RootGrid.RequestedTheme = _appearance.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            "Midnight" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        var color = _appearance.Accent switch
        {
            "Ocean" => ColorHelper.FromArgb(255, 46, 126, 246),
            "Emerald" => ColorHelper.FromArgb(255, 28, 164, 116),
            "Violet" => ColorHelper.FromArgb(255, 132, 91, 246),
            _ => ColorHelper.FromArgb(255, 255, 106, 61)
        };

        if (_webReaderInitialized)
            _ = WebReader.ExecuteScriptAsync(GetWebReaderModeScript());
        if (Application.Current.Resources["AccentBrush"] is SolidColorBrush brush) brush.Color = color;
        Application.Current.Resources["AccentColor"] = color;
        DispatcherQueue.TryEnqueue(UpdateTitleBarColors);
    }

    private static void ApplyDarkPalette(bool midnight)
    {
        if (Application.Current.Resources.ThemeDictionaries["Default"] is not ResourceDictionary resources) return;
        var palette = midnight
            ? new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#000000",
                ["NavBackgroundBrush"] = "#000000",
                ["InkBrush"] = "#FAFAFA",
                ["MutedBrush"] = "#A1A1AA",
                ["SubtleBrush"] = "#71717A",
                ["SurfaceBrush"] = "#0A0A0A",
                ["Surface2Brush"] = "#111111",
                ["LineBrush"] = "#27272A",
                ["HeroBrush"] = "#080808",
                ["CardBrush"] = "#090909"
            }
            : new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#0C0F16",
                ["NavBackgroundBrush"] = "#10141D",
                ["InkBrush"] = "#F6F2E9",
                ["MutedBrush"] = "#9AA3B2",
                ["SubtleBrush"] = "#697386",
                ["SurfaceBrush"] = "#171B25",
                ["Surface2Brush"] = "#202633",
                ["LineBrush"] = "#2E3544",
                ["HeroBrush"] = "#17243B",
                ["CardBrush"] = "#1B202B"
            };
        foreach (var (key, hex) in palette)
            if (resources[key] is SolidColorBrush brush) brush.Color = ParseColor(hex);
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return ColorHelper.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private async void PageAnimation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAppearance || PageAnimationBox.SelectedItem is not ComboBoxItem item) return;
        _appearance.PageTurnAnimation = item.Tag?.ToString() ?? "Slide";
        await _appearanceService.SaveAsync(_appearance);
    }

    private void UpdateTitleBarColors()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        var light = RootGrid.ActualTheme == ElementTheme.Light;
        appWindow.TitleBar.ButtonForegroundColor = light ? Colors.Black : Colors.White;
        appWindow.TitleBar.ButtonHoverForegroundColor = light ? Colors.Black : Colors.White;
        appWindow.TitleBar.ButtonHoverBackgroundColor = light
            ? ColorHelper.FromArgb(24, 0, 0, 0)
            : ColorHelper.FromArgb(28, 255, 255, 255);
    }

    private static void LoadCover(ComicBook book)
    {
        if (File.Exists(book.CoverPath)) book.CoverImage = new BitmapImage(new Uri(book.CoverPath));
        else if (Uri.TryCreate(book.CoverPath, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            book.CoverImage = new BitmapImage(uri);
    }

    private async void AddSeriesLink_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "https://weebcentral.com/series/…",
            MinWidth = 500,
            TextWrapping = TextWrapping.NoWrap
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "Paste a WeebCentral series page link. InkShelf stores chapter metadata only; chapters open through the website’s embedded reader and require internet access.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        });
        content.Children.Add(input);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Add linked manga",
            Content = content,
            PrimaryButtonText = "Add series",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        try
        {
            var info = await _webSeriesService.ImportAsync(input.Text);
            var id = $"wc-{info.SeriesId.ToLowerInvariant()}";
            var existing = _books.FirstOrDefault(b => b.Id == id);
            if (existing is not null)
            {
                existing.Title = info.Title;
                existing.CoverPath = info.CoverUrl;
                existing.SourceUrl = info.SourceUrl;
                existing.PageCount = info.Chapters.Count;
                existing.LastWebRefreshAt = info.RefreshedAt;
                LoadCover(existing);
                ShowMessage("Series refreshed", $"Updated {info.Title} with {info.Chapters.Count} chapters.", InfoBarSeverity.Success);
            }
            else
            {
                var book = new ComicBook
                {
                    Id = id,
                    Title = info.Title,
                    CoverPath = info.CoverUrl,
                    SourceType = "WeebCentral",
                    SourceUrl = info.SourceUrl,
                    WebSeriesId = info.SeriesId,
                    PageCount = info.Chapters.Count,
                    LastWebRefreshAt = info.RefreshedAt
                };
                LoadCover(book);
                _books.Insert(0, book);
                ShowMessage("Linked series added", $"{info.Title} is now on your shelf with {info.Chapters.Count} chapters.", InfoBarSeverity.Success);
            }
            await _libraryService.SaveAsync(_books);
            RefreshLibrary();
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn’t add series", ex.Message, InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".cbz");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ImportPathsAsync(files.Select(f => f.Path));
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var paths = Directory.EnumerateFiles(folder.Path, "*.cbz", SearchOption.AllDirectories).Take(1000).ToList();
        if (paths.Count == 0)
        {
            ShowMessage("No CBZ files found", "That folder and its subfolders do not contain any .cbz files.", InfoBarSeverity.Warning);
            return;
        }
        await ImportPathsAsync(paths);
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        SetBusy(true);
        var imported = 0;
        var failed = 0;
        foreach (var path in paths)
        {
            try
            {
                var book = await _libraryService.ImportAsync(path);
                if (_books.Any(b => b.Id == book.Id)) continue;
                LoadCover(book);
                _books.Insert(0, book);
                imported++;
            }
            catch { failed++; }
        }
        await _libraryService.SaveAsync(_books);
        SetBusy(false);
        RefreshLibrary();
        if (imported > 0) ShowMessage("Import complete", $"Added {imported} comic{(imported == 1 ? string.Empty : "s")} to your shelf.", InfoBarSeverity.Success);
        if (failed > 0) ShowMessage("Some files were skipped", $"{failed} file{(failed == 1 ? " was" : "s were")} not a readable CBZ archive.", InfoBarSeverity.Warning);
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button) return;
        _filter = button.Tag?.ToString() ?? "all";
        PageTitle.Text = _filter switch { "continue" => "Continue reading", "favorites" => "Favorites", _ => "All comics" };
        RefreshLibrary();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLibrary();
    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshLibrary();

    private void RefreshLibrary()
    {
        if (LibraryGrid is null) return;
        IEnumerable<ComicBook> query = _books;
        query = _filter switch
        {
            "continue" => query.Where(b => b.CurrentPage > 0 && b.Progress < 99.9),
            "favorites" => query.Where(b => b.IsFavorite),
            _ => query
        };

        var search = SearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(b => b.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        var sort = (SortBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        query = sort switch
        {
            "title" => query.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase),
            "opened" => query.OrderByDescending(b => b.LastOpenedAt ?? DateTimeOffset.MinValue),
            _ => query.OrderByDescending(b => b.AddedAt)
        };

        _visibleBooks.Clear();
        foreach (var book in query) _visibleBooks.Add(book);
        LibraryCount.Text = $"{_visibleBooks.Count} title{(_visibleBooks.Count == 1 ? string.Empty : "s")}";
        EmptyState.Visibility = _visibleBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryGrid.Visibility = _visibleBooks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _continueBook = _books.Where(b => b.CurrentPage > 0 && b.Progress < 99.9).OrderByDescending(b => b.LastOpenedAt).FirstOrDefault();
        ContinueCard.Visibility = _filter == "all" && string.IsNullOrWhiteSpace(search) && _continueBook is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_continueBook is not null)
        {
            ContinueCover.Source = _continueBook.CoverImage;
            ContinueTitle.Text = _continueBook.Title;
            ContinueProgress.Text = _continueBook.ProgressLabel;
            ContinueProgressBar.Value = _continueBook.Progress;
        }
    }

    private async void LibraryGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ComicBook book) return;
        if (book.IsWebSeries) await OpenWebSeriesAsync(book);
        else await OpenReaderAsync(book);
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_continueBook is null) return;
        if (_continueBook.IsWebSeries) await OpenWebSeriesAsync(_continueBook);
        else await OpenReaderAsync(_continueBook);
    }

    private async Task EnsureWebReaderAsync()
    {
        if (_webReaderInitialized) return;
        var dataRoot = Environment.GetEnvironmentVariable("INKSHELF_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkShelf");
        var webViewData = Path.Combine(dataRoot, "WebView2");
        Directory.CreateDirectory(webViewData);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewData, EnvironmentVariableTarget.Process);
        await WebReader.EnsureCoreWebView2Async();
        WebReader.CoreWebView2.Settings.AreDevToolsEnabled = false;
        WebReader.CoreWebView2.Settings.IsStatusBarEnabled = false;
        await WebReader.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetWebReaderModeScript());
        WebReader.CoreWebView2.NewWindowRequested += (webView, args) =>
        {
            args.Handled = true;
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) return;
            if (WebSeriesService.IsAllowedReaderUri(uri)) WebReader.Source = uri;
            else _ = Launcher.LaunchUriAsync(uri);
        };
        _webReaderInitialized = true;
    }

    private string GetWebReaderModeScript()
    {
        var systemLight = _appearance.Theme == "System" && RootGrid.ActualTheme == ElementTheme.Light;
        var background = _appearance.Theme == "Light" || systemLight
            ? "#f5f5f7"
            : _appearance.Theme == "Midnight" ? "#000000" : "#0c0f16";
        var css = $$"""
            html, body, main { margin: 0 !important; padding: 0 !important; background: {{background}} !important; }
            html, body { overflow-x: hidden !important; }
            #main-nav, #nav-top, #last-chapter-top, #chapter-top-ad,
            #chapter-images ~ *, footer, iframe, dialog, .modal, [id*="ad-container"] { display: none !important; }
            main { display: block !important; width: 100% !important; min-height: 100vh !important; }
            main > section:not(#chapter-images) { display: none !important; }
            #chapter-images { display: block !important; width: 100% !important; min-height: 100vh !important; background: {{background}} !important; }
            #chapter-images img { display: block !important; max-width: 100% !important; height: auto !important; margin: 0 auto !important; }
            """;
        var cssJson = System.Text.Json.JsonSerializer.Serialize(css);
        return $$"""
            (() => {
                const applyInkShelfReaderMode = () => {
                    let style = document.getElementById('inkshelf-reader-mode');
                    if (!style) {
                        style = document.createElement('style');
                        style.id = 'inkshelf-reader-mode';
                        (document.head || document.documentElement).appendChild(style);
                    }
                    style.textContent = {{cssJson}};
                    document.documentElement.style.background = '{{background}}';
                };
                if (document.readyState === 'loading')
                    document.addEventListener('DOMContentLoaded', applyInkShelfReaderMode, { once: true });
                else
                    applyInkShelfReaderMode();
            })();
            """;
    }

    private async void AppearanceFlyout_Opening(object sender, object e)
    {
        await UpdateWebCacheSizeAsync();
    }

    private async Task UpdateWebCacheSizeAsync()
    {
        WebCacheSizeText.Text = "Calculating…";
        var bytes = await Task.Run(GetWebCacheSize);
        WebCacheSizeText.Text = bytes switch
        {
            < 1024 => $"{bytes} B cached",
            < 1024 * 1024 => $"{bytes / 1024d:0.0} KB cached",
            < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB cached",
            _ => $"{bytes / 1024d / 1024d / 1024d:0.00} GB cached"
        };
    }

    private static long GetWebCacheSize()
    {
        var dataRoot = Environment.GetEnvironmentVariable("INKSHELF_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkShelf");
        var root = Path.Combine(dataRoot, "WebView2");
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!segments.Any(segment =>
                    segment.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("Code Cache", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("GPUCache", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("CacheStorage", StringComparison.OrdinalIgnoreCase))) continue;
                try { total += new FileInfo(path).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    private async void ClearWebCache_Click(object sender, RoutedEventArgs e)
    {
        var clearButton = sender as Button;
        if (clearButton is not null) clearButton.IsEnabled = false;
        try
        {
            await EnsureWebReaderAsync();
            await WebReader.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
            await UpdateWebCacheSizeAsync();
            ShowMessage("Web reader cache cleared", "Cached images and temporary web files were removed. Site cookies and reading progress were kept.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn’t clear the web cache", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (clearButton is not null) clearButton.IsEnabled = true;
        }
    }

    private async Task OpenWebSeriesAsync(ComicBook book, bool forceRefresh = false)
    {
        _activeWebBook = book;
        WebSeriesView.Visibility = Visibility.Visible;
        WebSeriesTitle.Text = book.Title;
        WebSeriesStatus.Text = forceRefresh ? "Refreshing chapters…" : "Loading chapters…";
        WebChapterCount.Text = string.Empty;
        WebChapterList.ItemsSource = null;
        var hasOpenChapter = _webReaderInitialized &&
            WebReader.Source is Uri currentUri &&
            !string.Equals(currentUri.Scheme, "about", StringComparison.OrdinalIgnoreCase);
        WebReaderEmpty.Visibility = hasOpenChapter ? Visibility.Collapsed : Visibility.Visible;
        WebReaderProgress.IsActive = true;
        WebReaderProgress.Visibility = Visibility.Visible;
        try
        {
            await EnsureWebReaderAsync();
            var info = await _webSeriesService.GetAsync(book, forceRefresh);
            if (_activeWebBook != book) return;
            _activeWebSeries = info;
            book.Title = info.Title;
            book.CoverPath = info.CoverUrl;
            book.SourceUrl = info.SourceUrl;
            book.PageCount = info.Chapters.Count;
            book.LastWebRefreshAt = info.RefreshedAt;
            WebSeriesTitle.Text = info.Title;
            WebSeriesStatus.Text = $"WeebCentral · refreshed {info.RefreshedAt.ToLocalTime():g}";
            ApplyWebChapterSort();
            if (!string.IsNullOrWhiteSpace(book.LastWebChapterUrl))
            {
                var previous = info.Chapters.FirstOrDefault(c => c.Url == book.LastWebChapterUrl);
                if (previous is not null) WebChapterList.SelectedItem = previous;
            }
            LoadCover(book);
            await _libraryService.SaveAsync(_books);
        }
        catch (Exception ex)
        {
            WebSeriesStatus.Text = "Couldn’t load chapters";
            ShowMessage("Couldn’t open linked series", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            WebReaderProgress.IsActive = false;
            WebReaderProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void WebChapterSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WebChapterSortBox.SelectedItem is not ComboBoxItem item) return;
        _appearance.WebChapterSort = item.Tag?.ToString() == "Oldest" ? "Oldest" : "Newest";
        ApplyWebChapterSort();
        if (!_updatingAppearance) await _appearanceService.SaveAsync(_appearance);
    }

    private void ApplyWebChapterSort()
    {
        if (_activeWebSeries is null) return;
        var newestFirst = _appearance.WebChapterSort != "Oldest";
        var chapters = newestFirst
            ? _activeWebSeries.Chapters.ToList()
            : _activeWebSeries.Chapters.AsEnumerable().Reverse().ToList();
        WebChapterList.ItemsSource = chapters;
        WebChapterCount.Text = $"{chapters.Count} chapters";
        if (!string.IsNullOrWhiteSpace(_activeWebBook?.LastWebChapterUrl))
            WebChapterList.SelectedItem = chapters.FirstOrDefault(c => c.Url == _activeWebBook.LastWebChapterUrl);
    }

    private async void WebChapterList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_activeWebBook is null || _activeWebSeries is null || e.ClickedItem is not WebChapter chapter) return;
        await EnsureWebReaderAsync();
        WebReaderEmpty.Visibility = Visibility.Collapsed;
        WebReader.Source = new Uri(chapter.Url);
        _activeWebBook.LastWebChapterUrl = chapter.Url;
        _activeWebBook.LastWebChapterTitle = chapter.Title;
        var newestIndex = _activeWebSeries.Chapters.IndexOf(chapter);
        _activeWebBook.CurrentPage = Math.Max(0, _activeWebSeries.Chapters.Count - newestIndex - 1);
        _activeWebBook.LastOpenedAt = DateTimeOffset.Now;
        await _libraryService.SaveAsync(_books);
    }

    private async void RefreshWebSeries_Click(object sender, RoutedEventArgs e)
    {
        if (_activeWebBook is not null) await OpenWebSeriesAsync(_activeWebBook, true);
    }

    private async void OpenWebSeriesExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_activeWebBook?.SourceUrl is not null && Uri.TryCreate(_activeWebBook.SourceUrl, UriKind.Absolute, out var uri))
            await Launcher.LaunchUriAsync(uri);
    }

    private async void CloseWebSeries_Click(object sender, RoutedEventArgs e)
    {
        WebSeriesView.Visibility = Visibility.Collapsed;
        if (_webReaderInitialized) WebReader.Source = new Uri("about:blank");
        _activeWebBook = null;
        _activeWebSeries = null;
        await _libraryService.SaveAsync(_books);
        RefreshLibrary();
    }

    private void WebReader_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !WebSeriesService.IsAllowedReaderUri(uri))
        {
            args.Cancel = true;
            if (uri is not null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                _ = Launcher.LaunchUriAsync(uri);
            return;
        }
        WebReaderProgress.IsActive = true;
        WebReaderProgress.Visibility = Visibility.Visible;
        WebSeriesStatus.Text = "Loading chapter through WeebCentral…";
    }

    private void WebReader_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        WebReaderProgress.IsActive = false;
        WebReaderProgress.Visibility = Visibility.Collapsed;
        if (_activeWebSeries is not null)
            WebSeriesStatus.Text = args.IsSuccess ? "WeebCentral · Online reader" : "The website could not load this chapter";
        if (args.IsSuccess) _ = WebReader.ExecuteScriptAsync(GetWebReaderModeScript());
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: ComicBook book } item) return;
        book.IsFavorite = item.IsChecked;
        await _libraryService.SaveAsync(_books);
        if (_filter == "favorites") RefreshLibrary();
    }

    private void TrackingFlyout_Opening(object sender, object e)
    {
        TrackingClientIdBox.Text = _trackingService.Settings.AniListClientId;
        AutoSyncToggle.IsOn = _trackingService.Settings.AutoSyncOnCompletion;
        UpdateTrackingAccountUi();
    }

    private void UpdateTrackingAccountUi()
    {
        if (TrackingAccountStatus is null) return;
        TrackingAccountStatus.Text = _trackingService.AniList.IsAuthenticated
            ? $"Connected to AniList as {_trackingService.ViewerName}."
            : "AniList is not connected.";
        ConnectAniListButton.Content = _trackingService.AniList.IsAuthenticated ? "Reconnect" : "Connect AniList";
    }

    private async void OpenAniListDeveloper_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("https://anilist.co/settings/developer"));

    private async void AutoSync_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoSyncToggle is null) return;
        _trackingService.Settings.AutoSyncOnCompletion = AutoSyncToggle.IsOn;
        await _trackingService.SaveSettingsAsync();
    }

    private async void ConnectAniList_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var viewer = await _trackingService.ConnectAsync(TrackingClientIdBox.Text, timeout.Token);
            UpdateTrackingAccountUi();
            ShowMessage("AniList connected", $"Signed in as {viewer}.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage("Couldn’t connect AniList", ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private async void DisconnectAniList_Click(object sender, RoutedEventArgs e)
    {
        await _trackingService.DisconnectAsync();
        UpdateTrackingAccountUi();
        ShowMessage("AniList disconnected", "The saved access token was removed from Windows Credential Locker.", InfoBarSeverity.Success);
    }

    private async void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        await SyncPendingWithUiAsync();
    }

    private async Task SyncPendingWithUiAsync()
    {
        SetBusy(true);
        try
        {
            var result = await _trackingService.SyncPendingAsync(_books);
            await _libraryService.SaveAsync(_books);
            RefreshLibrary();
            ShowMessage("AniList sync complete", $"Synced {result.Synced}; {result.Pending} update{(result.Pending == 1 ? string.Empty : "s")} still pending.", result.Pending == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { ShowMessage("Couldn’t sync AniList", ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private async void LinkAniList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ComicBook book }) return;
        if (!_trackingService.AniList.IsAuthenticated)
        {
            ShowMessage("Connect AniList first", "Open Tracking in the sidebar, enter your AniList client ID, and connect your account.", InfoBarSeverity.Warning);
            return;
        }

        var searchBox = new TextBox { Header = "Search title", Text = book.TrackerTitle ?? book.SeriesTitle ?? book.Title, PlaceholderText = "Manga title" };
        var resultBox = new ComboBox { Header = "AniList result", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select Search to find matches" };
        var searchButton = new Button { Content = "Search AniList", HorizontalAlignment = HorizontalAlignment.Left };
        var chapterStart = new NumberBox { Header = "First chapter in this CBZ", Minimum = 0, Maximum = 100000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = book.ChapterStart ?? InferNumber(book.Title, "chapter", "ch") ?? double.NaN };
        var chapterEnd = new NumberBox { Header = "Last chapter in this CBZ", Minimum = 0, Maximum = 100000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = book.ChapterEnd ?? InferNumber(book.Title, "chapter", "ch") ?? double.NaN };
        var volume = new NumberBox { Header = "Volume (optional)", Minimum = 0, Maximum = 10000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = book.VolumeNumber ?? InferNumber(book.Title, "volume", "vol") ?? double.NaN };
        var status = new TextBlock { Foreground = (Brush)Application.Current.Resources["MutedBrush"], TextWrapping = TextWrapping.Wrap };
        searchButton.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(searchBox.Text)) return;
            searchButton.IsEnabled = false;
            status.Text = "Searching…";
            try
            {
                var results = await _trackingService.AniList.SearchAsync(searchBox.Text.Trim());
                resultBox.ItemsSource = results;
                resultBox.SelectedIndex = results.Count > 0 ? 0 : -1;
                status.Text = results.Count > 0 ? $"Found {results.Count} matches. Confirm the correct series." : "No matches found.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { searchButton.IsEnabled = true; }
        };
        var panel = new StackPanel { Width = 430, Spacing = 10 };
        panel.Children.Add(searchBox);
        panel.Children.Add(searchButton);
        panel.Children.Add(resultBox);
        panel.Children.Add(chapterStart);
        panel.Children.Add(chapterEnd);
        panel.Children.Add(volume);
        panel.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Link comic to AniList",
            Content = panel,
            PrimaryButtonText = "Link",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary) return;
        if (resultBox.SelectedItem is not TrackerSearchResult selected || double.IsNaN(chapterEnd.Value))
        {
            ShowMessage("Tracking details incomplete", "Select an AniList result and enter the last chapter contained in this CBZ.", InfoBarSeverity.Warning);
            return;
        }
        book.TrackerProvider = "AniList";
        book.TrackerMediaId = selected.MediaId;
        book.TrackerTitle = selected.Title;
        book.TrackerTotalChapters = selected.Chapters;
        book.ChapterStart = ToNullableInt(chapterStart.Value);
        book.ChapterEnd = ToNullableInt(chapterEnd.Value);
        book.VolumeNumber = ToNullableInt(volume.Value);
        book.TrackerSyncState = "Linked";
        await _libraryService.SaveAsync(_books);
        RefreshLibrary();
        ShowMessage("Linked to AniList", $"{book.Title} will update {selected.Title} through chapter {book.ChapterEnd}.", InfoBarSeverity.Success);
    }

    private async void SyncBook_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ComicBook book }) return;
        try
        {
            await _trackingService.QueueProgressAsync(book);
            await _libraryService.SaveAsync(_books);
            await SyncPendingWithUiAsync();
        }
        catch (Exception ex) { ShowMessage("Couldn’t sync this comic", ex.Message, InfoBarSeverity.Error); }
    }

    private async void UnlinkTracker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ComicBook book }) return;
        await _trackingService.RemoveQueuedUpdatesAsync(book.Id);
        book.TrackerProvider = null;
        book.TrackerMediaId = null;
        book.TrackerTitle = null;
        book.TrackerTotalChapters = null;
        book.TrackerSyncState = string.Empty;
        await _libraryService.SaveAsync(_books);
        RefreshLibrary();
        ShowMessage("Tracking link removed", $"{book.Title} is no longer linked to AniList.", InfoBarSeverity.Success);
    }

    private static int? InferNumber(string title, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = System.Text.RegularExpressions.Regex.Match(title, $@"(?i)\b{System.Text.RegularExpressions.Regex.Escape(label)}(?:apter|ume)?\.?\s*#?\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var value)) return value;
        }
        return null;
    }

    private static int? ToNullableInt(double value) => double.IsNaN(value) ? null : Math.Max(0, (int)Math.Round(value));

    private async Task OpenReaderAsync(ComicBook book)
    {
        SetBusy(true);
        var opened = false;
        try
        {
            _pages = await _libraryService.ExtractPagesAsync(book);
            _activeBook = book;
            _pageIndex = Math.Clamp(book.CurrentPage, 0, _pages.Count - 1);
            book.LastOpenedAt = DateTimeOffset.Now;
            ReaderTitle.Text = book.Title;
            ReaderView.Visibility = Visibility.Visible;
            BubbleZoomOverlay.Visibility = Visibility.Collapsed;
            _currentBubblePage = null;
            _smartZoomEnabled = false;
            _guidedPage = null;
            _guidedViewEnabled = false;
            GuidedViewOverlay.Visibility = Visibility.Collapsed;
            _updatingTranslationToggle = true;
            TranslationToggle.IsOn = book.TranslationPreference == "Translate";
            _updatingTranslationToggle = false;
            TranslationPanel.Visibility = TranslationToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            _updatingReader = true;
            PageSlider.Minimum = 1;
            PageSlider.Maximum = _pages.Count;
            PageSlider.Value = _pageIndex + 1;
            _updatingReader = false;
            RenderReader();
            ReaderOptionsButton.Focus(FocusState.Programmatic);
            await _libraryService.SaveAsync(_books);
            opened = true;
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn’t open this comic", ex.Message, InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
        if (opened) await PromptForOfflineTranslationAsync(book);
    }

    private void RenderReader()
    {
        if (_activeBook is null || _pages.Count == 0) return;
        _pageIndex = Math.Clamp(_pageIndex, 0, _pages.Count - 1);
        _activeBook.CurrentPage = _pageIndex;
        ReaderCounter.Text = $"Page {_pageIndex + 1} of {_pages.Count}";
        _updatingReader = true;
        PageSlider.Value = _pageIndex + 1;
        _updatingReader = false;
        ReaderPagesPanel.Children.Clear();

        var layout = LayoutControl.SelectedIndex;
        ReaderPagesPanel.Orientation = layout == 2 ? Orientation.Vertical : Orientation.Horizontal;
        ReaderPagesPanel.VerticalAlignment = layout == 2 ? VerticalAlignment.Top : VerticalAlignment.Center;
        var indexes = layout switch
        {
            1 when _pageIndex + 1 < _pages.Count => new[] { _pageIndex, _pageIndex + 1 },
            2 => Enumerable.Range(0, _pages.Count).ToArray(),
            _ => new[] { _pageIndex }
        };
        if (layout == 1 && MangaToggle.IsOn) indexes = indexes.Reverse().ToArray();

        foreach (var index in indexes)
        {
            var image = new Image
            {
                Source = new BitmapImage(new Uri(_pages[index])),
                Stretch = Stretch.Uniform,
                MaxHeight = layout == 2 ? 1500 : Math.Max(500, ReaderScroll.ActualHeight - 56),
                MaxWidth = layout == 1 ? Math.Max(360, (ReaderScroll.ActualWidth - 70) / 2) : Math.Max(500, ReaderScroll.ActualWidth - 56),
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = index
            };
            image.Tapped += ReaderImage_Tapped;
            ReaderPagesPanel.Children.Add(image);
        }
        ReaderScroll.ChangeView(0, layout == 2 ? null : 0, (float)(ZoomSlider.Value / 100d), true);
        _ = _libraryService.SaveAsync(_books);
        if (TranslationToggle.IsOn) _ = LoadCurrentPageTranslationAsync();
        _ = LoadSmartZoomForPageAsync(_pageIndex);
    }

    private async Task PromptForOfflineTranslationAsync(ComicBook book)
    {
        if (book.TranslationPreference != "Ask") return;
        if (!string.IsNullOrWhiteSpace(book.Language) && !book.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return;
        if (!_translationService.IsAvailable)
        {
            ShowMessage("Offline translation unavailable", "This build does not contain the local OCR and Japanese-to-English models.", InfoBarSeverity.Warning);
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Translate this manga?",
            Content = new TextBlock
            {
                MaxWidth = 460,
                TextWrapping = TextWrapping.Wrap,
                Text = "InkShelf can read Japanese on each page and show an English transcript. Processing stays on this PC and translations are cached. The first page can take 10–30 seconds on this computer."
            },
            PrimaryButtonText = "Translate offline",
            SecondaryButtonText = "Not now",
            CloseButtonText = "Keep original",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            book.TranslationPreference = "Translate";
            _updatingTranslationToggle = true;
            TranslationToggle.IsOn = true;
            _updatingTranslationToggle = false;
            TranslationPanel.Visibility = Visibility.Visible;
            await _libraryService.SaveAsync(_books);
            await LoadCurrentPageTranslationAsync();
        }
        else if (result == ContentDialogResult.None)
        {
            book.TranslationPreference = "Original";
            await _libraryService.SaveAsync(_books);
        }
    }

    private async void TranslationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingTranslationToggle || _activeBook is null) return;
        if (TranslationToggle.IsOn && !_translationService.IsAvailable)
        {
            _updatingTranslationToggle = true;
            TranslationToggle.IsOn = false;
            _updatingTranslationToggle = false;
            ShowMessage("Offline translation unavailable", "The local translation runtime or models are missing.", InfoBarSeverity.Error);
            return;
        }
        _activeBook.TranslationPreference = TranslationToggle.IsOn ? "Translate" : "Original";
        TranslationPanel.Visibility = TranslationToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        await _libraryService.SaveAsync(_books);
        if (TranslationToggle.IsOn) await LoadCurrentPageTranslationAsync();
        else _translationCancellation?.Cancel();
    }

    private async Task LoadCurrentPageTranslationAsync(bool force = false)
    {
        if (_activeBook is null || _pages.Count == 0 || !TranslationToggle.IsOn) return;
        var book = _activeBook;
        var pageIndex = _pageIndex;
        var imagePath = _pages[pageIndex];
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = new CancellationTokenSource();
        var token = _translationCancellation.Token;
        TranslationPanel.Visibility = Visibility.Visible;
        TranslationProgress.IsActive = true;
        TranslationProgress.Visibility = Visibility.Visible;
        TranslationStatus.Text = force ? "Translating again locally…" : "Reading Japanese and translating locally…";
        TranslationPageLabel.Text = $"Page {pageIndex + 1} of {_pages.Count} · Japanese → English · Offline";
        EnglishTranslationText.Text = string.Empty;
        JapaneseTranslationText.Text = string.Empty;
        try
        {
            if (force) await _translationService.ClearCachedPageAsync(book.Id, pageIndex);
            var result = await _translationService.TranslatePageAsync(book.Id, pageIndex, imagePath, token);
            if (token.IsCancellationRequested || _activeBook != book || _pageIndex != pageIndex) return;
            EnglishTranslationText.Text = result.EnglishText;
            JapaneseTranslationText.Text = result.JapaneseText;
            TranslationStatus.Text = "Translated on this PC · cached for next time";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            TranslationStatus.Text = "Translation failed";
            EnglishTranslationText.Text = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested && _activeBook == book && _pageIndex == pageIndex)
            {
                TranslationProgress.IsActive = false;
                TranslationProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CloseTranslation_Click(object sender, RoutedEventArgs e)
    {
        _updatingTranslationToggle = true;
        TranslationToggle.IsOn = false;
        _updatingTranslationToggle = false;
        TranslationPanel.Visibility = Visibility.Collapsed;
        _translationCancellation?.Cancel();
        if (_activeBook is not null)
        {
            _activeBook.TranslationPreference = "Original";
            _ = _libraryService.SaveAsync(_books);
        }
    }

    private async void RefreshTranslation_Click(object sender, RoutedEventArgs e) => await LoadCurrentPageTranslationAsync(true);

    private async Task LoadSmartZoomForPageAsync(int pageIndex)
    {
        if (_activeBook is null || pageIndex < 0 || pageIndex >= _pages.Count) return;
        var book = _activeBook;
        var analysis = await _bubbleZoomService.GetCachedPageAsync(book.Id, pageIndex);
        if (_activeBook != book || _pageIndex != pageIndex) return;
        _currentBubblePage = analysis;
        UpdateSmartZoomButton();
        UpdateGuidedViewButton();
    }

    private void UpdateSmartZoomButton()
    {
        if (_isAnalyzingBubbles) return;
        var count = _currentBubblePage?.Bubbles.Count ?? 0;
        SmartZoomButtonText.Text = _smartZoomEnabled && count > 0 ? $"Smart Zoom · {count}" : "Smart Zoom";
        SmartZoomButton.Background = _smartZoomEnabled && count > 0
            ? Application.Current.Resources["AccentBrush"] as Brush
            : null;
        SmartZoomButton.Foreground = _smartZoomEnabled && count > 0 ? new SolidColorBrush(Colors.White) : null;
    }

    private void UpdateGuidedViewButton()
    {
        var count = _guidedViewEnabled ? _guidedPage?.Panels.Count ?? 0 : _currentBubblePage?.Panels.Count ?? 0;
        GuidedViewButtonText.Text = _guidedViewEnabled && count > 0 ? $"Guided · {count}" : "Guided View";
        GuidedViewButton.Background = _guidedViewEnabled ? Application.Current.Resources["AccentBrush"] as Brush : null;
        GuidedViewButton.Foreground = _guidedViewEnabled ? new SolidColorBrush(Colors.White) : null;
    }

    private async void SmartZoom_Click(object sender, RoutedEventArgs e)
    {
        if (_activeBook is null || _pages.Count == 0) return;
        if (_guidedViewEnabled) ExitGuidedView();
        if (_isAnalyzingBubbles)
        {
            _bubbleAnalysisCancellation?.Cancel();
            SmartZoomButtonText.Text = "Stopping…";
            return;
        }

        if (_currentBubblePage is { Bubbles.Count: > 0 })
        {
            _smartZoomEnabled = !_smartZoomEnabled;
            UpdateSmartZoomButton();
            ShowMessage(_smartZoomEnabled ? "Smart Zoom is on" : "Smart Zoom is off",
                _smartZoomEnabled ? "Click a detected speech bubble to magnify it." : "Page clicks now use the normal reader behavior.",
                InfoBarSeverity.Informational);
            return;
        }

        if (!_bubbleZoomService.IsAvailable)
        {
            ShowMessage("Smart Zoom unavailable", "The offline speech-bubble analyzer is missing from this build.", InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Analyze comic layout",
            Content = new TextBlock
            {
                MaxWidth = 470,
                TextWrapping = TextWrapping.Wrap,
                Text = "InkShelf will detect speech bubbles and panels entirely on this PC, determine their reading order, and cache only their positions. Analyze the whole comic once, or try the current page first."
            },
            PrimaryButtonText = "Analyze comic",
            SecondaryButtonText = "Current page",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None) return;
        await AnalyzeBubblesAsync(choice == ContentDialogResult.Primary);
    }

    private async Task AnalyzeBubblesAsync(bool wholeComic)
    {
        if (_activeBook is null) return;
        var book = _activeBook;
        var indexes = wholeComic ? Enumerable.Range(0, _pages.Count).ToArray() : new[] { _pageIndex };
        _bubbleAnalysisCancellation?.Cancel();
        _bubbleAnalysisCancellation?.Dispose();
        _bubbleAnalysisCancellation = new CancellationTokenSource();
        var token = _bubbleAnalysisCancellation.Token;
        _isAnalyzingBubbles = true;
        SmartZoomButton.IsEnabled = true;
        GuidedViewButton.IsEnabled = false;

        var totalBubbles = 0;
        var totalPanels = 0;
        try
        {
            for (var position = 0; position < indexes.Length; position++)
            {
                token.ThrowIfCancellationRequested();
                SmartZoomButtonText.Text = $"Analyzing {position + 1}/{indexes.Length}";
                var page = indexes[position];
                var result = await _bubbleZoomService.AnalyzePageAsync(book.Id, page, _pages[page], MangaToggle.IsOn, cancellationToken: token);
                totalBubbles += result.Bubbles.Count;
                totalPanels += result.Panels.Count;
                if (page == _pageIndex) _currentBubblePage = result;
            }
            _smartZoomEnabled = _currentBubblePage is { Bubbles.Count: > 0 };
            ShowMessage("Comic analysis complete",
                $"Found {totalPanels} panels and {totalBubbles} speech regions. Guided View and Smart Zoom are ready.",
                totalPanels == 0 && totalBubbles == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Smart Zoom stopped", "Completed pages remain cached and can be used immediately.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowMessage("Smart Zoom analysis failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isAnalyzingBubbles = false;
            GuidedViewButton.IsEnabled = true;
            UpdateSmartZoomButton();
            UpdateGuidedViewButton();
        }
    }

    private async void ReaderImage_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_smartZoomEnabled || _activeBook is null || sender is not Image image || image.Tag is not int pageIndex) return;
        var analysis = pageIndex == _pageIndex
            ? _currentBubblePage
            : await _bubbleZoomService.GetCachedPageAsync(_activeBook.Id, pageIndex);
        if (analysis is null || analysis.Bubbles.Count == 0) return;

        var point = e.GetPosition(image);
        var sourceWidth = Math.Max(1, analysis.ImageWidth);
        var sourceHeight = Math.Max(1, analysis.ImageHeight);
        var scale = Math.Min(image.ActualWidth / sourceWidth, image.ActualHeight / sourceHeight);
        var renderedWidth = sourceWidth * scale;
        var renderedHeight = sourceHeight * scale;
        var normalizedX = (point.X - (image.ActualWidth - renderedWidth) / 2) / renderedWidth;
        var normalizedY = (point.Y - (image.ActualHeight - renderedHeight) / 2) / renderedHeight;
        var index = analysis.Bubbles.FindIndex(b =>
            normalizedX >= b.X - 0.012 && normalizedX <= b.X + b.Width + 0.012 &&
            normalizedY >= b.Y - 0.012 && normalizedY <= b.Y + b.Height + 0.012);
        if (index < 0) return;

        e.Handled = true;
        _currentBubblePage = analysis;
        _bubblePageIndex = pageIndex;
        await ShowBubbleAsync(index);
    }

    private async Task ShowBubbleAsync(int index)
    {
        if (_activeBook is null || _currentBubblePage is null || _currentBubblePage.Bubbles.Count == 0) return;
        _bubbleIndex = Math.Clamp(index, 0, _currentBubblePage.Bubbles.Count - 1);
        var bubble = _currentBubblePage.Bubbles[_bubbleIndex];
        try
        {
            var crop = await _bubbleZoomService.GetCropAsync(_activeBook.Id, _bubblePageIndex, _pages[_bubblePageIndex], bubble);
            BubbleZoomImage.Source = new BitmapImage(new Uri(crop));
            BubbleZoomCounter.Text = $"Bubble {_bubbleIndex + 1} of {_currentBubblePage.Bubbles.Count} · Page {_bubblePageIndex + 1}";
            PreviousBubbleButton.IsEnabled = _bubbleIndex > 0;
            NextBubbleButton.IsEnabled = _bubbleIndex + 1 < _currentBubblePage.Bubbles.Count;
            BubbleZoomOverlay.Visibility = Visibility.Visible;
            BubbleZoomCard.Opacity = 0;
            BubbleZoomScale.ScaleX = 0.92;
            BubbleZoomScale.ScaleY = 0.92;
            await PlayPageAnimationAsync(180,
                (BubbleZoomCard, "Opacity", 0, 1),
                (BubbleZoomScale, "ScaleX", 0.92, 1),
                (BubbleZoomScale, "ScaleY", 0.92, 1));
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn’t open Bubble Zoom", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void PreviousBubble_Click(object sender, RoutedEventArgs e) => await ShowBubbleAsync(_bubbleIndex - 1);
    private async void NextBubble_Click(object sender, RoutedEventArgs e) => await ShowBubbleAsync(_bubbleIndex + 1);
    private void CloseBubbleZoom_Click(object sender, RoutedEventArgs e) => CloseBubbleZoom();
    private void BubbleZoomBackdrop_Tapped(object sender, TappedRoutedEventArgs e) => CloseBubbleZoom();
    private void BubbleZoomCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void CloseBubbleZoom()
    {
        BubbleZoomOverlay.Visibility = Visibility.Collapsed;
        BubbleZoomImage.Source = null;
    }

    private async void GuidedView_Click(object sender, RoutedEventArgs e)
    {
        if (_activeBook is null || _pages.Count == 0 || _guidedTransition) return;
        if (_guidedViewEnabled)
        {
            ExitGuidedView();
            return;
        }

        CloseBubbleZoom();
        if (_currentBubblePage is not { Panels.Count: > 0 })
            await AnalyzeBubblesAsync(false);
        if (_currentBubblePage is not { Panels.Count: > 0 })
        {
            ShowMessage("Guided View unavailable", "No readable panels were detected on this page.", InfoBarSeverity.Warning);
            return;
        }

        _guidedViewEnabled = true;
        _smartZoomEnabled = false;
        UpdateSmartZoomButton();
        UpdateGuidedViewButton();
        await ShowGuidedPanelAsync(_pageIndex, 0, 1);
    }

    private async Task ShowGuidedPanelAsync(int pageIndex, int panelIndex, int direction)
    {
        if (_activeBook is null || pageIndex < 0 || pageIndex >= _pages.Count || _guidedTransition) return;
        var book = _activeBook;
        _guidedTransition = true;
        try
        {
            GuidedViewOverlay.Visibility = Visibility.Visible;
            var analysis = await _bubbleZoomService.GetCachedPageAsync(book.Id, pageIndex)
                ?? await _bubbleZoomService.AnalyzePageAsync(book.Id, pageIndex, _pages[pageIndex], MangaToggle.IsOn);
            if (_activeBook != book || !_guidedViewEnabled || analysis.Panels.Count == 0) return;

            var nextIndex = panelIndex == int.MaxValue ? analysis.Panels.Count - 1 : Math.Clamp(panelIndex, 0, analysis.Panels.Count - 1);
            if (GuidedPanelImage.Source is not null)
            {
                await PlayPageAnimationAsync(105,
                    (GuidedPanelImage, "Opacity", 1, 0),
                    (GuidedPanelTranslate, "X", 0, -42 * Math.Sign(direction)));
            }

            if (_pageIndex != pageIndex)
            {
                _pageIndex = pageIndex;
                RenderReader();
            }
            _guidedPage = analysis;
            _currentBubblePage = analysis;
            _guidedPanelIndex = nextIndex;
            var panel = analysis.Panels[nextIndex];
            var crop = await _bubbleZoomService.GetPanelCropAsync(book.Id, pageIndex, _pages[pageIndex], panel);
            if (_activeBook != book || !_guidedViewEnabled) return;

            GuidedPanelImage.Source = new BitmapImage(new Uri(crop));
            GuidedPanelCounter.Text = $"Panel {nextIndex + 1} of {analysis.Panels.Count} · Page {pageIndex + 1} of {_pages.Count}";
            PreviousGuidedPanelButton.IsEnabled = nextIndex > 0 || pageIndex > 0;
            NextGuidedPanelButton.IsEnabled = nextIndex + 1 < analysis.Panels.Count || pageIndex + 1 < _pages.Count;
            GuidedPanelTranslate.X = 42 * Math.Sign(direction);
            GuidedPanelImage.Opacity = 0;
            await PlayPageAnimationAsync(165,
                (GuidedPanelImage, "Opacity", 0, 1),
                (GuidedPanelTranslate, "X", 42 * Math.Sign(direction), 0));
            UpdateGuidedViewButton();
            _ = _libraryService.SaveAsync(_books);
        }
        catch (Exception ex)
        {
            ExitGuidedView();
            ShowMessage("Guided View failed", ex.Message, InfoBarSeverity.Error);
        }
        finally { _guidedTransition = false; }
    }

    private async Task MoveGuidedPanelAsync(int delta)
    {
        if (!_guidedViewEnabled || _guidedPage is null || _guidedTransition) return;
        var nextPanel = _guidedPanelIndex + delta;
        if (nextPanel >= 0 && nextPanel < _guidedPage.Panels.Count)
        {
            await ShowGuidedPanelAsync(_pageIndex, nextPanel, delta);
            return;
        }

        var nextPage = _pageIndex + Math.Sign(delta);
        if (nextPage < 0 || nextPage >= _pages.Count)
        {
            ShowMessage("End of guided sequence", delta > 0 ? "You reached the final panel." : "You are at the first panel.", InfoBarSeverity.Informational);
            return;
        }
        await ShowGuidedPanelAsync(nextPage, delta > 0 ? 0 : int.MaxValue, delta);
    }

    private async void PreviousGuidedPanel_Click(object sender, RoutedEventArgs e) => await MoveGuidedPanelAsync(-1);
    private async void NextGuidedPanel_Click(object sender, RoutedEventArgs e) => await MoveGuidedPanelAsync(1);
    private async void GuidedPanelImage_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        await MoveGuidedPanelAsync(1);
    }

    private void ExitGuidedView()
    {
        _guidedViewEnabled = false;
        _guidedPage = null;
        GuidedViewOverlay.Visibility = Visibility.Collapsed;
        GuidedPanelImage.Source = null;
        GuidedPanelImage.Opacity = 1;
        GuidedPanelTranslate.X = 0;
        UpdateGuidedViewButton();
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedViewEnabled) await MoveGuidedPanelAsync(-1);
        else await MovePageAsync(-StepSize());
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedViewEnabled) await MoveGuidedPanelAsync(1);
        else await MovePageAsync(StepSize());
    }
    private int StepSize() => LayoutControl.SelectedIndex == 1 ? 2 : 1;

    private async Task MovePageAsync(int delta)
    {
        if (_pages.Count == 0 || _isPageAnimating) return;
        var next = Math.Clamp(_pageIndex + delta, 0, _pages.Count - 1);
        if (next == _pageIndex) return;
        var direction = Math.Sign(delta) * (MangaToggle.IsOn ? -1 : 1);
        await AnimatePageOutAsync(direction);
        _pageIndex = next;
        RenderReader();
        await AnimatePageInAsync(direction);
        if (_activeBook is not null && _pageIndex == _pages.Count - 1)
            await HandleCompletedComicAsync(_activeBook);
    }

    private async Task HandleCompletedComicAsync(ComicBook book)
    {
        if (!_trackingService.Settings.AutoSyncOnCompletion || book.TrackerMediaId is null || book.ChapterEnd is not > 0 || book.LastSyncedChapter >= book.ChapterEnd) return;
        try
        {
            await _trackingService.QueueProgressAsync(book);
            if (_trackingService.AniList.IsAuthenticated) await _trackingService.SyncPendingAsync(_books);
            await _libraryService.SaveAsync(_books);
            ShowMessage(book.TrackerSyncState == "Pending sync" ? "AniList update queued" : "AniList updated", $"{book.TrackerTitle}: chapter {book.ChapterEnd}.", book.TrackerSyncState == "Pending sync" ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch
        {
            book.TrackerSyncState = "Pending sync";
            await _libraryService.SaveAsync(_books);
        }
    }

    private async void PageSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingReader || _pages.Count == 0 || _isPageAnimating || _guidedTransition) return;
        var next = Math.Clamp((int)Math.Round(e.NewValue) - 1, 0, _pages.Count - 1);
        if (next == _pageIndex) return;
        if (_guidedViewEnabled)
        {
            await ShowGuidedPanelAsync(next, 0, Math.Sign(next - _pageIndex));
            return;
        }
        var direction = Math.Sign(next - _pageIndex) * (MangaToggle.IsOn ? -1 : 1);
        await AnimatePageOutAsync(direction);
        _pageIndex = next;
        RenderReader();
        await AnimatePageInAsync(direction);
    }

    private async Task AnimatePageOutAsync(int direction)
    {
        var mode = _appearance.PageTurnAnimation;
        if (mode == "None" || LayoutControl.SelectedIndex == 2) return;
        _isPageAnimating = true;
        if (mode == "Fade")
            await PlayPageAnimationAsync(120, (ReaderPagesPanel, "Opacity", 1, 0));
        else if (mode == "PageTurn")
        {
            await CapturePageCurlAsync();
        }
        else
            await PlayPageAnimationAsync(135,
                (ReaderTranslate, "X", 0, -80 * direction),
                (ReaderPagesPanel, "Opacity", 1, 0));
    }

    private async Task AnimatePageInAsync(int direction)
    {
        var mode = _appearance.PageTurnAnimation;
        ResetPageAnimation();
        if (mode == "None" || LayoutControl.SelectedIndex == 2) { _isPageAnimating = false; return; }
        if (mode == "Fade")
        {
            ReaderPagesPanel.Opacity = 0;
            await PlayPageAnimationAsync(170, (ReaderPagesPanel, "Opacity", 0, 1));
        }
        else if (mode == "PageTurn")
        {
            try { await PlayPageCurlAsync(direction); }
            finally { PageCurlOverlay.Children.Clear(); PageCurlOverlay.Visibility = Visibility.Collapsed; _isPageAnimating = false; }
        }
        else
        {
            ReaderTranslate.X = 80 * direction;
            ReaderPagesPanel.Opacity = 0;
            await PlayPageAnimationAsync(190,
                (ReaderTranslate, "X", 80 * direction, 0),
                (ReaderPagesPanel, "Opacity", 0, 1));
        }
        ResetPageAnimation();
        _isPageAnimating = false;
    }

    private static async Task PlayPageAnimationAsync(int milliseconds, params (DependencyObject Target, string Property, double From, double To)[] tracks)
    {
        var storyboard = new Storyboard();
        foreach (var track in tracks)
        {
            var animation = new DoubleAnimation
            {
                From = track.From,
                To = track.To,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, track.Target);
            Storyboard.SetTargetProperty(animation, track.Property);
            storyboard.Children.Add(animation);
        }
        var completion = new TaskCompletionSource<bool>();
        storyboard.Completed += (_, _) => completion.TrySetResult(true);
        storyboard.Begin();
        await completion.Task;
    }

    private void ResetPageAnimation()
    {
        ReaderPagesPanel.Opacity = 1;
        ReaderTranslate.X = 0;
        ReaderProjection.RotationY = 0;
    }

    private void LayoutControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReaderView.Visibility == Visibility.Visible) RenderReader();
    }

    private void ReaderSetting_Changed(object sender, RoutedEventArgs e)
    {
        DimOverlay.Visibility = DimToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (ReaderView.Visibility == Visibility.Visible) RenderReader();
    }

    private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ReaderScroll is not null && ReaderView.Visibility == Visibility.Visible)
            ReaderScroll.ChangeView(null, null, (float)(e.NewValue / 100d), true);
    }

    private async void CloseReader_Click(object sender, RoutedEventArgs e)
    {
        _translationCancellation?.Cancel();
        _bubbleAnalysisCancellation?.Cancel();
        CloseBubbleZoom();
        ExitGuidedView();
        ReaderView.Visibility = Visibility.Collapsed;
        if (_isFullScreen) ToggleFullscreen();
        await _libraryService.SaveAsync(_books);
        RefreshLibrary();
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _isFullScreen = !_isFullScreen;
        _appWindow.SetPresenter(_isFullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        DispatcherQueue.TryEnqueue(UpdateReaderCaptionInset);
    }

    private void UpdateReaderCaptionInset()
    {
        var rightInset = _isFullScreen ? 0 : _appWindow.TitleBar.RightInset;
        ReaderCaptionInsetColumn.Width = new GridLength(Math.Max(12, rightInset + 12));
        WebCaptionInsetColumn.Width = new GridLength(Math.Max(12, rightInset + 12));
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ReaderView.Visibility != Visibility.Visible) return;
        if (BubbleZoomOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == VirtualKey.Escape) CloseBubbleZoom();
            else if (e.Key == VirtualKey.Right && NextBubbleButton.IsEnabled) _ = ShowBubbleAsync(_bubbleIndex + 1);
            else if (e.Key == VirtualKey.Left && PreviousBubbleButton.IsEnabled) _ = ShowBubbleAsync(_bubbleIndex - 1);
            else return;
            e.Handled = true;
        }
        else if (_guidedViewEnabled)
        {
            if (e.Key == VirtualKey.Escape) ExitGuidedView();
            else if (e.Key == VirtualKey.Space) _ = MoveGuidedPanelAsync(1);
            else if (e.Key == VirtualKey.Right) _ = MoveGuidedPanelAsync(MangaToggle.IsOn ? -1 : 1);
            else if (e.Key == VirtualKey.Left) _ = MoveGuidedPanelAsync(MangaToggle.IsOn ? 1 : -1);
            else return;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape) { CloseReader_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == VirtualKey.Space) { _ = MovePageAsync(StepSize()); e.Handled = true; }
        else if (e.Key == VirtualKey.Right) { _ = MovePageAsync(MangaToggle.IsOn ? -StepSize() : StepSize()); e.Handled = true; }
        else if (e.Key == VirtualKey.Left) { _ = MovePageAsync(MangaToggle.IsOn ? StepSize() : -StepSize()); e.Handled = true; }
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RootGrid.IsHitTestVisible = !busy;
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }
}
