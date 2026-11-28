using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mosaic.App;

/// <summary>The WinUI application. Owned by the winui area.</summary>
public sealed class App : Application
{
    private readonly string[] arguments;
    private MainWindow? window;

    public App(string[] arguments)
    {
        this.arguments = arguments;
    }

    public static App CurrentApp => (App)Current;

    public IReadOnlyList<string> Arguments => arguments;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Fluent controls' styles (there is no App.xaml to merge them in).
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        window = new MainWindow();
        window.Activate();
    }
}
