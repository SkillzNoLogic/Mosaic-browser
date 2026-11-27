using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Mosaic.Core.Browser;

namespace Mosaic.App.Views;

/// <summary>
/// The content blocker's popover: the master switch, the switch for the site in front, what was
/// blocked, and the state of the engine and its lists. Shown by <see cref="MainWindow"/> in a
/// <see cref="Popup"/>, because it opens over the page, which WPF content cannot draw over.
/// </summary>
/// <remarks>
/// The switches show the model's state one way and act through its methods on click, as the
/// Extensions window's do, so the model stays the only place a setting changes.
/// </remarks>
public partial class BlockerPopover : UserControl
{
    public BlockerPopover() => InitializeComponent();

    private BrowserModel? Model => DataContext as BrowserModel;

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle) Model?.SetBlockerEnabled(toggle.IsChecked == true);
    }

    private void Site_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && Model is { SelectedTab: { } tab } model) model.SetBlocking(toggle.IsChecked == true, tab);
    }

    // Safe as async void: UpdateListsAsync handles whatever a download or a file write can throw.
    private async void UpdateLists_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) await model.Blocker.UpdateListsAsync();
    }
}
