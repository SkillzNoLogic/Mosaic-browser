using Mosaic.Core.Browser;

namespace Mosaic.Core.Shell;

/// <summary>One row under the address field.</summary>
/// <param name="Title">The main line (page title, or the search text).</param>
/// <param name="Url">What opening it loads (or the text to search for when <paramref name="Kind"/> is "search").</param>
/// <param name="Kind">"history", "bookmark", "tab", "search", "url"…: picks the row's icon.</param>
/// <param name="Detail">A second line (the address), optional.</param>
public sealed record AddressSuggestion(string Title, string Url, string Kind, string? Detail = null);

/// <summary>
/// The address field's suggestions hook. The library area sets <see cref="Provider"/> (and, if
/// opening a row means more than navigating, <see cref="Open"/>) from its module initialiser;
/// until then the field shows no suggestions. Port of macOS's <c>shellSuggestionStream</c>.
/// </summary>
/// <remarks>Owner: shell (the hook); library (what fills it).</remarks>
public static class AddressSuggestions
{
    /// <summary>
    /// Suggestions for the typed text as they arrive (local results first, remote ones later);
    /// each list replaces the previous one. Cancelled when the text changes or the field closes.
    /// </summary>
    public static Func<BrowserModel, string, CancellationToken, IAsyncEnumerable<IReadOnlyList<AddressSuggestion>>>? Provider { get; set; }

    /// <summary>Opens a row (switching to an open tab, for example). Default: navigate the selected tab.</summary>
    public static Action<BrowserModel, AddressSuggestion, bool>? Open { get; set; }

    /// <summary>Opens <paramref name="suggestion"/> (in a new tab when <paramref name="inNewTab"/>).</summary>
    public static void OpenSuggestion(BrowserModel model, AddressSuggestion suggestion, bool inNewTab)
    {
        if (Open is { } open)
        {
            open(model, suggestion, inNewTab);
            return;
        }
        if (inNewTab) model.NewTab(suggestion.Url);
        else model.Navigate(suggestion.Url);
    }
}
