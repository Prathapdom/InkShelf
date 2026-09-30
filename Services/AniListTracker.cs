using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InkShelf.Models;
using Windows.Security.Credentials;
using Windows.System;

namespace InkShelf.Services;

public sealed class AniListTracker : ITrackerProvider
{
    public const string RedirectUri = "http://127.0.0.1:43871/callback";
    private const string VaultResource = "InkShelf.AniList";
    private const string VaultUser = "oauth";
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://graphql.anilist.co/") };
    private string? _accessToken;

    public string Name => "AniList";
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_accessToken);

    public async Task<string?> RestoreSessionAsync()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(VaultResource, VaultUser);
            credential.RetrievePassword();
            _accessToken = credential.Password;
            return await GetViewerNameAsync();
        }
        catch
        {
            _accessToken = null;
            return null;
        }
    }

    public async Task<string> AuthenticateAsync(string clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !clientId.All(char.IsDigit))
            throw new InvalidOperationException("Enter the numeric client ID from your AniList developer application.");

        var listener = new TcpListener(IPAddress.Loopback, 43871);
        listener.Start();
        try
        {
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var authorize = new Uri($"https://anilist.co/api/v2/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&response_type=token&state={state}");
            if (!await Launcher.LaunchUriAsync(authorize))
                Process.Start(new ProcessStartInfo(authorize.AbsoluteUri) { UseShellExecute = true });

            using var registration = cancellationToken.Register(listener.Stop);
            using (var callback = await listener.AcceptTcpClientAsync(cancellationToken))
            {
                _ = await ReadRequestAsync(callback.GetStream(), cancellationToken);
                var html = $"<!doctype html><meta charset='utf-8'><title>InkShelf</title><style>body{{font:16px system-ui;background:#10141d;color:#fff;padding:48px;max-width:640px;margin:auto}}h1{{color:#ff6a3d}}</style><h1>Connecting InkShelf…</h1><p id='s'>Completing AniList authorization.</p><script>const p=new URLSearchParams(location.hash.slice(1));const t=p.get('access_token'),s=p.get('state');if(t&&s){{fetch('/token',{{method:'POST',headers:{{'Content-Type':'application/x-www-form-urlencoded'}},body:'access_token='+encodeURIComponent(t)+'&state='+encodeURIComponent(s)}}).then(()=>document.getElementById('s').textContent='Connected. You can close this tab.')}}else{{document.getElementById('s').textContent='Authorization was not completed.'}}</script>";
                await WriteResponseAsync(callback.GetStream(), "200 OK", "text/html; charset=utf-8", html, cancellationToken);
            }

            using var tokenClient = await listener.AcceptTcpClientAsync(cancellationToken);
            var request = await ReadRequestAsync(tokenClient.GetStream(), cancellationToken);
            var token = ParseFormValue(request.Body, "access_token");
            var returnedState = ParseFormValue(request.Body, "state");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(returnedState ?? string.Empty)))
                throw new InvalidOperationException("The AniList sign-in response could not be verified. Please try again.");
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("AniList did not return an access token.");
            await WriteResponseAsync(tokenClient.GetStream(), "204 No Content", "text/plain", string.Empty, cancellationToken);

            _accessToken = token;
            var viewer = await GetViewerNameAsync();
            StoreToken(token);
            return viewer;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("AniList sign-in was not completed within three minutes.");
        }
        finally { listener.Stop(); }
    }

    public Task SignOutAsync()
    {
        _accessToken = null;
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(VaultResource, VaultUser));
        }
        catch { }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TrackerSearchResult>> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        const string query = "query($search:String!){Page(perPage:12){media(search:$search,type:MANGA,sort:SEARCH_MATCH){id title{userPreferred romaji english native} chapters volumes status}}}";
        using var document = await SendAsync(query, new { search = title }, cancellationToken);
        var results = new List<TrackerSearchResult>();
        foreach (var media in document.RootElement.GetProperty("data").GetProperty("Page").GetProperty("media").EnumerateArray())
        {
            var titles = media.GetProperty("title");
            results.Add(new TrackerSearchResult
            {
                MediaId = media.GetProperty("id").GetInt32(),
                Title = FirstString(titles, "userPreferred", "english", "romaji", "native") ?? "Untitled",
                NativeTitle = FirstString(titles, "native") ?? string.Empty,
                Chapters = NullableInt(media, "chapters"),
                Volumes = NullableInt(media, "volumes"),
                Status = FirstString(media, "status") ?? string.Empty
            });
        }
        return results;
    }

    public async Task UpdateProgressAsync(int mediaId, int chapter, int? volume, int? totalChapters, CancellationToken cancellationToken = default)
    {
        const string mutation = "mutation($mediaId:Int!,$progress:Int!,$volumes:Int,$status:MediaListStatus){SaveMediaListEntry(mediaId:$mediaId,progress:$progress,progressVolumes:$volumes,status:$status){id progress progressVolumes status}}";
        var status = totalChapters is > 0 && chapter >= totalChapters ? "COMPLETED" : "CURRENT";
        using var _ = await SendAsync(mutation, new { mediaId, progress = chapter, volumes = volume, status }, cancellationToken);
    }

    private async Task<string> GetViewerNameAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync("query{Viewer{name}}", new { }, cancellationToken);
        return document.RootElement.GetProperty("data").GetProperty("Viewer").GetProperty("name").GetString() ?? "AniList user";
    }

    private async Task<JsonDocument> SendAsync(string query, object variables, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated) throw new InvalidOperationException("Connect an AniList account first.");
        using var request = new HttpRequestMessage(HttpMethod.Post, string.Empty);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) _accessToken = null;
            throw new InvalidOperationException(ReadApiError(content) ?? $"AniList returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        var document = JsonDocument.Parse(content);
        if (document.RootElement.TryGetProperty("errors", out var errors))
        {
            var message = errors.EnumerateArray().FirstOrDefault().TryGetProperty("message", out var value) ? value.GetString() : null;
            document.Dispose();
            throw new InvalidOperationException(message ?? "AniList could not complete the request.");
        }
        return document;
    }

    private static string? ReadApiError(string content)
    {
        try
        {
            using var json = JsonDocument.Parse(content);
            return json.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0
                ? errors[0].GetProperty("message").GetString() : null;
        }
        catch { return null; }
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
        return null;
    }

    private static int? NullableInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static void StoreToken(string token)
    {
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(VaultResource, VaultUser)); } catch { }
        vault.Add(new PasswordCredential(VaultResource, VaultUser, token));
    }

    private static string? ParseFormValue(string body, string name)
    {
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == name) return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
        }
        return null;
    }

    private static async Task<(string Headers, string Body)> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[4096];
        var headerEnd = -1;
        var contentLength = 0;
        while (headerEnd < 0)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            headerEnd = FindHeaderEnd(bytes);
        }
        if (headerEnd < 0) return (string.Empty, string.Empty);
        var headers = Encoding.UTF8.GetString(bytes.Take(headerEnd).ToArray());
        foreach (var line in headers.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out contentLength);
        var bodyStart = headerEnd + 4;
        while (bytes.Count - bodyStart < contentLength)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
        }
        return (headers, Encoding.UTF8.GetString(bytes.Skip(bodyStart).Take(contentLength).ToArray()));
    }

    private static int FindHeaderEnd(List<byte> bytes)
    {
        for (var i = 0; i <= bytes.Count - 4; i++)
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, string body, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes, cancellationToken);
    }
}
