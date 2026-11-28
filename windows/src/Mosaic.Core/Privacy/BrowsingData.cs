namespace Mosaic.Core.Privacy;

// Clearing browsing data
//
// The Clear Browsing Data dialog (Settings › Privacy & Security) clears what both engines hold,
// and also Mosaic's own records: the history list (library area), the downloads list (downloads
// area) and anything else that keeps a record of browsing. Those services register here; the
// privacy area never calls into them directly.
//
// Adopting it, e.g. in the library:
//
//     public sealed class HistoryStore : IBrowsingDataClearing
//     {
//         public string BrowsingDataName => "Mosaic history";
//         public IReadOnlySet<BrowsingDataKind> ClearableBrowsingData { get; } =
//             new HashSet<BrowsingDataKind> { BrowsingDataKind.History };
//         public Task ClearBrowsingDataAsync(IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since,
//                                            CancellationToken cancellation = default)
//             => DeleteHistoryAsync(since);          // null = everything
//     }
//     // once, when the service starts:
//     BrowsingDataRegistry.Shared.Register(historyStore);
//
// The registry holds services weakly, so registering never extends a lifetime.
//
// Port of Sources/MosaicCore/Privacy/BrowsingData.swift.

/// <summary>The kinds of data the Clear Browsing Data dialog offers.</summary>
public enum BrowsingDataKind
{
    /// <summary>Pages visited (Mosaic's history, Gecko's history, back/forward lists).</summary>
    History,
    /// <summary>Cookies, local storage, IndexedDB, service workers, cache storage.</summary>
    CookiesAndSiteData,
    /// <summary>The HTTP cache.</summary>
    Cache,
    /// <summary>The list of past downloads (never the downloaded files themselves).</summary>
    Downloads,
    /// <summary>Saved form entries (not passwords).</summary>
    FormData,
}

/// <summary>How far back to clear.</summary>
public enum BrowsingDataTimeRange
{
    LastHour,
    Last24Hours,
    Last7Days,
    Last4Weeks,
    AllTime,
}

public static class BrowsingDataKinds
{
    /// <summary>Every kind, in the order the dialog lists them.</summary>
    public static IReadOnlyList<BrowsingDataKind> All { get; } = Enum.GetValues<BrowsingDataKind>();

    /// <summary>The macOS raw value ("history", "cookiesAndSiteData", …), used by automation and the Gecko agent.</summary>
    public static string Id(this BrowsingDataKind kind) => kind switch
    {
        BrowsingDataKind.History => "history",
        BrowsingDataKind.CookiesAndSiteData => "cookiesAndSiteData",
        BrowsingDataKind.Cache => "cache",
        BrowsingDataKind.Downloads => "downloads",
        BrowsingDataKind.FormData => "formData",
        _ => kind.ToString(),
    };

    public static string Title(this BrowsingDataKind kind) => kind switch
    {
        BrowsingDataKind.History => "Browsing history",
        BrowsingDataKind.CookiesAndSiteData => "Cookies and site data",
        BrowsingDataKind.Cache => "Cached images and files",
        BrowsingDataKind.Downloads => "Download history",
        BrowsingDataKind.FormData => "Saved form entries",
        _ => kind.ToString(),
    };

    public static BrowsingDataKind? FromId(string id) =>
        All.Cast<BrowsingDataKind?>().FirstOrDefault(kind => kind!.Value.Id() == id);
}

public static class BrowsingDataTimeRanges
{
    public static IReadOnlyList<BrowsingDataTimeRange> All { get; } = Enum.GetValues<BrowsingDataTimeRange>();

    /// <summary>The macOS raw value ("lastHour", …, "allTime").</summary>
    public static string Id(this BrowsingDataTimeRange range) => range switch
    {
        BrowsingDataTimeRange.LastHour => "lastHour",
        BrowsingDataTimeRange.Last24Hours => "last24Hours",
        BrowsingDataTimeRange.Last7Days => "last7Days",
        BrowsingDataTimeRange.Last4Weeks => "last4Weeks",
        _ => "allTime",
    };

    public static string Title(this BrowsingDataTimeRange range) => range switch
    {
        BrowsingDataTimeRange.LastHour => "Last hour",
        BrowsingDataTimeRange.Last24Hours => "Last 24 hours",
        BrowsingDataTimeRange.Last7Days => "Last 7 days",
        BrowsingDataTimeRange.Last4Weeks => "Last 4 weeks",
        _ => "All time",
    };

    public static BrowsingDataTimeRange? FromId(string id) =>
        All.Cast<BrowsingDataTimeRange?>().FirstOrDefault(range => range!.Value.Id() == id);

    /// <summary>The earliest moment to clear from; null means everything.</summary>
    public static DateTimeOffset? StartDate(this BrowsingDataTimeRange range, DateTimeOffset now) => range switch
    {
        BrowsingDataTimeRange.LastHour => now.AddHours(-1),
        BrowsingDataTimeRange.Last24Hours => now.AddDays(-1),
        BrowsingDataTimeRange.Last7Days => now.AddDays(-7),
        BrowsingDataTimeRange.Last4Weeks => now.AddDays(-28),
        _ => null,
    };
}

/// <summary>
/// A service that keeps browsing data and can delete it on request (see the notes at the top of
/// this file). Register it with <see cref="BrowsingDataRegistry.Shared"/>.
/// </summary>
public interface IBrowsingDataClearing
{
    /// <summary>What the dialog calls this store, e.g. "Mosaic history".</summary>
    string BrowsingDataName { get; }

    /// <summary>The kinds this service holds; it is only asked about these.</summary>
    IReadOnlySet<BrowsingDataKind> ClearableBrowsingData { get; }

    /// <summary>
    /// Deletes data of <paramref name="kinds"/> (a subset of <see cref="ClearableBrowsingData"/>)
    /// recorded at or after <paramref name="since"/>; everything when it is null. Completes when done.
    /// Called on the UI thread; do file or database work in the background.
    /// </summary>
    Task ClearBrowsingDataAsync(IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since, CancellationToken cancellation = default);
}

/// <summary>Where services that keep browsing data register (see <see cref="IBrowsingDataClearing"/>).</summary>
public sealed class BrowsingDataRegistry
{
    public static BrowsingDataRegistry Shared { get; } = new();

    private readonly Lock gate = new();
    private readonly List<WeakReference<IBrowsingDataClearing>> entries = [];

    /// <summary>Adds a service. Registering the same object twice has no effect.</summary>
    public void Register(IBrowsingDataClearing clearer)
    {
        lock (gate)
        {
            Prune();
            if (entries.Any(entry => entry.TryGetTarget(out var target) && ReferenceEquals(target, clearer))) return;
            entries.Add(new WeakReference<IBrowsingDataClearing>(clearer));
        }
    }

    public void Unregister(IBrowsingDataClearing clearer)
    {
        lock (gate)
        {
            entries.RemoveAll(entry => !entry.TryGetTarget(out var target) || ReferenceEquals(target, clearer));
        }
    }

    /// <summary>Services still alive, in registration order.</summary>
    public IReadOnlyList<IBrowsingDataClearing> Clearers
    {
        get
        {
            lock (gate)
            {
                List<IBrowsingDataClearing> alive = [];
                foreach (var entry in entries)
                {
                    if (entry.TryGetTarget(out var target)) alive.Add(target);
                }
                return alive;
            }
        }
    }

    /// <summary>Kinds at least one registered service holds.</summary>
    public IReadOnlySet<BrowsingDataKind> CoveredKinds =>
        Clearers.SelectMany(clearer => clearer.ClearableBrowsingData).ToHashSet();

    /// <summary>
    /// Asks every service holding any of <paramref name="kinds"/> to clear them, one after the
    /// other. Returns the names of the services asked. A service that throws is skipped (its
    /// name is not returned); the others still run.
    /// </summary>
    public async Task<IReadOnlyList<string>> ClearAsync(
        IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since, CancellationToken cancellation = default)
    {
        List<string> asked = [];
        foreach (var clearer in Clearers)
        {
            var relevant = kinds.Where(clearer.ClearableBrowsingData.Contains).ToHashSet();
            if (relevant.Count == 0) continue;
            try
            {
                await clearer.ClearBrowsingDataAsync(relevant, since, cancellation);
                asked.Add(clearer.BrowsingDataName);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine($"[Privacy] {clearer.BrowsingDataName} could not clear: {error.Message}");
            }
        }
        return asked;
    }

    private void Prune() => entries.RemoveAll(entry => !entry.TryGetTarget(out _));
}
