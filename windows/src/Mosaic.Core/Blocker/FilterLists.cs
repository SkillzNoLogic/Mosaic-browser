using System.Text;

namespace Mosaic.Core.Blocker;

/// <summary>A filter list Mosaic's blocker uses.</summary>
/// <param name="Id">uBlock Origin's identifier for the list; also its file name stem.</param>
/// <param name="Title">The list's name, as uBlock Origin shows it.</param>
/// <param name="Url">Where uBlock Origin fetches the list from.</param>
/// <param name="Group">uBO's catalogue group: default, ads, privacy, malware, annoyances, multipurpose.</param>
/// <param name="Summary">One line on what the list is for (optional lists only).</param>
public sealed record FilterList(string Id, string Title, Uri Url, string Group = "default", string Summary = "")
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

    /// <summary>
    /// Stricter lists from uBO's catalogue (<c>assets/assets.json</c>, entries marked <c>off</c>),
    /// offered in Settings. Not shipped with the app: downloaded when the user turns one on. Left
    /// out: lists made mostly of things Mosaic does not apply (AdGuard URL Tracking is all
    /// <c>$removeparam</c>; Mosaic strips tracking parameters itself) and regional lists.
    /// </summary>
    public static IReadOnlyList<FilterList> Optional { get; } =
    [
        UAssets("ublock-experimental", "uBlock filters – Experimental", "filters/experimental.txt", "default",
                "Filters uBO is still testing. May break sites."),
        new("adguard-generic", "AdGuard – Ads", new Uri("https://filters.adtidy.org/extension/ublock/filters/2_without_easylist.txt"),
            "ads", "AdGuard’s ad filters, without the parts EasyList already covers."),
        UAssets("block-lan", "Block Outsider Intrusion into LAN", "filters/lan-block.txt", "privacy",
                "Stops public websites from reaching devices on your local network."),
        new("curben-phishing", "Phishing URL Blocklist", new Uri("https://malware-filter.gitlab.io/phishing-filter/phishing-filter.txt"),
            "malware", "Known phishing sites."),
        UAssets("fanboy-cookiemonster", "EasyList – Cookie Notices", "thirdparties/easylist-cookies.txt",
                "annoyances", "Hides cookie consent banners."),
        UAssets("ublock-cookies-easylist", "uBlock filters – Cookie Notices", "filters/annoyances-cookies.txt",
                "annoyances", "uBO’s additions to the cookie notice lists."),
        new("adguard-cookies", "AdGuard – Cookie Notices", new Uri("https://filters.adtidy.org/extension/ublock/filters/18.txt"),
            "annoyances", "AdGuard’s cookie consent banner filters."),
        UAssets("fanboy-social", "EasyList – Social Widgets", "thirdparties/easylist-social.txt", "annoyances",
                "Share buttons and social media embeds."),
        new("adguard-popup-overlays", "AdGuard – Popup Overlays", new Uri("https://filters.adtidy.org/extension/ublock/filters/19.txt"),
            "annoyances", "Subscription and sign-up pop-overs."),
        UAssets("easylist-newsletters", "EasyList – Newsletter Notices", "thirdparties/easylist-newsletters.txt",
                "annoyances", "Newsletter sign-up boxes."),
        UAssets("easylist-notifications", "EasyList – Notifications", "thirdparties/easylist-notifications.txt",
                "annoyances", "Requests to show notifications."),
        UAssets("easylist-chat", "EasyList – Chat Widgets", "thirdparties/easylist-chat.txt", "annoyances",
                "Support and sales chat bubbles."),
        UAssets("easylist-annoyances", "EasyList – Other Annoyances", "thirdparties/easylist-annoyances.txt",
                "annoyances", "Other page clutter."),
        UAssets("ublock-annoyances", "uBlock filters – Annoyances", "filters/annoyances.txt", "annoyances",
                "uBO’s own annoyance filters."),
        new("dpollock-0", "Dan Pollock’s hosts file", new Uri("https://someonewhocares.org/hosts/hosts"),
            "multipurpose", "A long-running hosts-format block list."),
    ];

    /// <summary>Every list Mosaic knows, defaults first.</summary>
    public static IReadOnlyList<FilterList> All { get; } = [.. Defaults, .. Optional];

    /// <summary>The settings page's groups, in order, with their titles.</summary>
    public static IReadOnlyList<(string Id, string Title)> Groups { get; } =
    [
        ("default", "uBlock Origin"), ("ads", "Ads"), ("privacy", "Privacy"), ("malware", "Malware and phishing"),
        ("annoyances", "Cookie notices and annoyances"), ("multipurpose", "Multipurpose"),
    ];

    /// <summary>The lists for a set of enabled IDs, in catalogue order.</summary>
    public static IReadOnlyList<FilterList> ListsEnabled(IEnumerable<string> enabled)
    {
        var ids = enabled.ToHashSet(StringComparer.Ordinal);
        return [.. All.Where(list => ids.Contains(list.Id))];
    }

    public static bool IsDefault(FilterList list) => Defaults.Any(item => item.Id == list.Id);

    /// <summary>The name the user's own filters are compiled under (and their file's stem).</summary>
    public const string UserFiltersName = "user-filters";

    private static FilterList UAssets(string id, string title, string path, string group = "default", string summary = "") =>
        new(id, title, new Uri($"https://ublockorigin.github.io/uAssets/{path}"), group, summary);
}

/// <summary>A filter list's name and text, as handed to <see cref="BlockerEngine"/>.</summary>
public sealed record FilterListText(string Name, string Text);

/// <summary>Outcome of <see cref="FilterListStore.UpdateAsync"/>: the files refreshed and those left as they were.</summary>
public sealed record FilterListUpdate(IReadOnlyList<string> Updated, IReadOnlyList<string> Failed)
{
    /// <summary>"All lists are up to date." or "Could not download: EasyList, …", for the settings page.</summary>
    public string Message => Failed.Count == 0
        ? "All lists are up to date."
        : "Could not download: " + string.Join(", ", Failed.Select(id => FilterListCatalog.All.FirstOrDefault(list => list.Id == id)?.Title ?? id)) + ".";
}

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

    private IReadOnlyList<FilterList> lists = lists ?? FilterListCatalog.Defaults;
    private FilterListUpdate? lastUpdate;

    public string Directory { get; } = directory;

    /// <summary>The lists in use. Replaced as a whole (<see cref="UseLists"/>), so readers on other threads see one set or the other.</summary>
    public IReadOnlyList<FilterList> Lists => Volatile.Read(ref lists);

    /// <summary>Changes which lists are in use (the user turned one on or off); takes effect at the next build.</summary>
    public void UseLists(IReadOnlyList<FilterList> replacement) => Volatile.Write(ref lists, [.. replacement]);

    /// <summary>The outcome of the last <see cref="UpdateAsync"/>, or null if none ran in this session.</summary>
    public FilterListUpdate? LastUpdate => Volatile.Read(ref lastUpdate);

    /// <summary>The user's own filters ("My filters"), compiled after every list.</summary>
    public string UserFiltersPath => Path.Combine(Directory, FilterListCatalog.UserFiltersName + ".txt");

    /// <summary>The user's own filters, or "" when there are none.</summary>
    public string UserFilters() => ReadText(UserFiltersPath) ?? "";

    /// <summary>Saves the user's own filters (replacing the file in one step).</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">As above, for want of permission.</exception>
    public void SaveUserFilters(string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = UserFiltersPath + ".part";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, UserFiltersPath, overwrite: true);
    }

    /// <summary>Enabled lists that have not been downloaded yet.</summary>
    public IReadOnlyList<FilterList> MissingLists() => [.. Lists.Where(list => !File.Exists(PathFor(list)))];

    public bool IsDownloaded(FilterList list) => File.Exists(PathFor(list));

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

    /// <summary>Every list that is on disk, in catalogue order, then the user's filters.</summary>
    public IReadOnlyList<FilterListText> LoadLists()
    {
        List<FilterListText> loaded =
        [
            .. Lists.Select(list => (list.Id, Text: ReadText(PathFor(list))))
                .Where(entry => !string.IsNullOrEmpty(entry.Text))
                .Select(entry => new FilterListText(entry.Id, entry.Text!)),
        ];
        if (UserFilters() is { Length: > 0 } user) loaded.Add(new FilterListText(FilterListCatalog.UserFiltersName, user));
        return loaded;
    }

    /// <summary>Paths of the lists that are on disk, in catalogue order, then the user's filters.</summary>
    public IReadOnlyList<string> AvailableListPaths()
    {
        List<string> paths = [.. Lists.Select(PathFor).Where(File.Exists)];
        if (UserFilters().Length > 0) paths.Add(UserFiltersPath);
        return paths;
    }

    /// <summary>
    /// Which loaded list contains <paramref name="filter"/> as a line, for the dashboard ("My
    /// filters" for the user's own). uBO's logger shows filters in a normalised form, so this finds
    /// most but not all of them; null when not found. Reads the lists from disk: call it off the UI thread.
    /// </summary>
    public string? ListContaining(string filter)
    {
        if (filter.Length == 0) return null;
        var candidates = new HashSet<string>(StringComparer.Ordinal) { filter, filter.StartsWith("||", StringComparison.Ordinal) ? filter : $"||{filter}^" };
        static IEnumerable<string> Lines(string text) => text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (Lines(UserFilters()).Any(line => candidates.Contains(line.Trim()))) return "My filters";
        foreach (var list in Lists)
        {
            if (ReadText(PathFor(list)) is not { } text) continue;
            if (Lines(text).Any(candidates.Contains)) return list.Title;
        }
        return null;
    }

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
    /// <param name="only">Refresh just these lists (by ID), not the suffix list.</param>
    public async Task<FilterListUpdate> UpdateAsync(HttpMessageHandler? handler = null, CancellationToken cancellation = default, IReadOnlySet<string>? only = null)
    {
        var result = await DownloadAsync(handler, cancellation, only).ConfigureAwait(false);
        Volatile.Write(ref lastUpdate, result);
        return result;
    }

    private async Task<FilterListUpdate> DownloadAsync(HttpMessageHandler? handler, CancellationToken cancellation, IReadOnlySet<string>? only)
    {
        List<string> updated = [];
        List<string> failed = [];
        using var client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
        {
            Timeout = DownloadTimeout,
        };

        List<(string Name, Uri Url, string Destination, bool IsSuffixList)> targets = [];
        if (only is null) targets.Add((FilterListCatalog.SuffixListFileName, FilterListCatalog.SuffixListUrl, SuffixListPath, true));
        targets.AddRange(Lists.Where(list => only?.Contains(list.Id) ?? true).Select(list => (list.Id, list.Url, PathFor(list), false)));
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
