using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Performance;

/// <summary>One named moment of start-up, in milliseconds since the process was created.</summary>
public readonly record struct StartupMark(string Name, double Milliseconds, int ThreadId);

/// <summary>
/// Cheap named marks on Mosaic's start-up path, timed from the moment Windows created the
/// process (so the runtime's own start-up is included), exposed as the <c>performance</c> key
/// of the automation <c>dump</c> and read by <c>windows/scripts/perf</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Mark"/> costs a timestamp and a short lock: no allocation beyond the entry, no
/// I/O, no clock conversion. Only the first mark of a name counts, so a call site that can run
/// more than once (an event handler) needs no guard of its own. The process creation time is
/// looked up only when the marks are read, which is never on the start-up path.
/// </para>
/// <para>
/// When the environment variable <see cref="TraceFileVariable"/> names a file, the marks are
/// also written there (as the same JSON) after each new mark, from the thread pool. The
/// performance harness uses this to read start-up times without the automation pipe.
/// </para>
/// <para>Call sites (the 4–6 that matter; see <c>windows/docs/performance.md</c>):</para>
/// <list type="table">
/// <item><term><see cref="Main"/></term><description>first line of <c>Program.Main</c> (winui; until then the perf module initializer marks it)</description></item>
/// <item><term><see cref="AppLaunched"/></term><description>first line of <c>App.OnLaunched</c> (winui)</description></item>
/// <item><term><see cref="WindowCreated"/></term><description>after <c>new MainWindow(…)</c> returns (winui)</description></item>
/// <item><term><see cref="WindowActivated"/></term><description>the window's first <c>Activated</c> event (perf startup hook, or winui)</description></item>
/// <item><term><see cref="FirstFrame"/></term><description>the first <c>CompositionTarget.Rendering</c> after the window exists (perf startup hook)</description></item>
/// <item><term><see cref="ModelReady"/></term><description>the browser model exists (perf startup hook: <see cref="StartupProbe"/>)</description></item>
/// <item><term><see cref="FirstNavigationStarted"/> / <see cref="FirstNavigationCompleted"/></term><description>the first tab starts / finishes loading (<see cref="StartupProbe"/>)</description></item>
/// <item><term><see cref="GeckoReady"/></term><description>the Gecko engine is running (<see cref="StartupProbe"/>)</description></item>
/// <item><term><see cref="ChromiumReady"/></term><description>the shared WebView2 environment exists (winui, <c>Chromium/**</c>)</description></item>
/// <item><term><see cref="AutomationReady"/></term><description>the automation pipe listens (winui, <c>Automation/**</c>)</description></item>
/// </list>
/// </remarks>
public static class StartupTrace
{
    public const string Main = "main";
    public const string AppLaunched = "appLaunched";
    public const string WindowCreated = "windowCreated";
    public const string WindowActivated = "windowActivated";
    public const string FirstFrame = "firstFrame";
    public const string ModelReady = "modelReady";
    public const string FirstNavigationStarted = "firstNavigationStarted";
    public const string FirstNavigationCompleted = "firstNavigationCompleted";
    public const string GeckoReady = "geckoReady";
    public const string ChromiumReady = "chromiumReady";
    public const string AutomationReady = "automationReady";

    /// <summary>Environment variable naming a file the marks are written to as they happen.</summary>
    public const string TraceFileVariable = "MOSAIC_STARTUP_TRACE";

    private readonly record struct Entry(string Name, long Timestamp, int ThreadId);

    private static readonly Lock gate = new();
    private static readonly List<Entry> entries = new(16);
    private static readonly Dictionary<string, string> notes = [];
    private static readonly Lock fileGate = new();
    private static long processStartTimestamp;   // Stopwatch ticks; 0 until first read
    private static DateTime processStartUtc;
    private static string? traceFile;
    private static bool traceFileRead;
    private static int writeVersion;
    private static int writtenVersion;

    /// <summary>Records <paramref name="name"/> now, unless it has been recorded already.</summary>
    public static void Mark(string name)
    {
        var now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            foreach (var entry in entries)
            {
                if (entry.Name == name) return;
            }
            entries.Add(new Entry(name, now, Environment.CurrentManagedThreadId));
        }
        PersistSoon();
    }

    /// <summary>Attaches a short fact to the trace (e.g. which engine loaded the first page). Last write wins.</summary>
    public static void Note(string key, string value)
    {
        lock (gate) notes[key] = value;
        PersistSoon();
    }

    public static bool Has(string name)
    {
        lock (gate) return entries.Exists(entry => entry.Name == name);
    }

    /// <summary>Milliseconds from process creation to <paramref name="name"/>, or null if it has not happened.</summary>
    public static double? Elapsed(string name)
    {
        Entry? found = null;
        lock (gate)
        {
            foreach (var entry in entries)
            {
                if (entry.Name == name) { found = entry; break; }
            }
        }
        return found is { } mark ? ToMilliseconds(mark.Timestamp) : null;
    }

    /// <summary>Every mark so far, in the order they happened.</summary>
    public static IReadOnlyList<StartupMark> Marks
    {
        get
        {
            Entry[] copy;
            lock (gate) copy = [.. entries];
            return [.. copy.OrderBy(entry => entry.Timestamp)
                .Select(entry => new StartupMark(entry.Name, ToMilliseconds(entry.Timestamp), entry.ThreadId))];
        }
    }

    /// <summary>Milliseconds since the process was created.</summary>
    public static double Uptime => ToMilliseconds(Stopwatch.GetTimestamp());

    /// <summary>
    /// The <c>performance</c> object of the automation <c>dump</c>:
    /// <c>{processStartUtc, uptimeMs, marks: {name: ms}, threads: {name: id}, notes: {…}, process: {…}, runtime: {…}}</c>.
    /// </summary>
    public static JsonObject ToJson(bool includeProcess = true)
    {
        var marks = new JsonObject();
        var threads = new JsonObject();
        foreach (var mark in Marks)
        {
            marks[mark.Name] = Math.Round(mark.Milliseconds, 1);
            threads[mark.Name] = mark.ThreadId;
        }
        var noteObject = new JsonObject();
        lock (gate)
        {
            foreach (var (key, value) in notes) noteObject[key] = value;
        }
        var result = new JsonObject
        {
            ["processStartUtc"] = ProcessStartUtc.ToString("O", CultureInfo.InvariantCulture),
            ["uptimeMs"] = Math.Round(Uptime, 1),
            ["marks"] = marks,
            ["threads"] = threads,
            ["notes"] = noteObject,
            ["runtime"] = RuntimeFacts(),
        };
        if (includeProcess) result["process"] = ProcessFacts();
        return result;
    }

    /// <summary>When Windows created this process (UTC).</summary>
    public static DateTime ProcessStartUtc
    {
        get
        {
            EnsureProcessStart();
            return processStartUtc;
        }
    }

    private static double ToMilliseconds(long timestamp)
    {
        EnsureProcessStart();
        return (timestamp - processStartTimestamp) * 1000.0 / Stopwatch.Frequency;
    }

    private static void EnsureProcessStart()
    {
        if (Volatile.Read(ref processStartTimestamp) != 0) return;
        DateTime start;
        try
        {
            using var process = Process.GetCurrentProcess();
            start = process.StartTime.ToUniversalTime();
        }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Unknown: time from the first mark instead (the runtime's own start is then missing).
            Entry? first;
            lock (gate) first = entries.Count > 0 ? entries[0] : null;
            var origin = first?.Timestamp ?? Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (processStartTimestamp == 0)
                {
                    processStartUtc = DateTime.UtcNow - Stopwatch.GetElapsedTime(origin);
                    Volatile.Write(ref processStartTimestamp, origin);
                }
            }
            return;
        }
        // Both clocks read back to back: the process start in Stopwatch ticks.
        var nowTicks = Stopwatch.GetTimestamp();
        var nowUtc = DateTime.UtcNow;
        var sinceStart = (nowUtc - start).TotalSeconds;
        lock (gate)
        {
            if (processStartTimestamp != 0) return;
            processStartUtc = start;
            Volatile.Write(ref processStartTimestamp, nowTicks - (long)(sinceStart * Stopwatch.Frequency));
        }
    }

    private static JsonObject RuntimeFacts() => new()
    {
        ["framework"] = RuntimeInformation.FrameworkDescription,
        ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        ["os"] = RuntimeInformation.OSDescription,
        ["serverGc"] = GCSettings.IsServerGC,
        ["gcLatencyMode"] = GCSettings.LatencyMode.ToString(),
        ["tieredCompilation"] = Switch("System.Runtime.TieredCompilation"),
        ["tieredPgo"] = Switch("System.Runtime.TieredPGO"),
        ["invariantGlobalization"] = Switch("System.Globalization.Invariant"),
        ["dynamicCode"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
        ["processorCount"] = Environment.ProcessorCount,
    };

    private static JsonNode? Switch(string name) => AppContext.GetData(name) switch
    {
        null => null,
        bool flag => flag,
        var other => other.ToString(),
    };

    private static JsonObject ProcessFacts()
    {
        var facts = new JsonObject
        {
            ["managedHeapBytes"] = GC.GetTotalMemory(false),
            ["gcCommittedBytes"] = GC.GetGCMemoryInfo().TotalCommittedBytes,
            ["gcCollections"] = new JsonArray(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)),
            ["jitCompiledMethods"] = System.Runtime.JitInfo.GetCompiledMethodCount(),
            ["jitTimeMs"] = Math.Round(System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds, 1),
            ["loadedAssemblies"] = AppDomain.CurrentDomain.GetAssemblies().Length,
        };
        try
        {
            using var process = Process.GetCurrentProcess();
            facts["workingSetBytes"] = process.WorkingSet64;
            facts["privateBytes"] = process.PrivateMemorySize64;
            facts["peakWorkingSetBytes"] = process.PeakWorkingSet64;
            facts["threads"] = process.Threads.Count;
            facts["handles"] = process.HandleCount;
            facts["cpuMs"] = Math.Round(process.TotalProcessorTime.TotalMilliseconds, 1);
        }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            facts["error"] = error.Message;
        }
        return facts;
    }

    // ---- the trace file ----------------------------------------------------------------------

    private static void PersistSoon()
    {
        if (TraceFile is null) return;
        Interlocked.Increment(ref writeVersion);
        ThreadPool.UnsafeQueueUserWorkItem(static _ => WriteTraceFile(), null);
    }

    private static string? TraceFile
    {
        get
        {
            if (Volatile.Read(ref traceFileRead)) return traceFile;
            var path = Environment.GetEnvironmentVariable(TraceFileVariable);
            traceFile = string.IsNullOrWhiteSpace(path) ? null : path;
            Volatile.Write(ref traceFileRead, true);
            return traceFile;
        }
    }

    private static void WriteTraceFile()
    {
        lock (fileGate)
        {
            var version = Volatile.Read(ref writeVersion);
            if (version == writtenVersion || traceFile is not { } path) return;
            writtenVersion = version;
            try
            {
                // Written aside and moved into place, so a reader never sees half a file. No
                // process facts: those are what the harness measures from outside.
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, ToJson(includeProcess: false).ToJsonString(), new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Diagnostics are optional; an unwritable path must not disturb the browser.
            }
        }
    }

    /// <summary>For tests: forgets every mark and note, and re-reads <see cref="TraceFileVariable"/>.</summary>
    internal static void Reset()
    {
        lock (fileGate)
        lock (gate)
        {
            entries.Clear();
            notes.Clear();
            Volatile.Write(ref traceFileRead, false);
            traceFile = null;
            writeVersion = 0;
            writtenVersion = 0;
        }
    }

    /// <summary>For tests: waits until the trace file holds every mark made so far.</summary>
    internal static void FlushTraceFile() => WriteTraceFile();
}
