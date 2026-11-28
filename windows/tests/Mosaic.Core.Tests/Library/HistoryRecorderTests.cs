using Mosaic.Core.Library;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Library;

/// <summary>The rules of <c>HistoryRecorder.swift</c>: one visit per navigation, with the page's final title.</summary>
public class HistoryRecorderTests
{
    private sealed class Harness
    {
        public ManualTimeProvider Time { get; } = new();
        public List<HistoryRecorder.Visit> Visits { get; } = [];
        public List<(string Url, string Title)> Retitles { get; } = [];
        public HistoryRecorder Recorder { get; }
        public Guid Tab { get; } = Guid.NewGuid();
        public DateTimeOffset Load { get; set; }

        public Harness()
        {
            Recorder = new HistoryRecorder(Visits.Add, (url, title) => Retitles.Add((url, title)), Time);
            Load = Time.GetUtcNow();
        }

        public void Report(string url, string title = "", bool loading = false, bool excluded = false) =>
            Recorder.Observe(Tab, url, title, loading, Load, excluded);

        /// <summary>An explicit load (typed, bookmark): a new load request time.</summary>
        public async Task NewLoadAsync()
        {
            await Time.AdvanceAsync(TimeSpan.FromMilliseconds(10));
            Load = Time.GetUtcNow();
        }

        public Task WaitAsync(double seconds) => Time.AdvanceAsync(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void OneVisitOnceThePageHasSettledWithItsFinalTitle() => TestLoop.Run(async () =>
    {
        var h = new Harness();
        h.Report("https://example.com/", loading: true);
        h.Report("https://example.com/", "", loading: false);
        h.Report("https://example.com/", "Example");
        await h.WaitAsync(1);
        Assert.Empty(h.Visits);
        h.Report("https://example.com/", "Example Domain");
        await h.WaitAsync(1);
        Assert.Empty(h.Visits);
        await h.WaitAsync(1);
        Assert.Equal([new HistoryRecorder.Visit("https://example.com/", "Example Domain", false)], h.Visits);

        // Later reports of the same page add nothing; a new title only renames it.
        h.Report("https://example.com/", "Example Domain");
        h.Report("https://example.com/", "Renamed");
        await h.WaitAsync(5);
        Assert.Single(h.Visits);
        Assert.Equal([("https://example.com/", "Renamed")], h.Retitles);
    });

    [Fact]
    public void MovingOnBeforeSettlingCommitsAtOnce() => TestLoop.Run(async () =>
    {
        var h = new Harness();
        h.Report("https://a.test/", "A");
        h.Report("https://b.test/", "", loading: true);
        Assert.Equal(["https://a.test/"], h.Visits.Select(v => v.Url));
        h.Report("https://b.test/", "B");
        await h.WaitAsync(2);
        Assert.Equal(["https://a.test/", "https://b.test/"], h.Visits.Select(v => v.Url));
    });

    [Fact]
    public void ReloadsDoNotAddVisitsButAnExplicitLoadDoes() => TestLoop.Run(async () =>
    {
        var h = new Harness();
        h.Report("https://a.test/", "A");
        await h.WaitAsync(2);
        h.Report("https://a.test/", "A", loading: true);
        h.Report("https://a.test/", "A");
        await h.WaitAsync(2);
        Assert.Single(h.Visits);

        await h.NewLoadAsync();
        h.Report("https://a.test/", "A", loading: true);
        h.Report("https://a.test/", "A");
        await h.WaitAsync(2);
        Assert.Equal(2, h.Visits.Count);
    });

    [Fact]
    public void ErrorPagesExcludedTabsAndInternalPagesAreNotRecorded() => TestLoop.Run(async () =>
    {
        var h = new Harness();
        h.Report("https://down.test/", "Problem loading page");
        h.Report("https://private.test/", "Secret", excluded: true);
        h.Report("mosaic://history", "History");
        h.Report("about:blank");
        await h.WaitAsync(5);
        Assert.Empty(h.Visits);
    });

    [Fact]
    public void APageThatTurnsOutToBeAnErrorIsDropped() => TestLoop.Run(async () =>
    {
        var h = new Harness();
        h.Report("https://down.test/", "");
        h.Report("https://down.test/", "This site can’t be reached");
        await h.WaitAsync(5);
        Assert.Empty(h.Visits);
    });

    [Fact]
    public void TypedLoadsAreMarked() => TestLoop.Run(async () =>
    {
        var h = new Harness();
        await h.NewLoadAsync();
        h.Recorder.NoteTyped(h.Tab, h.Load);
        h.Report("https://typed.test/", "Typed");
        await h.WaitAsync(2);
        // A link followed from the typed page is not typed.
        h.Report("https://typed.test/next", "Next");
        await h.WaitAsync(2);
        Assert.Equal([true, false], h.Visits.Select(v => v.Typed));
    });

    [Fact]
    public void ASameSiteRewriteWhileSettlingIsOneVisitAtTheFinalAddress() => TestLoop.Run(async () =>
    {
        var h = new Harness();
        h.Report("https://site.test/", "Site");
        await h.WaitAsync(0.5);
        h.Report("https://site.test/home", "Site Home");
        await h.WaitAsync(2);
        Assert.Equal([new HistoryRecorder.Visit("https://site.test/home", "Site Home", false)], h.Visits);
    });

    [Fact]
    public void ATitleThatKeepsChangingIsRecordedAfterTheMaximumDelay() => TestLoop.Run(async () =>
    {
        var h = new Harness();
        for (var i = 0; i < 4; i++)
        {
            h.Report("https://ticker.test/", $"Price {i}");
            await h.WaitAsync(1);
        }
        h.Report("https://ticker.test/", "Price 4");
        await h.WaitAsync(0.1);
        Assert.Single(h.Visits);
    });

    [Fact]
    public void FlushAllAndPruneCommitPendingVisits() => TestLoop.Run(() =>
    {
        var h = new Harness();
        h.Report("https://a.test/", "A");
        h.Recorder.FlushAll();
        Assert.Single(h.Visits);
        h.Report("https://b.test/", "B");
        h.Recorder.Prune(new HashSet<Guid>());
        Assert.Equal(2, h.Visits.Count);
        Assert.Equal(0, h.Recorder.TrackedTabs);
    });
}
