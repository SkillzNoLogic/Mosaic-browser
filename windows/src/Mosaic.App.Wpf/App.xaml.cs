using System.Diagnostics;
using System.Windows;
using Mosaic.App.Chromium;
using Mosaic.App.Hosting;
using Mosaic.Core;
using Mosaic.Core.Browser;
using Mosaic.Core.Extensions;
using Mosaic.Core.Gecko;

namespace Mosaic.App;

/// <summary>
/// Application entry point: enforces a single instance, wires the two engines, the model and the
/// window together, and takes everything down again on exit.
/// </summary>
public partial class App : Application
{
    /// <summary>Command-line switch selecting <see cref="GeckoHostMode.Owned"/>, the fallback way of hosting Gecko's window.</summary>
    private const string OwnedGeckoHostSwitch = "--gecko-host=owned";

    private SingleInstance? instance;
    private ChildProcessJob? geckoJob;
    private AutomationPipe? automationPipe;
    private MainWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EnableTraceFile();
        var automated = e.Args.Contains(Automation.Flag);

        instance = new SingleInstance(AppPaths.DataRoot);
        if (!instance.IsFirst)
        {
            // A scripted launch must not pull a window to the front; a person's launch should.
            if (!automated) SingleInstance.ActivateRunningInstance();
            Shutdown();
            return;
        }

        // Gecko is a separate process. The job makes Windows end it with Mosaic no matter how
        // Mosaic ends, including a crash that skips all of the clean-up below.
        var job = geckoJob = new ChildProcessJob();
        var gecko = new GeckoEngine(new GeckoEngineOptions { ProcessLaunched = processId => job.Add(processId) });
        var chromium = new WebView2Engine();

        var settings = new BrowserSettings(BrowserSettings.DefaultPath);
        var extensions = new ExtensionManager(
            gecko,
            () => new ExtensionStoreLibrary(new ExtensionStore(AppPaths.Extensions)),
            settings,
            chromium.IsAvailable ? chromium : null);
        var model = new BrowserModel(gecko, chromium, extensions, settings);
        // The model has started the blocker, so whether it is part of this build is known; the
        // first Chromium tab is created later, once the window is up.
        if (model.Blocker.IsAvailable) chromium.RequestFilter = model.Blocker.Filter;

        window = new MainWindow(model, chromium);
        if (e.Args.Contains(OwnedGeckoHostSwitch)) window.GeckoHostMode = GeckoHostMode.Owned;
        MainWindow = window;

        if (automated)
        {
            var shown = window;
            var automation = new Automation(model, path =>
            {
                WindowCapture.SavePng(shown, path);
                return Task.CompletedTask;
            });
            automationPipe = new AutomationPipe(automation, shown.Close);
            automationPipe.Start();
            // In automation mode Mosaic never takes keyboard focus on its own, starting here.
            window.ShowActivated = false;
        }

        window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        // Signing out or shutting down may end the process without the window ever closing.
        if (!e.Cancel) window?.ShutDownBrowser();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        automationPipe?.Dispose();
        // Normally a no-op: closing the window has already done it.
        window?.ShutDownBrowser();
        // Last, so that anything of Gecko's that outlived the orderly stop is ended now.
        geckoJob?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
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
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            // Diagnostics are optional; an unwritable path must not stop the browser.
        }
    }
}
