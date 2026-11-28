using Mosaic.Core.WindowsIntegration;

namespace Mosaic.Core.Tests.WindowsIntegration;

[Collection(SystemStaticState.Name)]
public class UserNotificationsTests : IDisposable
{
    public UserNotificationsTests() => UserNotifications.Reset();

    public void Dispose() => UserNotifications.Reset();

    private sealed class FakePresenter : INotificationPresenter
    {
        public List<NotificationRequest> Shown { get; } = [];
        public string Status => "ready";
        public NotificationResult Show(NotificationRequest request)
        {
            Shown.Add(request);
            return new NotificationResult(true, "shown", (uint)Shown.Count);
        }
        public void Remove(string area, string tag) { }
    }

    [Fact]
    public void WithoutAPresenterPostsAreRecorded()
    {
        var result = UserNotifications.Post(new NotificationRequest("downloads", "Done", "a.pdf"));
        Assert.False(result.Shown);
        Assert.Equal("recorded", result.Status);
        Assert.Equal("Done", UserNotifications.State()["recent"]![0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void ThePresenterIsCreatedLazilyOnce()
    {
        var created = 0;
        var presenter = new FakePresenter();
        UserNotifications.PresenterFactory = () => { created++; return presenter; };
        Assert.Equal(0, created);
        Assert.Equal("not started", UserNotifications.Status);
        UserNotifications.Post(new NotificationRequest("downloads", "One", ""));
        UserNotifications.Post(new NotificationRequest("downloads", "Two", ""));
        Assert.Equal(1, created);
        Assert.Equal(2, presenter.Shown.Count);
        Assert.Equal("ready", UserNotifications.Status);
    }

    [Fact]
    public void PresenterFailuresBecomeResults()
    {
        UserNotifications.PresenterFactory = () => throw new InvalidOperationException("no COM");
        Assert.Equal("recorded", UserNotifications.Post(new NotificationRequest("a", "b", "c")).Status);
    }

    [Fact]
    public void RecentIsBounded()
    {
        for (var i = 0; i < 50; i++) UserNotifications.Post(new NotificationRequest("a", $"n{i}", ""));
        Assert.Equal(20, UserNotifications.State()["recent"]!.AsArray().Count);
    }

    [Fact]
    public void ActivationsBeforeSubscriptionAreDeliveredToTheFirstSubscriber()
    {
        UserNotifications.Deliver(new NotificationActivation(
            new Dictionary<string, string> { ["area"] = "downloads", ["download"] = "7" }, new Dictionary<string, string>()));
        NotificationActivation? got = null;
        UserNotifications.Activated += a => got = a;
        Assert.Equal("downloads", got?.Area);
        Assert.Equal("7", got!.Arguments["download"]);
        Assert.Equal(0, UserNotifications.State()["pendingActivations"]!.GetValue<int>());
    }
}

[Collection(SystemStaticState.Name)]
public class TaskbarProgressTests : IDisposable
{
    public TaskbarProgressTests() => TaskbarProgress.Reset();

    public void Dispose() => TaskbarProgress.Reset();

    [Fact]
    public void NothingMeansNone() => Assert.Equal((TaskbarProgressState.None, 0d), TaskbarProgress.Current);

    [Fact]
    public void SourcesAreCombined()
    {
        TaskbarProgress.Set("downloads", TaskbarProgressState.Normal, 0.2);
        TaskbarProgress.Set("gecko", TaskbarProgressState.Normal, 0.6);
        var (state, value) = TaskbarProgress.Current;
        Assert.Equal(TaskbarProgressState.Normal, state);
        Assert.Equal(0.4, value, 6);
    }

    [Fact]
    public void ErrorWinsThenPausedOnlyWhenAllPaused()
    {
        TaskbarProgress.Set("a", TaskbarProgressState.Paused, 0.5);
        Assert.Equal(TaskbarProgressState.Paused, TaskbarProgress.Current.State);
        TaskbarProgress.Set("b", TaskbarProgressState.Normal, 0.5);
        Assert.Equal(TaskbarProgressState.Normal, TaskbarProgress.Current.State);
        TaskbarProgress.Set("c", TaskbarProgressState.Error, 0.1);
        Assert.Equal(TaskbarProgressState.Error, TaskbarProgress.Current.State);
    }

    [Fact]
    public void IndeterminateOnlyWithoutDeterminateSources()
    {
        TaskbarProgress.Set("a", TaskbarProgressState.Indeterminate);
        Assert.Equal(TaskbarProgressState.Indeterminate, TaskbarProgress.Current.State);
        TaskbarProgress.Set("b", TaskbarProgressState.Normal, 0.3);
        Assert.Equal(TaskbarProgressState.Normal, TaskbarProgress.Current.State);
    }

    [Fact]
    public void ChangedFiresOnlyOnChangeAndValuesAreClamped()
    {
        var count = 0;
        Action handler = () => count++;
        TaskbarProgress.Changed += handler;
        try
        {
            TaskbarProgress.Set("a", TaskbarProgressState.Normal, 2);
            TaskbarProgress.Set("a", TaskbarProgressState.Normal, 1);
            TaskbarProgress.Set("a", TaskbarProgressState.None);
            TaskbarProgress.Clear("a");
        }
        finally
        {
            TaskbarProgress.Changed -= handler;
        }
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("normal", TaskbarProgressState.Normal)]
    [InlineData(" Paused ", TaskbarProgressState.Paused)]
    [InlineData("off", TaskbarProgressState.None)]
    public void ParsesStates(string text, TaskbarProgressState state) => Assert.Equal(state, TaskbarProgress.ParseState(text));
}

[Collection(SystemStaticState.Name)]
public class SystemAppearanceMonitorTests : IDisposable
{
    public SystemAppearanceMonitorTests() => SystemAppearanceMonitor.Reset();

    public void Dispose() => SystemAppearanceMonitor.Reset();

    [Fact]
    public void StartsOnFirstUseAndRaisesOnlyOnChange()
    {
        var starts = 0;
        SystemAppearanceMonitor.Starter = () => starts++;
        var seen = new List<SystemAppearance>();
        SystemAppearanceMonitor.Changed += seen.Add;
        Assert.Equal(1, starts);
        _ = SystemAppearanceMonitor.Current;
        Assert.Equal(1, starts);

        var dark = SystemAppearance.Default with { Dark = true };
        SystemAppearanceMonitor.Update(dark);
        SystemAppearanceMonitor.Update(dark with { Accent = new Dictionary<string, string>(dark.Accent) });
        SystemAppearanceMonitor.Update(dark with { HighContrast = true, HighContrastScheme = "Night sky" });
        Assert.Equal(2, seen.Count);
        Assert.True(SystemAppearanceMonitor.Current.HighContrast);
        Assert.Equal("Night sky", SystemAppearanceMonitor.Current.ToJson()["highContrastScheme"]!.GetValue<string>());
    }

    [Fact]
    public void HexFormatting() => Assert.Equal("#0A7BFF", SystemAppearance.Hex(10, 123, 255));
}
