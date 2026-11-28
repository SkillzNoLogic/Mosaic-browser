using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mosaic.Core.Tabs;

/// <summary>How the previous run of Mosaic ended, as far as the session folder can tell.</summary>
public enum PreviousRun
{
    /// <summary>No earlier session and no marker: a new profile.</summary>
    FirstRun,
    Clean,
    /// <summary>The previous run did not quit normally (crash, killed, power loss).</summary>
    Crashed,
}

/// <summary>
/// Session files in the profile folder, and the marker that tells a crash from a quit.
/// Port of <c>Sources/MosaicCore/Tabs/SessionStore.swift</c>:
/// <code>
/// &lt;directory&gt;\session.json           the current session, rewritten as it changes
/// &lt;directory&gt;\previous-session.json  the session a launch did not restore
/// &lt;directory&gt;\running                present while Mosaic runs; holds its process ID
/// </code>
/// A launch that finds <c>running</c> left behind by a process that no longer exists knows the
/// previous run did not end cleanly.
/// </summary>
/// <remarks>Thread-safe for one writer at a time; the model serialises its writes.</remarks>
public sealed class SessionStore(string directory)
{
    /// <summary>Where Mosaic keeps its session: <c>%LOCALAPPDATA%\Mosaic\Session</c>.</summary>
    public static string DefaultDirectory => Path.Combine(AppPaths.DataRoot, "Session");

    public string Directory { get; } = directory;
    public string SessionPath => Path.Combine(Directory, "session.json");
    public string PreviousSessionPath => Path.Combine(Directory, "previous-session.json");
    internal string MarkerPath => Path.Combine(Directory, "running");

    // ---- Run marker ----

    /// <summary>
    /// Records that this process is running and reports how the previous run ended.
    /// <paramref name="isAlive"/> is replaceable for tests.
    /// </summary>
    public PreviousRun BeginRun(int? processId = null, Func<int, bool>? isAlive = null)
    {
        var pid = processId ?? Environment.ProcessId;
        isAlive ??= ProcessIsAlive;
        System.IO.Directory.CreateDirectory(Directory);
        var previous = File.Exists(SessionPath) ? PreviousRun.Clean : PreviousRun.FirstRun;
        if (ReadText(MarkerPath) is { } text)
        {
            var first = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            // A marker held by another live process means a second copy is running on this
            // profile, not a crash.
            previous = int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var owner)
                && owner != pid && isAlive(owner)
                ? PreviousRun.Clean
                : PreviousRun.Crashed;
        }
        WriteAtomically(MarkerPath, $"{pid}\n");
        return previous;
    }

    /// <summary>Records a clean exit. Call after the final save.</summary>
    public void EndRun()
    {
        try { File.Delete(MarkerPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- Sessions ----

    /// <summary>The saved session, or null if there is none.</summary>
    /// <exception cref="SessionDecodeException">A file that cannot be used (see <see cref="SetAsideUnreadable"/>).</exception>
    public SessionSnapshot? Load() => Read(SessionPath);

    public SessionSnapshot? LoadPrevious() => Read(PreviousSessionPath);

    public void Save(SessionSnapshot snapshot) => WriteAtomically(SessionPath, SessionCodec.Encode(snapshot));

    /// <summary>Writes an already encoded session (the model encodes off the UI thread).</summary>
    public void SaveEncoded(string json) => WriteAtomically(SessionPath, json);

    /// <summary>
    /// Keeps <paramref name="snapshot"/> as the "previous session" a launch chose not to restore,
    /// so it can still be reopened.
    /// </summary>
    public void SavePrevious(SessionSnapshot snapshot) => WriteAtomically(PreviousSessionPath, SessionCodec.Encode(snapshot));

    public void RemovePrevious()
    {
        try { File.Delete(PreviousSessionPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Moves an unusable session file aside (e.g. one from a newer Mosaic) instead of
    /// overwriting it. Returns where it went.
    /// </summary>
    public string? SetAsideUnreadable()
    {
        if (!File.Exists(SessionPath)) return null;
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var destination = Path.Combine(Directory, $"session-unreadable-{stamp}.json");
        try
        {
            File.Move(SessionPath, destination, overwrite: true);
            return destination;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static SessionSnapshot? Read(string path) => ReadText(path) is { } text ? SessionCodec.Decode(text) : null;

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes to a temporary file, flushes it to disk, then replaces the target.</summary>
    private void WriteAtomically(string path, string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static bool ProcessIsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            using var current = Process.GetCurrentProcess();
            // Windows reuses process IDs; only another Mosaic counts.
            return !process.HasExited && process.ProcessName == current.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
