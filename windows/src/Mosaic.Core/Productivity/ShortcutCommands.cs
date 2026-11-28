using Mosaic.Core.Browser;

namespace Mosaic.Core.Productivity;

/// <summary>
/// A command that can carry keyboard shortcuts.
/// </summary>
/// <param name="Id">Stable identifier, "Menu/Item" as on macOS (where it is the menu path), e.g. "File/New Tab".</param>
/// <param name="Title">What the Shortcuts settings page lists.</param>
/// <param name="Group">Where it lives, e.g. "File" or "View".</param>
/// <param name="Defaults">
/// The shortcuts it comes with, the main one first (shown in menus and the palette). Windows
/// commands often have several (Ctrl+R and F5 both reload); a user's change replaces them all.
/// </param>
public sealed record ShortcutCommand(string Id, string Title, string Group, IReadOnlyList<KeyCombo> Defaults)
{
    public ShortcutCommand(string id, string title, string group, params KeyCombo[] defaults)
        : this(id, title, group, (IReadOnlyList<KeyCombo>)defaults) { }

    public KeyCombo? DefaultCombo => Defaults.Count > 0 ? Defaults[0] : null;

    public bool Equals(ShortcutCommand? other) =>
        other is not null && Id == other.Id && Title == other.Title && Group == other.Group && Defaults.SequenceEqual(other.Defaults);

    public override int GetHashCode() => HashCode.Combine(Id, Title, Group);
}

/// <summary>
/// The catalogue of commands that can have shortcuts, and what each does. On macOS the main
/// menu is the catalogue; Windows has no menu bar, so areas register here instead:
/// <code>
/// ShortcutCommands.Register(
///     new ShortcutCommand("Edit/Find…", "Find…", "Edit", KeyCombo.Ctrl('F'), KeyCombo.Plain(KeyCombo.FunctionKey(3))),
///     model => model.ShowFindBar());
/// </code>
/// Registering an id again replaces the command (an area can take over a built-in). Each
/// model's <see cref="ShortcutManager"/> applies the user's changes, turns them into the
/// window's keyboard accelerators and forwards them from Gecko pages.
/// </summary>
/// <remarks>
/// Defaults follow Windows conventions (Chrome/Edge/Firefox on Windows): Ctrl for ⌘, F5 to
/// reload, Alt+Left/Right for back and forward, Ctrl+Tab and Ctrl+Page Down for tabs, F11
/// for full screen. Copy/paste/undo stay with text fields (see <see cref="KeyCombo.SystemReserved"/>).
/// </remarks>
public static class ShortcutCommands
{
    private sealed record Entry(ShortcutCommand Command, Action<BrowserModel> Perform, Func<BrowserModel, bool>? CanPerform);

    private static readonly List<Entry> entries = [];

    /// <summary>Raised (on the registering thread) when a command is added, replaced or removed.</summary>
    public static event Action? Changed;

    static ShortcutCommands()
    {
        foreach (var (command, perform, canPerform) in BuiltIn())
        {
            entries.Add(new Entry(command, perform, canPerform));
        }
    }

    /// <summary>Every command, in catalogue order (built-ins first, then in registration order).</summary>
    public static IReadOnlyList<ShortcutCommand> All
    {
        get { lock (entries) return entries.Select(entry => entry.Command).ToList(); }
    }

    /// <param name="command">Its id, title, group and default shortcuts.</param>
    /// <param name="perform">What the shortcut does.</param>
    /// <param name="canPerform">When it returns false the shortcut does nothing (like a disabled menu item).</param>
    public static void Register(ShortcutCommand command, Action<BrowserModel> perform, Func<BrowserModel, bool>? canPerform = null)
    {
        lock (entries)
        {
            var index = entries.FindIndex(entry => entry.Command.Id == command.Id);
            var entry = new Entry(command, perform, canPerform);
            if (index >= 0) entries[index] = entry;
            else entries.Add(entry);
        }
        Changed?.Invoke();
    }

    public static void Unregister(string id)
    {
        int removed;
        lock (entries) removed = entries.RemoveAll(entry => entry.Command.Id == id);
        if (removed > 0) Changed?.Invoke();
    }

    public static ShortcutCommand? Find(string id)
    {
        lock (entries) return entries.FirstOrDefault(entry => entry.Command.Id == id)?.Command;
    }

    /// <summary>Runs the command; false when it is unknown or cannot run now.</summary>
    internal static bool Perform(string id, BrowserModel model)
    {
        Entry? entry;
        lock (entries) entry = entries.FirstOrDefault(e => e.Command.Id == id);
        if (entry is null) return false;
        if (entry.CanPerform is { } can && !can(model)) return false;
        entry.Perform(model);
        return true;
    }

    internal static bool CanPerform(string id, BrowserModel model)
    {
        Entry? entry;
        lock (entries) entry = entries.FirstOrDefault(e => e.Command.Id == id);
        return entry is not null && (entry.CanPerform?.Invoke(model) ?? true);
    }

    // ---- Built-in commands (what BrowserModel can do today) ----

    public const string NewTab = "File/New Tab";
    public const string OpenLocation = "File/Open Location…";
    public const string CloseTab = "File/Close Tab";
    public const string Reload = "View/Reload Page";
    public const string Stop = "View/Stop";
    public const string ToggleEngine = "View/Switch Rendering Engine";
    public const string NextSearchEngine = "View/Next Search Engine";
    public const string ToggleBlocker = "View/Block Ads and Trackers";
    public const string ReaderView = "View/Reader View";
    public const string Back = "History/Back";
    public const string Forward = "History/Forward";
    public const string NextTab = "Window/Show Next Tab";
    public const string PreviousTab = "Window/Show Previous Tab";
    public const string Extensions = "Window/Extensions";
    public const string CommandPalette = "Tools/Command Palette…";
    public const string SelectLastTab = "Tab/Select Last Tab";

    /// <summary>"Tab/Select Tab 1" … "Tab/Select Tab 8".</summary>
    public static string SelectTab(int number) => $"Tab/Select Tab {number}";

    private static IEnumerable<(ShortcutCommand, Action<BrowserModel>, Func<BrowserModel, bool>?)> BuiltIn()
    {
        static bool HasPage(BrowserModel model) => model.SelectedTab is { IsStartPage: false };

        yield return (new(NewTab, "New Tab", "File", KeyCombo.Ctrl('T')), model => model.NewTab(), null);
        yield return (new(OpenLocation, "Open Location…", "File", KeyCombo.Ctrl('L'), KeyCombo.Alt('D'), KeyCombo.Plain(KeyCombo.FunctionKey(6))),
            model => model.FocusAddressBar(activating: true), null);
        yield return (new(CloseTab, "Close Tab", "File", KeyCombo.Ctrl('W'), KeyCombo.Ctrl(KeyCombo.FunctionKey(4))),
            model => model.CloseSelectedTab(), model => model.SelectedTab is not null);

        yield return (new(Reload, "Reload Page", "View", KeyCombo.Ctrl('R'), KeyCombo.Plain(KeyCombo.FunctionKey(5))),
            model => model.Reload(), model => model.CanReload);
        yield return (new(Stop, "Stop", "View"), model => model.StopLoading(), model => model.IsLoading);
        yield return (new(ToggleEngine, "Switch Rendering Engine", "View", KeyCombo.CtrlShift('E')), model => model.ToggleEngine(), null);
        yield return (new(NextSearchEngine, "Next Search Engine", "View", KeyCombo.CtrlShift('S')), model => model.CycleSearchEngine(), null);
        // Ctrl+Shift+B is the bookmarks bar (as in Chrome and Edge, and Mosaic on macOS).
        yield return (new(ToggleBlocker, "Block Ads and Trackers", "View",
                new KeyCombo('B', ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift)),
            model => model.ToggleBlocker(), model => model.Blocker.IsAvailable);
        // F9 is the reader key on Windows (Edge's Immersive Reader, Firefox's reader view).
        yield return (new(ReaderView, "Reader View", "View", KeyCombo.Plain(KeyCombo.FunctionKey(9))),
            model => model.ToggleReaderMode(), HasPage);

        yield return (new(Back, "Back", "History", KeyCombo.Alt(KeyCombo.Left)), model => model.GoBack(), model => model.CanGoBack);
        yield return (new(Forward, "Forward", "History", KeyCombo.Alt(KeyCombo.Right)), model => model.GoForward(), model => model.CanGoForward);

        yield return (new(NextTab, "Show Next Tab", "Window", KeyCombo.Ctrl(KeyCombo.Tab), KeyCombo.Ctrl(KeyCombo.PageDown)),
            model => model.SelectAdjacentTab(1), model => model.Tabs.Count > 1);
        yield return (new(PreviousTab, "Show Previous Tab", "Window", KeyCombo.CtrlShift(KeyCombo.Tab), KeyCombo.Ctrl(KeyCombo.PageUp)),
            model => model.SelectAdjacentTab(-1), model => model.Tabs.Count > 1);
        yield return (new(Extensions, "Extensions", "Window", KeyCombo.CtrlShift('X')), model => model.ShowExtensions = true, null);

        for (var number = 1; number <= 8; number++)
        {
            var index = number - 1;
            yield return (new(SelectTab(number), $"Select Tab {number}", "Tab", KeyCombo.Ctrl('0' + number)),
                model => model.Select(model.Tabs[index]), model => model.Tabs.Count > index);
        }
        yield return (new(SelectLastTab, "Select Last Tab", "Tab", KeyCombo.Ctrl('9')),
            model => model.Select(model.Tabs[^1]), model => model.Tabs.Count > 0);

        yield return (new(CommandPalette, "Command Palette…", "Tools", KeyCombo.Ctrl('K')), model => model.ShowCommandPalette(), null);
    }
}
