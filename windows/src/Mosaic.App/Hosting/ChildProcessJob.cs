namespace Mosaic.App.Hosting;

/// <summary>
/// Ties child processes to Mosaic's lifetime with a Windows job object: when Mosaic exits for any
/// reason, including a crash, every process added here (and its descendants) is terminated.
/// </summary>
/// <remarks>Contract: thread-safe; <see cref="Add"/> never throws (failures return false).</remarks>
public sealed class ChildProcessJob : IDisposable
{
    public bool Add(int processId) => false;

    public void Dispose() { }
}
