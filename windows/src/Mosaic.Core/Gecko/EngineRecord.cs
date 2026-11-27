using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Gecko;

/// <summary>
/// Names one particular process. A process ID alone is reused by the operating system once its
/// process is gone; together with the start time it identifies a single process for good.
/// </summary>
internal readonly record struct ProcessIdentity(int Id, long StartedUtcTicks)
{
    public static ProcessIdentity Current { get; } = Of(Process.GetCurrentProcess())
        ?? new ProcessIdentity(Environment.ProcessId, 0);

    /// <summary>The identity of a running process, or null if it has exited or may not be inspected.</summary>
    public static ProcessIdentity? Of(Process process)
    {
        try
        {
            return new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the process if this exact one is still running. Null when it has exited, when its ID
    /// now belongs to a different process, and when that cannot be established (in which case it
    /// must be left alone).
    /// </summary>
    public Process? Open()
    {
        Process process;
        try
        {
            process = Process.GetProcessById(Id);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null; // nothing has this ID any more
        }

        if (Of(process) == this) return process;
        process.Dispose();
        return null;
    }
}

internal enum StaleEngine
{
    /// <summary>No engine from an earlier launch is running on the profile.</summary>
    None,

    /// <summary>One was running and has been terminated.</summary>
    Terminated,

    /// <summary>One is running, but the Mosaic that launched it is too: it is in use, not stale.</summary>
    InUse,

    /// <summary>One is running and could not be terminated.</summary>
    Survived,
}

/// <summary>
/// What Mosaic remembers, on disk, about the engine it launched on a profile, so that a later
/// Mosaic can recognise that engine with certainty if this one dies without stopping it.
/// </summary>
/// <remarks>
/// <para>
/// An engine that outlives its Mosaic holds the profile lock. A second Gecko started on a locked
/// profile does not fail in a way its launcher can tell apart from success: depending on platform
/// and version it either hands its command line to the first one, or waits a few seconds and
/// gives up with "a copy of Firefox is already open" (a dialog, unless headless), and both ways
/// it exits with code 0. So the leftover has to be terminated first, and Mosaic must be certain
/// that what it terminates is its own engine and never, say, the user's Firefox.
/// </para>
/// <para>
/// The macOS app finds leftovers by searching every process's command line for its profile path.
/// Windows offers no portable way to read another process's command line (that is WMI or
/// undocumented process internals), so this takes the opposite approach and never searches: the
/// only processes ever terminated are the ones written down here, at the moment Mosaic itself
/// created them, each as a <see cref="ProcessIdentity"/> so a reused process ID cannot match.
/// The record lives inside the profile it is about, which keeps the "on Mosaic's profile" half of
/// the guarantee structural. Without a record nothing is terminated; the engine then reports that
/// the profile is in use instead (see <see cref="GeckoProfile.IsLocked"/>).
/// </para>
/// </remarks>
/// <param name="Token">The launch token of the engine; see <see cref="GeckoEngine"/>.</param>
/// <param name="Owner">The Mosaic process that launched the engine.</param>
/// <param name="Launched">The process Mosaic created (Firefox's launcher process, where there is one).</param>
/// <param name="Browser">Gecko's browser process as reported by the agent, once known.</param>
internal sealed record EngineRecord(string Token, ProcessIdentity Owner, ProcessIdentity Launched, ProcessIdentity? Browser)
{
    public const string FileName = "mosaic-engine.json";

    private static string PathIn(string profileDirectory) => Path.Combine(profileDirectory, FileName);

    /// <summary>
    /// The engine's own processes. Deliberately not their descendants: content processes exit on
    /// their own when the browser process goes, while a program the user opened from a download
    /// is also a descendant and is none of Mosaic's business.
    /// </summary>
    public IEnumerable<ProcessIdentity> Processes =>
        Browser is { } browser && browser != Launched ? [Launched, browser] : [Launched];

    public static EngineRecord? Load(string profileDirectory)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(PathIn(profileDirectory))) is not JsonObject root ||
                root["token"] is not JsonValue text || !text.TryGetValue(out string? token) ||
                Identity(root["owner"]) is not { } owner ||
                Identity(root["launched"]) is not { } launched)
            {
                return null;
            }
            return new EngineRecord(token, owner, launched, Identity(root["browser"]));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unreadable. Nothing can be proven from it, so nothing will be terminated.
            return null;
        }

        static ProcessIdentity? Identity(JsonNode? node) =>
            node is JsonObject value &&
            value["pid"] is JsonValue pid && pid.TryGetValue(out int id) &&
            value["started"] is JsonValue started && started.TryGetValue(out long ticks)
                ? new ProcessIdentity(id, ticks)
                : null;
    }

    /// <summary>Writes the record so that a crash mid-write cannot leave half of one behind.</summary>
    public void Save(string profileDirectory)
    {
        static JsonNode Identity(ProcessIdentity identity) =>
            new JsonObject { ["pid"] = identity.Id, ["started"] = identity.StartedUtcTicks };

        var root = new JsonObject
        {
            ["token"] = Token,
            ["owner"] = Identity(Owner),
            ["launched"] = Identity(Launched),
        };
        if (Browser is { } browser) root["browser"] = Identity(browser);

        var path = PathIn(profileDirectory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Removes the record, unless a later launch has replaced it with its own.</summary>
    public static void Delete(string profileDirectory, string token)
    {
        if (Load(profileDirectory) is { } record && record.Token != token) return;
        try
        {
            File.Delete(PathIn(profileDirectory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left behind, it names processes that no longer exist, which Open() sees through.
        }
    }

    /// <summary>
    /// Terminates the engine an earlier Mosaic left running on this profile, if there is one.
    /// </summary>
    public static async Task<StaleEngine> TerminateStaleAsync(
        string profileDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        if (Load(profileDirectory) is not { } record) return StaleEngine.None;

        var running = record.Processes.Select(identity => identity.Open()).OfType<Process>().ToList();
        try
        {
            if (running.Count == 0)
            {
                Delete(profileDirectory, record.Token);
                return StaleEngine.None;
            }

            // A second Mosaic sharing the data folder: its engine is not ours to take. (An engine
            // launched earlier by this very process is: nothing else here can still want it.)
            if (record.Owner != ProcessIdentity.Current && record.Owner.Open() is { } owner)
            {
                owner.Dispose();
                return StaleEngine.InUse;
            }

            foreach (var process in running) Kill(process);
            if (!await WaitForExitAsync(running, timeout, cancellation).ConfigureAwait(false)) return StaleEngine.Survived;

            Delete(profileDirectory, record.Token);
            return StaleEngine.Terminated;
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }
    }

    /// <summary>Ends a process at once. Used only on processes Mosaic has positively identified.</summary>
    public static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or going.
        }
    }

    /// <summary>Waits for every process to exit; false if the time ran out first.</summary>
    public static async Task<bool> WaitForExitAsync(
        IReadOnlyCollection<Process> processes, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (timeout <= TimeSpan.Zero) return processes.All(HasExited);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(limit.Token))).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    public static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return true; // no longer something that can be asked, which only happens once it is gone
        }
    }
}
