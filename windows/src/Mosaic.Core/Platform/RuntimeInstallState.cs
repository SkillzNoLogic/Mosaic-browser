using System.Globalization;

namespace Mosaic.Core.Platform;

/// <summary>
/// Why installing or updating the Gecko runtime failed, in words a person can act on. Windows
/// counterpart of <c>RuntimeInstallFailure</c> in <c>Sources/MosaicCore/Platform/RuntimeInstallState.swift</c>.
/// </summary>
public abstract record RuntimeInstallFailure
{
    public abstract string Message { get; }

    public sealed record Offline : RuntimeInstallFailure
    {
        public override string Message => "You appear to be offline. Connect to the internet and try again.";
    }

    /// <summary>Another transport error (DNS, TLS, timeout…).</summary>
    public sealed record Network(string Detail) : RuntimeInstallFailure
    {
        public override string Message => $"The download from Mozilla did not complete ({Detail}). Check your connection and try again.";
    }

    public sealed record Server(int Status) : RuntimeInstallFailure
    {
        public override string Message => $"Mozilla’s download server returned an error (HTTP {Status}). Try again in a few minutes.";
    }

    /// <summary>A redirect tried to leave HTTPS or Mozilla's servers.</summary>
    public sealed record UntrustedSource(string Host) : RuntimeInstallFailure
    {
        public override string Message => $"The download was redirected away from Mozilla’s secure servers (to {Host}), so Mosaic stopped it.";
    }

    public sealed record NotEnoughSpace(int NeededMB) : RuntimeInstallFailure
    {
        public override string Message => $"There is not enough free disk space. The Mozilla engine needs about {NeededMB} MB; free some space and try again.";
    }

    /// <summary>The download is not a program Windows can run (truncated, or an error page).</summary>
    public sealed record DamagedDownload : RuntimeInstallFailure
    {
        public override string Message => "The downloaded file is damaged or is not Firefox’s installer. Try again; if it keeps happening, your network may be altering downloads.";
    }

    /// <summary>Firefox's installer could not unpack its files.</summary>
    public sealed record ExtractionFailed(string Detail) : RuntimeInstallFailure
    {
        public override string Message => $"Firefox’s installer could not unpack its files ({Detail}). Nothing was installed.";
    }

    public sealed record NoFirefoxInDownload : RuntimeInstallFailure
    {
        public override string Message => "The download does not contain firefox.exe. Nothing was installed.";
    }

    /// <summary>Unsigned, altered after signing, or damaged.</summary>
    public sealed record SignatureInvalid(string Detail) : RuntimeInstallFailure
    {
        public override string Message => "The downloaded Firefox failed its signature check, so it was not installed. It may be damaged or tampered with."
            + (Detail.Length == 0 ? "" : $" ({Detail})");
    }

    /// <summary>Intact signature, but not Mozilla's.</summary>
    public sealed record WrongSigner(string? Found) : RuntimeInstallFailure
    {
        public override string Message => "The downloaded program is not signed by Mozilla, so it was not installed."
            + (Found is null ? " It has no publisher signature." : $" It is signed by: {Found}.");
    }

    /// <summary>Built for another processor than this PC's.</summary>
    public sealed record WrongArchitecture(string Expected) : RuntimeInstallFailure
    {
        public override string Message => $"The downloaded Firefox is not built for this PC’s processor ({Expected}), so it was not installed.";
    }

    /// <summary>Signed by Mozilla, but not a Gecko browser Mosaic can drive.</summary>
    public sealed record NotGeckoBrowser : RuntimeInstallFailure
    {
        public override string Message => "The downloaded program is not a Firefox build Mosaic can use. Nothing was installed.";
    }

    /// <summary>The running Mozilla engine did not quit, so its files could not be replaced.</summary>
    public sealed record EngineBusy : RuntimeInstallFailure
    {
        public override string Message => "The Mozilla engine did not quit, so it could not be replaced. Close Mozilla tabs and try again.";
    }

    public sealed record CopyFailed(string Detail) : RuntimeInstallFailure
    {
        public override string Message => $"The engine could not be put into place ({Detail}). Your previous engine, if any, was kept.";
    }

    public sealed record RemoveFailed(string Detail) : RuntimeInstallFailure
    {
        public override string Message => $"The engine could not be removed ({Detail}).";
    }

    /// <summary>Installing needs Windows (the signature check is Windows' own).</summary>
    public sealed record Unsupported : RuntimeInstallFailure
    {
        public override string Message => "Installing the Mozilla engine needs Windows, which checks Mozilla’s signature. Nothing was installed.";
    }

    /// <summary>Classifies a transport error.</summary>
    public static RuntimeInstallFailure FromHttpError(HttpRequestException error)
    {
        if (error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
            && !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            return new Offline();
        }
        return new Network(error.Message);
    }
}

/// <summary>Thrown inside the installer to end an attempt with a <see cref="RuntimeInstallFailure"/>.</summary>
public sealed class RuntimeInstallException(RuntimeInstallFailure failure) : Exception(failure.Message)
{
    public RuntimeInstallFailure Failure { get; } = failure;
}

/// <summary>Where an install or update of the Gecko runtime is. Driven by <see cref="RuntimeInstallMachine"/>.</summary>
public abstract record RuntimeInstallPhase
{
    public static readonly RuntimeInstallPhase Idle = new IdlePhase();
    public static readonly RuntimeInstallPhase Verifying = new VerifyingPhase();
    public static readonly RuntimeInstallPhase StoppingEngine = new StoppingEnginePhase();
    public static readonly RuntimeInstallPhase Installing = new InstallingPhase();
    public static readonly RuntimeInstallPhase Cancelled = new CancelledPhase();

    public sealed record IdlePhase : RuntimeInstallPhase;

    /// <summary><paramref name="Expected"/> is null until the server reports a length.</summary>
    public sealed record Downloading(long Received, long? Expected) : RuntimeInstallPhase;

    /// <summary>Checking the installer's signature, unpacking it, and checking firefox.exe's.</summary>
    public sealed record VerifyingPhase : RuntimeInstallPhase;

    /// <summary>Quitting the running Mozilla engine so its files can be replaced (updates only).</summary>
    public sealed record StoppingEnginePhase : RuntimeInstallPhase;

    /// <summary>Moving the verified files into Mosaic's private folder.</summary>
    public sealed record InstallingPhase : RuntimeInstallPhase;

    public sealed record Finished(string Version) : RuntimeInstallPhase;

    public sealed record Failed(RuntimeInstallFailure Failure) : RuntimeInstallPhase;

    public sealed record CancelledPhase : RuntimeInstallPhase;

    /// <summary>True while work is under way.</summary>
    public bool IsActive => this is Downloading or VerifyingPhase or StoppingEnginePhase or InstallingPhase;

    /// <summary>
    /// Cancelling is allowed until files start being moved into place, so a cancelled install never
    /// leaves a half-installed engine behind.
    /// </summary>
    public bool IsCancellable => this is Downloading or VerifyingPhase;

    /// <summary>0…1 while the download length is known; null otherwise.</summary>
    public double? Fraction => this is Downloading { Expected: > 0 } downloading
        ? Math.Clamp((double)downloading.Received / downloading.Expected.Value, 0, 1)
        : null;

    /// <summary>Short machine-readable name, for automation.</summary>
    public string Name => this switch
    {
        IdlePhase => "idle",
        Downloading => "downloading",
        VerifyingPhase => "verifying",
        StoppingEnginePhase => "stoppingEngine",
        InstallingPhase => "installing",
        Finished => "finished",
        Failed => "failed",
        _ => "cancelled",
    };

    /// <summary>One line for the UI.</summary>
    public string Summary => this switch
    {
        IdlePhase => "",
        Downloading { Expected: > 0 } d => $"Downloading Firefox ESR… {Megabytes(d.Received)} of {Megabytes(d.Expected!.Value)}",
        Downloading d => $"Downloading Firefox ESR… {Megabytes(d.Received)}",
        VerifyingPhase => "Checking Mozilla’s signature…",
        StoppingEnginePhase => "Closing the Mozilla engine…",
        InstallingPhase => "Installing…",
        Finished f => $"Firefox ESR {f.Version} is installed.",
        Failed f => f.Failure.Message,
        _ => "Cancelled. Nothing was changed.",
    };

    internal static string Megabytes(long bytes) =>
        $"{Math.Round(bytes / 1_000_000.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} MB";
}

public abstract record RuntimeInstallEvent
{
    public static readonly RuntimeInstallEvent Start = new StartEvent();
    public static readonly RuntimeInstallEvent Downloaded = new DownloadedEvent();
    public static readonly RuntimeInstallEvent EngineStopped = new EngineStoppedEvent();
    public static readonly RuntimeInstallEvent Cancel = new CancelEvent();
    public static readonly RuntimeInstallEvent Reset = new ResetEvent();

    public sealed record StartEvent : RuntimeInstallEvent;
    public sealed record Progress(long Received, long? Expected) : RuntimeInstallEvent;
    public sealed record DownloadedEvent : RuntimeInstallEvent;
    /// <summary>Signatures accepted. <paramref name="EngineRunning"/>: a running engine must quit first.</summary>
    public sealed record Verified(bool EngineRunning) : RuntimeInstallEvent;
    public sealed record EngineStoppedEvent : RuntimeInstallEvent;
    public sealed record Installed(string Version) : RuntimeInstallEvent;
    public sealed record Fail(RuntimeInstallFailure Failure) : RuntimeInstallEvent;
    public sealed record CancelEvent : RuntimeInstallEvent;
    public sealed record ResetEvent : RuntimeInstallEvent;
}

/// <summary>
/// The installer's state machine. Events that make no sense in the current phase are ignored (the
/// phase is returned unchanged), so late callbacks from a cancelled download cannot revive it.
/// </summary>
public static class RuntimeInstallMachine
{
    public static RuntimeInstallPhase Next(RuntimeInstallPhase phase, RuntimeInstallEvent change) => (phase, change) switch
    {
        (_, RuntimeInstallEvent.StartEvent) when !phase.IsActive => new RuntimeInstallPhase.Downloading(0, null),
        (RuntimeInstallPhase.Downloading, RuntimeInstallEvent.Progress p) => new RuntimeInstallPhase.Downloading(p.Received, p.Expected),
        (RuntimeInstallPhase.Downloading, RuntimeInstallEvent.DownloadedEvent) => RuntimeInstallPhase.Verifying,
        (RuntimeInstallPhase.VerifyingPhase, RuntimeInstallEvent.Verified v) =>
            v.EngineRunning ? RuntimeInstallPhase.StoppingEngine : RuntimeInstallPhase.Installing,
        (RuntimeInstallPhase.StoppingEnginePhase, RuntimeInstallEvent.EngineStoppedEvent) => RuntimeInstallPhase.Installing,
        (RuntimeInstallPhase.InstallingPhase, RuntimeInstallEvent.Installed i) => new RuntimeInstallPhase.Finished(i.Version),
        (_, RuntimeInstallEvent.Fail f) when phase.IsActive => new RuntimeInstallPhase.Failed(f.Failure),
        (_, RuntimeInstallEvent.CancelEvent) when phase.IsCancellable => RuntimeInstallPhase.Cancelled,
        (_, RuntimeInstallEvent.ResetEvent) when !phase.IsActive => RuntimeInstallPhase.Idle,
        _ => phase,
    };
}

/// <summary>
/// Free space the install needs: Firefox ESR's installer (~70 MB) plus the unpacked engine
/// (~250 MB), with room to spare for keeping the previous engine until the new one is in place.
/// </summary>
public static class RuntimeInstallRequirements
{
    public const long NeededBytes = 800_000_000;

    public static bool HasEnoughSpace(long? available) => available is not { } bytes || bytes >= NeededBytes;
}
