# InkShelf

InkShelf is a local-first CBZ comic reader for Windows, built with the same stack as BudgetBook: **.NET 8 + WinUI 3 (Windows App SDK)**.

## Features

- Import one or many `.cbz` files, or recursively scan a folder.
- Add a WeebCentral series URL, browse its current chapter list, and read chapters in an embedded WebView.
- A cover-first library with search, sorting, recent reading, and favorites filtering.
- Automatic reading-progress persistence in `%LOCALAPPDATA%\InkShelf\library.json`.
- Managed offline copies of imported comics in `%LOCALAPPDATA%\InkShelf\Comics`.
- Single-page, two-page spread, and vertical-scroll layouts.
- Manga direction, night dimming, zoom, page scrubber, full screen, and keyboard navigation.
- Persistent System, Light, Dark, and pure-black Midnight (OLED) themes with Coral, Ocean, Emerald, and Violet accents.
- Remembered page-turn animation options: None, Fade, Slide, and a 3D Page Turn.
- Optional AniList tracking with browser OAuth, secure Windows Credential Locker token storage, title matching, chapter/volume mapping, and an offline retry queue.
- Automatic chapter metadata import from `ComicInfo.xml` when it is present in a CBZ.
- Fully local Japanese-to-English page translation with manga-specific OCR, an English reader overlay, and per-page caching.
- Offline Smart Zoom for speech-bubble magnification and Guided View for animated panel-to-panel reading, with cached manga-aware layout analysis.
- Natural page sorting (`2.jpg` before `10.jpg`) and guarded archive extraction.

## Build and run

```powershell
$env:DOTNET_ROOT="F:\dev\dotnet"
$env:Path="F:\dev\dotnet;$env:Path"
$env:NUGET_PACKAGES="F:\dev\nuget\packages"
dotnet build -c Debug
& .\bin\Debug\net8.0-windows10.0.26100.0\win-x64\InkShelf.exe
```

## Controls

- `←` / `→`: turn pages (reversed in manga mode)
- `Space`: next page, or next panel in Guided View
- `Esc`: close Bubble Zoom/Guided View, then return to the library

The interface is an original Windows design informed by the clean, cover-forward library and configurable reading modes of Panels. No Panels assets or source code are included.

For isolated testing, set `INKSHELF_DATA_DIR` to a temporary directory before launching.

## AniList tracking setup

1. Create an API client at `https://anilist.co/settings/developer`.
2. Set its redirect URL to `http://127.0.0.1:43871/callback`.
3. In InkShelf, open **Tracking**, enter the numeric client ID, and select **Connect AniList**.
4. Right-click a comic, choose **Link to AniList…**, select its series, and confirm the chapters/volume contained in that CBZ.

Tracking is optional and requires internet only while signing in, searching, or synchronizing. Failed updates remain queued locally. MyAnimeList can be added later through the provider-neutral tracking interface after its application credentials are registered.

## Offline Japanese translation

When a Japanese or language-unknown CBZ is opened for the first time, InkShelf offers **Translate offline**. The reader then displays an English transcript panel for the current page. Use **Reading options → Offline English translation** to show or hide it, and **Translate again** to replace a cached result.

OCR and translation run in the bundled `TranslationRuntime`, using local models in `TranslationModels`. Page text and images are never submitted to an online service. First-page processing includes model startup and can take longer; later pages reuse the running worker, and completed results are cached in `%LOCALAPPDATA%\InkShelf\Translations`.

## Smart Zoom and Guided View

Use **Smart Zoom** to analyze the current page or a complete comic. The same completely offline pass finds speech regions and panel boundaries, calculates left-to-right or manga right-to-left order, and stores compact metadata in `%LOCALAPPDATA%\InkShelf\SmartZoom`. Select **Guided View** to read one panel at a time; click the panel or press `Space` to advance. Irregular pages without reliable panel boundaries gracefully fall back to a whole-page step.

## Linked web series

Select **Add series link** in the sidebar and paste a WeebCentral series URL. InkShelf stores series metadata, chapter links, last-read chapter, and reading progress. Its clean reader mode hides the surrounding site interface and leaves the manga pages on an app-themed background. Images remain website-delivered and the persistent WebView cache reuses previously viewed resources; this is not a bulk downloader or permanent offline archive. Internet access may still be required while reading. Use the chapter-list menu to switch between newest-first and oldest-first order, and use the refresh button above the chapter list to fetch newly published chapters. **Appearance → Web reader cache** displays cache usage and can clear cached images without deleting cookies or chapter history. Any CAPTCHA requested by the website still requires manual completion.
