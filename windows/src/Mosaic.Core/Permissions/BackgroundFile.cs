namespace Mosaic.Core.Permissions;

/// <summary>
/// Writes a small file off the calling thread, latest content wins: a burst of changes becomes
/// at most one write in flight and one queued. Writes are atomic (temporary file, then move),
/// so a crash leaves either the old file or the new one.
/// </summary>
internal sealed class BackgroundFile(string path)
{
    private readonly object gate = new();
    private string? pending;
    private Task writing = Task.CompletedTask;

    public string Path { get; } = path;

    /// <summary>Queues <paramref name="content"/> to be written.</summary>
    public void Write(string content)
    {
        lock (gate)
        {
            var idle = pending is null;
            pending = content;
            if (idle) writing = writing.ContinueWith(_ => Drain(), TaskScheduler.Default);
        }
    }

    /// <summary>Waits until everything queued so far is on disk (tests, shutdown).</summary>
    public Task FlushAsync()
    {
        lock (gate) return writing;
    }

    private void Drain()
    {
        string? content;
        lock (gate)
        {
            content = pending;
            pending = null;
        }
        if (content is null) return;
        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, content);
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Nothing to tell anyone: the decision still applies for this run, and the next
            // change writes the whole file again.
        }
    }
}
