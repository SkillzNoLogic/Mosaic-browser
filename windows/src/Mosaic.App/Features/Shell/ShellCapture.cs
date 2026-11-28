using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Screenshots of the shell for CI (<c>shellScreenshot &lt;path.png&gt;</c>) and a gallery of every
/// Mosaic, light and dark, and the shell's settings pages (<c>shellGallery &lt;folder&gt;</c>). The
/// gallery shows each layout through an override, so the user's layouts are never changed.
/// The engines' page is a native or composited surface and is not in these images: they show
/// Mosaic's chrome around an empty page card (the window <c>screenshot</c> shows everything).
/// </summary>
internal static class ShellCapture
{
    public static async Task CaptureAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var dpi = 96 * (element.XamlRoot?.RasterizationScale ?? 1);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
        await encoder.FlushAsync();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        stream.Seek(0);
        await using var file = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(file);
    }

    /// <summary>The gallery's scenes: name, layout, and the settings page to open (or none).</summary>
    public static IEnumerable<(string Name, MosaicLayout Layout, SettingsCategory? Settings, bool SidebarCollapsed)> Scenes()
    {
        var edge = MosaicLayout.Factory(MosaicPresetID.Edge);
        yield return ("zenArc", MosaicLayout.Factory(MosaicPresetID.ZenArc), null, false);
        yield return ("zenArc-collapsed", MosaicLayout.Factory(MosaicPresetID.ZenArc), null, true);
        yield return ("chrome", MosaicLayout.Factory(MosaicPresetID.Chrome), null, false);
        yield return ("opera", MosaicLayout.Factory(MosaicPresetID.Opera), null, false);
        yield return ("opera-gx", MosaicLayout.Factory(MosaicPresetID.Opera).WithGXStyling(), null, false);
        yield return ("edge", edge, null, false);
        yield return ("edge-vertical", edge.WithTabPlacement(TabPlacement.SidebarLeading), null, false);
        yield return ("edge-vertical-collapsed", edge.WithTabPlacement(TabPlacement.SidebarLeading), null, true);
        yield return ("settings-mosaics", edge, SettingsCategory.Mosaics, false);
        yield return ("settings-general", edge, SettingsCategory.General, false);
        yield return ("settings-accessibility", edge, SettingsCategory.Accessibility, false);
        yield return ("settings-search", edge, null, false);
    }

    public static async Task<JsonArray> GalleryAsync(ShellRoot root, string folder)
    {
        var model = root.Model;
        var written = new JsonArray();
        var settingsBefore = model.SettingsPage;
        var collapsedBefore = model.ShellSettings.SidebarCollapsed;
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var (name, layout, settings, collapsed) in Scenes())
            {
                foreach (var scheme in new[] { ColorSchemePreference.Light, ColorSchemePreference.Dark })
                {
                    root.LayoutOverride = layout;
                    root.SchemeOverride = layout.HasGXStyling ? ColorSchemePreference.Dark : scheme;
                    if (layout.HasGXStyling && scheme == ColorSchemePreference.Light) continue;
                    model.ShellSettings.SidebarCollapsed = collapsed;
                    if (name == "settings-search")
                    {
                        model.ShowSettings(SettingsCategory.Mosaics);
                        model.SettingsQuery = "mica";
                    }
                    else if (settings is { } category)
                    {
                        model.SettingsQuery = "";
                        model.ShowSettings(category);
                    }
                    else
                    {
                        model.DismissSettings();
                    }
                    root.ScheduleRebuild();
                    root.UpdateOverlays();
                    await Settle(root);
                    var path = Path.Combine(folder, $"shell-{name}-{scheme.Raw()}.png");
                    await CaptureAsync(root, path);
                    written.Add(path);
                }
            }
        }
        finally
        {
            root.LayoutOverride = null;
            root.SchemeOverride = null;
            model.SettingsQuery = "";
            model.ShellSettings.SidebarCollapsed = collapsedBefore;
            if (settingsBefore is { } before) model.ShowSettings(before);
            else model.DismissSettings();
            root.ScheduleRebuild();
            root.UpdateOverlays();
        }
        return written;
    }

    /// <summary>Lets layout and rendering finish (a few frames).</summary>
    public static async Task Settle(FrameworkElement root)
    {
        for (var i = 0; i < 3; i++)
        {
            var done = new TaskCompletionSource();
            root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => done.TrySetResult());
            await done.Task;
            await Task.Delay(120);
        }
        root.UpdateLayout();
    }
}
