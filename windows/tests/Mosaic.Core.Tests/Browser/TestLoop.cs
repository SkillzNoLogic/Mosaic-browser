using System.Collections.Concurrent;

namespace Mosaic.Core.Tests.Browser;

/// <summary>
/// A single-threaded message loop for tests, standing in for the UI thread: the browser model is
/// written for one thread with a <see cref="SynchronizationContext"/>, and its behaviour is only
/// deterministic when tested the same way.
/// </summary>
internal sealed class TestLoop : SynchronizationContext
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
    private readonly int thread = Environment.CurrentManagedThreadId;

    /// <summary>Runs <paramref name="test"/> on the calling thread, pumping continuations until it finishes.</summary>
    public static void Run(Func<Task> test)
    {
        var previous = Current;
        var loop = new TestLoop();
        SetSynchronizationContext(loop);
        try
        {
            var task = test();
            task.ContinueWith(
                _ => loop.queue.CompleteAdding(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            while (!loop.queue.IsCompleted)
            {
                if (!loop.queue.TryTake(out var work, StallTimeout))
                {
                    if (loop.queue.IsCompleted) break;
                    throw new TimeoutException("The test is waiting for something that never happens.");
                }
                work.Callback(work.State);
            }
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>Runs a test that never needs to wait.</summary>
    public static void Run(Action test) => Run(() =>
    {
        test();
        return Task.CompletedTask;
    });

    /// <summary>Lets everything already queued on the loop (and what that queues in turn) run.</summary>
    public static async Task Settle()
    {
        for (var round = 0; round < 8; round++) await Task.Yield();
    }

    public override void Post(SendOrPostCallback callback, object? state)
    {
        try
        {
            queue.Add((callback, state));
        }
        catch (InvalidOperationException)
        {
            // The test has finished; a straggler from the thread pool has nowhere to go.
        }
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (Environment.CurrentManagedThreadId == thread)
        {
            callback(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Post(_ => { callback(state); done.Set(); }, null);
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>A clock that only moves when a test says so.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, fires the timers that came due, and lets their effects settle.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        now += by;
        foreach (var timer in timers.Where(timer => timer.Due <= now).OrderBy(timer => timer.Due).ToList())
        {
            timer.Fire();
        }
        await TestLoop.Settle();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.timers.Remove(this);
            if (dueTime == Timeout.InfiniteTimeSpan) return true;
            Due = owner.now + dueTime;
            owner.timers.Add(this);
            return true;
        }

        public void Fire()
        {
            owner.timers.Remove(this);
            callback(state);
        }

        public void Dispose() => owner.timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
