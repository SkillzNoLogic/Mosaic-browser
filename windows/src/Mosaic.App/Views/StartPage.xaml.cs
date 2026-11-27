using System.Windows.Controls;
using System.Windows.Input;
using Mosaic.Core.Browser;

namespace Mosaic.App.Views;

/// <summary>The page a blank tab shows: Mosaic's mark and a search field.</summary>
public partial class StartPage : UserControl
{
    public StartPage() => InitializeComponent();

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not BrowserModel model) return;
        e.Handled = true;
        var query = QueryBox.Text;
        // One start page serves every blank tab; it must not greet the next one with this query.
        QueryBox.Clear();
        model.Navigate(query);
    }
}
