using System.Diagnostics;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Tests.Gecko;

/// <summary>
/// The leftover-engine design: only a process Mosaic wrote down when it created it is ever
/// terminated, and only while it is provably still that process.
/// </summary>
public class EngineRecordTests
{
    /// <summary>A process that does nothing for a long time, standing in for an engine.</summary>
    internal static Process StartIdleProcess() => Process.Start(
        OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", ["-n", "600", "127.0.0.1"]) { CreateNoWindow = true, RedirectStandardOutput = true }
            : new ProcessStartInfo("sleep", ["600"]))!;

    private static ProcessIdentity Identity(Process process) => ProcessIdentity.Of(process)!.Value;

    /// <summary>Stands in for a Mosaic that is no longer running.</summary>
    private static readonly ProcessIdentity DeadOwner = new(int.MaxValue - 7, 1);

    [Fact]
    public void RoundTripsThroughTheProfile()
    {
        using var profile = new TemporaryDirectory();
        var record = new EngineRecord("token", new ProcessIdentity(1, 2), new ProcessIdentity(3, 4), new ProcessIdentity(5, 6));

        record.Save(profile.Path);

        Assert.Equal(record, EngineRecord.Load(profile.Path));
        Assert.Equal([new ProcessIdentity(3, 4), new ProcessIdentity(5, 6)], record.Processes);
        Assert.Equal([new ProcessIdentity(3, 4)], (record with { Browser = null }).Processes);
        Assert.Equal([new ProcessIdentity(3, 4)], (record with { Browser = new ProcessIdentity(3, 4) }).Processes);

        (record with { Browser = null }).Save(profile.Path);
        Assert.Null(EngineRecord.Load(profile.Path)!.Browser);
    }

    [Fact]
    public void AMissingOrDamagedRecordProvesNothing()
    {
        using var profile = new TemporaryDirectory();
        Assert.Null(EngineRecord.Load(profile.Path));
        Assert.Null(EngineRecord.Load(profile.File("no-such-profile")));

        foreach (var damaged in new[] { "", "{", "[]", "{\"token\":5}", "{\"token\":\"t\",\"owner\":{\"pid\":1}}" })
        {
            File.WriteAllText(profile.File(EngineRecord.FileName), damaged);
            Assert.Null(EngineRecord.Load(profile.Path));
        }
    }

    [Fact]
    public void DeletingLeavesALaterLaunchesRecordAlone()
    {
        using var profile = new TemporaryDirectory();
        new EngineRecord("newer", DeadOwner, new ProcessIdentity(3, 4), null).Save(profile.Path);

        EngineRecord.Delete(profile.Path, "older");
        Assert.NotNull(EngineRecord.Load(profile.Path));

        EngineRecord.Delete(profile.Path, "newer");
        Assert.False(File.Exists(profile.File(EngineRecord.FileName)));
    }

    [Fact]
    public void AnIdentityOpensOnlyTheVeryProcessItNames()
    {
        using var process = StartIdleProcess();
        try
        {
            var identity = Identity(process);
            // The start time is stable across separate looks at the same process.
            using (var again = Process.GetProcessById(process.Id)) Assert.Equal(identity, ProcessIdentity.Of(again));

            using (var opened = identity.Open()) Assert.Equal(process.Id, opened!.Id);
            // Same ID, different start time: what a reused process ID looks like.
            Assert.Null((identity with { StartedUtcTicks = identity.StartedUtcTicks - 1 }).Open());
            Assert.Null((identity with { StartedUtcTicks = 0 }).Open());

            process.Kill();
            process.WaitForExit();
            Assert.Null(identity.Open());
        }
        finally
        {
            EngineRecord.Kill(process);
        }
    }

    [Fact]
    public void TheCurrentProcessCanBeIdentified()
    {
        Assert.Equal(Environment.ProcessId, ProcessIdentity.Current.Id);
        using var self = ProcessIdentity.Current.Open();
        Assert.NotNull(self);
    }

    [Fact]
    public async Task NothingIsTerminatedWithoutARecord()
    {
        using var profile = new TemporaryDirectory();
        Assert.Equal(StaleEngine.None, await EngineRecord.TerminateStaleAsync(profile.Path, TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task ALeftoverEngineOfADeadMosaicIsTerminated()
    {
        using var profile = new TemporaryDirectory();
        using var launcher = StartIdleProcess();
        using var browser = StartIdleProcess();
        try
        {
            new EngineRecord("token", DeadOwner, Identity(launcher), Identity(browser)).Save(profile.Path);

            var outcome = await EngineRecord.TerminateStaleAsync(profile.Path, TimeSpan.FromSeconds(5), default);

            Assert.Equal(StaleEngine.Terminated, outcome);
            Assert.True(launcher.HasExited);
            Assert.True(browser.HasExited);
            Assert.False(File.Exists(profile.File(EngineRecord.FileName)));
        }
        finally
        {
            EngineRecord.Kill(launcher);
            EngineRecord.Kill(browser);
        }
    }

    [Fact]
    public async Task AnEngineThisProcessLaunchedEarlierCountsAsLeftover()
    {
        using var profile = new TemporaryDirectory();
        using var engine = StartIdleProcess();
        try
        {
            new EngineRecord("token", ProcessIdentity.Current, Identity(engine), null).Save(profile.Path);

            Assert.Equal(StaleEngine.Terminated,
                await EngineRecord.TerminateStaleAsync(profile.Path, TimeSpan.FromSeconds(5), default));
            Assert.True(engine.HasExited);
        }
        finally
        {
            EngineRecord.Kill(engine);
        }
    }

    [Fact]
    public async Task AnEngineWhoseMosaicIsStillRunningIsLeftAlone()
    {
        using var profile = new TemporaryDirectory();
        using var otherMosaic = StartIdleProcess();
        using var engine = StartIdleProcess();
        try
        {
            new EngineRecord("token", Identity(otherMosaic), Identity(engine), null).Save(profile.Path);

            var outcome = await EngineRecord.TerminateStaleAsync(profile.Path, TimeSpan.FromSeconds(5), default);

            Assert.Equal(StaleEngine.InUse, outcome);
            Assert.False(engine.HasExited);
            Assert.NotNull(EngineRecord.Load(profile.Path));
        }
        finally
        {
            EngineRecord.Kill(otherMosaic);
            EngineRecord.Kill(engine);
        }
    }

    [Fact]
    public async Task AProcessThatReusedARecordedIdIsLeftAlone()
    {
        using var profile = new TemporaryDirectory();
        using var bystander = StartIdleProcess();
        try
        {
            // The record names the bystander's ID, but from an earlier process's lifetime.
            var reused = Identity(bystander) with { StartedUtcTicks = Identity(bystander).StartedUtcTicks - TimeSpan.TicksPerHour };
            new EngineRecord("token", DeadOwner, reused, null).Save(profile.Path);

            var outcome = await EngineRecord.TerminateStaleAsync(profile.Path, TimeSpan.FromSeconds(5), default);

            Assert.Equal(StaleEngine.None, outcome);
            Assert.False(bystander.HasExited);
            // The record described nothing that is still running, so it is cleared away.
            Assert.False(File.Exists(profile.File(EngineRecord.FileName)));
        }
        finally
        {
            EngineRecord.Kill(bystander);
        }
    }

    [Fact]
    public async Task WaitingForExitIsBounded()
    {
        using var process = StartIdleProcess();
        try
        {
            Assert.False(await EngineRecord.WaitForExitAsync([process], TimeSpan.Zero));
            Assert.False(await EngineRecord.WaitForExitAsync([process], TimeSpan.FromMilliseconds(100)));
            EngineRecord.Kill(process);
            Assert.True(await EngineRecord.WaitForExitAsync([process], TimeSpan.FromSeconds(5)));
            Assert.True(await EngineRecord.WaitForExitAsync([process], TimeSpan.Zero));
        }
        finally
        {
            EngineRecord.Kill(process);
        }
    }
}
