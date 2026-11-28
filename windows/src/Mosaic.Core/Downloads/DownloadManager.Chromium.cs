using Mosaic.Core.Browser;

namespace Mosaic.Core.Downloads;

/// <summary>
/// Chromium's side of the download manager (port of <c>DownloadManager+Chromium.swift</c>). The
/// app's WebView2 glue calls <see cref="ChromiumStartingAsync"/> from <c>DownloadStarting</c> (holding
/// its deferral) and <see cref="ChromiumUpdated"/> from the operation's events.
/// </summary>
public sealed partial class DownloadManager
{
    /// <summary>Speed samples per live Chromium download (WebView2 reports bytes, not speed).</summary>
    private readonly Dictionary<IChromiumDownload, (DateTimeOffset At, long Bytes, double Speed)> chromiumSamples =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Chromium is about to write a download: decide where (or whether) it goes. Returns the full
    /// path to save to, or null to cancel it (nothing is kept; a hand-off or a "no" ends here).
    /// </summary>
    /// <param name="download">The new download.</param>
    /// <param name="suggestedName">The name the server or page suggested (raw, from Content-Disposition
    /// or the path WebView2 proposes); null to derive it from the URL.</param>
    /// <param name="pageUrl">The page it was started from, if known.</param>
    public async Task<string?> ChromiumStartingAsync(IChromiumDownload download, string? suggestedName, string? pageUrl)
    {
        var suggested = !string.IsNullOrWhiteSpace(suggestedName) ? suggestedName : DownloadFileName.Suggested(download.Url);
        var fileName = DownloadFileName.Sanitize(suggested);
        Guid? retryId = pendingRetries.Remove(download.Url, out var pending) && pending != Guid.Empty ? pending : null;

        Guid id;
        if (retryId is { } retry && Record(retry) is not null)
        {
            id = retry;
            Update(retry, r =>
            {
                r.Engine = DownloadEngine.Chromium;
                r.State = DownloadState.InProgress;
                r.ErrorMessage = null;
                r.ReceivedBytes = 0;
                r.TotalBytes = download.TotalBytes;
                r.EndedAt = null;
            });
        }
        else
        {
            retryId = null;
            var record = new DownloadRecord
            {
                Url = download.Url,
                Referrer = string.IsNullOrEmpty(pageUrl) || pageUrl == "about:blank" ? null : pageUrl,
                FileName = fileName,
                SuggestedName = suggested,
                Engine = DownloadEngine.Chromium,
                MimeType = string.IsNullOrEmpty(download.MimeType) ? null : download.MimeType,
                TotalBytes = download.TotalBytes,
                StartedAt = time.GetUtcNow(),
            };
            Insert(record);
            id = record.Id;
        }
        chromiumLinks[download] = id;
        undecided.Add(id);

        var choice = await ChooseStartAsync(id, isRetry: retryId is not null);
        undecided.Remove(id);
        if (Record(id) is not { } current)
        {
            chromiumLinks.Remove(download);
            return null;
        }
        switch (choice)
        {
            case StartChoice.Save save:
                string destination;
                if (save.Path is { } chosen) destination = chosen;
                else if (retryId is not null && current.DestinationPath is { } previous && !File.Exists(previous)) destination = previous;
                else destination = DefaultDestination(current.FileName, current.MimeType);
                try { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                reservedPaths.Add(destination);
                Update(id, r =>
                {
                    r.DestinationPath = destination;
                    r.FileName = Path.GetFileName(destination);
                });
                return destination;
            case StartChoice.HandOff:
                // Nothing is kept: WebView2 drops the download when it is cancelled here.
                chromiumLinks.Remove(download);
                await HandOffAsync(id, EngineKind.Chromium);
                return null;
            case StartChoice.OpenExisting existing:
                chromiumLinks.Remove(download);
                DeleteRecord(id);
                _ = OpenAsync(existing.Existing.Id);
                return null;
            default:
                chromiumLinks.Remove(download);
                if (retryId is not null) Update(id, r => { r.State = DownloadState.Cancelled; r.EndedAt = time.GetUtcNow(); });
                else DeleteRecord(id);
                return null;
        }
    }

    /// <summary>A Chromium download's state or progress changed.</summary>
    public void ChromiumUpdated(IChromiumDownload download)
    {
        if (!chromiumLinks.TryGetValue(download, out var id) || Record(id) is null) return;
        if (undecided.Contains(id))
        {
            // Still asking; Chromium may already be fetching into a temporary file.
            Update(id, r => { if (download.TotalBytes > 0) r.TotalBytes = download.TotalBytes; });
            return;
        }
        var now = time.GetUtcNow();
        var speed = SampleSpeed(download, now);
        Update(id, r =>
        {
            if (download.TotalBytes > 0) r.TotalBytes = download.TotalBytes;
            r.ReceivedBytes = download.ReceivedBytes;
            r.Speed = speed;
            switch (download.State)
            {
                case ChromiumDownloadState.InProgress:
                    r.State = DownloadState.InProgress;
                    r.CanResume = false;
                    r.ErrorMessage = null;
                    r.EndedAt = null;
                    break;
                case ChromiumDownloadState.Paused:
                    r.State = DownloadState.Paused;
                    r.Speed = 0;
                    r.CanResume = true;
                    break;
                case ChromiumDownloadState.Completed:
                    r.State = DownloadState.Completed;
                    r.Speed = 0;
                    r.EndedAt ??= now;
                    if (!string.IsNullOrEmpty(download.ResultFilePath))
                    {
                        r.DestinationPath = download.ResultFilePath;
                        r.FileName = Path.GetFileName(download.ResultFilePath);
                    }
                    break;
                case ChromiumDownloadState.Cancelled:
                    r.State = DownloadState.Cancelled;
                    r.Speed = 0;
                    r.EndedAt ??= now;
                    r.CanResume = false;
                    break;
                case ChromiumDownloadState.Interrupted:
                    r.State = DownloadState.Failed;
                    r.Speed = 0;
                    r.ErrorMessage = download.InterruptReason ?? "Download failed";
                    r.CanResume = download.CanResume;
                    r.EndedAt ??= now;
                    break;
            }
        });
        switch (download.State)
        {
            case ChromiumDownloadState.Completed:
                chromiumLinks.Remove(download);
                chromiumSamples.Remove(download);
                FinishCompleted(id);
                break;
            case ChromiumDownloadState.Cancelled:
                chromiumLinks.Remove(download);
                chromiumSamples.Remove(download);
                if (Record(id)?.DestinationPath is { } path) reservedPaths.Remove(path);
                break;
        }
    }

    private double SampleSpeed(IChromiumDownload download, DateTimeOffset now)
    {
        if (download.State != ChromiumDownloadState.InProgress)
        {
            chromiumSamples.Remove(download);
            return 0;
        }
        if (!chromiumSamples.TryGetValue(download, out var last))
        {
            chromiumSamples[download] = (now, download.ReceivedBytes, 0);
            return 0;
        }
        var elapsed = (now - last.At).TotalSeconds;
        if (elapsed < 0.4) return last.Speed;
        var current = Math.Max(0, download.ReceivedBytes - last.Bytes) / elapsed;
        // Smoothed, so the time left does not jump about.
        var speed = last.Speed <= 0 ? current : last.Speed * 0.5 + current * 0.5;
        chromiumSamples[download] = (now, download.ReceivedBytes, speed);
        return speed;
    }

    /// <summary>
    /// Starts <paramref name="url"/> in Chromium; with <paramref name="reusing"/>, the existing entry
    /// is continued (retry). Falls back to Gecko when Chromium cannot start it.
    /// </summary>
    public void StartInChromium(string url, Guid? reusing = null)
    {
        if (reusing is { } id)
        {
            Update(id, r =>
            {
                r.State = DownloadState.InProgress;
                r.Engine = DownloadEngine.Chromium;
                r.ErrorMessage = null;
                r.ReceivedBytes = 0;
                r.EndedAt = null;
            });
        }
        pendingRetries[url] = reusing ?? Guid.Empty;
        if (Chromium?.StartDownload(url) == true) return;
        // No Chromium to start it from.
        pendingRetries.Remove(url);
        StartInGecko(url, reusing);
    }

    /// <summary>
    /// The Chromium helper that <see cref="StartInChromium"/> used found a page rather than a file
    /// at <paramref name="url"/>: download it with Gecko instead (it fetches any URL as a file).
    /// </summary>
    public void ChromiumStartFailed(string url)
    {
        if (!pendingRetries.Remove(url, out var reusing)) return;
        StartInGecko(url, reusing == Guid.Empty ? null : reusing);
    }
}
