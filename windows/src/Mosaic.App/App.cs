using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Mosaic.App.AutomationSupport;
using Mosaic.App.Chromium;
using Mosaic.App.Features;
using Mosaic.App.Hosting;
using Mosaic.Core;
using Mosaic.Core.Browser;
using Mosaic.Core.Extensions;
using Mosaic.Core.Gecko;

namespace Mosaic.App;

/// <summary>
/// The WinUI application: wires the two engines, the model and the window together, starts the
/// automation pipe when asked to, runs the feature areas' start-up hooks, and takes everything
/// down again on exit. Owned by the winui area.
/// </summary>
/// <remarks>
/// There is no App.xaml, so no generated XAML type information either: the app hands WinUI the
/// Fluent controls' own (<see cref="IXamlMetadataProvider"/>), which <c>XamlControlsResources</c>
/// needs to load its styles.
/// </remarks>
public sealed class App : Application, IXamlMetadataProvider
{
    /// <summary>Command-line switch selecting <see cref="GeckoHostMode.Owned"/>, the fallback way of hosting Gecko's window.</summary>
    private const string OwnedGeckoHostSwitch = "--gecko-host=owned";

    private readonly string[] arguments;
    private readonly SingleInstance instance;
    private ChildProcessJob? geckoJob;
    private AutomationPipe? automationPipe;
    private MainWindow? window;
    private readonly XamlControlsXamlMetaDataProvider controlsMetadata;

    internal App(string[] arguments, SingleInstance instance)
    {
        XamlControlsXamlMetaDataProvider.Initialize();
        controlsMetadata = new XamlControlsXamlMetaDataProvider();
        this.arguments = arguments;
        this.instance = instance;
        UnhandledException += (_, e) => Trace.TraceError($"[App] Unhandled exception: {e.Exception}");
    }

    public static App CurrentApp => (App)Current;

    public IReadOnlyList<string> Arguments => arguments;

    /// <summary>The main window, once launched.</summary>
    public MainWindow? MainWindow => window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Fluent controls' styles (there is no App.xaml to merge them in).
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var automated = arguments.Contains(Automation.Flag);

        // Gecko is a separate process. The job makes Windows end it with Mosaic no matter how
        // Mosaic ends, including a crash that skips all of the clean-up below. Nothing here
        // starts Gecko: the model starts it when the first Mozilla page loads.
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

        GeckoHostMode? hostMode = arguments.Contains(OwnedGeckoHostSwitch, StringComparer.OrdinalIgnoreCase) ? GeckoHostMode.Owned : null;
        var shown = window = new MainWindow(model, chromium, settings, hostMode);

        if (automated)
        {
            var automation = new Automation(model, path =>
                // Off the UI thread: PrintWindow asks the compositor, and the PNG encoding is not
                // UI work.
                Task.Run(() => WindowCapture.SavePng(shown.Handle, path)));
            automationPipe = new AutomationPipe(automation, shown.Close);
            automationPipe.Start();
        }
        WindowAutomation.Register(shown);

        // In automation mode Mosaic never takes keyboard focus on its own, starting here.
        shown.Show(activate: !automated);

        // Addresses on the command line, and from later launches (SingleInstance).
        foreach (var address in SingleInstance.AddressesIn(arguments)) model.NewTab(address);
        instance.Listen((addresses, activate) =>
        {
            foreach (var address in addresses) model.NewTab(address);
            if (activate) shown.BringToFront();
        });

        foreach (var (area, start) in FeatureMounts.Startup)
        {
            try
            {
                start(model, shown);
            }
            catch (Exception error)
            {
                Trace.TraceError($"[App] Start-up hook of the {area} area failed: {error}");
            }
        }
    }

    public IXamlType GetXamlType(Type type) => controlsMetadata.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => controlsMetadata.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => controlsMetadata.GetXmlnsDefinitions();

    /// <summary>Called by <see cref="Program"/> once the last window has closed.</summary>
    internal void ShutDown()
    {
        automationPipe?.Dispose();
        // Normally a no-op: closing the window has already done it.
        window?.ShutDownBrowser();
        // Last, so that anything of Gecko's that outlived the orderly stop is ended now.
        geckoJob?.Dispose();
    }
}
