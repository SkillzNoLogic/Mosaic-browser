using System.Globalization;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Downloads;

/// <summary>Where a download is in its life. Raw values (<c>DownloadStates.Id</c>) match macOS.</summary>
public enum DownloadState
{
    InProgress,
    Paused,
    Completed,
    Failed,
    Cancelled,
    /// <summary>Handed to Free Download Manager; Mosaic's engine did not download it.</summary>
    HandedOff,
}

/// <summary>Who did (or is doing) the downloading.</summary>
public enum DownloadEngine
{
    Chromium,
    Gecko,
    /// <summary>Free Download Manager.</summary>
    Fdm,
}

public static class DownloadStates
{
    public static bool IsActive(this DownloadState state) => state is DownloadState.InProgress or DownloadState.Paused;

    public static string Id(this DownloadState state) => state switch
    {
        DownloadState.InProgress => "inProgress",
        DownloadState.Paused => "paused",
        DownloadState.Completed => "completed",
        DownloadState.Failed => "failed",
        DownloadState.Cancelled => "cancelled",
        _ => "handedOff",
    };

    public static DownloadState? FromId(string? id) => id switch
    {
        "inProgress" => DownloadState.InProgress,
        "paused" => DownloadState.Paused,
        "completed" => DownloadState.Completed,
        "failed" => DownloadState.Failed,
        "cancelled" => DownloadState.Cancelled,
        "handedOff" => DownloadState.HandedOff,
        _ => null,
    };

    public static string Title(this DownloadState state) => state switch
    {
        DownloadState.InProgress => "Downloading",
        DownloadState.Paused => "Paused",
        DownloadState.Completed => "Completed",
        DownloadState.Failed => "Failed",
        DownloadState.Cancelled => "Cancelled",
        _ => "Sent to Free Download Manager",
    };

    public static string Id(this DownloadEngine engine) => engine switch
    {
        DownloadEngine.Chromium => "chromium",
        DownloadEngine.Gecko => "gecko",
        _ => "fdm",
    };

    public static DownloadEngine? EngineFromId(string? id) => id switch
    {
        "chromium" => DownloadEngine.Chromium,
        "gecko" => DownloadEngine.Gecko,
        "fdm" => DownloadEngine.Fdm,
        _ => null,
    };

    public static string DisplayName(this DownloadEngine engine) => engine switch
    {
        DownloadEngine.Chromium => "Chromium",
        DownloadEngine.Gecko => "Mozilla",
        _ => "Free Download Manager",
    };
}

/// <summary>
/// One download, as listed on the Downloads page and kept in the download history.
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Downloads/DownloadRecord.swift</c>. <see cref="ToJson"/> writes the
/// same keys and value formats as the macOS app (ISO 8601 dates without fractions, upper-case
/// UUIDs, absent keys for missing values), so a history file moves between platforms.
/// </remarks>
public sealed record DownloadRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>The URL the file came from (after redirects, when the engine reports them).</summary>
    public required string Url { get; set; }
    /// <summary>The URL that was originally requested, if different.</summary>
    public string? OriginalUrl { get; set; }
    /// <summary>The page the download was started from.</summary>
    public string? Referrer { get; set; }
    /// <summary>The file's name on disk (or the name it will have).</summary>
    public required string FileName { get; set; }
    /// <summary>The name the server or page suggested, before sanitising; kept for safety checks.</summary>
    public string? SuggestedName { get; set; }
    /// <summary>Full path of the file once Mosaic knows it.</summary>
    public string? DestinationPath { get; set; }
    public DownloadEngine Engine { get; set; }
    public string? MimeType { get; set; }
    /// <summary>Total size in bytes; -1 when the server did not say.</summary>
    public long TotalBytes { get; set; } = -1;
    public long ReceivedBytes { get; set; }
    public DownloadState State { get; set; } = DownloadState.InProgress;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>The user saw the safety warning for this file and chose to keep it.</summary>
    public bool WarningAcknowledged { get; set; }

    // Live values, not persisted.
    /// <summary>Bytes per second while downloading.</summary>
    public double Speed { get; set; }
    /// <summary>The engine can continue this download from where it stopped.</summary>
    public bool CanResume { get; set; }

    /// <summary>0…1 when the size is known.</summary>
    public double? FractionCompleted
    {
        get
        {
            if (State == DownloadState.Completed) return 1;
            if (TotalBytes <= 0) return null;
            return Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1);
        }
    }

    /// <summary>Seconds left at the current speed, when that can be estimated.</summary>
    public double? SecondsRemaining =>
        State == DownloadState.InProgress && TotalBytes > 0 && Speed > 1
            ? Math.Max(0, TotalBytes - ReceivedBytes) / Speed
            : null;

    /// <summary>Host of the source, for display and search.</summary>
    public string? Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    /// <summary>Safety assessment of the file's name.</summary>
    public DownloadAssessment Assessment => DownloadSafety.Assess(SuggestedName ?? FileName, FileName);

    /// <summary>A completed file that still needs the user to confirm they want to keep or open it.</summary>
    public bool NeedsSafetyConfirmation => !WarningAcknowledged && Assessment.NeedsWarning;

    /// <summary>
    /// Whether <paramref name="query"/> (space-separated terms, all required) matches this record's
    /// name, address, host or folder. Case- and diacritic-insensitive.
    /// </summary>
    public bool Matches(string? query)
    {
        var terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return true;
        var haystack = string.Join("\n", FileName, Url, OriginalUrl ?? "", Referrer ?? "", DestinationPath ?? "", Host ?? "");
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return terms.All(term => compare.IndexOf(haystack, term, options) >= 0);
    }

    // ---- JSON (macOS format) ----

    public JsonObject ToJson()
    {
        // Keys in sorted order, as the macOS encoder writes them (.sortedKeys).
        var json = new JsonObject();
        void Put(string key, JsonNode? value)
        {
            if (value is not null) json[key] = value;
        }
        Put("destinationPath", DestinationPath);
        Put("endedAt", EndedAt is { } ended ? FormatDate(ended) : null);
        Put("engine", Engine.Id());
        Put("errorMessage", ErrorMessage);
        Put("fileName", FileName);
        Put("id", Id.ToString().ToUpperInvariant());
        Put("mimeType", MimeType);
        Put("originalURL", OriginalUrl);
        Put("receivedBytes", ReceivedBytes);
        Put("referrer", Referrer);
        Put("startedAt", FormatDate(StartedAt));
        Put("state", State.Id());
        Put("suggestedName", SuggestedName);
        Put("totalBytes", TotalBytes);
        Put("url", Url);
        Put("warningAcknowledged", WarningAcknowledged);
        return json;
    }

    /// <summary>Reads one entry; null when a required field is missing or malformed.</summary>
    public static DownloadRecord? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json) return null;
        if (Text(json, "id") is not { } idText || !Guid.TryParse(idText, out var id)) return null;
        if (Text(json, "url") is not { } url || Text(json, "fileName") is not { } fileName) return null;
        if (DownloadStates.EngineFromId(Text(json, "engine")) is not { } engine) return null;
        if (DownloadStates.FromId(Text(json, "state")) is not { } state) return null;
        if (ParseDate(Text(json, "startedAt")) is not { } started) return null;
        return new DownloadRecord
        {
            Id = id,
            Url = url,
            OriginalUrl = Text(json, "originalURL"),
            Referrer = Text(json, "referrer"),
            FileName = fileName,
            SuggestedName = Text(json, "suggestedName"),
            DestinationPath = Text(json, "destinationPath"),
            Engine = engine,
            MimeType = Text(json, "mimeType"),
            TotalBytes = Number(json, "totalBytes") ?? -1,
            ReceivedBytes = Number(json, "receivedBytes") ?? 0,
            State = state,
            ErrorMessage = Text(json, "errorMessage"),
            StartedAt = started,
            EndedAt = ParseDate(Text(json, "endedAt")),
            WarningAcknowledged = json["warningAcknowledged"] is JsonValue flag && flag.TryGetValue(out bool acknowledged) && acknowledged,
        };
    }

    /// <summary>ISO 8601 in UTC without fractional seconds, which is all Swift's <c>.iso8601</c> strategy reads.</summary>
    internal static string FormatDate(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    private static string? Text(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static long? Number(JsonObject json, string key)
    {
        if (json[key] is not JsonValue value) return null;
        if (value.TryGetValue(out long number)) return number;
        if (value.TryGetValue(out int small)) return small;
        if (value.TryGetValue(out double real)) return (long)real;
        return null;
    }
}
