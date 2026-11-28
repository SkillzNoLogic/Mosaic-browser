using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;
using Mosaic.Core.Tabs;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace Mosaic.App.Features.Tabs;

/// <summary>
/// Plugs the tabs area into the app: the Tabs &amp; Workspaces and Performance settings pages,
/// the "workspaces" layout component, the overlay (tab search, duplicate-tab notice, crash
/// recovery), and at startup the session restore, the keyboard shortcuts and the automation
/// commands. Registering only stores delegates; the session is read after the window is up.
/// </summary>
internal static class TabsMounts
{
    [ModuleInitializer]
    internal static void Register()
    {
        FeatureMounts.SettingsPages[SettingsCategory.TabsAndWorkspaces] = model => new TabsSettingsPage(model);
        FeatureMounts.SettingsPages[SettingsCategory.Performance] = model => new PerformanceSettingsPage(model);
        FeatureMounts.Components["workspaces"] = model => new WorkspaceSwitcher(model);
        FeatureMounts.Overlays.Add(("tabs", model => new TabsOverlay(model)));
        FeatureMounts.Startup.Add(("tabs", Start));
    }

    private static void Start(BrowserModel model, Window window)
    {
        TabsAutomation.Register();
        AutomationRegistry.Register((m, command, value, output) =>
            command == "renderTabsView" ? RenderAsync(m, window, value, output) : Task.FromResult<AutomationReply?>(null));
        AttachShortcuts(model, window);
        _ = StartTabsAsync(model);
    }

    private static async Task StartTabsAsync(BrowserModel model)
    {
        try
        {
            await model.StartTabsAsync();
        }
        catch (Exception error)
        {
            // The session is a convenience; Mosaic runs without it.
            model.Notice = $"Your tabs could not be restored: {error.Message}";
        }
    }

    /// <summary>
    /// Ctrl+Shift+T, Ctrl+Shift+A, Ctrl+1…9 and the workspace keys while Mosaic's own controls
    /// have focus. Keys pressed in a Gecko page come as agent events; Chromium pages need the
    /// winui area to pass unhandled keys to <see cref="TabsShortcuts.Handle"/>.
    /// </summary>
    private static void AttachShortcuts(BrowserModel model, Window window)
    {
        if (window.Content is not UIElement root) return;
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        void Add(VirtualKey key, VirtualKeyModifiers modifiers)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                var mods = ShortcutModifiers.None;
                if (modifiers.HasFlag(VirtualKeyModifiers.Control)) mods |= ShortcutModifiers.Control;
                if (modifiers.HasFlag(VirtualKeyModifiers.Shift)) mods |= ShortcutModifiers.Shift;
                if (modifiers.HasFlag(VirtualKeyModifiers.Menu)) mods |= ShortcutModifiers.Alt;
                e.Handled = TabsShortcuts.Handle(model, (int)key, mods);
            };
            root.KeyboardAccelerators.Add(accelerator);
        }
        Add(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        Add(VirtualKey.A, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        for (var key = VirtualKey.Number1; key <= VirtualKey.Number9; key++) Add(key, VirtualKeyModifiers.Control);
        Add(VirtualKey.Right, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);
        Add(VirtualKey.Left, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);
        Add(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);
    }

    /// <summary>
    /// Automation <c>renderTabsView</c> (as on macOS): draws one of the area's views into a PNG at
    /// <paramref name="output"/>, so it can be checked before the shell places it. Names:
    /// tabSearch, overlay, tabsSettings, performanceSettings, workspaces.
    /// </summary>
    private static async Task<AutomationReply?> RenderAsync(BrowserModel model, Window window, string name, string? output)
    {
        if (output is null) return AutomationRegistry.Error("renderTabsView needs \"out\": the PNG to write.");
        if (window.Content?.XamlRoot is not { } root) return AutomationRegistry.Error("The window has no content yet.");
        (FrameworkElement view, double width, double height) = name switch
        {
            "tabSearch" => ((FrameworkElement)new TabSearchPanel(model) { Margin = new Thickness(30) }, 640d, 560d),
            "overlay" => (new TabsOverlay(model), 1000d, 700d),
            "tabsSettings" => (new TabsSettingsPage(model), 760d, 900d),
            "performanceSettings" => (new PerformanceSettingsPage(model), 760d, 900d),
            "workspaces" => (new WorkspaceSwitcher(model) { Margin = new Thickness(12) }, 320d, 60d),
            _ => (null!, 0d, 0d),
        };
        if (view is null) return AutomationRegistry.Error($"Unknown view \"{name}\".");

        var host = new Grid
        {
            Width = width,
            Height = height,
            Background = TabsStyle.Resource("SolidBackgroundFillColorBaseBrush", Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
            RequestedTheme = (window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        host.Children.Add(view);
        var popup = new Popup { XamlRoot = root, Child = host };
        FeatureMounts.CoverPage("tabs.render", true);
        try
        {
            popup.IsOpen = true;
            // Let layout (and the views' Loaded handlers) run before drawing.
            await Task.Delay(400);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(host);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var scale = root.RasterizationScale;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(output, bytes);
            return AutomationRegistry.Ok();
        }
        finally
        {
            popup.IsOpen = false;
            popup.Child = null;
            FeatureMounts.CoverPage("tabs.render", false);
        }
    }
}
