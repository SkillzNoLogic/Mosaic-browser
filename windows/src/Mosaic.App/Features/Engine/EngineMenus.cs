using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Engine;
using Windows.System;

namespace Mosaic.App.Features.Engine;

/// <summary>
/// Menus of the engine area: the Chromium page context menu (built from
/// <see cref="PageContextMenu"/>), the page commands for the shell's "menu" component, and the
/// keyboard shortcuts for them.
/// </summary>
public static class EngineMenus
{
    /// <summary>MenuFlyout items for <paramref name="entries"/>; <paramref name="chosen"/> runs the picked one.</summary>
    internal static IEnumerable<MenuFlyoutItemBase> Build(IReadOnlyList<PageMenuEntry> entries, Action<PageMenuEntry> chosen)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                yield return new MenuFlyoutSeparator();
                continue;
            }
            if (entry.Children is { Count: > 0 } children)
            {
                var submenu = new MenuFlyoutSubItem { Text = entry.Label, IsEnabled = entry.IsEnabled };
                foreach (var child in Build(children, chosen)) submenu.Items.Add(child);
                yield return submenu;
                continue;
            }
            MenuFlyoutItem item = entry.IsCheckable
                ? new ToggleMenuFlyoutItem { IsChecked = entry.IsChecked }
                : new MenuFlyoutItem();
            item.Text = entry.Label;
            item.IsEnabled = entry.IsEnabled;
            if (entry.Icon is { } icon) item.Icon = Icons.Make(icon);
            if (!string.IsNullOrEmpty(entry.Shortcut)) item.KeyboardAcceleratorTextOverride = entry.Shortcut;
            item.Click += (_, _) => chosen(entry);
            yield return item;
        }
    }

    /// <summary>
    /// The page commands, for the shell's main menu ("menu" component): find, zoom, print, PDF,
    /// screenshots, Picture in Picture and developer tools. Rebuild each time the menu opens:
    /// the zoom item shows the page's zoom.
    /// </summary>
    public static IReadOnlyList<MenuFlyoutItemBase> PageCommands(BrowserModel model)
    {
        var pages = model.Pages;
        var onPage = pages.IsWebPage(model.SelectedTab);
        MenuFlyoutItem Item(string text, string icon, string? shortcut, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = Icons.Make(icon), IsEnabled = onPage && enabled };
            if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
            item.Click += (_, _) => action();
            return item;
        }

        var zoom = new MenuFlyoutSubItem { Text = $"Zoom ({pages.ZoomLabel})", Icon = Icons.Make("plus.magnifyingglass"), IsEnabled = onPage };
        zoom.Items.Add(Item("Zoom In", "plus.magnifyingglass", "Ctrl+Plus", pages.ZoomIn, pages.CanZoomIn));
        zoom.Items.Add(Item("Zoom Out", "minus.magnifyingglass", "Ctrl+Minus", pages.ZoomOut, pages.CanZoomOut));
        zoom.Items.Add(Item("Actual Size", "1.magnifyingglass", "Ctrl+0", pages.ResetZoom, pages.CanResetZoom));

        var screenshot = new MenuFlyoutSubItem { Text = "Take Screenshot", Icon = Icons.Make("camera.viewfinder"), IsEnabled = onPage };
        screenshot.Items.Add(Item("Visible Area", "camera.viewfinder", "Ctrl+Shift+S", () => _ = pages.TakeScreenshotAsync(fullPage: false)));
        screenshot.Items.Add(Item("Full Page", "doc.viewfinder", null, () => _ = pages.TakeScreenshotAsync(fullPage: true)));
        screenshot.Items.Add(new MenuFlyoutSeparator());
        screenshot.Items.Add(Item("Save Visible Area As…", "square.and.arrow.down", null, () => _ = pages.TakeScreenshotAsync(false, ScreenshotTarget.Ask)));
        screenshot.Items.Add(Item("Save Full Page As…", "square.and.arrow.down", null, () => _ = pages.TakeScreenshotAsync(true, ScreenshotTarget.Ask)));
        screenshot.Items.Add(new MenuFlyoutSeparator());
        screenshot.Items.Add(Item("Copy Visible Area", "doc.on.doc", null, () => _ = pages.TakeScreenshotAsync(false, ScreenshotTarget.Clipboard)));
        screenshot.Items.Add(Item("Copy Full Page", "doc.on.doc", null, () => _ = pages.TakeScreenshotAsync(true, ScreenshotTarget.Clipboard)));

        return
        [
            Item("Find in Page…", "magnifyingglass", "Ctrl+F", pages.ShowFindBar),
            zoom,
            new MenuFlyoutSeparator(),
            Item("Print…", "printer", "Ctrl+P", () => pages.Print()),
            Item("Save Page as PDF…", "doc.richtext", null, () => _ = pages.ExportPdfAsync()),
            screenshot,
            new MenuFlyoutSeparator(),
            Item("Picture in Picture", "pip", null, () => _ = pages.TogglePictureInPictureAsync()),
            Item("Developer Tools", "chevron.left.forwardslash.chevron.right", "F12", () => pages.ToggleDevTools()),
        ];
    }

    /// <summary>
    /// The page shortcuts, as keyboard accelerators on an element that is always in the window
    /// (they apply wherever focus is in Mosaic's own UI and in Chromium pages; inside a Gecko
    /// page the agent forwards Ctrl+F / F3 and Gecko handles the rest itself).
    /// </summary>
    internal static void AddShortcuts(UIElement owner, BrowserModel model)
    {
        var pages = model.Pages;
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                action();
            };
            owner.KeyboardAccelerators.Add(accelerator);
        }
        const VirtualKey plus = (VirtualKey)0xBB;  // VK_OEM_PLUS, the "=" / "+" key
        const VirtualKey minus = (VirtualKey)0xBD; // VK_OEM_MINUS

        Add(VirtualKey.F, VirtualKeyModifiers.Control, pages.ShowFindBar);
        Add(VirtualKey.F3, VirtualKeyModifiers.None, () => pages.FindNext());
        Add(VirtualKey.F3, VirtualKeyModifiers.Shift, () => pages.FindNext(backwards: true));
        Add(VirtualKey.G, VirtualKeyModifiers.Control, () => pages.FindNext());
        Add(VirtualKey.G, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => pages.FindNext(backwards: true));
        Add(plus, VirtualKeyModifiers.Control, pages.ZoomIn);
        Add(plus, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, pages.ZoomIn);
        Add(VirtualKey.Add, VirtualKeyModifiers.Control, pages.ZoomIn);
        Add(minus, VirtualKeyModifiers.Control, pages.ZoomOut);
        Add(VirtualKey.Subtract, VirtualKeyModifiers.Control, pages.ZoomOut);
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, pages.ResetZoom);
        Add(VirtualKey.NumberPad0, VirtualKeyModifiers.Control, pages.ResetZoom);
        Add(VirtualKey.P, VirtualKeyModifiers.Control, () => pages.Print());
        Add(VirtualKey.F12, VirtualKeyModifiers.None, () => pages.ToggleDevTools());
        Add(VirtualKey.I, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => pages.ToggleDevTools());
        Add(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = pages.TakeScreenshotAsync(fullPage: false));
    }
}
