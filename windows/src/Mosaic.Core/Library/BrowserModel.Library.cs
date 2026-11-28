using Mosaic.Core.Library;

namespace Mosaic.Core.Browser;

/// <summary>Mosaic's own pages that belong to the library (<c>mosaic://&lt;host&gt;</c>).</summary>
public enum LibraryPage { Bookmarks, History, ReadingList }

public static class LibraryPages
{
    /// <summary>The page's host: <c>mosaic://bookmarks</c> → "bookmarks" (the key in FeatureMounts.InternalPages).</summary>
    public static string Host(this LibraryPage page) => page switch
    {
        LibraryPage.Bookmarks => "bookmarks",
        LibraryPage.History => "history",
        _ => "reading-list",
    };

    public static string Url(this LibraryPage page) => "mosaic://" + page.Host();

    public static string Title(this LibraryPage page) => page switch
    {
        LibraryPage.Bookmarks => "Bookmarks",
        LibraryPage.History => "History",
        _ => "Reading List",
    };

    public static LibraryPage? FromHost(string host) => host.ToLowerInvariant() switch
    {
        "bookmarks" => LibraryPage.Bookmarks,
        "history" => LibraryPage.History,
        "reading-list" => LibraryPage.ReadingList,
        _ => null,
    };
}

/// <summary>The library's part of the browser model (owner: library). See <see cref="BrowserLibrary"/>.</summary>
public sealed partial class BrowserModel
{
    private BrowserLibrary? library;

    /// <summary>
    /// Bookmarks, history, the reading list, search keywords and address-bar suggestions.
    /// Created on first use, without I/O; the database opens in the background when needed.
    /// </summary>
    public BrowserLibrary Library => library ??= new BrowserLibrary(this, time: time);

    /// <summary>Replaces the library before first use (tests: an in-memory database, a fake network).</summary>
    public void UseLibrary(LibraryOptions options) => library = new BrowserLibrary(this, options, time);

    /// <summary>The search engine id saved in settings (it may name a custom engine the library adds later).</summary>
    internal string? LibrarySavedSearchEngineId => settings.SearchEngineId;

    /// <summary>
    /// Opens one of the library's pages, reusing a tab that already shows it. The page is the
    /// internal address <c>mosaic://bookmarks</c> (etc.); the shell draws it from
    /// <c>FeatureMounts.InternalPages</c>.
    /// </summary>
    public void OpenLibraryPage(LibraryPage page)
    {
        var url = page.Url();
        if (Tabs.FirstOrDefault(tab => string.Equals(tab.Url, url, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            Select(existing);
            return;
        }
        if (selectedTab is { IsStartPage: true } blank) Load(url, blank);
        else NewTab(url);
    }

    /// <summary>
    /// Opens an address from a library page or the bookmarks bar: in the current tab, or in a
    /// new one (in the background unless <paramref name="select"/>), as Ctrl+click does in other browsers.
    /// </summary>
    public void OpenFromLibrary(string url, bool newTab = false, bool select = true)
    {
        if (newTab)
        {
            NewTab(url, select: select);
            return;
        }
        var tab = selectedTab ?? NewTab();
        EndAddressEditing();
        Load(url, tab);
        FocusContent();
    }

    // ---- Hooks called from BrowserModel.cs (one line each, marked "// library:") ----

    /// <summary>Every change of every tab, both engines, selected or not: history recording.</summary>
    private void LibraryTabChanged(BrowserTab tab) => Library.TabDidChange(tab);

    /// <summary>The address bar loaded <paramref name="tab"/>: the visit counts as typed.</summary>
    private void LibraryNoteTyped(BrowserTab tab) => library?.NoteTypedNavigation(tab);

    /// <summary>"w swift" → the keyword engine's search address; anything else unchanged.</summary>
    private string LibraryResolveKeyword(string input) => Library.KeywordSearchUrl(input) ?? input;

    /// <summary>Library shortcuts pressed while a Gecko page has focus (the agent forwards them as events).</summary>
    private bool LibraryHandleGeckoEvent(string eventType) => Library.HandleGeckoEvent(eventType);

    /// <summary>On quit: visits still waiting for their title are written.</summary>
    private void LibraryShutdown() => library?.FlushPendingVisits();
}
