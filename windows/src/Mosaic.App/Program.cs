using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Mosaic.Core;
using Mosaic.Core.Browser;

namespace Mosaic.App;

/// <summary>
/// Entry point (there is no App.xaml). Settles single-instance first, so a second launch hands
/// its addresses to the running Mosaic and exits without starting WinUI at all.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        EnableTraceFile();
        var automated = args.Contains(Automation.Flag);

        using var instance = new SingleInstance(AppPaths.DataRoot);
        if (!instance.IsFirst)
        {
            // A scripted launch must not pull a window to the front; a person's launch should.
            instance.ForwardToRunningInstance(SingleInstance.AddressesIn(args), activate: !automated);
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        App? app = null;
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            app = new App(args, instance);
        });
        // The last window has closed.
        app?.ShutDown();
        Trace.Flush();
        return 0;
    }

    /// <summary>
    /// A GUI process has nowhere to show <see cref="Trace"/> output, and the Gecko window host
    /// reports every Win32 step it takes through it. Setting <c>MOSAIC_TRACE_FILE</c> sends those
    /// diagnostics to a file, which is what someone debugging a real PC (or the CI smoke test)
    /// needs in order to see why a Mozilla page did not appear.
    /// </summary>
    private static void EnableTraceFile()
    {
        if (Environment.GetEnvironmentVariable("MOSAIC_TRACE_FILE") is not { Length: > 0 } path) return;
        try
        {
            Trace.Listeners.Add(new TextWriterTraceListener(path) { TraceOutputOptions = TraceOptions.DateTime });
            Trace.AutoFlush = true;
            Trace.WriteLine($"Mosaic {typeof(App).Assembly.GetName().Version} trace started");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Diagnostics are optional; an unwritable path must not stop the browser.
        }
    }
}
