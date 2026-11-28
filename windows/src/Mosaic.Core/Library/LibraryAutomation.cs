using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Library;

/// <summary>
/// Library commands for the automation pipe, with the macOS names
/// (<c>Sources/Mosaic/Features/Library/LibraryAutomation.swift</c>). Commands that produce data
/// write it as JSON to <c>out</c> (or to <c>value</c> for commands that take nothing else) and
/// also return it in the reply's <c>result</c>. Unlike macOS, each command waits for its change
/// to be stored before replying, so a script can check <c>dump</c> straight away.
/// <code>
/// libraryBookmark [edit]                  bookmark the current page (edit: show the editor)
/// libraryBookmarks [path]                 every bookmark
/// libraryHistory [path]                   counts and the latest visits
/// librarySuggest "git" [out]              suggestions: local and final (after remote ones)
/// libraryImport C:\bookmarks.html         import a Netscape HTML or Chrome JSON file
/// libraryExport C:\out.html|C:\out.json   export (".json" writes Chrome's format)
/// libraryReadingAdd                       add the current page to the reading list
/// libraryReadingList [path]
/// libraryClearHistory hour|today|all
/// libraryBar show|hide
/// libraryTopSites [path]
/// libraryRemoteSuggestions on|off
/// libraryAddEngine "Name|keyword|https://x.test/?q=%s[|suggest template]"
/// libraryOpen bookmarks|history|reading-list|search-settings
/// libraryRecordVisit "url|title"           record a visit directly (Windows addition, for scripts)
/// </code>
/// <c>dump</c> gains a <c>library</c> object (<see cref="BrowserLibrary.DumpState"/>).
/// </summary>
public static class LibraryAutomation
{
    public static IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "libraryBookmark", "libraryBookmarks", "libraryHistory", "librarySuggest", "libraryImport",
        "libraryExport", "libraryReadingAdd", "libraryReadingList", "libraryClearHistory", "libraryBar",
        "libraryTopSites", "libraryRemoteSuggestions", "libraryAddEngine", "libraryOpen", "libraryRecordVisit",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static int registered;

    /// <summary>Adds the commands and the <c>library</c> dump key (once; later calls do nothing).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("library", model => model.Library.DumpState());
    }

    /// <summary>Runs one library command; null when <paramref name="command"/> is not one.</summary>
    public static async Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        if (!Commands.Contains(command)) return null;
        var library = model.Library;
        await library.WhenLoaded;
        try
        {
            switch (command)
            {
                case "libraryBookmark":
                    var bookmark = await library.BookmarkCurrentPageAsync(showEditor: value == "edit", forceEditor: value == "edit");
                    return bookmark is null ? AutomationRegistry.Error(model.Notice ?? "The page cannot be bookmarked.") : Result(Json(bookmark));
                case "libraryBookmarks":
                    return await WriteAsync(new JsonArray([.. library.Bookmarks.Select(b => (JsonNode)Json(b))]), output ?? value);
                case "libraryHistory":
                    var counts = library.HistoryCounts;
                    var visits = await library.VisitsAsync(limit: 50);
                    var entries = await library.RecentHistoryAsync(50);
                    return await WriteAsync(new JsonObject
                    {
                        ["addresses"] = counts.Addresses,
                        ["visits"] = counts.Visits,
                        ["latest"] = new JsonArray([.. visits.Select(v => (JsonNode)new JsonObject
                        {
                            ["url"] = v.Url, ["title"] = v.Title, ["time"] = v.Time.ToUnixTimeMilliseconds() / 1000.0, ["typed"] = v.Typed,
                        })]),
                        ["entries"] = new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
                        {
                            ["url"] = e.Url, ["title"] = e.Title, ["visitCount"] = e.VisitCount, ["typedCount"] = e.TypedCount, ["frecency"] = e.Frecency,
                        })]),
                    }, output ?? value);
                case "librarySuggest":
                    IReadOnlyList<LibrarySuggestion> final = [];
                    var updates = 0;
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        await foreach (var rows in library.SuggestionStream(value, timeout.Token))
                        {
                            final = rows;
                            updates++;
                        }
                    }
                    return await WriteAsync(new JsonObject
                    {
                        ["input"] = value,
                        ["updates"] = updates,
                        ["local"] = new JsonArray([.. library.Suggestions(value).Select(s => (JsonNode)Json(s))]),
                        ["final"] = new JsonArray([.. final.Select(s => (JsonNode)Json(s))]),
                    }, output);
                case "libraryImport":
                    var added = await library.ImportBookmarksAsync(Environment.ExpandEnvironmentVariables(value));
                    model.Notice = added == 0 ? "No new bookmarks to import" : $"Imported {BrowserLibrary.Plural(added, "bookmark")}";
                    return Result(new JsonObject { ["added"] = added });
                case "libraryExport":
                    var format = value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(value) == "Bookmarks"
                        ? BookmarkFileFormat.ChromeJson : BookmarkFileFormat.NetscapeHtml;
                    await library.ExportBookmarksAsync(Environment.ExpandEnvironmentVariables(value), format);
                    return AutomationRegistry.Ok();
                case "libraryReadingAdd":
                    return await library.AddCurrentPageToReadingListAsync() ? AutomationRegistry.Ok() : AutomationRegistry.Error(model.Notice ?? "");
                case "libraryReadingList":
                    return await WriteAsync(new JsonArray([.. library.ReadingList.Select(item => (JsonNode)new JsonObject
                    {
                        ["id"] = item.Id, ["url"] = item.Url, ["title"] = item.Title, ["read"] = item.IsRead,
                    })]), output ?? value);
                case "libraryClearHistory":
                    await library.ClearHistoryAsync(value switch
                    {
                        "hour" => LibraryClearRange.LastHour,
                        "today" => LibraryClearRange.Today,
                        _ => LibraryClearRange.All,
                    });
                    return AutomationRegistry.Ok();
                case "libraryBar":
                    library.ShowsBookmarksBar = value != "hide";
                    await library.Idle();
                    return AutomationRegistry.Ok();
                case "libraryTopSites":
                    var sites = await library.TopSitesAsync(8);
                    return await WriteAsync(new JsonArray([.. sites.Select(s => (JsonNode)Json(s))]), output ?? value);
                case "libraryRemoteSuggestions":
                    library.RemoteSuggestionsEnabled = value == "on";
                    await library.Idle();
                    return AutomationRegistry.Ok();
                case "libraryAddEngine":
                    var parts = value.Split('|');
                    if (parts.Length < 3) return AutomationRegistry.Error("Expected \"Name|keyword|https://…%s[|suggest template]\".");
                    if (CustomSearchEngine.Problem(parts[0], parts[1], parts[2]) is { } problem) return AutomationRegistry.Error(problem);
                    if (!library.IsKeywordAvailable(parts[1], null)) return AutomationRegistry.Error($"The keyword \"{parts[1]}\" is taken.");
                    await library.SaveEngineAsync(new CustomSearchEngine
                    {
                        Name = parts[0], Keyword = parts[1], Template = parts[2], SuggestTemplate = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null,
                    });
                    return AutomationRegistry.Ok();
                case "libraryOpen":
                    if (value == "search-settings")
                    {
                        library.OpenSearchSettings();
                        return AutomationRegistry.Ok();
                    }
                    if (LibraryPages.FromHost(value) is not { } page) return AutomationRegistry.Error($"There is no library page \"{value}\".");
                    model.OpenLibraryPage(page);
                    return AutomationRegistry.Ok();
                case "libraryRecordVisit":
                    var visit = value.Split('|', 2);
                    await library.RecordVisitAsync(visit[0], visit.Length > 1 ? visit[1] : "");
                    return AutomationRegistry.Ok();
            }
        }
        catch (LibraryException error)
        {
            return AutomationRegistry.Error(error.Message);
        }
        return null;
    }

    private static async Task<AutomationReply> WriteAsync(JsonNode result, string? path)
    {
        if (path is { Length: > 0 }) await File.WriteAllTextAsync(path, result.ToJsonString(Indented));
        return Result(result);
    }

    private static AutomationReply Result(JsonNode result) =>
        new(new JsonObject { ["ok"] = true, ["result"] = result.DeepClone() }.ToJsonString());

    internal static JsonObject Json(Bookmark bookmark) => new()
    {
        ["id"] = bookmark.Id,
        ["parent"] = bookmark.ParentId ?? "",
        ["kind"] = bookmark.IsFolder ? "folder" : "url",
        ["title"] = bookmark.Title,
        ["url"] = bookmark.Url ?? "",
        ["position"] = bookmark.Position,
        ["tags"] = new JsonArray([.. bookmark.Tags.Select(tag => (JsonNode)tag)]),
    };

    internal static JsonObject Json(LibrarySuggestion suggestion) => new()
    {
        ["kind"] = suggestion.Kind switch
        {
            SuggestionKind.History => "history",
            SuggestionKind.Bookmark => "bookmark",
            SuggestionKind.Search => "search",
            _ => "openTab",
        },
        ["title"] = suggestion.Title,
        ["value"] = suggestion.Value,
        ["detail"] = suggestion.Detail,
        ["url"] = suggestion.Url ?? "",
        ["remote"] = suggestion.IsRemote,
        ["bookmarked"] = suggestion.IsBookmarked,
    };
}
