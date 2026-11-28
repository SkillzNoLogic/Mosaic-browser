using Mosaic.Core.Browser;
using Mosaic.Core.Shell;

namespace Mosaic.Core.Productivity;

/// <summary>
/// Hooks for features other areas own, which the productivity area cannot reference while they
/// are being built in parallel. The owning area (or the integrator) sets them once at start-up;
/// until then the palette leaves the matching entries out.
/// </summary>
public static class ProductivityIntegration
{
    /// <summary>Opens Settings at a category, inside the window (shell).</summary>
    public static Action<BrowserModel, SettingsCategory>? OpenSettings { get; set; }

    /// <summary>Puts text on the clipboard (the app sets this; Core has no clipboard).</summary>
    public static Action<string>? CopyText { get; set; }

    /// <summary>
    /// The most visited sites, best first, for Speed Dial (library): at most the given number of
    /// (title, address) pairs. Called on the UI thread; must not block.
    /// </summary>
    public static Func<BrowserModel, int, IReadOnlyList<(string Title, string Url)>>? TopSites { get; set; }

    /// <summary>Opens one of Mosaic's own pages (<c>mosaic://&lt;host&gt;</c>); by default it is loaded like an address.</summary>
    public static Action<BrowserModel, string> OpenInternalPage { get; set; } =
        (model, host) => model.OpenFromProductivity($"mosaic://{host}");
}

/// <summary>
/// Command-palette actions for features built by other areas (macOS:
/// <c>IntegrationPaletteActions.swift</c>). Every command another area adds to the shortcut
/// catalogue (<see cref="ShortcutCommands.Register"/>) is offered here automatically, with its
/// current shortcut, so a feature that has a shortcut is also in the palette without extra work.
/// Areas with richer palette entries register their own provider (<see cref="PaletteRegistry"/>).
/// </summary>
internal static class IntegrationPaletteActions
{
    /// <summary>Catalogue commands the built-in actions already offer (under their macOS palette ids).</summary>
    private static readonly HashSet<string> Covered =
    [
        ShortcutCommands.NewTab, ShortcutCommands.OpenLocation, ShortcutCommands.CloseTab, ShortcutCommands.Reload,
        ShortcutCommands.Stop, ShortcutCommands.ToggleEngine, ShortcutCommands.NextSearchEngine, ShortcutCommands.ToggleBlocker,
        ShortcutCommands.ReaderView, ShortcutCommands.Back, ShortcutCommands.Forward, ShortcutCommands.NextTab,
        ShortcutCommands.PreviousTab, ShortcutCommands.Extensions, ShortcutCommands.CommandPalette, ShortcutCommands.SelectLastTab,
        .. Enumerable.Range(1, 8).Select(ShortcutCommands.SelectTab),
    ];

    public static IEnumerable<PaletteAction> Actions(BrowserModel model)
    {
        var shortcuts = model.Productivity.Shortcuts;
        foreach (var command in shortcuts.Commands)
        {
            if (Covered.Contains(command.Id) || !ShortcutCommands.CanPerform(command.Id, model)) continue;
            var id = command.Id;
            yield return new PaletteAction($"command:{id}", command.Title, () => ShortcutCommands.Perform(id, model))
            {
                Keywords = [command.Group],
                Icon = "command",
                Shortcut = shortcuts.Combo(command),
            };
        }
    }
}
