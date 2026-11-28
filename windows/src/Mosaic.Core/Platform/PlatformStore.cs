using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Platform;

/// <summary>
/// The platform area's preferences: <c>platform.json</c> beside <c>settings.json</c>, with the key
/// names the macOS app uses in its defaults (<c>engineRules</c>, <c>contentLanguages</c>,
/// <c>updateCheckFrequency</c>, <c>updateLastCheck</c>, <c>updateAutoInstall</c>,
/// <c>geckoRuntimeLastCheck</c>, <c>geckoRuntimeLatestESR</c>) and the same value shapes, so the
/// rules and languages move between the two apps.
/// </summary>
/// <remarks>
/// Read lazily, on first use (never on the start-up path), and written off the UI thread, latest
/// state wins. Like <c>BrowserSettings</c>, a convenience and never a reason to fail: an unreadable
/// file means defaults; a file that cannot be written means the change lasts for this run. A store
/// without a path (the model could not tell where its settings live) keeps everything in memory.
/// </remarks>
public sealed class PlatformStore
{
    public const string FileName = "platform.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string? path;
    private readonly object gate = new();
    private JsonObject? values;
    private Task pendingSave = Task.CompletedTask;
    private int saveGeneration;

    /// <param name="path">The file; null keeps everything in memory.</param>
    public PlatformStore(string? path) => this.path = path;

    public string? Path => path;

    public JsonNode? Get(string key)
    {
        lock (gate) return Values[key]?.DeepClone();
    }

    public string? GetString(string key) =>
        Get(key) is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public bool? GetBool(string key) =>
        Get(key) is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    public DateTimeOffset? GetDate(string key) =>
        DateTimeOffset.TryParse(GetString(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;

    public IReadOnlyList<string>? GetStrings(string key) =>
        Get(key) is JsonArray array
            ? [.. array.OfType<JsonValue>().Select(item => item.TryGetValue(out string? text) ? text : null).OfType<string>()]
            : null;

    /// <summary>Sets (or with null removes) a value and saves in the background.</summary>
    public void Set(string key, JsonNode? value)
    {
        lock (gate)
        {
            if (value is null) Values.Remove(key);
            else Values[key] = value.DeepClone();
        }
        ScheduleSave();
    }

    public void SetDate(string key, DateTimeOffset? date) =>
        Set(key, date is { } value ? JsonValue.Create(value.ToString("o", CultureInfo.InvariantCulture)) : null);

    /// <summary>Waits for writes already scheduled (tests, and the app as it quits).</summary>
    public Task FlushAsync()
    {
        lock (gate) return pendingSave;
    }

    private JsonObject Values
    {
        get
        {
            if (values is not null) return values;
            values = Read() ?? [];
            return values;
        }
    }

    private JsonObject? Read()
    {
        if (path is null) return null;
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void ScheduleSave()
    {
        if (path is null) return;
        lock (gate)
        {
            var generation = ++saveGeneration;
            pendingSave = pendingSave.ContinueWith(_ => Write(generation), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Write(int generation)
    {
        string text;
        lock (gate)
        {
            // A later change has its own write queued behind this one.
            if (generation != saveGeneration || values is null) return;
            text = values.ToJsonString(Indented);
        }
        try
        {
            if (System.IO.Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text);
            File.Move(temporary, path!, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // See the class remarks.
        }
    }
}
