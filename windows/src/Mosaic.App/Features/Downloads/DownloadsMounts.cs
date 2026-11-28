using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core.Browser;
using Mosaic.Core.Downloads;
using Mosaic.Core.Shell;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Mosaic.App.Features.Downloads;

/// <summary>
/// Where the downloads area plugs into the app: Settings › Downloads, the "downloads" toolbar
/// component, <c>mosaic://downloads</c>, the question overlay, Ctrl+J, and the startup hook that
/// gives the download manager Windows (Explorer, Recycle Bin, notifications, taskbar),
/// WebView2's downloads and Free Download Manager.
/// </summary>
internal static class DownloadsMounts
{
    private static WebView2Downloads? chromium;
    private static Window? window;

    /// <summary>Mosaic's main window (pickers need it); 0 until the startup hook ran.</summary>
    public static nint WindowHandle { get; private set; }

    public static bool IsScripted { get; } = Environment.GetCommandLineArgs().Contains(Automation.Flag);

#pragma warning disable CA2255 // the documented way areas plug in (FeatureMounts); it only stores delegates
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        // Downloads go to the Downloads known folder, wherever the user has moved it.
        DownloadSettings.DefaultFolderProvider = () => NativeMethods.KnownDownloadsFolder()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        FeatureMounts.SettingsPages[SettingsCategory.Downloads] = model => new DownloadsSettingsPage(model);
        FeatureMounts.Components["downloads"] = model => new DownloadsToolbarButton(model.Downloads);
        FeatureMounts.InternalPages["downloads"] = model => new DownloadsPage(model);
        FeatureMounts.Overlays.Add(("downloads.prompt", model => new DownloadPromptOverlay(model.Downloads, IsScripted)));
        FeatureMounts.Startup.Add(("downloads", Start));

        DownloadsAutomation.Register();
        AutomationRegistry.Register(HandleAppCommandAsync);
        AutomationRegistry.RegisterState("downloads", model =>
        {
            var state = DownloadsAutomation.State(model.Downloads);
            state["taskbar"] = DownloadSystemHooks.TaskbarStatus;
            state["notificationStatus"] = DownloadSystemHooks.NotificationStatus;
            state["chromiumHooked"] = chromium is not null;
            state["knownDownloadsFolder"] = NativeMethods.KnownDownloadsFolder() ?? "";
            return state;
        });
    }

    /// <summary>
    /// For the winui area's Chromium code: send this WebView2's downloads to Mosaic. Call it once
    /// per CoreWebView2, right after it is created (until then they are found in the visual tree).
    /// </summary>
    public static void AttachChromium(CoreWebView2 core) => chromium?.Attach(core);

    private static void Start(BrowserModel model, Window mainWindow)
    {
        window = mainWindow;
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var downloads = model.Downloads;
        downloads.Shell = new WindowsDownloadShell(WindowHandle, IsScripted, dispatcher);
        var fdm = new FreeDownloadManager { AllowForeground = () => NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY) };
        downloads.Fdm = fdm;
        // The registry look-up stays off the UI thread; the settings page refreshes it when shown.
        _ = Task.Run(fdm.Refresh);

        chromium = new WebView2Downloads(downloads, WindowHandle);
        downloads.Chromium = chromium;
        WatchForWebViews(model, mainWindow, dispatcher);

        // Ctrl+J: the Downloads page (inside Gecko pages the agent forwards it as showDownloads).
        if (mainWindow.Content is UIElement content)
        {
            var accelerator = new KeyboardAccelerator { Key = VirtualKey.J, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, args) =>
            {
                downloads.OpenDownloadsPage();
                args.Handled = true;
            };
            content.KeyboardAccelerators.Add(accelerator);
        }

        DownloadSystemHooks.NotificationClicked += _ => dispatcher.TryEnqueue(() =>
        {
            mainWindow.Activate();
            downloads.OpenDownloadsPage();
        });

        mainWindow.Closed += (_, _) =>
        {
            downloads.Prompter.CancelAll();
            chromium?.Close();
            downloads.Flush();
        };
    }

    /// <summary>
    /// Finds the WebView2 controls of Chromium tabs whenever tabs change, until the winui area
    /// calls <see cref="AttachChromium"/> itself. Batched: one look per burst of changes.
    /// </summary>
    private static void WatchForWebViews(BrowserModel model, Window mainWindow, DispatcherQueue dispatcher)
    {
        var queued = false;
        void Scan()
        {
            if (queued) return;
            queued = true;
            dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                queued = false;
                chromium?.AttachAll(mainWindow.Content);
            });
        }
        void Watch(BrowserTab tab) => tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BrowserTab.Chromium)) Scan();
        };
        foreach (var tab in model.Tabs) Watch(tab);
        model.Tabs.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is { } added)
            {
                foreach (var tab in added.OfType<BrowserTab>()) Watch(tab);
            }
            Scan();
        };
        Scan();
    }

    /// <summary>Opens Settings › Downloads through the shell's <c>openSettings</c> command.</summary>
    public static async Task OpenSettingsAsync(BrowserModel model)
    {
        foreach (var handler in AutomationRegistry.Handlers.ToList())
        {
            if (await handler(model, "openSettings", SettingsCategory.Downloads.Id(), null) is not null) return;
        }
    }

    // ---- App-side automation ----

    private static async Task<AutomationReply?> HandleAppCommandAsync(BrowserModel model, string command, string value, string? output)
    {
        if (command != "downloadsSnapshot") return null;
        if (window?.Content?.XamlRoot is not { } root) return AutomationRegistry.Error("The window is not ready.");
        if (value.Length == 0) return AutomationRegistry.Error("downloadsSnapshot needs a path.");
        var stem = value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
        await RenderAsync(root, new DownloadsFlyoutContent(model.Downloads), 384, 470, $"{stem}-popover.png");
        await RenderAsync(root, new DownloadsPage(model), 980, 640, $"{stem}-page.png");
        await RenderAsync(root, new DownloadsSettingsPage(model), 760, 1300, $"{stem}-settings.png");
        await RenderAsync(root, new DownloadsToolbarButton(model.Downloads), 60, 44, $"{stem}-button.png");
        var prompt = new DownloadPromptOverlay(model.Downloads, isScripted: true);
        if (model.Downloads.Prompter.Pending is not null) await RenderAsync(root, prompt, 460, 260, $"{stem}-prompt.png");
        return AutomationRegistry.Ok();
    }

    /// <summary>
    /// Renders a view offscreen-sized in a popup and writes a PNG, so the views can be checked
    /// before (or regardless of where) the shell places them.
    /// </summary>
    private static async Task RenderAsync(XamlRoot root, UIElement view, int width, int height, string path)
    {
        var host = new Border
        {
            Width = width,
            Height = height,
            Child = view,
            Padding = new Thickness(8),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            RequestedTheme = (window?.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        var popup = new Popup { Child = host, XamlRoot = root, IsOpen = true };
        try
        {
            await Task.Delay(400);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(host);
            var pixels = await bitmap.GetPixelsAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var dpi = root.RasterizationScale * 96;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels.ToArray());
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            stream.Seek(0);
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }
            await File.WriteAllBytesAsync(path, bytes);
        }
        finally
        {
            popup.IsOpen = false;
            popup.Child = null;
        }
    }
}
