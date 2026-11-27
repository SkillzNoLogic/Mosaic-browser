using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mosaic.Core.Tests.Gecko;

/// <summary>Polling assertions for things that happen on other threads or in other processes.</summary>
internal static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, double seconds = 10, string? because = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed.TotalSeconds < seconds, because ?? "condition was not met in time");
            await Task.Delay(20);
        }
    }
}

/// <summary>A folder under the system temporary directory, removed when disposed.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("mosaic-gecko-test-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A process that was just terminated can hold files for a moment longer.
        }
    }
}

/// <summary>
/// A synchronization context with one thread of its own, standing in for a UI thread: whatever
/// is posted to it runs on that thread, in order.
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        })
        { IsBackground = true, Name = "mosaic.test.ui" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));

    /// <summary>Runs <paramref name="function"/> on the context's thread and returns its result.</summary>
    public Task<T> RunAsync<T>(Func<T> function)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                done.SetResult(function());
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        }, null);
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>A fact that needs a POSIX shell to stand in for an executable.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Uses a shell script as a stand-in executable.";
    }
}
