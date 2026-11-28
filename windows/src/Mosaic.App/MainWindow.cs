using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mosaic.App;

/// <summary>
/// Mosaic's window. Skeleton from the integrator: the winui area ports the WPF window's
/// behaviour (engines, Gecko child window, automation) into it, and the shell area's
/// composed shell becomes its content.
/// </summary>
public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "Mosaic";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        Content = new TextBlock
        {
            Text = "Mosaic",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
