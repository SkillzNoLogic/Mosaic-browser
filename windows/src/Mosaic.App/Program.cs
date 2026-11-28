using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mosaic.App;

/// <summary>Entry point (there is no App.xaml): starts WinUI on this thread.</summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(args);
        });
    }
}
