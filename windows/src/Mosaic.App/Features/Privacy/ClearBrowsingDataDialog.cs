using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Privacy;
using static Mosaic.App.Features.Privacy.PrivacyUi;

namespace Mosaic.App.Features.Privacy;

/// <summary>
/// Clear Browsing Data (Ctrl+Shift+Delete): picks a time range and kinds of data, then clears them
/// from both engines and from Mosaic's own lists. Port of <c>ClearBrowsingDataSheet.swift</c>.
/// The caller covers the page while it is open (<see cref="FeatureMounts.CoverPage"/>).
/// </summary>
internal sealed class ClearBrowsingDataDialog : ContentDialog
{
    private readonly PrivacyController privacy;
    private readonly ComboBox range;
    private readonly Dictionary<BrowsingDataKind, (CheckBox Box, TextBlock Note)> kinds = [];
    private readonly ProgressRing progress = new() { IsActive = false, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };

    public ClearBrowsingDataDialog(PrivacyController privacy)
    {
        this.privacy = privacy;
        Title = "Clear Browsing Data";
        PrimaryButtonText = "Clear";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetName(this, "Clear browsing data");

        var body = new StackPanel { Spacing = 12, MinWidth = 400, MaxWidth = 460 };
        range = new ComboBox
        {
            ItemsSource = BrowsingDataTimeRanges.All.Select(item => item.Title()).ToList(),
            SelectedIndex = BrowsingDataTimeRanges.All.ToList().IndexOf(BrowsingDataTimeRange.Last24Hours),
            Header = "Time range",
            MinWidth = 200,
        };
        AutomationProperties.SetName(range, "Time range");
        range.SelectionChanged += (_, _) => UpdateNotes();
        body.Children.Add(range);

        var chosen = new HashSet<BrowsingDataKind> { BrowsingDataKind.History, BrowsingDataKind.CookiesAndSiteData, BrowsingDataKind.Cache };
        foreach (var kind in BrowsingDataKinds.All)
        {
            var note = Caption("");
            var label = new StackPanel { Spacing = 1 };
            label.Children.Add(Text(kind.Title()));
            label.Children.Add(note);
            var box = new CheckBox { Content = label, IsChecked = chosen.Contains(kind) };
            AutomationProperties.SetName(box, kind.Title());
            box.Checked += (_, _) => UpdateButton();
            box.Unchecked += (_, _) => UpdateButton();
            kinds[kind] = (box, note);
            body.Children.Add(box);
        }
        body.Children.Add(Caption("Private tabs keep nothing to clear: their data disappears when the last one closes."));
        body.Children.Add(progress);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UpdateNotes();
        UpdateButton();

        PrimaryButtonClick += async (_, args) =>
        {
            // Keep the dialog open while clearing, with the buttons off, then close it.
            var deferral = args.GetDeferral();
            IsPrimaryButtonEnabled = false;
            progress.IsActive = true;
            try
            {
                var selected = kinds.Where(pair => pair.Value.Box.IsChecked == true).Select(pair => pair.Key).ToHashSet();
                await privacy.ClearBrowsingDataAsync(selected, SelectedRange);
            }
            finally
            {
                progress.IsActive = false;
                deferral.Complete();
            }
        };
    }

    private BrowsingDataTimeRange SelectedRange =>
        range.SelectedIndex >= 0 ? BrowsingDataTimeRanges.All[range.SelectedIndex] : BrowsingDataTimeRange.Last24Hours;

    private void UpdateButton() => IsPrimaryButtonEnabled = kinds.Values.Any(entry => entry.Box.IsChecked == true);

    private void UpdateNotes()
    {
        foreach (var (kind, entry) in kinds) entry.Note.Text = Note(kind);
    }

    private string Note(BrowsingDataKind kind)
    {
        var registered = BrowsingDataRegistry.Shared.CoveredKinds.Contains(kind);
        return kind switch
        {
            BrowsingDataKind.History => registered
                ? "Mosaic’s history and both engines’ own."
                : "Both engines’ own history.",
            BrowsingDataKind.CookiesAndSiteData => SelectedRange == BrowsingDataTimeRange.AllTime
                ? "Signs you out of most sites, in both engines."
                : "Data stored in this period, in both engines.",
            BrowsingDataKind.Cache => "Cached images and files from this period, in both engines.",
            BrowsingDataKind.Downloads => registered
                ? "Mosaic’s downloads list and the engines’. Downloaded files are not deleted."
                : "The engines’ download history. Downloaded files are not deleted.",
            BrowsingDataKind.FormData => "Entries the engines remember for form fields. Passwords are not affected.",
            _ => "",
        };
    }
}
