using System.Windows;
using System.Windows.Controls;
using Mosaic.Core.Browser;

namespace Mosaic.App.Views;

/// <summary>
/// What the content area says when there is no page to show: an engine is starting, or the page
/// failed and can be reloaded.
/// </summary>
public partial class EngineMessageView : UserControl
{
    public EngineMessageView() => InitializeComponent();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BrowserModel model) model.Reload();
    }
}
