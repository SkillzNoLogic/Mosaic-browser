namespace Mosaic.Core.Library;

/// <summary>
/// Turns the stream of tab changes from both engines into one history visit per navigation,
/// recorded once the page has settled so the visit carries its final title.
/// </summary>
/// <remarks>
/// Port of <c>Sources/Mosaic/Features/Library/HistoryRecorder.swift</c>. The model reports every
/// change of every tab (selected or not, Gecko or Chromium). A visit is committed when a tab
/// has finished loading a new address and its title has been stable for a moment, or straight
/// away when the tab moves on before that. Reloads of the same page and title changes do not
/// add visits; a title that changes later only updates the stored title.
/// Single-threaded: use it on the UI thread; its timers resume there.
/// </remarks>
public sealed class HistoryRecorder
{
    public sealed record Visit(string Url, string Title, bool Typed);

    private readonly Action<Visit> commit;
    private readonly Action<string, string> retitle;
    private readonly TimeProvider time;
    private readonly Dictionary<Guid, Track> tracks = [];

    public HistoryRecorder(Action<Visit> commit, Action<string, string> retitle, TimeProvider? time = null)
    {
        this.commit = commit;
        this.retitle = retitle;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>How long a finished page's title must stay unchanged before its visit is recorded.</summary>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>A page whose title keeps changing is recorded after this long anyway.</summary>
    public TimeSpan MaximumDelay { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Tabs being tracked (for tests and pruning).</summary>
    public int TrackedTabs => tracks.Count;

    private sealed class Track(DateTimeOffset loadRequest)
    {
        public DateTimeOffset LoadRequest = loadRequest;
        public DateTimeOffset? TypedRequest;
        public string? LastRecordedUrl;
        public string RecordedTitle = "";
        public Visit? Pending;
        public DateTimeOffset PendingSince;
        public CancellationTokenSource? Timer;
    }

    /// <summary>
    /// Error pages keep the failed address, so they are recognised by their titles: Mosaic's own
    /// Chromium error page, WebView2's, and Gecko's network and certificate error pages.
    /// </summary>
    public static IReadOnlySet<string> ErrorTitles { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Can’t open this page",
        "Problem loading page", "Server Not Found", "Hmm. We’re having trouble finding that site.",
        "Unable to connect", "The connection has timed out", "Secure Connection Failed",
        "Warning: Potential Security Risk Ahead", "Did Not Connect: Potential Security Issue",
        "Page Not Found", "Offline mode", "Server not found",
        // WebView2 / Chromium's own error pages.
        "This site can’t be reached", "This page isn’t working", "Your connection isn’t private",
        "Hmmm… can't reach this page", "Hmmm… can’t reach this page",
    };

    /// <summary>One report about a tab. <paramref name="excluded"/> tabs (private, internal pages, failures) never record.</summary>
    public void Observe(Guid id, string url, string title, bool isLoading, DateTimeOffset loadRequest, bool excluded)
    {
        if (!tracks.TryGetValue(id, out var track))
        {
            track = new Track(loadRequest);
            tracks[id] = track;
        }

        // An explicit load (typed, a bookmark, a link opened in a new tab): whatever was
        // pending is a finished visit, and the same address may be visited again.
        if (track.LoadRequest != loadRequest)
        {
            Flush(track);
            track.LoadRequest = loadRequest;
            track.LastRecordedUrl = null;
        }
        var isError = ErrorTitles.Contains(title);
        var now = time.GetUtcNow();
        // The page rewrote its address on the same site while its visit was still settling
        // (history.replaceState or a script redirect right after loading; Chromium reports
        // these as a brief load): one visit, at the final address.
        if (track.Pending is { } settling && settling.Url != url && !excluded && !isError && LibraryUrl.IsRecordable(url)
            && LibraryUrl.Host(url) == LibraryUrl.Host(settling.Url) && now - track.PendingSince < SettleDelay)
        {
            track.Pending = settling with { Url = url, Title = title };
            Schedule(id, track);
            return;
        }
        if (track.Pending is { } moved && (moved.Url != url || excluded || isError))
        {
            // Moved on before the page settled (or it turned out to be an error page).
            if (isError && moved.Url == url) CancelPending(track);
            else Flush(track);
        }
        if (excluded || isError || !LibraryUrl.IsRecordable(url) || isLoading) return;

        if (url == track.LastRecordedUrl)
        {
            if (title.Length > 0 && title != track.RecordedTitle)
            {
                track.RecordedTitle = title;
                retitle(url, title);
            }
            return;
        }
        if (track.Pending is { } pending)
        {
            if (pending.Title == title) return;
            track.Pending = pending with { Title = title };
        }
        else
        {
            var typed = track.TypedRequest == track.LoadRequest && track.LastRecordedUrl is null;
            track.Pending = new Visit(url, title, typed);
            track.PendingSince = now;
        }
        Schedule(id, track);
    }

    /// <summary>The model's last load in this tab came from the address bar.</summary>
    public void NoteTyped(Guid id, DateTimeOffset loadRequest)
    {
        if (!tracks.TryGetValue(id, out var track))
        {
            track = new Track(loadRequest);
            tracks[id] = track;
        }
        track.TypedRequest = loadRequest;
    }

    /// <summary>Commits every pending visit now (on quit).</summary>
    public void FlushAll()
    {
        foreach (var track in tracks.Values) Flush(track);
    }

    /// <summary>Drops bookkeeping for tabs that no longer exist (their pending visits commit first).</summary>
    public void Prune(IReadOnlySet<Guid> live)
    {
        foreach (var (id, track) in tracks.Where(pair => !live.Contains(pair.Key)).ToList())
        {
            Flush(track);
            tracks.Remove(id);
        }
    }

    private void Schedule(Guid id, Track track)
    {
        track.Timer?.Cancel();
        var elapsed = time.GetUtcNow() - track.PendingSince;
        var remaining = MaximumDelay - elapsed;
        var delay = remaining < SettleDelay ? remaining : SettleDelay;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        var timer = new CancellationTokenSource();
        track.Timer = timer;
        _ = FireAsync(id, delay, timer.Token);
    }

    private async Task FireAsync(Guid id, TimeSpan delay, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(delay, time, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellation.IsCancellationRequested || !tracks.TryGetValue(id, out var current)) return;
        Flush(current);
    }

    private void Flush(Track track)
    {
        track.Timer?.Cancel();
        track.Timer = null;
        if (track.Pending is not { } visit) return;
        track.Pending = null;
        track.LastRecordedUrl = visit.Url;
        track.RecordedTitle = visit.Title;
        commit(visit);
    }

    private static void CancelPending(Track track)
    {
        track.Timer?.Cancel();
        track.Timer = null;
        track.Pending = null;
    }
}
