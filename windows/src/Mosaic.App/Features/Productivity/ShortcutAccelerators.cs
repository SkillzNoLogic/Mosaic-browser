using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Windows.System;

namespace Mosaic.App.Features.Productivity;

/// <summary>
/// The user's shortcuts as <see cref="KeyboardAccelerator"/>s on the window's root element,
/// rebuilt whenever they change (applied live, no restart). Accelerators on the root with no
/// scope owner work wherever focus is in the window, including text boxes; WinUI's WebView2
/// forwards a page's accelerator keys to them. Gecko pages forward theirs through the agent
/// (<see cref="ShortcutManager.GeckoForwardingPairs"/>).
/// </summary>
internal sealed class ShortcutAccelerators(BrowserModel model, Window window)
{
    private readonly ShortcutManager shortcuts = model.Productivity.Shortcuts;
    private readonly List<KeyboardAccelerator> installed = [];
    private UIElement? root;

    public void Attach()
    {
        shortcuts.Changed += Rebuild;
        // The shell may replace the window's content after start-up: follow it.
        window.Activated += (_, _) =>
        {
            if (!ReferenceEquals(window.Content, root)) Rebuild();
        };
        Rebuild();
    }

    private void Rebuild()
    {
        if (root is not null)
        {
            foreach (var accelerator in installed) root.KeyboardAccelerators.Remove(accelerator);
        }
        installed.Clear();
        root = window.Content;
        if (root is null) return;
        // No accelerator tooltips on everything in the window.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        foreach (var (combo, commandId) in shortcuts.ActiveBindings)
        {
            var accelerator = new KeyboardAccelerator
            {
                Key = (VirtualKey)combo.Key,
                Modifiers = Modifiers(combo.Modifiers),
            };
            var id = commandId;
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                // Not from inside the key event: a forwarded WebView2 key blocks its browser
                // process until handled, and the command may call back into WebView2.
                window.DispatcherQueue.TryEnqueue(() => shortcuts.Perform(id));
            };
            root.KeyboardAccelerators.Add(accelerator);
            installed.Add(accelerator);
        }
    }

    internal static VirtualKeyModifiers Modifiers(ShortcutModifiers modifiers)
    {
        var result = VirtualKeyModifiers.None;
        if (modifiers.HasFlag(ShortcutModifiers.Control)) result |= VirtualKeyModifiers.Control;
        if (modifiers.HasFlag(ShortcutModifiers.Shift)) result |= VirtualKeyModifiers.Shift;
        if (modifiers.HasFlag(ShortcutModifiers.Alt)) result |= VirtualKeyModifiers.Menu;
        return result;
    }
}
