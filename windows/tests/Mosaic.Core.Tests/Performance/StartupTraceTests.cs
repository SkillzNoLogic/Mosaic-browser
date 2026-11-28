using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Performance;
using Mosaic.Core.Tests.Browser;
using static Mosaic.Core.Tests.Browser.Snapshots;

namespace Mosaic.Core.Tests.Performance;

/// <summary>StartupTrace is process-wide state, so its tests never run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StartupTraceCollection
{
    public const string Name = "StartupTrace";
}

[Collection(StartupTraceCollection.Name)]
public sealed class StartupTraceTests : IDisposable
{
    public StartupTraceTests() => StartupTrace.Reset();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StartupTrace.TraceFileVariable, null);
        StartupTrace.Reset();
    }

    [Fact]
    public void OnlyTheFirstMarkOfANameCounts()
    {
        StartupTrace.Mark("one");
        var first = StartupTrace.Elapsed("one");
        Thread.Sleep(20);
        StartupTrace.Mark("one");

        Assert.Equal(first, StartupTrace.Elapsed("one"));
        Assert.Single(StartupTrace.Marks, mark => mark.Name == "one");
    }

    [Fact]
    public void MarksAreTimedFromProcessCreationAndInOrder()
    {
        StartupTrace.Mark("a");
        Thread.Sleep(15);
        StartupTrace.Mark("b");

        var marks = StartupTrace.Marks;
        Assert.Equal(["a", "b"], marks.Select(mark => mark.Name));
        // The test host has been running for a while: the marks are measured from its creation,
        // not from the first mark.
        Assert.True(marks[0].Milliseconds > 0);
        Assert.True(marks[1].Milliseconds - marks[0].Milliseconds >= 10);
        Assert.True(StartupTrace.Uptime >= marks[1].Milliseconds);
        var sinceStart = (DateTime.UtcNow - StartupTrace.ProcessStartUtc).TotalMilliseconds;
        Assert.InRange(StartupTrace.Uptime, sinceStart - 1000, sinceStart + 1000);
    }

    [Fact]
    public void UnknownMarkHasNoTime()
    {
        Assert.Null(StartupTrace.Elapsed("never"));
        Assert.False(StartupTrace.Has("never"));
    }

    [Fact]
    public void JsonCarriesMarksNotesRuntimeAndProcess()
    {
        StartupTrace.Mark(StartupTrace.Main);
        StartupTrace.Mark(StartupTrace.WindowCreated);
        StartupTrace.Note("firstNavigationEngine", "gecko");

        var json = StartupTrace.ToJson();

        var marks = Assert.IsType<JsonObject>(json["marks"]);
        Assert.Equal([StartupTrace.Main, StartupTrace.WindowCreated], marks.Select(pair => pair.Key));
        Assert.True(marks[StartupTrace.Main]!.GetValue<double>() > 0);
        Assert.Equal("gecko", json["notes"]!["firstNavigationEngine"]!.GetValue<string>());
        Assert.NotNull(json["runtime"]!["framework"]);
        Assert.True(json["process"]!["workingSetBytes"]!.GetValue<long>() > 0);
        Assert.True(json["uptimeMs"]!.GetValue<double>() >= marks[StartupTrace.WindowCreated]!.GetValue<double>());
        Assert.True(DateTime.TryParse(json["processStartUtc"]!.GetValue<string>(), out _));
    }

    [Fact]
    public void TraceFileFollowsTheMarks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mosaic-startup-{Guid.NewGuid():N}.json");
        try
        {
            Environment.SetEnvironmentVariable(StartupTrace.TraceFileVariable, path);
            StartupTrace.Reset();

            StartupTrace.Mark(StartupTrace.Main);
            StartupTrace.Mark(StartupTrace.FirstFrame);
            StartupTrace.FlushTraceFile();

            var json = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.NotNull(json["marks"]![StartupTrace.Main]);
            Assert.NotNull(json["marks"]![StartupTrace.FirstFrame]);
            Assert.Null(json["process"]);   // measured from outside by the harness
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithoutTheVariableNoFileIsWritten()
    {
        Environment.SetEnvironmentVariable(StartupTrace.TraceFileVariable, null);
        StartupTrace.Reset();
        StartupTrace.Mark("quiet");
        StartupTrace.FlushTraceFile();   // nothing to do, and nothing thrown
        Assert.True(StartupTrace.Has("quiet"));
    }

    [Fact]
    public void MarksFromManyThreadsAreAllKept()
    {
        Parallel.For(0, 64, index => StartupTrace.Mark($"m{index % 16}"));
        Assert.Equal(16, StartupTrace.Marks.Count);
    }
}

[Collection(StartupTraceCollection.Name)]
public sealed class StartupProbeTests : IDisposable
{
    public StartupProbeTests() => StartupTrace.Reset();

    public void Dispose() => StartupTrace.Reset();

    [Fact]
    public void GeckoPageMarksTheFirstNavigationAndGeckoReady() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        StartupProbe.Attach(fixture.Model);
        Assert.True(StartupTrace.Has(StartupTrace.ModelReady));
        Assert.False(StartupTrace.Has(StartupTrace.GeckoReady));
        Assert.False(StartupTrace.Has(StartupTrace.FirstNavigationStarted));

        var tab = fixture.OpenGeckoTab("https://example.com/");
        Assert.True(StartupTrace.Has(StartupTrace.GeckoReady));
        Assert.True(tab.IsLoading);
        Assert.True(StartupTrace.Has(StartupTrace.FirstNavigationStarted));
        Assert.False(StartupTrace.Has(StartupTrace.FirstNavigationCompleted));

        fixture.Gecko.Report(Tab(1, "https://example.com/", "Example", loading: false, selected: true));
        Assert.False(tab.IsLoading);
        Assert.True(StartupTrace.Has(StartupTrace.FirstNavigationCompleted));
        Assert.True(StartupTrace.Elapsed(StartupTrace.FirstNavigationCompleted) >= StartupTrace.Elapsed(StartupTrace.FirstNavigationStarted));
        Assert.Equal("gecko", StartupTrace.ToJson(includeProcess: false)["notes"]!["firstNavigationEngine"]!.GetValue<string>());
        return Task.CompletedTask;
    });

    [Fact]
    public void ChromiumPageMarksTheFirstNavigationOnceOnly() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        StartupProbe.Attach(fixture.Model);

        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");
        Assert.True(tab.IsLoading);
        browser.RaiseLoadState(loading: false);
        var completed = StartupTrace.Elapsed(StartupTrace.FirstNavigationCompleted);
        Assert.NotNull(completed);

        // Later loads change nothing: the probe has let go of the tabs.
        var (_, second) = fixture.OpenChromiumTab("https://example.org/");
        second.RaiseLoadState(loading: false);
        Assert.Equal(completed, StartupTrace.Elapsed(StartupTrace.FirstNavigationCompleted));
        Assert.Equal("chromium", StartupTrace.ToJson(includeProcess: false)["notes"]!["firstNavigationEngine"]!.GetValue<string>());
        return Task.CompletedTask;
    });

    [Fact]
    public void ABackgroundTabFinishingFirstDoesNotCountForTheFirstNavigation() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        StartupProbe.Attach(fixture.Model);

        var (_, first) = fixture.OpenChromiumTab("https://example.com/");
        var (_, other) = fixture.OpenChromiumTab("https://example.org/");
        other.RaiseLoadState(loading: false);
        Assert.False(StartupTrace.Has(StartupTrace.FirstNavigationCompleted));

        first.RaiseLoadState(loading: false);
        Assert.True(StartupTrace.Has(StartupTrace.FirstNavigationCompleted));
        return Task.CompletedTask;
    });
}
