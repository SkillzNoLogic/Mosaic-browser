using System.Text;

namespace Mosaic.Core.Blocker;

/// <summary>A filter list Mosaic's blocker uses.</summary>
/// <param name="Id">uBlock Origin's identifier for the list; also its file name stem.</param>
/// <param name="Title">The list's name, as uBlock Origin shows it.</param>
/// <param name="Url">Where uBlock Origin fetches the list from.</param>
public sealed record FilterList(string Id, string Title, Uri Url)
{
    public string FileName => $"{Id}.txt";
}

/// <summary>
/// The lists uBlock Origin enables out of the box (its <c>assets.json</c>, entries that are not
/// marked <c>off</c>), fetched from the same places uBO fetches them.
/// </summary>
/// <remarks>
/// Port of <c>FilterListCatalog</c> in <c>Sources/MosaicCore/Blocker/FilterLists.swift</c>. Keep it
/// in step with that file and with the list snapshot <c>scripts/bootstrap.ps1</c> takes.
/// </remarks>
public static class FilterListCatalog
{
    public const string SuffixListFileName = "public_suffix_list.dat";
    public static Uri SuffixListUrl { get; } = new("https://publicsuffix.org/list/public_suffix_list.dat");

    public static IReadOnlyList<FilterList> Defaults { get; } =
    [
        UAssets("ublock-filters", "uBlock filters – Ads", "filters/filters.txt"),
        UAssets("ublock-badware", "uBlock filters – Badware risks", "filters/badware.txt"),
        UAssets("ublock-privacy", "uBlock filters – Privacy", "filters/privacy.txt"),
        UAssets("ublock-unbreak", "uBlock filters – Unbreak", "filters/unbreak.txt"),
        UAssets("ublock-quick-fixes", "uBlock filters – Quick fixes", "filters/quick-fixes.txt"),
        UAssets("easylist", "EasyList", "thirdparties/easylist.txt"),
        UAssets("easyprivacy", "EasyPrivacy", "thirdparties/easyprivacy.txt"),
        new("urlhaus-1", "Online Malicious URL Blocklist",
            new Uri("https://malware-filter.gitlab.io/urlhaus-filter/urlhaus-filter-vivaldi-online.txt")),
        new("plowe-0", "Peter Lowe’s Ad and tracking server list",
            new Uri("https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=1&mimetype=plaintext")),
    ];

    private static FilterList UAssets(string id, string title, string path) =>
        new(id, title, new Uri($"https://ublockorigin.github.io/uAssets/{path}"));
}

/// <summary>A filter list's name and text, as handed to <see cref="BlockerEngine"/>.</summary>
public sealed record FilterListText(string Name, string Text);

/// <summary>Outcome of <see cref="FilterListStore.UpdateAsync"/>: the files refreshed and those left as they were.</summary>
public sealed record FilterListUpdate(IReadOnlyList<string> Updated, IReadOnlyList<string> Failed);

/// <summary>
/// The filter lists on disk:
/// <code>
/// &lt;directory&gt;\public_suffix_list.dat
/// &lt;directory&gt;\&lt;list id&gt;.txt
/// </code>
/// The app ships a snapshot of every list (taken when it was built) so blocking works from the
/// first launch, offline; <see cref="Seed"/> copies it here. <see cref="UpdateAsync"/> refreshes
/// the files from the network.
/// </summary>
/// <remarks>Port of <c>FilterListStore</c> in <c>Sources/MosaicCore/Blocker/FilterLists.swift</c>.</remarks>
public sealed class FilterListStore(string directory, IReadOnlyList<FilterList>? lists = null)
{
    /// <summary>Generous for a list of a few megabytes on a slow connection, short enough not to hang an update.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Smallest download accepted as a list; anything shorter is an error message or a truncation.</summary>
    private const int MinimumListSize = 1024;

    public string Directory { get; } = directory;
    public IReadOnlyList<FilterList> Lists { get; } = lists ?? FilterListCatalog.Defaults;

    public string SuffixListPath => Path.Combine(Directory, FilterListCatalog.SuffixListFileName);

    public string PathFor(FilterList list) => Path.Combine(Directory, list.FileName);

    /// <summary>Copies any list that is missing here from the snapshot in <paramref name="bundled"/>.</summary>
    /// <exception cref="IOException">The folder cannot be created or a file cannot be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">As above, for want of permission.</exception>
    public void Seed(string bundled)
    {
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var name in Lists.Select(list => list.FileName).Prepend(FilterListCatalog.SuffixListFileName))
        {
            var destination = Path.Combine(Directory, name);
            var source = Path.Combine(bundled, name);
            if (!File.Exists(destination) && File.Exists(source)) File.Copy(source, destination);
        }
    }

    /// <summary>The Public Suffix List's text, or null if it is not on disk.</summary>
    public string? SuffixList() => ReadText(SuffixListPath);

    /// <summary>Every list that is on disk, in catalogue order.</summary>
    public IReadOnlyList<FilterListText> LoadLists() =>
    [
        .. Lists.Select(list => (list.Id, Text: ReadText(PathFor(list))))
            .Where(entry => !string.IsNullOrEmpty(entry.Text))
            .Select(entry => new FilterListText(entry.Id, entry.Text!)),
    ];

    /// <summary>Paths of the lists that are on disk, in catalogue order.</summary>
    public IReadOnlyList<string> AvailableListPaths() => [.. Lists.Select(PathFor).Where(File.Exists)];

    /// <summary>When the least recently refreshed file was written; null if any is missing.</summary>
    public DateTimeOffset? OldestModification()
    {
        DateTimeOffset? oldest = null;
        foreach (var path in Lists.Select(PathFor).Prepend(SuffixListPath))
        {
            if (!File.Exists(path)) return null;
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (oldest is null || written < oldest) oldest = written;
        }
        return oldest;
    }

    /// <summary>
    /// Downloads fresh copies of every list. A download that fails, or that does not look like the
    /// list it should be, leaves the existing file untouched. Never throws for a failed download.
    /// </summary>
    /// <param name="handler">Sends the requests; tests pass a stub. Default: a new <see cref="HttpClientHandler"/>.</param>
    /// <param name="cancellation">Abandons the update; files already replaced stay replaced.</param>
    public async Task<FilterListUpdate> UpdateAsync(HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        List<string> updated = [];
        List<string> failed = [];
        using var client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
        {
            Timeout = DownloadTimeout,
        };

        List<(string Name, Uri Url, string Destination, bool IsSuffixList)> targets =
        [
            (FilterListCatalog.SuffixListFileName, FilterListCatalog.SuffixListUrl, SuffixListPath, true),
            .. Lists.Select(list => (list.Id, list.Url, PathFor(list), false)),
        ];
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new FilterListUpdate([], [.. targets.Select(target => target.Name)]);
        }

        foreach (var (name, url, destination, isSuffixList) in targets)
        {
            try
            {
                using var response = await client.GetAsync(url, cancellation).ConfigureAwait(false);
                var data = response.StatusCode == System.Net.HttpStatusCode.OK
                    ? await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false)
                    : null;
                if (data is null || !LooksValid(data, isSuffixList))
                {
                    failed.Add(name);
                    continue;
                }
                await WriteReplacingAsync(destination, data, cancellation).ConfigureAwait(false);
                updated.Add(name);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                              && !cancellation.IsCancellationRequested)
            {
                failed.Add(name);
            }
        }
        return new FilterListUpdate(updated, failed);
    }

    /// <summary>Guards against saving an error page or a truncated download as a list.</summary>
    internal static bool LooksValid(byte[] data, bool isSuffixList)
    {
        if (data.Length < MinimumListSize) return false;
        // Decoded leniently: the checks below are about how the text starts, and a character cut
        // in half at the end of the sample must not count against it.
        var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 4096));
        var lowered = head.ToLowerInvariant();
        if (lowered.Contains("<html", StringComparison.Ordinal) || lowered.Contains("<!doctype", StringComparison.Ordinal)) return false;
        if (isSuffixList)
        {
            return head.Contains("publicsuffix.org", StringComparison.Ordinal)
                || head.Contains("===BEGIN ICANN DOMAINS===", StringComparison.Ordinal);
        }
        // Filter lists open with a header or comment line: "[Adblock Plus 2.0]", "! Title: …", "# …".
        var firstLine = head.Split(['\n', '\r'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return firstLine.StartsWith('[') || firstLine.StartsWith('!') || firstLine.StartsWith('#');
    }

    /// <summary>Writes beside the file and renames over it, so a reader never sees half a list.</summary>
    private static async Task WriteReplacingAsync(string path, byte[] data, CancellationToken cancellation)
    {
        var temporary = path + ".part";
        await File.WriteAllBytesAsync(temporary, data, cancellation).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
