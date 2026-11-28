using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Mosaic.Core.Browser;
using Mosaic.Core.Search;

namespace Mosaic.Core.Library;

/// <summary>What a row in the address bar's suggestion list is.</summary>
public enum SuggestionKind { History, Bookmark, Search, OpenTab }

/// <summary>One row in the address bar's suggestion list.</summary>
/// <param name="Title">First line: the page title, or the search terms.</param>
/// <param name="Value">What to load, or the search terms for <see cref="SuggestionKind.Search"/>.</param>
/// <param name="Kind">History, bookmark, search or open tab.</param>
public sealed record LibrarySuggestion(string Title, string Value, SuggestionKind Kind)
{
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>Secondary line: the address, or "Search Google", "Switch to tab"…</summary>
    public string Detail { get; init; } = "";
    /// <summary>The exact address this row opens (for a search, the results page).</summary>
    public string? Url { get; init; }
    /// <summary><see cref="SuggestionKind.OpenTab"/> only: the tab to switch to.</summary>
    public Guid? TabId { get; init; }
    /// <summary>The address is bookmarked (the address field may show a star).</summary>
    public bool IsBookmarked { get; init; }
    /// <summary>Came from the search engine's suggestion service (over the network).</summary>
    public bool IsRemote { get; init; }
}

/// <summary>The ranges the History page and the clear-history menu offer.</summary>
public enum LibraryClearRange { LastHour, Today, All }

/// <summary>File formats bookmarks can be exported to.</summary>
public enum BookmarkFileFormat
{
    /// <summary>The Netscape bookmark file every browser imports (<c>.html</c>).</summary>
    NetscapeHtml,
    /// <summary>Chrome's <c>Bookmarks</c> JSON.</summary>
    ChromeJson,
}

/// <summary>How <see cref="BrowserLibrary"/> is set up; the defaults are the real ones.</summary>
public sealed class LibraryOptions
{
    /// <summary>The database file; null keeps everything in memory (tests).</summary>
    public string? DatabasePath { get; init; }

    /// <summary>Answers remote search-suggestion requests (tests); null uses the network.</summary>
    public HttpMessageHandler? SuggestionHandler { get; init; }

    /// <summary>
    /// Where the default options put the database: <c>Library.sqlite</c> in
    /// <see cref="AppPaths.DataRoot"/>. Tests replace this so they never touch a real profile;
    /// returning null means in memory.
    /// </summary>
    public static Func<string?> DefaultDatabasePath { get; set; } = () => Path.Combine(AppPaths.DataRoot, LibraryStore.FileName);
}

/// <summary>
/// Bookmarks, history, the reading list, custom search engines and the address bar's
/// suggestions, for one <see cref="BrowserModel"/> (<c>model.Library</c>).
/// </summary>
/// <remarks>
/// <para>Port of <c>Sources/Mosaic/Features/Library/Library.swift</c>.</para>
/// <para>
/// Threading: create and use it on the UI thread. Its state (<see cref="Bookmarks"/>,
/// <see cref="ReadingList"/>, <see cref="CustomEngines"/>, counts) is an in-memory copy, so the
/// synchronous members (<see cref="IsBookmarked"/>, <see cref="Suggestions"/>,
/// <see cref="Children"/>…) never touch the database. Everything that does runs on one
/// background queue, in order; the <c>…Async</c> methods resume on the caller's context and
/// update the in-memory state there.
/// </para>
/// <para>
/// Lazy: constructing it does no I/O. The database is opened on the background queue the first
/// time something needs it (<see cref="EnsureLoaded"/>: the address field's first keystroke,
/// the first visit recorded, a library page, the bookmarks bar after the window is shown).
/// </para>
/// </remarks>
public sealed class BrowserLibrary : ObservableObject
{
    /// <summary>The engine the database starts with (as on macOS), assumed until it is read.</summary>
    internal static readonly CustomSearchEngine SeedWikipedia = new()
    {
        Id = "custom.wikipedia", Name = "Wikipedia", Keyword = "w",
        Template = "https://en.wikipedia.org/wiki/Special:Search?search=%s",
        SuggestTemplate = "https://en.wikipedia.org/w/api.php?action=opensearch&limit=5&search=%s",
    };

    private const string ShowsBarKey = "pref.showsBookmarksBar";
    private const string RemoteSuggestionsKey = "pref.remoteSearchSuggestions";
    private const string LastFolderKey = "pref.lastBookmarkFolder";
    private const int HistoryIndexSize = 4000;

    private static readonly Func<BrowserTab, bool> TabPrivacyProperty = FindTabPrivacyProperty();

    private readonly BrowserModel model;
    private readonly SynchronizationContext? ui;
    private readonly TimeProvider time;
    private readonly string? databasePath;
    private readonly HttpMessageHandler? suggestionHandler;
    private readonly HistoryRecorder recorder;
    private readonly Lock queueGate = new();
    private readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task tail = Task.CompletedTask;
    private LibraryStore? store;
    private string? openProblem;
    private bool loadStarted;
    private bool indexRefreshPending;
    private int observationCount;
    private HttpClient? http;

    private bool isLoaded;
    private string? storageProblem;
    private IReadOnlyList<Bookmark> bookmarks = [];
    private HashSet<string> bookmarkedKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<ReadingListItem> readingList = [];
    private IReadOnlyList<CustomSearchEngine> customEngines = [SeedWikipedia];
    private IReadOnlyList<SuggestionRanker.Candidate> historyIndex = [];
    private HistoryCounts historyCounts;
    private int historyRevision;
    private bool showsBookmarksBar = true;
    private bool remoteSuggestionsEnabled;
    private string defaultFolderId = Bookmark.BarId;
    private string? lastError;

    public BrowserLibrary(BrowserModel model, LibraryOptions? options = null, TimeProvider? time = null)
    {
        this.model = model;
        ui = SynchronizationContext.Current;
        this.time = time ?? TimeProvider.System;
        databasePath = options is null ? LibraryOptions.DefaultDatabasePath() : options.DatabasePath;
        suggestionHandler = options?.SuggestionHandler;
        recorder = new HistoryRecorder(CommitVisit, Retitle, this.time);
        SearchEngine.Registered = [.. customEngines.Select(engine => engine.SearchEngine)];
    }

    public BrowserModel Model => model;

    /// <summary>The visit recorder (its delays can be shortened for tests).</summary>
    public HistoryRecorder Recorder => recorder;

    /// <summary>
    /// Asked for every tab before recording history and before sending typed text to a
    /// suggestion service; the privacy area sets it (<c>library.IsPrivateTab = tab => tab.IsPrivate</c>).
    /// Until then a public <c>bool IsPrivate</c> on <see cref="BrowserTab"/> is honoured if one exists.
    /// </summary>
    public Func<BrowserTab, bool> IsPrivateTab { get; set; } = TabPrivacyProperty;

    // ---- State (UI thread) ----

    /// <summary>True once the database has been read (or failed over to memory).</summary>
    public bool IsLoaded { get => isLoaded; private set => Set(ref isLoaded, value); }

    /// <summary>Completes once <see cref="IsLoaded"/> is true. Starts loading.</summary>
    public Task WhenLoaded
    {
        get
        {
            EnsureLoaded();
            return loaded.Task;
        }
    }

    /// <summary>Set when the database could not be opened; history and bookmarks then last only until quit.</summary>
    public string? StorageProblem { get => storageProblem; private set => Set(ref storageProblem, value); }

    /// <summary>Every bookmark and folder.</summary>
    public IReadOnlyList<Bookmark> Bookmarks { get => bookmarks; private set => Set(ref bookmarks, value); }

    public IReadOnlyList<ReadingListItem> ReadingList { get => readingList; private set => Set(ref readingList, value); }

    public IReadOnlyList<CustomSearchEngine> CustomEngines { get => customEngines; private set => Set(ref customEngines, value); }

    /// <summary>Bumped whenever history changes, so pages showing it reload.</summary>
    public int HistoryRevision { get => historyRevision; private set => Set(ref historyRevision, value); }

    public HistoryCounts HistoryCounts { get => historyCounts; private set => Set(ref historyCounts, value); }

    /// <summary>The last failure of a library change (also shown as the window's notice).</summary>
    public string? LastError { get => lastError; private set => Set(ref lastError, value); }

    /// <summary>Whether the bookmarks bar is shown (the bar component draws nothing when false).</summary>
    public bool ShowsBookmarksBar
    {
        get => showsBookmarksBar;
        set
        {
            if (!Set(ref showsBookmarksBar, value)) return;
            SavePreference(ShowsBarKey, value ? "1" : "0");
        }
    }

    /// <summary>
    /// Network search suggestions from the default engine. Off by default: when on, what is typed
    /// in the address bar is sent to the search engine as it is typed.
    /// </summary>
    public bool RemoteSuggestionsEnabled
    {
        get => remoteSuggestionsEnabled;
        set
        {
            if (!Set(ref remoteSuggestionsEnabled, value)) return;
            SavePreference(RemoteSuggestionsKey, value ? "1" : "0");
        }
    }

    /// <summary>The folder new bookmarks go to: the one used last, or the bookmarks bar.</summary>
    public string DefaultFolderId
    {
        get => BookmarkById(defaultFolderId)?.IsFolder == true ? defaultFolderId : Bookmark.BarId;
        set
        {
            if (!Set(ref defaultFolderId, value)) return;
            SavePreference(LastFolderKey, value);
        }
    }

    /// <summary>Starts reading the database in the background (once). Cheap to call often.</summary>
    public void EnsureLoaded()
    {
        if (loadStarted) return;
        loadStarted = true;
        _ = LoadAsync();
    }

    private sealed record Initial(
        LibraryStore Store, IReadOnlyList<Bookmark> Bookmarks, IReadOnlyList<ReadingListItem> ReadingList,
        IReadOnlyList<CustomSearchEngine> Engines, IReadOnlyList<HistoryEntry> TopHistory, HistoryCounts Counts,
        string? ShowsBar, string? Remote, string? LastFolder, DateTimeOffset? FrecencyComputed, string? Problem);

    private async Task LoadAsync()
    {
        try
        {
            var initial = await Enqueue(store => new Initial(
                store, store.AllBookmarks(), store.ReadingList(), store.CustomEngines(), store.TopHistory(HistoryIndexSize),
                store.HistoryCounts(), store.Meta(ShowsBarKey), store.Meta(RemoteSuggestionsKey), store.Meta(LastFolderKey),
                store.FrecencyComputed, openProblem)).ConfigureAwait(false);
            OnUi(() =>
            {
                StorageProblem = initial.Problem;
                ApplyBookmarks(initial.Bookmarks);
                ReadingList = initial.ReadingList;
                ApplyEngines(initial.Engines);
                historyIndex = Index(initial.TopHistory);
                HistoryCounts = initial.Counts;
                // Preferences read from the file win over the defaults, but not over a change
                // made (for example by automation) while the file was being read.
                if (initial.ShowsBar is { } bar) Set(ref showsBookmarksBar, bar != "0", nameof(ShowsBookmarksBar));
                if (initial.Remote is { } remote) Set(ref remoteSuggestionsEnabled, remote == "1", nameof(RemoteSuggestionsEnabled));
                if (initial.LastFolder is { } folder) Set(ref defaultFolderId, folder, nameof(DefaultFolderId));
                RestoreCustomDefaultEngine();
                IsLoaded = true;
                loaded.TrySetResult();
            });
            if (initial.FrecencyComputed is not { } computed || time.GetUtcNow() - computed > TimeSpan.FromDays(1))
            {
                // Long, in slices under the store's lock; not on the queue, so changes are not held up.
                _ = Task.Run(() => initial.Store.RecomputeFrecency(time.GetUtcNow()));
            }
        }
        catch (Exception error)
        {
            OnUi(() =>
            {
                StorageProblem = error.Message;
                IsLoaded = true;
                loaded.TrySetResult();
            });
        }
    }

    /// <summary>The user's default search engine is one of their own: it can be chosen once the library knows it.</summary>
    private void RestoreCustomDefaultEngine()
    {
        if (model.LibrarySavedSearchEngineId is not { } id || id == model.SearchEngine.Id) return;
        if (SearchEngine.Registered.FirstOrDefault(engine => engine.Id == id) is { } custom) model.SearchEngine = custom;
    }

    // ---- The background queue ----

    /// <summary>Runs <paramref name="work"/> on the library's queue, after everything queued before it.</summary>
    private Task<T> Enqueue<T>(Func<LibraryStore, T> work)
    {
        lock (queueGate)
        {
            var next = tail.ContinueWith(_ => work(OpenStore()), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            tail = next;
            return next;
        }
    }

    /// <summary>Queue thread only.</summary>
    private LibraryStore OpenStore()
    {
        if (store is not null) return store;
        try
        {
            store = new LibraryStore(databasePath, time);
        }
        catch (Exception error) when (error is LibraryException or SqliteException or IOException or UnauthorizedAccessException)
        {
            openProblem = $"Mosaic could not open its library database ({error.Message}). Bookmarks and history will not be kept after you quit.";
            // An in-memory database cannot fail to open.
            store = new LibraryStore(null, time);
        }
        return store;
    }

    [Flags]
    private enum Reload { None = 0, Bookmarks = 1, ReadingList = 2, Engines = 4, History = 8 }

    private sealed record Snapshot(
        IReadOnlyList<Bookmark>? Bookmarks, IReadOnlyList<ReadingListItem>? ReadingList,
        IReadOnlyList<CustomSearchEngine>? Engines, HistoryCounts? Counts);

    private static Snapshot Take(LibraryStore store, Reload reload) => new(
        reload.HasFlag(Reload.Bookmarks) ? store.AllBookmarks() : null,
        reload.HasFlag(Reload.ReadingList) ? store.ReadingList() : null,
        reload.HasFlag(Reload.Engines) ? store.CustomEngines() : null,
        reload.HasFlag(Reload.History) ? store.HistoryCounts() : null);

    /// <summary>
    /// Runs a change against the store on the queue, refreshes the affected in-memory state, and
    /// reports a failure in the window's notice (returning default).
    /// </summary>
    private async Task<T?> Perform<T>(Reload reload, Func<LibraryStore, T> change)
    {
        EnsureLoaded();
        T result;
        Snapshot snapshot;
        try
        {
            (result, snapshot) = await Enqueue(store =>
            {
                try
                {
                    return (change(store), Take(store, reload));
                }
                catch (Exception error) when (error is LibraryException or SqliteException)
                {
                    throw new LibraryChangeFailed(error.Message, Take(store, reload));
                }
            });
        }
        catch (LibraryChangeFailed failure)
        {
            Apply(failure.Snapshot, reload);
            Fail(failure.Message);
            return default;
        }
        Apply(snapshot, reload);
        LastError = null;
        return result;
    }

    private Task Perform(Reload reload, Action<LibraryStore> change) =>
        Perform(reload, store => { change(store); return true; });

    private sealed class LibraryChangeFailed(string message, Snapshot snapshot) : Exception(message)
    {
        public Snapshot Snapshot { get; } = snapshot;
    }

    private void Apply(Snapshot snapshot, Reload reload) => OnUi(() =>
    {
        if (snapshot.Bookmarks is { } list) ApplyBookmarks(list);
        if (snapshot.ReadingList is { } items) ReadingList = items;
        if (snapshot.Engines is { } engines) ApplyEngines(engines);
        if (snapshot.Counts is { } counts) HistoryCounts = counts;
        if (reload.HasFlag(Reload.History)) HistoryChanged();
    });

    private void Fail(string message)
    {
        OnUi(() =>
        {
            LastError = message;
            model.Notice = $"Library: {message}";
        });
    }

    private void ApplyBookmarks(IReadOnlyList<Bookmark> list)
    {
        bookmarkedKeys = list.Where(b => b.Url is not null).Select(b => LibraryUrl.Normalized(b.Url!)).ToHashSet(StringComparer.Ordinal);
        Bookmarks = list;
        Notify(nameof(BookmarksBar));
    }

    private void ApplyEngines(IReadOnlyList<CustomSearchEngine> engines)
    {
        SearchEngine.Registered = [.. engines.Select(engine => engine.SearchEngine)];
        CustomEngines = engines;
        Notify(nameof(AllSearchEngines));
    }

    private void SavePreference(string key, string value)
    {
        EnsureLoaded();
        _ = Enqueue(store => { store.SetMeta(key, value); return true; });
    }

    /// <summary>Runs on the UI thread: now if already there, otherwise posted.</summary>
    private void OnUi(Action action)
    {
        if (ui is null || SynchronizationContext.Current == ui) action();
        else ui.Post(_ => action(), null);
    }

    // ---- Contract for the shell: suggestions ----

    /// <summary>
    /// Local suggestions for what has been typed so far, best first: a keyword or plain search
    /// row, history ranked by frecency, bookmarks and open tabs. Instant (in memory, no
    /// database work); see <see cref="SuggestionStream"/> for the complete list.
    /// </summary>
    public IReadOnlyList<LibrarySuggestion> Suggestions(string input)
    {
        EnsureLoaded();
        return Compose(input, [], []);
    }

    /// <summary>
    /// The address field's suggestions as they become available: the local list at once, then
    /// again with matches from all of history (searched off the UI thread), then again with the
    /// search engine's own suggestions when <see cref="RemoteSuggestionsEnabled"/> is on (never
    /// for a private tab). Cancel <paramref name="cancellation"/> when the text changes; the
    /// lookups and the network request stop and the sequence ends quietly.
    /// <code>
    /// cts?.Cancel(); cts = new();
    /// await foreach (var rows in model.Library.SuggestionStream(text, cts.Token)) list.ItemsSource = rows;
    /// </code>
    /// Resumes on the caller's context (call it from the UI thread).
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<LibrarySuggestion>> SuggestionStream(
        string input, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        EnsureLoaded();
        yield return Compose(input, [], []);
        var terms = input.Trim();
        if (terms.Length == 0 || cancellation.IsCancellationRequested) yield break;

        IReadOnlyList<HistoryEntry> deep;
        try
        {
            deep = await DeepHistoryAsync(terms, cancellation);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        if (cancellation.IsCancellationRequested) yield break;
        yield return Compose(input, deep, []);

        if (RemoteRequest(input) is not { } request) yield break;
        IReadOnlyList<string> remote;
        try
        {
            // A short pause, so fast typing does not send a request per keystroke.
            await Task.Delay(TimeSpan.FromMilliseconds(180), time, cancellation);
            remote = await FetchRemoteAsync(request, cancellation);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        if (cancellation.IsCancellationRequested || remote.Count == 0) yield break;
        yield return Compose(input, deep, remote);
    }

    private async Task<IReadOnlyList<HistoryEntry>> DeepHistoryAsync(string terms, CancellationToken cancellation)
    {
        // The store is thread-safe; a search does not wait behind queued writes.
        var opened = store ?? await Enqueue(store => store);
        cancellation.ThrowIfCancellationRequested();
        return await Task.Run(() => opened.SearchHistory(terms, 40), cancellation);
    }

    private sealed record SuggestRequest(string Url, SearchEngine Engine);

    private SuggestRequest? RemoteRequest(string input)
    {
        var text = input.Trim();
        if (!remoteSuggestionsEnabled || text.Length < 2 || AddressResolver.IsAddress(text)) return null;
        if (model.SelectedTab is { } tab && IsPrivateTab(tab)) return null;
        var engine = model.SearchEngine;
        var terms = text;
        if (SearchShortcuts.MatchKeyword(text, customEngines) is { } match)
        {
            engine = match.Engine;
            terms = match.Terms;
        }
        return SearchShortcuts.SuggestTemplate(engine, customEngines) is { } template
            && SearchShortcuts.SuggestUrl(template, terms) is { } url
            ? new SuggestRequest(url, engine)
            : null;
    }

    /// <summary>No cookies, no cache, no credentials: a suggestion request carries only the terms.</summary>
    private HttpClient Http => http ??= new HttpClient(
        suggestionHandler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.All,
        },
        disposeHandler: suggestionHandler is null)
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private async Task<IReadOnlyList<string>> FetchRemoteAsync(SuggestRequest request, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        message.Headers.Accept.ParseAdd("application/x-suggestions+json");
        message.Headers.Accept.ParseAdd("application/json");
        message.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        // A generic browser identity; some services answer nothing to unknown clients.
        message.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0");
        try
        {
            using var response = await Http.SendAsync(message, cancellation);
            if (response.StatusCode != HttpStatusCode.OK) return [];
            return SearchShortcuts.ParseSuggestions(await response.Content.ReadAsStringAsync(cancellation));
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return [];  // timed out
        }
    }

    /// <summary>
    /// Opens a suggestion: switches to the tab for an open-tab row, otherwise loads its address
    /// (in a new tab when asked).
    /// </summary>
    public void Open(LibrarySuggestion suggestion, bool inNewTab = false)
    {
        if (suggestion.Kind == SuggestionKind.OpenTab && suggestion.TabId is { } id
            && model.Tabs.FirstOrDefault(tab => tab.Id == id) is { } open)
        {
            model.Select(open);
            return;
        }
        var target = suggestion.Url ?? suggestion.Value;
        if (inNewTab) model.NewTab(target);
        else model.Navigate(target);
    }

    /// <summary>
    /// Builds the list: a keyword search or the plain search row, the best local matches
    /// (history, bookmarks, open tabs), then the search engine's suggestions.
    /// </summary>
    private IReadOnlyList<LibrarySuggestion> Compose(string input, IReadOnlyList<HistoryEntry> deepHistory, IReadOnlyList<string> remote)
    {
        var text = input.Trim();
        if (text.Length == 0) return [];
        var engine = model.SearchEngine;
        var rows = new List<LibrarySuggestion>();

        var keyword = SearchShortcuts.MatchKeyword(text, customEngines);
        if (keyword is not null)
        {
            rows.Add(new LibrarySuggestion(keyword.Terms, text, SuggestionKind.Search)
            {
                Detail = $"Search {keyword.Engine.Name}",
                Url = keyword.Engine.SearchUrl(keyword.Terms),
            });
        }

        var candidates = new List<SuggestionRanker.Candidate>(historyIndex);
        candidates.AddRange(deepHistory.Select(entry => new SuggestionRanker.Candidate(entry.Url, entry.Title, entry.Frecency, SuggestionSource.History)));
        foreach (var bookmark in bookmarks)
        {
            if (bookmark.IsFolder || bookmark.Url is not { } url) continue;
            candidates.Add(new SuggestionRanker.Candidate(url, bookmark.Title, 0, SuggestionSource.Bookmark, IsBookmarked: true));
        }
        var selected = model.SelectedTab;
        foreach (var tab in model.Tabs)
        {
            if (ReferenceEquals(tab, selected) || tab.IsStartPage) continue;
            candidates.Add(new SuggestionRanker.Candidate(tab.Url, tab.DisplayTitle, 0, SuggestionSource.OpenTab, TabId: tab.Id));
        }
        var ranked = keyword is null ? SuggestionRanker.Rank(text, candidates, 7) : [];
        var local = ranked.Select(Row).ToList();
        // A strong address match ("git" → github.com) goes above the search row.
        if (ranked.Count > 0 && ranked[0].MatchesHostPrefix)
        {
            rows.Add(local[0]);
            local.RemoveAt(0);
        }
        if (keyword is null && !AddressResolver.IsAddress(text))
        {
            rows.Add(new LibrarySuggestion(text, text, SuggestionKind.Search)
            {
                Detail = $"Search {engine.Name}",
                Url = engine.SearchUrl(text),
            });
        }
        rows.AddRange(local.Take(6 - Math.Min(rows.Count, 2)));

        var searchEngine = keyword?.Engine ?? engine;
        var shown = rows.Where(row => row.Kind == SuggestionKind.Search).Select(row => row.Title.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var phrase in remote.Take(8))
        {
            if (shown.Contains(phrase.ToLowerInvariant())) continue;
            rows.Add(new LibrarySuggestion(phrase, phrase, SuggestionKind.Search)
            {
                Detail = $"{searchEngine.Name} suggestion",
                Url = searchEngine.SearchUrl(phrase),
                IsRemote = true,
            });
            if (++added == 5) break;
        }
        return rows;
    }

    private LibrarySuggestion Row(SuggestionRanker.Ranked ranked)
    {
        var candidate = ranked.Candidate;
        var title = candidate.Title.Length == 0 ? LibraryUrl.Display(candidate.Url) : candidate.Title;
        switch (candidate.Source)
        {
            case SuggestionSource.OpenTab:
                return new LibrarySuggestion(title, candidate.Url, SuggestionKind.OpenTab)
                {
                    Detail = "Switch to tab", Url = candidate.Url, TabId = candidate.TabId, IsBookmarked = IsBookmarked(candidate.Url),
                };
            case SuggestionSource.Bookmark:
                return new LibrarySuggestion(title, candidate.Url, SuggestionKind.Bookmark)
                {
                    Detail = LibraryUrl.Display(candidate.Url), Url = candidate.Url, IsBookmarked = true,
                };
            default:
                var bookmarked = candidate.IsBookmarked || IsBookmarked(candidate.Url);
                return new LibrarySuggestion(title, candidate.Url, bookmarked ? SuggestionKind.Bookmark : SuggestionKind.History)
                {
                    Detail = LibraryUrl.Display(candidate.Url), Url = candidate.Url, IsBookmarked = bookmarked,
                };
        }
    }

    /// <summary>Most visited sites (one per site), for the new-tab page.</summary>
    public async Task<IReadOnlyList<LibrarySuggestion>> TopSitesAsync(int limit)
    {
        EnsureLoaded();
        var entries = await Enqueue(store => store.TopSites(limit));
        return [.. entries.Select(entry => new LibrarySuggestion(entry.DisplayTitle, entry.Url, SuggestionKind.History)
        {
            Detail = LibraryUrl.Display(entry.Url), Url = entry.Url, IsBookmarked = IsBookmarked(entry.Url),
        })];
    }

    /// <summary>Address-bar input → address, honouring search keywords ("w swift" → Wikipedia).</summary>
    public string? Resolve(string input, SearchEngine searchEngine) => SearchShortcuts.Resolve(input, searchEngine, customEngines);

    /// <summary>The search URL when <paramref name="input"/> starts with a search keyword, else null.</summary>
    public string? KeywordSearchUrl(string input) =>
        SearchShortcuts.MatchKeyword(input, customEngines) is { } match ? match.Engine.SearchUrl(match.Terms) : null;

    // ---- History recording (the model's hooks) ----

    /// <summary>The model calls this for every change of every tab (both engines, selected or not).</summary>
    internal void TabDidChange(BrowserTab tab)
    {
        var excluded = tab.Url.StartsWith("mosaic:", StringComparison.OrdinalIgnoreCase) || tab.Failure is not null || IsPrivateTab(tab);
        recorder.Observe(tab.Id, tab.Url, tab.Title, tab.IsLoading, tab.LastLoadRequest, excluded);
        if (++observationCount % 200 == 0) recorder.Prune(model.Tabs.Select(t => t.Id).ToHashSet());
    }

    /// <summary>The model's navigate loaded <paramref name="tab"/> from what was typed in the address bar.</summary>
    internal void NoteTypedNavigation(BrowserTab tab) => recorder.NoteTyped(tab.Id, tab.LastLoadRequest);

    /// <summary>Records a visit directly (the recorder does this for tab navigations).</summary>
    public Task RecordVisitAsync(string url, string title, bool typed = false, bool isPrivate = false)
    {
        if (isPrivate || !LibraryUrl.IsRecordable(url)) return Task.CompletedTask;
        return Perform(Reload.History, store => store.RecordVisit(url, title, typed));
    }

    private void CommitVisit(HistoryRecorder.Visit visit) => _ = RecordVisitAsync(visit.Url, visit.Title, visit.Typed);

    private void Retitle(string url, string title) => _ = Perform(Reload.History, store => store.UpdateTitle(url, title));

    /// <summary>Commits visits still waiting for their title and waits (briefly) for the queue (on quit).</summary>
    public void FlushPendingVisits(TimeSpan? timeout = null)
    {
        recorder.FlushAll();
        Task last;
        lock (queueGate) last = tail;
        try
        {
            last.Wait(timeout ?? TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // A failed write has already been reported.
        }
    }

    /// <summary>Waits for everything queued so far (tests, automation).</summary>
    public Task Idle()
    {
        lock (queueGate) return tail.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private void HistoryChanged()
    {
        HistoryRevision++;
        _ = RefreshHistoryIndexAsync(TimeSpan.FromSeconds(1.5));
    }

    /// <summary>The in-memory index of the best few thousand addresses the instant suggestions use.</summary>
    private async Task RefreshHistoryIndexAsync(TimeSpan delay)
    {
        if (indexRefreshPending) return;
        indexRefreshPending = true;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, time);
            var entries = await Enqueue(store => store.TopHistory(HistoryIndexSize));
            historyIndex = Index(entries);
        }
        catch (Exception error) when (error is LibraryException or SqliteException)
        {
        }
        finally
        {
            indexRefreshPending = false;
        }
    }

    private static IReadOnlyList<SuggestionRanker.Candidate> Index(IReadOnlyList<HistoryEntry> entries) =>
        [.. entries.Select(entry => new SuggestionRanker.Candidate(entry.Url, entry.Title, entry.Frecency, SuggestionSource.History))];

    // ---- History ----

    public static DateTimeOffset? Start(LibraryClearRange range, DateTimeOffset now) => range switch
    {
        LibraryClearRange.LastHour => now - TimeSpan.FromHours(1),
        LibraryClearRange.Today => new DateTimeOffset(now.ToLocalTime().Date, now.ToLocalTime().Offset),
        _ => null,
    };

    public Task ClearHistoryAsync(LibraryClearRange range) => ClearHistoryAsync(Start(range, time.GetUtcNow()));

    /// <summary>Clears visits from <paramref name="since"/> onwards (null: all history).</summary>
    public Task ClearHistoryAsync(DateTimeOffset? since) => Perform(Reload.History, store => store.ClearHistory(since));

    public Task DeleteVisitsAsync(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        return Perform(Reload.History, store => store.DeleteVisits(list));
    }

    public Task DeleteHistorySiteAsync(string site) => Perform(Reload.History, store => store.DeleteHistorySite(site));

    public Task DeleteHistoryUrlAsync(string url) => Perform(Reload.History, store => store.DeleteHistory(url));

    /// <summary>Visits newest first, filtered by words, before a time (paging) and since a time.</summary>
    public Task<IReadOnlyList<HistoryVisit>> VisitsAsync(string matching = "", DateTimeOffset? before = null, int limit = 500,
        DateTimeOffset? since = null)
    {
        EnsureLoaded();
        return Enqueue(store => store.Visits(matching, before, limit, since));
    }

    public Task<IReadOnlyList<HistoryEntry>> RecentHistoryAsync(int limit)
    {
        EnsureLoaded();
        return Enqueue(store => store.RecentHistory(limit));
    }

    public Task<HistoryEntry?> HistoryEntryAsync(string url)
    {
        EnsureLoaded();
        return Enqueue(store => store.HistoryEntry(url));
    }

    // ---- Bookmarks ----

    /// <summary>What the bookmarks bar shows.</summary>
    public IReadOnlyList<Bookmark> BookmarksBar => Children(Bookmark.BarId);

    public IReadOnlyList<Bookmark> Children(string folderId) =>
        [.. bookmarks.Where(b => b.ParentId == folderId).OrderBy(b => b.Position)];

    public Bookmark? BookmarkById(string id) => bookmarks.FirstOrDefault(b => b.Id == id);

    /// <summary>Bookmarks of <paramref name="url"/>, oldest first (from memory).</summary>
    public IReadOnlyList<Bookmark> BookmarksForUrl(string url)
    {
        var key = LibraryUrl.Normalized(url);
        return [.. bookmarks.Where(b => b.Url is { } address && LibraryUrl.Normalized(address) == key).OrderBy(b => b.Created)];
    }

    /// <summary>Whether <paramref name="url"/> is bookmarked (drives the address bar's star).</summary>
    public bool IsBookmarked(string url) => url.Length > 0 && bookmarkedKeys.Contains(LibraryUrl.Normalized(url));

    /// <summary>Folders in tree order with their depth, for folder pickers.</summary>
    public IReadOnlyList<(Bookmark Folder, int Depth)> FolderTree()
    {
        var result = new List<(Bookmark, int)>();
        void Walk(string id, int depth)
        {
            if (BookmarkById(id) is not { } folder) return;
            result.Add((folder, depth));
            foreach (var child in Children(id))
            {
                if (child.IsFolder) Walk(child.Id, depth + 1);
            }
        }
        Walk(Bookmark.BarId, 0);
        Walk(Bookmark.OtherId, 0);
        return result;
    }

    /// <summary>Every tag in use, alphabetically, without duplicates (ignoring case).</summary>
    public IReadOnlyList<string> AllTags
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return [.. bookmarks.SelectMany(b => b.Tags).Where(seen.Add).Order(StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    public IReadOnlyList<Bookmark> BookmarksTagged(string tag) =>
        [.. bookmarks.Where(b => b.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Bookmarks whose title, address or tags contain every word of <paramref name="text"/>.</summary>
    public Task<IReadOnlyList<Bookmark>> SearchBookmarksAsync(string text)
    {
        EnsureLoaded();
        return Enqueue(store => store.SearchBookmarks(text));
    }

    /// <summary>Adds a bookmark (to <see cref="DefaultFolderId"/> unless a folder is given). Null on failure.</summary>
    public Task<Bookmark?> AddBookmarkAsync(string url, string title, string? folderId = null, IEnumerable<string>? tags = null)
    {
        var folder = folderId ?? DefaultFolderId;
        var name = title.Length == 0 ? LibraryUrl.Display(url) : title;
        var tagList = tags?.ToList() ?? [];
        return Perform(Reload.Bookmarks, store => (Bookmark?)store.AddBookmark(url, name, folder, tagList));
    }

    public Task<Bookmark?> AddFolderAsync(string title, string parentId) =>
        Perform(Reload.Bookmarks, store => (Bookmark?)store.AddFolder(title, parentId));

    /// <summary>Changes a bookmark; null arguments stay as they are. Moving to a folder remembers it for next time.</summary>
    public Task UpdateBookmarkAsync(string id, string? title, string? url, IEnumerable<string>? tags, string? folderId)
    {
        var tagList = tags?.ToList();
        if (folderId is not null) DefaultFolderId = folderId;
        return Perform(Reload.Bookmarks, store =>
        {
            store.UpdateBookmark(id, title, url, tagList);
            if (folderId is not null && store.Bookmark(id)?.ParentId != folderId) store.MoveBookmark(id, folderId);
        });
    }

    public Task MoveBookmarkAsync(string id, string folderId, int? position = null) =>
        Perform(Reload.Bookmarks, store => store.MoveBookmark(id, folderId, position));

    public Task DeleteBookmarksAsync(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        return Perform(Reload.Bookmarks, store =>
        {
            foreach (var id in list) store.DeleteBookmark(id);
        });
    }

    /// <summary>Adds or removes the bookmark for a page; returns true when the page is now bookmarked.</summary>
    public async Task<bool> ToggleBookmarkAsync(string url, string title)
    {
        if (url.Length == 0 || !LibraryUrl.IsRecordable(url)) return false;
        if (IsBookmarked(url))
        {
            await Perform(Reload.Bookmarks, store => store.RemoveBookmarksForUrl(url));
            return false;
        }
        return await AddBookmarkAsync(url, title) is not null;
    }

    /// <summary>
    /// Imports a Netscape bookmark file (any browser's export) or a Chrome-family <c>Bookmarks</c>
    /// JSON file. Returns the number of bookmarks added.
    /// </summary>
    /// <exception cref="LibraryException">The file cannot be read or holds no bookmarks.</exception>
    public async Task<int> ImportBookmarksAsync(string path)
    {
        EnsureLoaded();
        var items = await Task.Run(() => ReadBookmarkFile(path));
        var added = 0;
        var ok = await Perform(Reload.Bookmarks, store =>
        {
            added = store.ImportBookmarks(items);
            return true;
        });
        if (!ok) throw new LibraryException(LastError ?? "The bookmarks could not be imported.");
        return added;
    }

    /// <summary>Reads a bookmark file in either format.</summary>
    public static IReadOnlyList<ImportedBookmark> ReadBookmarkFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException(error.Message, error);
        }
        if (ChromeBookmarks.Parse(text) is { } chrome) return chrome;
        if (!text.Contains("<a ", StringComparison.OrdinalIgnoreCase) && !text.Contains("NETSCAPE-Bookmark-file", StringComparison.OrdinalIgnoreCase))
        {
            throw new LibraryException("This file does not contain bookmarks in a format Mosaic can read.");
        }
        return NetscapeBookmarks.Parse(text);
    }

    /// <summary>Writes every bookmark to <paramref name="path"/> (replacing it in one step).</summary>
    /// <exception cref="LibraryException">The file cannot be written.</exception>
    public async Task ExportBookmarksAsync(string path, BookmarkFileFormat format = BookmarkFileFormat.NetscapeHtml)
    {
        EnsureLoaded();
        var all = await Enqueue(store => store.AllBookmarks());
        await Task.Run(() =>
        {
            var text = format == BookmarkFileFormat.ChromeJson ? ChromeBookmarks.Export(all) : NetscapeBookmarks.Export(all);
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new LibraryException(error.Message, error);
            }
        });
    }

    // ---- Reading list ----

    public Task AddToReadingListAsync(string url, string title)
    {
        if (!LibraryUrl.IsRecordable(url)) return Task.CompletedTask;
        return Perform(Reload.ReadingList, store => store.AddToReadingList(url, title));
    }

    public Task SetReadAsync(ReadingListItem item, bool read) => Perform(Reload.ReadingList, store => store.SetRead(item.Id, read));

    public Task RemoveFromReadingListAsync(ReadingListItem item) => Perform(Reload.ReadingList, store => store.RemoveFromReadingList(item.Id));

    public bool IsInReadingList(string url) => readingList.Any(item => item.Url == url);

    // ---- Search engines ----

    /// <summary>Built-in engines followed by the user's own.</summary>
    public IReadOnlyList<SearchEngine> AllSearchEngines => [.. SearchEngine.BuiltIn, .. customEngines.Select(engine => engine.SearchEngine)];

    public async Task SaveEngineAsync(CustomSearchEngine engine)
    {
        await Perform(Reload.Engines, store => store.SaveCustomEngine(engine));
        // Keep the default in step if it was the engine being edited.
        if (model.SearchEngine.Id == engine.Id) model.SearchEngine = engine.SearchEngine;
    }

    public async Task DeleteEngineAsync(CustomSearchEngine engine)
    {
        if (model.SearchEngine.Id == engine.Id) model.SearchEngine = SearchEngine.Default;
        await Perform(Reload.Engines, store => store.DeleteCustomEngine(engine.Id));
    }

    /// <summary>Whether <paramref name="keyword"/> is free (not a built-in "@" keyword or another engine's).</summary>
    public bool IsKeywordAvailable(string keyword, string? excludingId)
    {
        var word = keyword.ToLowerInvariant();
        if (word.Length == 0) return true;
        if (SearchEngine.BuiltIn.Any(engine => SearchShortcuts.Keyword(engine) == word)) return false;
        return !customEngines.Any(engine => engine.Id != excludingId && engine.Keyword.ToLowerInvariant() == word);
    }

    // ---- Automation ----

    /// <summary>The library's fields for automation's <c>dump</c> (from memory; starts loading).</summary>
    public JsonObject DumpState()
    {
        EnsureLoaded();
        var selected = model.SelectedTab;
        return new JsonObject
        {
            ["loaded"] = isLoaded,
            ["bookmarks"] = bookmarks.Count(b => !b.IsFolder),
            ["bookmarksBar"] = new JsonArray([.. BookmarksBar.Select(b => (JsonNode)b.DisplayTitle)]),
            ["showsBookmarksBar"] = showsBookmarksBar,
            ["historyAddresses"] = historyCounts.Addresses,
            ["historyVisits"] = historyCounts.Visits,
            ["readingList"] = readingList.Count,
            ["selectedIsBookmarked"] = selected is not null && IsBookmarked(selected.Url),
            ["remoteSuggestions"] = remoteSuggestionsEnabled,
            ["customEngines"] = new JsonArray([.. customEngines.Select(e => (JsonNode)$"{e.Keyword}={e.Name}")]),
            ["storageProblem"] = storageProblem ?? "",
            ["lastError"] = lastError ?? "",
            ["pendingVisits"] = recorder.TrackedTabs,
        };
    }

    // ---- Helpers ----

    /// <summary>A <c>bool IsPrivate</c> property on <see cref="BrowserTab"/>, when the privacy area has added one.</summary>
    private static Func<BrowserTab, bool> FindTabPrivacyProperty()
    {
        var property = typeof(BrowserTab).GetProperty("IsPrivate", BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(bool) || property.GetMethod is null) return _ => false;
        return tab => (bool)property.GetValue(tab)!;
    }

    internal static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.CurrentCulture)} {noun}s";
}
