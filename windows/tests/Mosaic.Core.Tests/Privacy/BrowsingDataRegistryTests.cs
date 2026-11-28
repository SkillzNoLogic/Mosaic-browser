using System.Runtime.CompilerServices;
using Mosaic.Core.Privacy;

namespace Mosaic.Core.Tests.Privacy;

/// <summary>Port of <c>BrowsingDataRegistryTests</c> in <c>Tests/MosaicCoreTests/PrivacyTests.swift</c>.</summary>
public sealed class BrowsingDataRegistryTests
{
    private sealed class FakeStore(string name, params BrowsingDataKind[] kinds) : IBrowsingDataClearing
    {
        public string BrowsingDataName { get; } = name;
        public IReadOnlySet<BrowsingDataKind> ClearableBrowsingData { get; } = kinds.ToHashSet();
        public List<(IReadOnlySet<BrowsingDataKind> Kinds, DateTimeOffset? Since)> Calls { get; } = [];
        public bool Throws { get; init; }

        public Task ClearBrowsingDataAsync(IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since, CancellationToken cancellation = default)
        {
            Calls.Add((kinds, since));
            return Throws ? Task.FromException(new IOException("disk gone")) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AsksOnlyServicesHoldingTheKinds()
    {
        var registry = new BrowsingDataRegistry();
        var history = new FakeStore("History", BrowsingDataKind.History);
        var downloads = new FakeStore("Downloads", BrowsingDataKind.Downloads);
        registry.Register(history);
        registry.Register(downloads);
        registry.Register(history);
        Assert.Equal(2, registry.Clearers.Count);
        Assert.True(registry.CoveredKinds.SetEquals([BrowsingDataKind.History, BrowsingDataKind.Downloads]));

        var since = DateTimeOffset.FromUnixTimeSeconds(1000);
        var asked = await registry.ClearAsync(new HashSet<BrowsingDataKind> { BrowsingDataKind.History, BrowsingDataKind.Cache }, since);
        Assert.Equal(["History"], asked);
        var call = Assert.Single(history.Calls);
        Assert.True(call.Kinds.SetEquals([BrowsingDataKind.History]));
        Assert.Equal(since, call.Since);
        Assert.Empty(downloads.Calls);

        registry.Unregister(history);
        Assert.Single(registry.Clearers);
    }

    [Fact]
    public async Task AFailingServiceDoesNotStopTheOthers()
    {
        var registry = new BrowsingDataRegistry();
        var broken = new FakeStore("Broken", BrowsingDataKind.History) { Throws = true };
        var fine = new FakeStore("Fine", BrowsingDataKind.History);
        registry.Register(broken);
        registry.Register(fine);
        var asked = await registry.ClearAsync(new HashSet<BrowsingDataKind> { BrowsingDataKind.History }, null);
        Assert.Equal(["Fine"], asked);
        Assert.Single(broken.Calls);
    }

    [Fact]
    public void HoldsServicesWeakly()
    {
        var registry = new BrowsingDataRegistry();
        RegisterTemporary(registry);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Empty(registry.Clearers);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterTemporary(BrowsingDataRegistry registry)
    {
        registry.Register(new FakeStore("Temporary", BrowsingDataKind.History));
        Assert.Single(registry.Clearers);
    }

    [Fact]
    public void TimeRanges()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        Assert.Null(BrowsingDataTimeRange.AllTime.StartDate(now));
        Assert.Equal(now.AddSeconds(-3600), BrowsingDataTimeRange.LastHour.StartDate(now));
        Assert.Equal(now.AddSeconds(-28 * 86_400), BrowsingDataTimeRange.Last4Weeks.StartDate(now));
    }

    [Fact]
    public void IdsMatchMacOSRawValues()
    {
        Assert.Equal(["history", "cookiesAndSiteData", "cache", "downloads", "formData"], BrowsingDataKinds.All.Select(kind => kind.Id()));
        Assert.Equal(BrowsingDataKind.FormData, BrowsingDataKinds.FromId("formData"));
        Assert.Null(BrowsingDataKinds.FromId("passwords"));
        Assert.Equal(["lastHour", "last24Hours", "last7Days", "last4Weeks", "allTime"], BrowsingDataTimeRanges.All.Select(range => range.Id()));
        Assert.Equal(BrowsingDataTimeRange.Last7Days, BrowsingDataTimeRanges.FromId("last7Days"));
        Assert.Equal("Cached images and files", BrowsingDataKind.Cache.Title());
    }
}
