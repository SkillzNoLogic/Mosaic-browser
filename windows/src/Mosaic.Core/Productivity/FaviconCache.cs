using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Mosaic.Core.Productivity;

/// <summary>
/// Site icons for Speed Dial tiles. Port of <c>Sources/Mosaic/Features/Productivity/FaviconCache.swift</c>.
/// </summary>
/// <remarks>
/// Privacy: an icon is fetched only for a site already on the user's Speed Dial, directly from
/// that site (never through a third-party icon service), with no cookies, no cache and no
/// referrer, and kept in the profile (<c>Productivity\Favicons\&lt;host&gt;.png</c>) for 30 days.
/// What the site sent is never stored as is: the app re-encodes it as a PNG of at most 128 px
/// (<see cref="Reencode"/>); without an encoder nothing is stored. Speed Dial's "Show site icons"
/// switch turns fetching off; tiles then show letters. Memory holds file paths only.
/// </remarks>
public sealed partial class FaviconCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);
    private const int PageLimit = 2_000_000;
    private const int IconLimit = 1_000_000;

    private readonly string directory;
    private readonly TimeProvider time;
    private readonly Lazy<HttpClient> client;
    private readonly HashSet<string> inFlight = [];
    private readonly Dictionary<string, DateTimeOffset> failures = [];
    private readonly Dictionary<string, string> known = [];
    private readonly SynchronizationContext? context;

    internal FaviconCache(string directory, TimeProvider time, HttpMessageHandler? handler = null)
    {
        this.directory = directory;
        this.time = time;
        context = SynchronizationContext.Current;
        client = new Lazy<HttpClient>(() =>
        {
            var http = new HttpClient(handler ?? new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = DecompressionMethods.All,
            }, disposeHandler: handler is null)
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0) Mosaic");
            return http;
        });
    }

    /// <summary>
    /// Turns downloaded image bytes into PNG bytes of at most 128 px, or null when they are not an
    /// image (the app sets this with Windows' imaging codecs).
    /// </summary>
    public static Func<byte[], Task<byte[]?>>? Reencode { get; set; }

    /// <summary>An icon file became available for this host (on the UI thread).</summary>
    public event Action<string>? IconReady;

    /// <summary>
    /// The icon file for <paramref name="url"/>'s site if one is cached; when
    /// <paramref name="fetch"/> is set and there is none, starts getting it (see <see cref="IconReady"/>).
    /// </summary>
    public string? IconPath(string url, bool fetch)
    {
        if (SpeedDial.Host(url) is not { } host) return null;
        if (known.TryGetValue(host, out var path)) return path;
        var file = FileFor(host);
        try
        {
            var info = new FileInfo(file);
            if (info.Exists && time.GetUtcNow() - info.LastWriteTimeUtc < MaxAge)
            {
                known[host] = file;
                return file;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (fetch) Start(host, url);
        return null;
    }

    /// <summary>Forgets every cached icon, files included.</summary>
    public void RemoveAll()
    {
        known.Clear();
        failures.Clear();
        var folder = directory;
        _ = Task.Run(() =>
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        });
    }

    internal string FileFor(string host)
    {
        var safe = new StringBuilder();
        foreach (var c in host) safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_');
        return Path.Combine(directory, safe + ".png");
    }

    private void Start(string host, string pageUrl)
    {
        if (Reencode is null || inFlight.Contains(host)) return;
        if (failures.TryGetValue(host, out var failed) && time.GetUtcNow() - failed < RetryAfter) return;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https")) return;
        var root = new Uri($"{page.Scheme}://{page.Authority}/");
        inFlight.Add(host);
        _ = FetchAsync(host, root);
    }

    /// <summary>Exposed for tests: fetches, re-encodes and stores; returns the file or null.</summary>
    internal async Task<string?> FetchAsync(string host, Uri siteRoot)
    {
        string? stored = null;
        try
        {
            stored = await Task.Run(async () =>
            {
                var png = await FetchIconAsync(siteRoot);
                if (png is null) return null;
                Directory.CreateDirectory(directory);
                var file = FileFor(host);
                var temporary = file + ".tmp";
                await File.WriteAllBytesAsync(temporary, png);
                File.Move(temporary, file, overwrite: true);
                return file;
            });
        }
        catch (Exception) { }
        Post(() =>
        {
            inFlight.Remove(host);
            if (stored is null)
            {
                failures[host] = time.GetUtcNow();
                return;
            }
            known[host] = stored;
            IconReady?.Invoke(host);
        });
        return stored;
    }

    private void Post(Action action)
    {
        if (context is null) action();
        else context.Post(_ => action(), null);
    }

    private async Task<(byte[] Data, Uri Final)?> GetAsync(Uri url, int limit)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is { } length && length > limit) return null;
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit) return null;
            }
            return (buffer.ToArray(), response.RequestMessage?.RequestUri ?? url);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>The site's best declared icon (apple-touch-icon, then the largest <c>rel=icon</c>), or /favicon.ico, as PNG.</summary>
    private async Task<byte[]?> FetchIconAsync(Uri siteRoot)
    {
        var candidates = new List<Uri>();
        if (await GetAsync(siteRoot, PageLimit) is { } page)
        {
            var html = Encoding.UTF8.GetString(page.Data, 0, Math.Min(page.Data.Length, 512_000));
            candidates.AddRange(IconLinks(html, page.Final));
        }
        candidates.Add(new Uri(siteRoot, "/favicon.ico"));
        foreach (var url in candidates)
        {
            if (await GetAsync(url, IconLimit) is not { } icon || Reencode is not { } reencode) continue;
            if (await reencode(icon.Data) is { Length: > 0 } png) return png;
        }
        return null;
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    /// <summary><c>&lt;link rel="…icon…" href="…"&gt;</c> targets, best first. SVG and data: URLs are skipped.</summary>
    internal static IReadOnlyList<Uri> IconLinks(string html, Uri baseUrl)
    {
        var found = new List<(Uri Url, int Rank)>();
        foreach (Match match in LinkTag().Matches(html))
        {
            var tag = match.Value;
            if (Attribute("rel", tag)?.ToLowerInvariant() is not { } rel || !rel.Contains("icon")) continue;
            if (Attribute("href", tag) is not { } href || href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUrl, WebUtility.HtmlDecode(href), out var url) || url.Scheme is not ("http" or "https")) continue;
            if (Attribute("type", tag)?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true) continue;
            var size = 16;
            if (Attribute("sizes", tag) is { } sizes && int.TryParse(sizes.ToLowerInvariant().Split('x')[0], out var parsed)) size = parsed;
            var rank = rel.Contains("apple-touch-icon") ? 1000 + size : size;
            found.Add((url, rank));
        }
        return found.OrderByDescending(item => item.Rank).Select(item => item.Url).ToList();
    }

    private static string? Attribute(string name, string tag)
    {
        var match = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        for (var group = 1; group <= 3; group++)
        {
            if (match.Groups[group].Success) return match.Groups[group].Value;
        }
        return null;
    }
}
