using System.Collections.ObjectModel;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Downloads;

/// <summary>
/// Downloads from both engines behind one list: progress, pause/resume/cancel/retry, opening
/// and revealing files, the persistent history, safety checks, the Mark of the Web,
/// notifications and the hand-off to Free Download Manager. Owner: <b>downloads</b>.
/// </summary>
/// <remarks>
/// <para>Port of <c>Sources/Mosaic/Features/Downloads/DownloadManager.swift</c>, kept free of UI and
/// Win32 so it is unit-tested on any OS. Chromium reports through <see cref="ChromiumStartingAsync"/>
/// and <see cref="ChromiumUpdated"/> (the app's WebView2 glue); Gecko through the agent's downloads
/// methods, polled while Gecko runs (<c>DownloadManager.Gecko.cs</c>).</para>
/// <para>Single-threaded like <see cref="BrowserModel"/>: create and call it on the UI thread.</para>
/// </remarks>
public sealed partial class DownloadManager : ObservableObject
{
    private static readonly TimeSpan ProgressSaveDelay = TimeSpan.FromSeconds(5);

    private readonly IGeckoEngine gecko;
    private readonly TimeProvider time;
    private readonly DownloadHistoryStore store;
    private readonly object writeLock = new();
    private Task lastWrite = Task.CompletedTask;
    private ITimer? saveTimer;
    private bool isPopoverShown;
    private bool hasUnseenCompletion;
    private string searchText = "";
    private bool loaded;

    // Links between records and the engines' own downloads.
    internal readonly Dictionary<IChromiumDownload, Guid> chromiumLinks = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<int, Guid> geckoLinks = [];
    /// <summary>Gecko downloads Mosaic stopped or dropped (handed off, duplicate, save cancelled).</summary>
    internal readonly HashSet<int> ignoredGeckoIds = [];
    /// <summary>URLs Mosaic is starting in Gecko itself; their downloads are linked by <see cref="StartInGecko"/>.</summary>
    internal readonly Dictionary<string, int> pendingGeckoStarts = [];
    /// <summary>Downloads still waiting for an answer (Free Download Manager, duplicate, save as).</summary>
    internal readonly HashSet<Guid> undecided = [];
    /// <summary>Gecko saves into the download folder; files that belong elsewhere are moved here when they complete.</summary>
    internal readonly Dictionary<Guid, string> geckoDestinations = [];
    /// <summary>Completed downloads whose file has been finished off (moved, marked, announced).</summary>
    internal readonly HashSet<Guid> finishedIds = [];
    /// <summary>Chromium starts in flight: URL → the record to reuse (retries), or Guid.Empty for a new one.</summary>
    internal readonly Dictionary<string, Guid> pendingRetries = [];
    /// <summary>Paths promised to downloads still running, so two downloads never get the same name.</summary>
    internal readonly HashSet<string> reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>For "Download in Mosaic instead" after FDM: the engine a handed-off item came from.</summary>
    internal readonly Dictionary<Guid, EngineKind?> fdmSourceEngines = [];

    /// <param name="gecko">The Mozilla engine (its agent has the downloads methods).</param>
    /// <param name="historyPath">The history file (<c>Downloads\history.json</c>).</param>
    /// <param name="settings">Settings › Downloads.</param>
    /// <param name="time">Clock for speeds, timestamps and delays.</param>
    public DownloadManager(IGeckoEngine gecko, string historyPath, DownloadSettings settings, TimeProvider? time = null)
    {
        this.gecko = gecko;
        this.time = time ?? TimeProvider.System;
        Settings = settings;
        store = new DownloadHistoryStore(historyPath);
        settings.EngineSettingsChanged += EngineSettingsChanged;
        gecko.StatusChanged += GeckoStatusChanged;
        if (gecko.Status == GeckoStatus.Running) GeckoStatusChanged();
    }

    public DownloadSettings Settings { get; }
    public DownloadPrompter Prompter { get; } = new();

    /// <summary>Opening files, notifications, taskbar progress. Set by the app at startup.</summary>
    public IDownloadShell Shell { get; set; } = new RecordingDownloadShell();

    /// <summary>Free Download Manager. Set by the app at startup (Windows only).</summary>
    public IFreeDownloadManager Fdm { get; set; } = new NoFreeDownloadManager();

    /// <summary>Starting Chromium downloads and what FDM needs from Chromium; null until the app provides it.</summary>
    public IChromiumDownloadService? Chromium { get; set; }

    /// <summary>The model's side: notices, the Downloads page, the current engine. Set by <see cref="BrowserModel"/>.</summary>
    public Action<string>? ShowNotice { get; set; }
    public Action? OpenPage { get; set; }
    public Func<bool>? IsPageShown { get; set; }
    public Func<EngineKind>? CurrentEngine { get; set; }

    /// <summary>Every download Mosaic knows about, newest first (the history).</summary>
    public ObservableCollection<DownloadRecord> Items { get; } = [];

    /// <summary>Raised after <see cref="Items"/> or any record in it changed (once per change).</summary>
    public event Action? Changed;

    /// <summary>The history file.</summary>
    public string HistoryPath => store.FilePath;

    /// <summary>The toolbar button's flyout.</summary>
    public bool IsPopoverShown
    {
        get => isPopoverShown;
        set
        {
            if (!Set(ref isPopoverShown, value)) return;
            if (value) HasUnseenCompletion = false;
        }
    }

    /// <summary>A download finished since the flyout was last opened.</summary>
    public bool HasUnseenCompletion { get => hasUnseenCompletion; private set => Set(ref hasUnseenCompletion, value); }

    /// <summary>The Downloads page's search field (kept here so it survives switching tabs).</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (Set(ref searchText, value ?? "")) Notify(nameof(FilteredItems));
        }
    }

    // Diagnostics for `dump`.
    public string? LastOpenRequest { get; private set; }
    public string? LastAutoOpen { get; private set; }
    public string? LastFdmRoute { get; private set; }
    public string? LastMarkOfTheWeb { get; private set; }
    public string? LastNotification { get; private set; }

    // ---- Reading ----

    public DownloadRecord? Record(Guid id) => store.Record(id);

    /// <summary>Downloads currently transferring.</summary>
    public int ActiveCount => store.Records.Count(record => record.State == DownloadState.InProgress);

    /// <summary>Combined progress of the running downloads, or null when a size is unknown.</summary>
    public double? OverallProgress
    {
        get
        {
            var running = store.Records.Where(record => record.State == DownloadState.InProgress).ToList();
            if (running.Count == 0 || running.Any(record => record.TotalBytes <= 0)) return null;
            var total = running.Sum(record => record.TotalBytes);
            var received = running.Sum(record => record.ReceivedBytes);
            return total > 0 ? Math.Min(1, (double)received / total) : null;
        }
    }

    /// <summary>Items matching the page's search.</summary>
    public IReadOnlyList<DownloadRecord> FilteredItems => store.Search(searchText);

    public static bool FileExists(DownloadRecord record) => record.DestinationPath is { } path && File.Exists(path);

    // ---- Loading and saving ----

    /// <summary>
    /// Reads the history on a background thread and merges it with whatever started meanwhile.
    /// Called once, off the startup path.
    /// </summary>
    public async Task LoadAsync()
    {
        if (loaded) return;
        loaded = true;
        var path = store.FilePath;
        var history = await Task.Run(() => DownloadHistoryStore.Read(path));
        var current = store.Records.ToList();
        var ids = current.Select(record => record.Id).ToHashSet();
        store.Replace(current.Concat(history.Where(record => !ids.Contains(record.Id))));
        Publish();
    }

    /// <summary>Writes the history now and waits for it (shutdown).</summary>
    public void Flush()
    {
        saveTimer?.Dispose();
        saveTimer = null;
        if (loaded) WriteHistory();
        lastWrite.Wait(TimeSpan.FromSeconds(3));
        Settings.Flush();
    }

    internal void Insert(DownloadRecord record)
    {
        store.Upsert(record);
        Publish();
        SaveSoon(immediately: true);
    }

    /// <summary>Changes a record; state changes are saved at once, progress every few seconds.</summary>
    internal void Update(Guid id, Action<DownloadRecord> change)
    {
        if (store.Record(id) is not { } before) return;
        var record = before with { };
        change(record);
        if (record == before) return;
        store.Upsert(record);
        Publish();
        SaveSoon(immediately: record.State != before.State || record.DestinationPath != before.DestinationPath);
    }

    internal void DeleteRecord(Guid id)
    {
        store.Remove(id);
        Publish();
        SaveSoon(immediately: true);
    }

    private void Publish()
    {
        // Keep the observable list in step with the store, reusing positions where possible.
        var records = store.Records;
        for (var index = 0; index < records.Count; index++)
        {
            if (index < Items.Count && Items[index].Id == records[index].Id)
            {
                if (!ReferenceEquals(Items[index], records[index])) Items[index] = records[index];
                continue;
            }
            var existing = -1;
            for (var other = index + 1; other < Items.Count; other++)
            {
                if (Items[other].Id == records[index].Id) { existing = other; break; }
            }
            if (existing >= 0)
            {
                Items.Move(existing, index);
                if (!ReferenceEquals(Items[index], records[index])) Items[index] = records[index];
            }
            else
            {
                Items.Insert(index, records[index]);
            }
        }
        while (Items.Count > records.Count) Items.RemoveAt(Items.Count - 1);
        Notify(nameof(ActiveCount));
        Notify(nameof(OverallProgress));
        Notify(nameof(FilteredItems));
        Shell.ShowProgress(ActiveCount, OverallProgress);
        Changed?.Invoke();
    }

    private void SaveSoon(bool immediately)
    {
        if (!loaded)
        {
            // Never write before the history has been read: that would replace it.
            return;
        }
        if (immediately)
        {
            saveTimer?.Dispose();
            saveTimer = null;
            WriteHistory();
            return;
        }
        if (saveTimer is not null) return;
        var context = SynchronizationContext.Current;
        saveTimer = time.CreateTimer(_ =>
        {
            void Save()
            {
                saveTimer?.Dispose();
                saveTimer = null;
                WriteHistory();
            }
            if (context is null) Save();
            else context.Post(_ => Save(), null);
        }, null, ProgressSaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void WriteHistory()
    {
        var text = store.Serialize();
        var path = store.FilePath;
        var previous = lastWrite;
        lastWrite = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            lock (writeLock)
            {
                try
                {
                    DownloadHistoryStore.Write(path, text);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
    }

    // ---- Settings ----

    private void EngineSettingsChanged()
    {
        if (gecko.Status == GeckoStatus.Running) _ = ConfigureGeckoAsync();
    }

    /// <summary>Creates the download folder if needed; falls back to the default folder if it cannot.</summary>
    public string EnsureFolder()
    {
        var folder = Settings.FolderPath;
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var fallback = DownloadSettings.DefaultFolder;
            try { Directory.CreateDirectory(fallback); }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return fallback;
        }
    }

    // ---- Deciding how a download starts ----

    internal abstract record StartChoice
    {
        /// <summary>Download it; to this file, or to the default place when null.</summary>
        public sealed record Save(string? Path) : StartChoice;
        public sealed record Cancel : StartChoice;
        public sealed record HandOff : StartChoice;
        public sealed record OpenExisting(DownloadRecord Existing) : StartChoice;
    }

    /// <summary>
    /// The questions asked before a download goes ahead: Free Download Manager (per the settings),
    /// duplicate download, where to save. Retries ask nothing.
    /// </summary>
    internal async Task<StartChoice> ChooseStartAsync(Guid id, bool isRetry)
    {
        if (Record(id) is not { } record) return new StartChoice.Cancel();
        if (isRetry) return new StartChoice.Save(null);

        Fdm.Refresh();
        long? size = record.TotalBytes >= 0 ? record.TotalBytes : null;
        switch (Settings.FdmPolicy.Decide(record.Url, size, Fdm.IsInstalled))
        {
            case FdmDecision.SendToFdm:
                return new StartChoice.HandOff();
            case FdmDecision.AskUser:
                var sizeText = size is { } bytes ? $" ({DownloadFormat.Bytes(bytes)})" : "";
                var answer = await Prompter.AskAsync(
                    "fdm", $"Download “{record.FileName}” with Free Download Manager?",
                    $"From {record.Host ?? record.Url}{sizeText}. Free Download Manager will ask you to confirm in its own window.",
                    ["Use Free Download Manager", "Download in Mosaic"]);
                if (answer == 0) return new StartChoice.HandOff();
                break;
        }

        if (store.CompletedDownload(record.Url, id) is { } existing)
        {
            var folder = existing.DestinationPath is { } path ? Path.GetDirectoryName(path) ?? path : "your download folder";
            var when = existing.EndedAt is { } ended ? $" on {ended.ToLocalTime():g}" : "";
            var answer = await Prompter.AskAsync(
                "duplicate", $"You already downloaded “{existing.FileName}”",
                $"The file from this address is in {folder} (downloaded{when}). Download it again?",
                ["Open Existing File", "Download Again", "Cancel"]);
            switch (answer)
            {
                case 0: return new StartChoice.OpenExisting(existing);
                case 1 or null: break;
                default: return new StartChoice.Cancel();
            }
        }

        if (Settings.AskWhereToSave)
        {
            var directory = Settings.Rules.Directory(record.FileName, record.MimeType, EnsureFolder());
            var chosen = await Prompter.ChooseDestinationAsync(record.FileName, directory);
            return chosen is null ? new StartChoice.Cancel() : new StartChoice.Save(chosen);
        }
        return new StartChoice.Save(null);
    }

    /// <summary>
    /// Where a file goes by default: the download folder, or its type's subfolder, under a name not
    /// used by an existing file or another running download.
    /// </summary>
    public string DefaultDestination(string fileName, string? mimeType)
    {
        var directory = Settings.Rules.Directory(fileName, mimeType, EnsureFolder());
        try { Directory.CreateDirectory(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { directory = EnsureFolder(); }
        return UniquePath(directory, fileName);
    }

    internal string UniquePath(string directory, string name)
    {
        var free = DownloadFileName.Unique(DownloadFileName.Sanitize(name), candidate =>
        {
            var path = Path.Combine(directory, candidate);
            return File.Exists(path) || Directory.Exists(path) || reservedPaths.Contains(path);
        });
        return Path.Combine(directory, free);
    }

    // ---- Finishing ----

    /// <summary>
    /// A download completed: put the file where it belongs, mark it with the Mark of the Web, run
    /// the safety check, announce it, and open it if the user wants safe files opened.
    /// </summary>
    internal void FinishCompleted(Guid id)
    {
        if (finishedIds.Contains(id) || undecided.Contains(id) || Record(id) is not { } record) return;
        finishedIds.Add(id);
        if (record.DestinationPath is { } reserved) reservedPaths.Remove(reserved);

        if (record.Engine == DownloadEngine.Gecko)
        {
            RelocateGeckoFile(id);
            if (GeckoId(id) is { } geckoId)
            {
                // Mosaic keeps the history from here on; Gecko's own list stays short.
                geckoLinks.Remove(geckoId);
                ignoredGeckoIds.Add(geckoId);
                gecko.Send("downloadAction", geckoId, "remove");
            }
        }

        if (Record(id) is not { } current || current.DestinationPath is not { } path || !File.Exists(path)) return;
        var mark = MarkOfTheWeb.Ensure(path, current.Url, current.Referrer);
        LastMarkOfTheWeb = $"{mark}:{path}";
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { size = current.TotalBytes; }
        Update(id, r =>
        {
            r.EndedAt ??= time.GetUtcNow();
            r.Speed = 0;
            r.CanResume = false;
            if (size > 0)
            {
                r.TotalBytes = size;
                r.ReceivedBytes = size;
            }
        });
        if (!IsPopoverShown) HasUnseenCompletion = true;

        if (Settings.NotifyWhenFinished)
        {
            var warning = current.NeedsSafetyConfirmation ? " Check the warning before opening it." : "";
            if (Shell.IsAppActive)
            {
                if (IsPageShown?.Invoke() != true) ShowNotice?.Invoke($"Downloaded “{current.FileName}”");
            }
            else
            {
                var body = $"“{current.FileName}”.{warning}";
                LastNotification = $"Download finished: {body}";
                Shell.ShowNotification("Download finished", body, id);
            }
        }

        if (Settings.OpenSafeFiles && !current.NeedsSafetyConfirmation &&
            DownloadSafety.IsSafeToOpenAutomatically(current.FileName))
        {
            LastAutoOpen = path;
            if (!Shell.IsScripted) Shell.OpenFile(path);
        }
    }

    /// <summary>
    /// Moves a completed Gecko file to the place chosen in "Save As", or to its type's subfolder.
    /// Gecko itself always saves into the download folder.
    /// </summary>
    private void RelocateGeckoFile(Guid id)
    {
        if (Record(id) is not { DestinationPath: { } current }) return;
        string target;
        if (geckoDestinations.Remove(id, out var chosen))
        {
            target = chosen;
        }
        else
        {
            var directory = Settings.Rules.Directory(Path.GetFileName(current), Record(id)!.MimeType, Settings.FolderPath);
            if (SamePath(directory, Path.GetDirectoryName(current) ?? "")) return;
            target = UniquePath(directory, Path.GetFileName(current));
        }
        if (SamePath(target, current)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // "Save As" already asked whether to replace it.
            File.Move(current, target, overwrite: true);
            Update(id, r =>
            {
                r.DestinationPath = target;
                r.FileName = Path.GetFileName(target);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Update(id, r => r.ErrorMessage = $"Saved in {Settings.FolderDisplayPath}: could not move it ({error.Message})");
        }
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- Actions ----

    public void Pause(Guid id)
    {
        if (Record(id) is not { State: DownloadState.InProgress } record) return;
        switch (record.Engine)
        {
            case DownloadEngine.Chromium:
                ChromiumLink(id)?.Pause();
                break;
            case DownloadEngine.Gecko:
                if (GeckoId(id) is { } link) gecko.Send("downloadAction", link, "pause");
                break;
        }
    }

    public void Resume(Guid id)
    {
        if (Record(id) is not { State: DownloadState.Paused } record) return;
        switch (record.Engine)
        {
            case DownloadEngine.Chromium:
                if (ChromiumLink(id) is { CanResume: true } link) link.Resume();
                else Retry(id);
                break;
            case DownloadEngine.Gecko:
                if (GeckoId(id) is { } geckoId) gecko.Send("downloadAction", geckoId, "resume");
                else Retry(id);
                break;
        }
    }

    /// <summary>Stops the download and removes its partial data; it stays listed and can be retried.</summary>
    public void Cancel(Guid id)
    {
        if (Record(id) is not { } record) return;
        void MarkCancelled()
        {
            if (record.State.IsActive()) Update(id, r => { r.State = DownloadState.Cancelled; r.Speed = 0; r.EndedAt = time.GetUtcNow(); });
        }
        switch (record.Engine)
        {
            case DownloadEngine.Chromium:
                if (ChromiumLink(id) is { } link && link.State is not (ChromiumDownloadState.Completed or ChromiumDownloadState.Cancelled))
                {
                    link.Cancel();
                    // WebView2 reports the cancellation as an interruption; say what the user did.
                    MarkCancelled();
                }
                else
                {
                    MarkCancelled();
                }
                break;
            case DownloadEngine.Gecko:
                if (GeckoId(id) is { } geckoId) gecko.Send("downloadAction", geckoId, "cancel");
                else MarkCancelled();
                break;
        }
    }

    /// <summary>
    /// Starts a failed or cancelled download again (Chromium resumes an interrupted one where the
    /// server allows it).
    /// </summary>
    public void Retry(Guid id)
    {
        if (Record(id) is not { } record || record.State == DownloadState.InProgress) return;
        finishedIds.Remove(id);
        switch (record.Engine)
        {
            case DownloadEngine.Chromium:
                if (ChromiumLink(id) is { CanResume: true } link)
                {
                    Update(id, r => { r.State = DownloadState.InProgress; r.ErrorMessage = null; r.EndedAt = null; });
                    link.Resume();
                }
                else
                {
                    StartInChromium(record.Url, id);
                }
                break;
            case DownloadEngine.Gecko:
                if (GeckoId(id) is { } geckoId)
                {
                    Update(id, r => { r.State = DownloadState.InProgress; r.ErrorMessage = null; r.EndedAt = null; });
                    gecko.Send("downloadAction", geckoId, "retry");
                }
                else
                {
                    StartInGecko(record.Url, id);
                }
                break;
            case DownloadEngine.Fdm:
                var engine = fdmSourceEngines.GetValueOrDefault(id) ?? CurrentEngine?.Invoke() ?? EngineKind.Gecko;
                if (engine == EngineKind.Chromium) StartInChromium(record.Url, id);
                else StartInGecko(record.Url, id);
                break;
        }
    }

    /// <summary>
    /// Opens the file, after a confirmation if its type or name deserves a warning. Windows checks
    /// it too (SmartScreen, Attachment Manager), because it carries the Mark of the Web.
    /// </summary>
    public async Task OpenAsync(Guid id)
    {
        if (Record(id) is not { State: DownloadState.Completed } record) return;
        if (record.DestinationPath is not { } path || !File.Exists(path))
        {
            ShowNotice?.Invoke($"“{record.FileName}” was moved or deleted.");
            return;
        }
        if (record.NeedsSafetyConfirmation)
        {
            var summary = record.Assessment.Summary ?? "";
            var answer = await Prompter.AskAsync(
                "safety", $"Open “{record.FileName}”?",
                $"{summary} Open it only if you trust {record.Host ?? "its source"}.",
                ["Open", "Cancel"], isWarning: true);
            if (answer != 0) return;
            Update(id, r => r.WarningAcknowledged = true);
        }
        LastOpenRequest = path;
        if (!Shell.IsScripted) Shell.OpenFile(path);
    }

    public void Open(Guid id) => _ = OpenAsync(id);

    /// <summary>Shows the file in File Explorer (its folder when the file has gone).</summary>
    public void Reveal(Guid id)
    {
        if (Record(id) is not { DestinationPath: { } path }) return;
        var exists = File.Exists(path);
        var target = exists ? path : Path.GetDirectoryName(path) ?? path;
        LastOpenRequest = "reveal:" + target;
        if (Shell.IsScripted) return;
        if (exists) Shell.RevealFile(target);
        else Shell.OpenFolder(target);
    }

    /// <summary>The user read the warning and keeps the file.</summary>
    public void Keep(Guid id) => Update(id, r => r.WarningAcknowledged = true);

    /// <summary>Moves the file to the Recycle Bin and removes it from the list.</summary>
    public void Trash(Guid id)
    {
        if (Record(id) is not { } record) return;
        if (record.State.IsActive()) Cancel(id);
        if (record.DestinationPath is { } path && File.Exists(path)) Shell.MoveToRecycleBin(path);
        Remove(id);
    }

    /// <summary>Removes the entry from the list (the file stays). A running download is cancelled.</summary>
    public void Remove(Guid id)
    {
        if (Record(id) is not { } record) return;
        if (record.State.IsActive()) Cancel(id);
        if (GeckoId(id) is { } geckoId)
        {
            geckoLinks.Remove(geckoId);
            ignoredGeckoIds.Add(geckoId);
            gecko.Send("downloadAction", geckoId, "remove");
        }
        if (ChromiumLink(id) is { } link) chromiumLinks.Remove(link);
        if (record.DestinationPath is { } path) reservedPaths.Remove(path);
        undecided.Remove(id);
        DeleteRecord(id);
    }

    /// <summary>Removes every finished entry; running and paused downloads stay.</summary>
    public void ClearFinished()
    {
        foreach (var record in store.Records.Where(record => !record.State.IsActive()).ToList())
        {
            if (GeckoId(record.Id) is { } geckoId)
            {
                geckoLinks.Remove(geckoId);
                ignoredGeckoIds.Add(geckoId);
                gecko.Send("downloadAction", geckoId, "remove");
            }
            if (ChromiumLink(record.Id) is { } link) chromiumLinks.Remove(link);
        }
        store.ClearFinished();
        Publish();
        SaveSoon(immediately: true);
    }

    /// <summary>
    /// Settings › Privacy › Clear browsing data: removes finished entries started since
    /// <paramref name="since"/> (all of them when null). Files on disk are never touched; running
    /// and paused downloads stay.
    /// </summary>
    public void ClearHistory(DateTimeOffset? since)
    {
        if (since is null)
        {
            ClearFinished();
            return;
        }
        foreach (var record in store.Records.Where(record => !record.State.IsActive() && record.StartedAt >= since).ToList())
        {
            Remove(record.Id);
        }
    }

    public void CopyLink(Guid id)
    {
        if (Record(id) is { } record) Shell.CopyText(record.Url);
    }

    public void OpenFolder()
    {
        var folder = EnsureFolder();
        LastOpenRequest = "folder:" + folder;
        if (!Shell.IsScripted) Shell.OpenFolder(folder);
    }

    public void OpenDownloadsPage()
    {
        IsPopoverShown = false;
        OpenPage?.Invoke();
    }

    internal IChromiumDownload? ChromiumLink(Guid id) =>
        chromiumLinks.FirstOrDefault(pair => pair.Value == id).Key;

    internal int? GeckoId(Guid id) =>
        geckoLinks.FirstOrDefault(pair => pair.Value == id) is { Value: var value, Key: var key } && value == id ? key : null;

    // ---- Free Download Manager ----

    /// <summary>
    /// "Download with Free Download Manager" for a listed download. A running download is stopped
    /// in Mosaic first; a completed one is listed again as a new entry.
    /// </summary>
    public void SendToFdm(Guid id)
    {
        if (Record(id) is not { } record || !Fdm.IsInstalled) return;
        EngineKind? source = record.Engine switch
        {
            DownloadEngine.Chromium => EngineKind.Chromium,
            DownloadEngine.Gecko => EngineKind.Gecko,
            _ => null,
        };
        var target = id;
        if (record.State == DownloadState.Completed)
        {
            var copy = new DownloadRecord
            {
                Url = record.Url, OriginalUrl = record.OriginalUrl, Referrer = record.Referrer,
                FileName = record.FileName, SuggestedName = record.SuggestedName, Engine = DownloadEngine.Fdm,
                MimeType = record.MimeType, TotalBytes = record.TotalBytes, State = DownloadState.HandedOff,
                StartedAt = time.GetUtcNow(),
            };
            Insert(copy);
            target = copy.Id;
        }
        else
        {
            if (record.State.IsActive())
            {
                if (ChromiumLink(id) is { } link)
                {
                    chromiumLinks.Remove(link);
                    link.Cancel();
                }
                if (GeckoId(id) is { } geckoId)
                {
                    geckoLinks.Remove(geckoId);
                    ignoredGeckoIds.Add(geckoId);
                    gecko.Send("downloadAction", geckoId, "remove");
                }
                if (record.DestinationPath is { } path) reservedPaths.Remove(path);
            }
            Update(id, r => { r.ReceivedBytes = 0; r.DestinationPath = null; });
        }
        _ = HandOffAsync(target, source);
    }

    /// <summary>A link's context-menu item, in either engine.</summary>
    public void SendLinkToFdm(string url, string? referrer, EngineKind engine)
    {
        if (!Fdm.IsInstalled || !Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        var record = new DownloadRecord
        {
            Url = url, Referrer = referrer, FileName = DownloadFileName.Suggested(url),
            Engine = DownloadEngine.Fdm, State = DownloadState.HandedOff, StartedAt = time.GetUtcNow(),
        };
        Insert(record);
        _ = HandOffAsync(record.Id, engine);
    }

    private const string SendingMessage = "Sending to Free Download Manager…";

    /// <summary>Gives the download to FDM. The record shows what happened, including FDM's answer.</summary>
    internal async Task HandOffAsync(Guid id, EngineKind? engine)
    {
        if (Record(id) is not { } record) return;
        fdmSourceEngines[id] = engine;
        Update(id, r =>
        {
            r.Engine = DownloadEngine.Fdm;
            r.State = DownloadState.HandedOff;
            r.Speed = 0;
            r.ErrorMessage = SendingMessage;
            r.EndedAt = time.GetUtcNow();
        });
        var (userAgent, cookies) = await RequestInfoAsync(
            record.Url, engine ?? CurrentEngine?.Invoke() ?? EngineKind.Chromium, Settings.FdmPassCookies);
        var request = new FdmDownloadRequest(record.Url, record.OriginalUrl, record.Referrer, userAgent, cookies);
        try
        {
            var route = await Fdm.SendAsync(request, bringToFront: !Shell.IsScripted, outcome => FdmAnswered(id, outcome));
            LastFdmRoute = route == FdmRoute.Integration ? "integration" : "openURL";
            Update(id, r =>
            {
                if (r.State != DownloadState.HandedOff || r.ErrorMessage != SendingMessage) return;
                r.ErrorMessage = route == FdmRoute.Integration
                    ? "Sent to Free Download Manager · confirm it there"
                    : "Opened in Free Download Manager (address only)";
            });
        }
        catch (Exception error)
        {
            Update(id, r =>
            {
                r.State = DownloadState.Failed;
                r.ErrorMessage = error.Message;
            });
        }
    }

    private void FdmAnswered(Guid id, FdmOutcome outcome) => Update(id, r =>
    {
        switch (outcome)
        {
            case FdmOutcome.Accepted:
                r.State = DownloadState.HandedOff;
                r.ErrorMessage = "Downloading in Free Download Manager";
                break;
            case FdmOutcome.CancelledByUser:
                r.State = DownloadState.Cancelled;
                r.ErrorMessage = "Cancelled in Free Download Manager";
                break;
            case FdmOutcome.Failed failed:
                r.State = DownloadState.Failed;
                r.ErrorMessage = $"Free Download Manager: {failed.Message}";
                break;
        }
    });

    /// <summary>What FDM needs to fetch the file as the engine would.</summary>
    private async Task<(string? UserAgent, string? Cookies)> RequestInfoAsync(string url, EngineKind engine, bool includeCookies)
    {
        if (engine == EngineKind.Chromium && Chromium is { } chromium)
        {
            if (!includeCookies) return (chromium.UserAgent, null);
            string? cookies = null;
            try { cookies = await chromium.CookiesAsync(url); }
            catch (Exception) { }
            return (chromium.UserAgent, string.IsNullOrEmpty(cookies) ? null : cookies);
        }
        if (gecko.Status != GeckoStatus.Running) return (null, null);
        try
        {
            if (await gecko.CallAsync("downloadRequestInfo", url, includeCookies) is not System.Text.Json.Nodes.JsonObject info)
            {
                return (null, null);
            }
            var agent = info["userAgent"]?.GetValue<string>();
            var cookies = info["cookies"]?.GetValue<string>();
            return (agent, string.IsNullOrEmpty(cookies) ? null : cookies);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }
}
