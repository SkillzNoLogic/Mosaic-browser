using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Downloads;

/// <summary>One download as the Gecko agent's <c>downloadsState</c> describes it.</summary>
public sealed record GeckoDownload(
    int Id, string Url, string Referrer, string Path, string ContentType, long TotalBytes, long CurrentBytes,
    double Speed, string State, string Error, bool HasPartialData, bool Restored)
{
    public static GeckoDownload? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json || json["id"] is not JsonValue id || !id.TryGetValue(out int number)) return null;
        return new GeckoDownload(
            number, Text(json, "url"), Text(json, "referrer"), Text(json, "path"), Text(json, "contentType"),
            Long(json, "totalBytes", -1), Long(json, "currentBytes", 0), Long(json, "speed", 0), Text(json, "state"),
            Text(json, "error"), Flag(json, "hasPartialData"), Flag(json, "restored"));
    }

    internal static string Text(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static long Long(JsonObject json, string key, long fallback)
    {
        if (json[key] is not JsonValue value) return fallback;
        if (value.TryGetValue(out long number)) return number;
        return value.TryGetValue(out double real) ? (long)real : fallback;
    }

    private static bool Flag(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue(out bool flag) && flag;
}

/// <summary>
/// Gecko's side of the download manager: the agent's <c>configureDownloads</c>,
/// <c>downloadsState</c>, <c>downloadAction</c>, <c>startDownload</c> and <c>downloadRequestInfo</c>
/// (port of <c>DownloadManager+Gecko.swift</c>).
/// </summary>
public sealed partial class DownloadManager
{
    private static readonly TimeSpan BusyPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(1);

    private int geckoVersion = -1;
    private CancellationTokenSource? geckoPolling;

    /// <summary>The agent has been told where to save and is watching Gecko's downloads.</summary>
    public bool GeckoWatching { get; private set; }

    private void GeckoStatusChanged()
    {
        if (gecko.Status == GeckoStatus.Running)
        {
            if (geckoPolling is not null) return;
            var polling = new CancellationTokenSource();
            geckoPolling = polling;
            _ = RunGeckoAsync(polling.Token);
            return;
        }
        geckoPolling?.Cancel();
        geckoPolling = null;
        GeckoWatching = false;
        geckoVersion = -1;
        // The agent's download IDs start again with a new Gecko process.
        foreach (var id in geckoLinks.Values.ToList())
        {
            Update(id, r =>
            {
                if (!r.State.IsActive()) return;
                r.State = DownloadState.Failed;
                r.Speed = 0;
                r.ErrorMessage = "The Mozilla engine stopped";
            });
            if (Record(id)?.DestinationPath is { } path) reservedPaths.Remove(path);
        }
        geckoLinks.Clear();
        ignoredGeckoIds.Clear();
    }

    private async Task RunGeckoAsync(CancellationToken cancellation)
    {
        await ConfigureGeckoAsync();
        while (!cancellation.IsCancellationRequested && gecko.Status == GeckoStatus.Running)
        {
            await PollGeckoAsync();
            // Twice a second while something is moving, once a second otherwise; an unchanged
            // poll costs one short reply.
            var busy = store.Records.Any(r => r.Engine == DownloadEngine.Gecko && r.State == DownloadState.InProgress) ||
                undecided.Count > 0;
            try
            {
                await Task.Delay(busy ? BusyPoll : IdlePoll, time, cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Tells Gecko where to save and what to offer; starts watching its downloads.</summary>
    internal async Task ConfigureGeckoAsync()
    {
        if (gecko.Status != GeckoStatus.Running) return;
        var options = new JsonObject
        {
            ["directory"] = EnsureFolder(),
            ["fdmMenu"] = Fdm.IsInstalled && Settings.FdmContextMenu,
        };
        try
        {
            GeckoWatching = await gecko.CallAsync("configureDownloads", options) is not null;
        }
        catch (Exception)
        {
            GeckoWatching = false;
        }
    }

    /// <summary>Asks the agent what changed since the last poll and applies it.</summary>
    internal async Task PollGeckoAsync()
    {
        JsonNode? value;
        try
        {
            value = await gecko.CallAsync("downloadsState", geckoVersion);
        }
        catch (Exception)
        {
            return;
        }
        if (value is not JsonObject snapshot) return;
        if (snapshot["unchanged"] is JsonValue unchanged && unchanged.TryGetValue(out bool same) && same) return;
        ReconcileGecko(snapshot);
    }

    internal void ReconcileGecko(JsonObject snapshot)
    {
        if (snapshot["version"] is JsonValue version && version.TryGetValue(out int number)) geckoVersion = number;
        if (snapshot["commands"] is JsonArray commands)
        {
            foreach (var command in commands.OfType<JsonObject>())
            {
                switch (GeckoDownload.Text(command, "type"))
                {
                    case "showDownloads":
                        OpenDownloadsPage();
                        break;
                    case "sendLinkToFDM":
                        var url = GeckoDownload.Text(command, "url");
                        var referrer = GeckoDownload.Text(command, "referrer");
                        if (url.Length > 0) SendLinkToFdm(url, referrer.Length > 0 ? referrer : null, EngineKind.Gecko);
                        break;
                }
            }
        }

        var seen = new HashSet<int>();
        foreach (var download in (snapshot["downloads"] as JsonArray ?? []).Select(GeckoDownload.FromJson).OfType<GeckoDownload>())
        {
            seen.Add(download.Id);
            if (ignoredGeckoIds.Contains(download.Id)) continue;
            if (geckoLinks.TryGetValue(download.Id, out var linked))
            {
                ApplyGecko(download, linked);
                continue;
            }
            // Started by Mosaic itself; StartInGecko links it when the agent answers.
            if (pendingGeckoStarts.ContainsKey(download.Url)) continue;
            if (download.Restored)
            {
                // From before this Gecko session: shown again only if it is still listed.
                var match = store.Records.FirstOrDefault(r =>
                    r.Engine == DownloadEngine.Gecko && r.Url == download.Url && r.DestinationPath == download.Path &&
                    GeckoId(r.Id) is null);
                if (match is not null)
                {
                    geckoLinks[download.Id] = match.Id;
                    ApplyGecko(download, match.Id);
                }
                else
                {
                    ignoredGeckoIds.Add(download.Id);
                    if (download.State is "completed" or "failed" or "cancelled") gecko.Send("downloadAction", download.Id, "remove");
                }
                continue;
            }
            AdoptGecko(download);
        }

        foreach (var (link, id) in geckoLinks.ToList())
        {
            if (seen.Contains(link)) continue;
            geckoLinks.Remove(link);
            Update(id, r =>
            {
                if (!r.State.IsActive()) return;
                r.State = DownloadState.Failed;
                r.Speed = 0;
                r.ErrorMessage = "Removed by the Mozilla engine";
            });
        }
    }

    /// <summary>
    /// A download a Gecko page started. Gecko is already fetching it into the download folder; the
    /// questions (FDM, duplicate, save as) are asked meanwhile.
    /// </summary>
    private void AdoptGecko(GeckoDownload download)
    {
        var name = Path.GetFileName(download.Path);
        var fileName = name.Length > 0 ? name : DownloadFileName.Suggested(download.Url);
        var record = new DownloadRecord
        {
            Url = download.Url,
            Referrer = download.Referrer.Length > 0 ? download.Referrer : null,
            FileName = fileName,
            SuggestedName = fileName,
            DestinationPath = download.Path.Length > 0 ? download.Path : null,
            Engine = DownloadEngine.Gecko,
            MimeType = download.ContentType.Length > 0 ? download.ContentType : null,
            TotalBytes = download.TotalBytes,
            ReceivedBytes = download.CurrentBytes,
            StartedAt = time.GetUtcNow(),
        };
        Insert(record);
        var id = record.Id;
        geckoLinks[download.Id] = id;
        if (download.Path.Length > 0) reservedPaths.Add(download.Path);
        undecided.Add(id);
        ApplyGecko(download, id);
        _ = DecideAdoptedGeckoAsync(download.Id, id);
    }

    private async Task DecideAdoptedGeckoAsync(int link, Guid id)
    {
        var choice = await ChooseStartAsync(id, isRetry: false);
        undecided.Remove(id);
        switch (choice)
        {
            case StartChoice.Save save:
                if (save.Path is { } chosen) geckoDestinations[id] = chosen;
                if (Record(id)?.State == DownloadState.Completed) FinishCompleted(id);
                break;
            case StartChoice.Cancel:
                DropGeckoDownload(link, id);
                DeleteRecord(id);
                break;
            case StartChoice.HandOff:
                DropGeckoDownload(link, id);
                Update(id, r => { r.DestinationPath = null; r.ReceivedBytes = 0; });
                await HandOffAsync(id, EngineKind.Gecko);
                break;
            case StartChoice.OpenExisting existing:
                DropGeckoDownload(link, id);
                DeleteRecord(id);
                await OpenAsync(existing.Existing.Id);
                break;
        }
    }

    /// <summary>Stops a Gecko download Mosaic does not want, removing what it wrote.</summary>
    private void DropGeckoDownload(int link, Guid id)
    {
        geckoLinks.Remove(link);
        ignoredGeckoIds.Add(link);
        gecko.Send("downloadAction", link, "remove");
        if (Record(id) is not { DestinationPath: { } path } record) return;
        reservedPaths.Remove(path);
        // It may have finished while the question was open.
        if (record.State == DownloadState.Completed && File.Exists(path)) Shell.MoveToRecycleBin(path);
    }

    private void ApplyGecko(GeckoDownload download, Guid id)
    {
        var finished = finishedIds.Contains(id);
        var now = time.GetUtcNow();
        Update(id, r =>
        {
            if (r.State == DownloadState.HandedOff) return;
            if (download.TotalBytes >= 0) r.TotalBytes = download.TotalBytes;
            r.ReceivedBytes = download.CurrentBytes;
            r.Speed = download.Speed;
            if (download.ContentType.Length > 0) r.MimeType = download.ContentType;
            if (!finished && download.Path.Length > 0)
            {
                r.DestinationPath = download.Path;
                r.FileName = Path.GetFileName(download.Path);
            }
            switch (download.State)
            {
                case "inProgress":
                    r.State = DownloadState.InProgress;
                    r.ErrorMessage = null;
                    r.CanResume = false;
                    r.EndedAt = null;
                    break;
                case "paused":
                    r.State = DownloadState.Paused;
                    r.Speed = 0;
                    r.CanResume = true;
                    break;
                case "completed":
                    r.State = DownloadState.Completed;
                    r.Speed = 0;
                    r.EndedAt ??= now;
                    break;
                case "failed":
                    r.State = DownloadState.Failed;
                    r.Speed = 0;
                    r.ErrorMessage = download.Error.Length == 0 ? "Download failed" : download.Error;
                    r.CanResume = download.HasPartialData;
                    r.EndedAt ??= now;
                    break;
                case "cancelled":
                    r.State = DownloadState.Cancelled;
                    r.Speed = 0;
                    r.ErrorMessage = null;
                    r.CanResume = false;
                    r.EndedAt ??= now;
                    break;
            }
        });
        switch (download.State)
        {
            case "completed":
                FinishCompleted(id);
                break;
            case "failed" or "cancelled":
                reservedPaths.Remove(download.Path);
                break;
        }
    }

    /// <summary>Starts <paramref name="url"/> in Gecko. A new download first goes through the usual questions.</summary>
    public void StartInGecko(string url, Guid? reusing = null, string? referrer = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        Guid recordId;
        if (reusing is { } existing)
        {
            recordId = existing;
        }
        else
        {
            var record = new DownloadRecord
            {
                Url = url, Referrer = referrer, FileName = DownloadFileName.Suggested(url), Engine = DownloadEngine.Gecko,
                StartedAt = time.GetUtcNow(),
            };
            Insert(record);
            recordId = record.Id;
        }
        Update(recordId, r =>
        {
            r.State = DownloadState.InProgress;
            r.Engine = DownloadEngine.Gecko;
            r.ErrorMessage = null;
            r.ReceivedBytes = 0;
            r.EndedAt = null;
        });
        undecided.Add(recordId);
        _ = StartInGeckoAsync(url, recordId, isRetry: reusing is not null);
    }

    private async Task StartInGeckoAsync(string url, Guid recordId, bool isRetry)
    {
        var choice = await ChooseStartAsync(recordId, isRetry);
        undecided.Remove(recordId);
        if (Record(recordId) is not { } record) return;
        string destination;
        switch (choice)
        {
            case StartChoice.Save save:
                if (save.Path is { } chosen) destination = chosen;
                else if (isRetry && record.DestinationPath is { } previous && !File.Exists(previous)) destination = previous;
                else destination = DefaultDestination(record.FileName, record.MimeType);
                break;
            case StartChoice.HandOff:
                await HandOffAsync(recordId, EngineKind.Gecko);
                return;
            case StartChoice.OpenExisting existing:
                DeleteRecord(recordId);
                await OpenAsync(existing.Existing.Id);
                return;
            default:
                DeleteRecord(recordId);
                return;
        }
        reservedPaths.Add(destination);
        Update(recordId, r =>
        {
            r.DestinationPath = destination;
            r.FileName = Path.GetFileName(destination);
        });
        pendingGeckoStarts[url] = pendingGeckoStarts.GetValueOrDefault(url) + 1;
        try
        {
            await gecko.StartAsync();
            if (await gecko.CallAsync("startDownload", url, destination) is not JsonValue value || !value.TryGetValue(out int link))
            {
                throw new InvalidOperationException("Gecko did not start the download");
            }
            geckoLinks[link] = recordId;
        }
        catch (Exception error)
        {
            reservedPaths.Remove(destination);
            Update(recordId, r =>
            {
                r.State = DownloadState.Failed;
                r.ErrorMessage = error.Message;
            });
        }
        finally
        {
            var left = pendingGeckoStarts.GetValueOrDefault(url, 1) - 1;
            if (left <= 0) pendingGeckoStarts.Remove(url);
            else pendingGeckoStarts[url] = left;
        }
    }
}
