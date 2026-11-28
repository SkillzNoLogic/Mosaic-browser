using Mosaic.Core.Browser;

namespace Mosaic.Core.Tabs;

/// <summary>
/// A named set of tabs ("Work", "School"…). Every tab belongs to exactly one workspace.
/// Shared by the tabs area (which owns the behaviour) and the shell (which draws it).
/// </summary>
/// <remarks>Only <see cref="BrowserModel"/> changes it; views observe it.</remarks>
public sealed class Workspace : ObservableObject
{
    private string name;
    private string symbol;

    public Workspace(Guid id, string name, string symbol = SessionWorkspace.DefaultSymbol)
    {
        Id = id;
        this.name = name;
        this.symbol = symbol;
    }

    public Guid Id { get; }

    public string Name { get => name; internal set => Set(ref name, value); }

    /// <summary>
    /// Icon name shown in the switcher: the SF Symbol names the macOS app saves ("person",
    /// "briefcase"…), so session files move between platforms; drawn with <c>Icons.Make</c>.
    /// </summary>
    public string Symbol { get => symbol; internal set => Set(ref symbol, value); }

    /// <summary>Icons offered for workspaces (same list as macOS).</summary>
    public static readonly string[] Symbols =
    [
        "person", "briefcase", "house", "book", "graduationcap", "hammer", "paintbrush",
        "gamecontroller", "cart", "heart", "star", "globe", "square.grid.2x2",
    ];

    internal SessionWorkspace ToSession() => new(Id, name, symbol);

    internal static Workspace FromSession(SessionWorkspace saved) => new(saved.Id, saved.Name, saved.Symbol);
}

/// <summary>The colours a tab group can have (names as saved by macOS).</summary>
public enum TabGroupColor { Grey, Blue, Red, Yellow, Green, Pink, Purple, Cyan, Orange }

public static class TabGroupColors
{
    public static string Id(this TabGroupColor color) => color.ToString().ToLowerInvariant();

    public static TabGroupColor? FromId(string? id) =>
        Enum.GetValues<TabGroupColor>().Cast<TabGroupColor?>().FirstOrDefault(c => c!.Value.Id() == id);

    public static string DisplayName(this TabGroupColor color) => color.ToString();

    /// <summary>The swatch as sRGB, close to the Windows/Fluent accent palette.</summary>
    public static (byte R, byte G, byte B) Rgb(this TabGroupColor color) => color switch
    {
        TabGroupColor.Grey => (0x8A, 0x88, 0x86),
        TabGroupColor.Blue => (0x00, 0x78, 0xD4),
        TabGroupColor.Red => (0xD1, 0x34, 0x38),
        TabGroupColor.Yellow => (0xC1, 0x9C, 0x00),
        TabGroupColor.Green => (0x10, 0x7C, 0x10),
        TabGroupColor.Pink => (0xE3, 0x00, 0x8C),
        TabGroupColor.Purple => (0x88, 0x17, 0x98),
        TabGroupColor.Cyan => (0x00, 0x99, 0xBC),
        _ => (0xCA, 0x50, 0x10),
    };
}

/// <summary>A collapsible, named, coloured group of tabs within a workspace.</summary>
public sealed class TabGroup : ObservableObject
{
    private string name;
    private TabGroupColor color;
    private bool isCollapsed;

    public TabGroup(Guid id, string name, TabGroupColor color = TabGroupColor.Blue, bool isCollapsed = false)
    {
        Id = id;
        this.name = name;
        this.color = color;
        this.isCollapsed = isCollapsed;
    }

    public Guid Id { get; }

    public string Name
    {
        get => name;
        internal set
        {
            if (Set(ref name, value)) Notify(nameof(DisplayName));
        }
    }

    public TabGroupColor Color { get => color; internal set => Set(ref color, value); }

    public bool IsCollapsed { get => isCollapsed; internal set => Set(ref isCollapsed, value); }

    /// <summary>What to show for a group without a name.</summary>
    public string DisplayName => name.Length == 0 ? "Unnamed group" : name;

    internal SessionGroup ToSession() => new(Id, name, color.Id(), isCollapsed);

    internal static TabGroup FromSession(SessionGroup saved) =>
        new(saved.Id, saved.Name, TabGroupColors.FromId(saved.Color) ?? TabGroupColor.Blue, saved.IsCollapsed);
}
