using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Downloads;

/// <summary>
/// The download history, kept as one JSON file in the profile folder
/// (<c>%LOCALAPPDATA%\Mosaic\Downloads\history.json</c>, the same format as macOS).
/// </summary>
/// <remarks>
/// Port of <c>DownloadHistoryStore.swift</c>. Newest first. Not thread-safe: use it from one
/// thread (the UI thread); <see cref="Read"/> and <see cref="Write"/> are the parts that may run
/// on a background thread.
/// </remarks>
public sealed class DownloadHistoryStore(string filePath, int limit = 1000)
{
    /// <summary>What a download that was running when Mosaic quit is marked with on the next launch.</summary>
    public const string InterruptedMessage = "Interrupted when Mosaic quit";

    private List<DownloadRecord> records = [];

    public string FilePath { get; } = filePath;

    /// <summary>Oldest finished entries beyond this are dropped.</summary>
    public int Limit { get; } = limit;

    public IReadOnlyList<DownloadRecord> Records => records;

    /// <summary>Reads the file into <see cref="Records"/> (see <see cref="Read"/>).</summary>
    public void Load() => records = Read(FilePath);

    /// <summary>Replaces the records with ones read elsewhere (e.g. on a background thread).</summary>
    public void Replace(IEnumerable<DownloadRecord> loaded) =>
        records = [.. loaded.OrderByDescending(record => record.StartedAt)];

    /// <summary>
    /// Reads a history file. A download that was still running (or paused) when Mosaic last quit
    /// cannot be continued by the engine any more, so it is marked failed and can be retried.
    /// A missing file gives an empty history; an unreadable one is kept aside as
    /// <c>&lt;name&gt;.corrupt</c> rather than overwritten. Safe on any thread.
    /// </summary>
    public static List<DownloadRecord> Read(string filePath)
    {
        string text;
        try
        {
            text = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || root["downloads"] is not JsonArray entries)
            {
                throw new JsonException("not a download history");
            }
            var loaded = new List<DownloadRecord>();
            foreach (var entry in entries)
            {
                if (DownloadRecord.FromJson(entry) is not { } record) continue;
                if (record.State.IsActive())
                {
                    record.State = DownloadState.Failed;
                    record.ErrorMessage = InterruptedMessage;
                    record.Speed = 0;
                }
                loaded.Add(record);
            }
            return [.. loaded.OrderByDescending(record => record.StartedAt)];
        }
        catch (JsonException)
        {
            try
            {
                File.Move(filePath, filePath + ".corrupt", overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            return [];
        }
    }

    /// <summary>The file's text for the current records (built on the calling thread).</summary>
    public string Serialize()
    {
        var downloads = new JsonArray();
        foreach (var record in records) downloads.Add(record.ToJson());
        return new JsonObject { ["downloads"] = downloads, ["version"] = 1 }.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>Writes the current records now, atomically.</summary>
    public void Save() => Write(FilePath, Serialize());

    /// <summary>Writes <paramref name="text"/> to <paramref name="filePath"/> atomically (temporary file, then a rename). Safe on any thread.</summary>
    public static void Write(string filePath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, filePath, overwrite: true);
    }

    public DownloadRecord? Record(Guid id) => records.FirstOrDefault(record => record.Id == id);

    /// <summary>Adds <paramref name="record"/>, or replaces the one with the same ID (keeping its place).</summary>
    public void Upsert(DownloadRecord record)
    {
        var index = records.FindIndex(existing => existing.Id == record.Id);
        if (index >= 0)
        {
            records[index] = record;
        }
        else
        {
            records.Insert(0, record);
            Trim();
        }
    }

    public void Remove(Guid id) => records.RemoveAll(record => record.Id == id);

    /// <summary>Removes every finished entry; downloads still running or paused stay.</summary>
    public void ClearFinished() => records.RemoveAll(record => !record.State.IsActive());

    /// <summary>Entries matching <paramref name="query"/> (see <see cref="DownloadRecord.Matches"/>), newest first.</summary>
    public IReadOnlyList<DownloadRecord> Search(string query) => [.. records.Where(record => record.Matches(query))];

    /// <summary>
    /// The most recent completed download of <paramref name="url"/> whose file is still there, for
    /// the duplicate-download warning. <paramref name="fileExists"/> is injectable for tests.
    /// </summary>
    public DownloadRecord? CompletedDownload(string url, Guid? excluding = null, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var key = Normalised(url);
        return records.FirstOrDefault(record =>
            record.Id != excluding && record.State == DownloadState.Completed &&
            (Normalised(record.Url) == key || (record.OriginalUrl is { } original && Normalised(original) == key)) &&
            record.DestinationPath is { } path && fileExists(path));
    }

    /// <summary>URLs compared without their fragment.</summary>
    internal static string Normalised(string url)
    {
        var hash = url.IndexOf('#');
        return hash >= 0 ? url[..hash] : url;
    }

    private void Trim()
    {
        if (records.Count <= Limit) return;
        // Drop the oldest finished entries first; never an active download.
        var excess = records.Count - Limit;
        for (var index = records.Count - 1; index >= 0 && excess > 0; index--)
        {
            if (records[index].State.IsActive()) continue;
            records.RemoveAt(index);
            excess--;
        }
    }
}
