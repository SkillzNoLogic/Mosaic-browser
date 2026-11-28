using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;

namespace Mosaic.App.Features.Library;

/// <summary>
/// The address field's suggestion list, for the shell's address field. The data API is
/// <see cref="BrowserLibrary.Suggestions"/> (instant, in memory) and
/// <see cref="BrowserLibrary.SuggestionStream"/> (local → all of history → the search engine's
/// suggestions when switched on); <see cref="BrowserLibrary.Open"/> acts on a chosen row. This
/// class wires them to an <see cref="AutoSuggestBox"/> for a shell that uses one, and draws rows
/// for a shell that has its own list.
/// </summary>
public static class AddressSuggestions
{
    /// <summary>
    /// Shows the library's suggestions under <paramref name="box"/> as the user types, and opens the
    /// chosen one (Enter on plain text navigates as usual). The box's <c>ItemsSource</c> holds
    /// elements made by <see cref="Row"/>; each one's <c>Tag</c> is its <see cref="LibrarySuggestion"/>.
    /// </summary>
    public static void Attach(AutoSuggestBox box, BrowserModel model)
    {
        CancellationTokenSource? lookup = null;
        box.UpdateTextOnSelect = false;
        box.TextChanged += async (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            lookup?.Cancel();
            var current = lookup = new CancellationTokenSource();
            var text = sender.Text;
            if (text.Trim().Length == 0)
            {
                sender.ItemsSource = null;
                return;
            }
            await foreach (var rows in model.Library.SuggestionStream(text, current.Token))
            {
                if (current.IsCancellationRequested) break;
                sender.ItemsSource = rows.Select(Row).ToList();
            }
        };
        box.QuerySubmitted += (sender, args) =>
        {
            lookup?.Cancel();
            sender.ItemsSource = null;
            if (args.ChosenSuggestion is FrameworkElement { Tag: LibrarySuggestion suggestion }) model.Library.Open(suggestion);
            else model.Navigate(args.QueryText);
        };
    }

    /// <summary>One suggestion as a row: an icon for its kind, the title, and its detail line.</summary>
    public static FrameworkElement Row(LibrarySuggestion suggestion)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4), Tag = suggestion };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = LibraryUi.Icon(suggestion.Kind switch
        {
            SuggestionKind.Search => "search",
            SuggestionKind.OpenTab => "plus.square.on.square",
            SuggestionKind.Bookmark => "star",
            _ => "clock",
        }, 14);
        icon.Foreground = LibraryUi.SecondaryText;
        row.Children.Add(icon);
        var title = LibraryUi.Text(suggestion.Title);
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var detail = LibraryUi.Text(suggestion.Detail, secondary: true, size: 12);
        detail.MaxWidth = 260;
        Grid.SetColumn(detail, 2);
        row.Children.Add(detail);
        AutomationProperties.SetName(row, suggestion.Detail.Length > 0 ? $"{suggestion.Title}, {suggestion.Detail}" : suggestion.Title);
        return row;
    }
}
