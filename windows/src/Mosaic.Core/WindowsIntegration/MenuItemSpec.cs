namespace Mosaic.Core.WindowsIntegration;

/// <summary>
/// One entry of a context menu, independent of WinUI so areas can build menus in Core and test
/// them; the app's <c>ContextMenus.Show</c> turns a list of these into a Fluent <c>MenuFlyout</c>.
/// </summary>
/// <param name="Title">The label (an access key may be marked with <c>&amp;</c>, as in "&amp;Copy").</param>
/// <param name="Invoke">What clicking does (null for a submenu or a disabled entry).</param>
public sealed record MenuItemSpec(string Title, Action? Invoke = null)
{
    /// <summary>An <c>Icons</c> name ("copy", "doc.on.doc", …).</summary>
    public string? Icon { get; init; }

    /// <summary>Shortcut text shown on the right ("Ctrl+C"); display only.</summary>
    public string? Shortcut { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Non-null makes it a toggle item.</summary>
    public bool? Checked { get; init; }

    /// <summary>Non-empty makes it a submenu.</summary>
    public IReadOnlyList<MenuItemSpec> Children { get; init; } = [];

    public bool IsSeparator { get; init; }

    /// <summary>Destructive actions (Delete, Close all) are drawn in the system's critical colour.</summary>
    public bool Destructive { get; init; }

    public static MenuItemSpec Separator { get; } = new("-") { IsSeparator = true };

    /// <summary>The title without the access-key marker, and the access key (or null).</summary>
    public (string Text, char? AccessKey) SplitAccessKey()
    {
        var index = Title.IndexOf('&');
        while (index >= 0 && index + 1 < Title.Length && Title[index + 1] == '&') index = Title.IndexOf('&', index + 2);
        var key = index >= 0 && index + 1 < Title.Length ? char.ToUpperInvariant(Title[index + 1]) : (char?)null;
        var text = (index >= 0 ? Title.Remove(index, 1) : Title).Replace("&&", "&");
        return (text, key);
    }

    /// <summary>Drops leading, trailing and doubled separators (menus built from optional parts).</summary>
    public static IReadOnlyList<MenuItemSpec> Tidy(IEnumerable<MenuItemSpec> items)
    {
        var result = new List<MenuItemSpec>();
        foreach (var item in items)
        {
            if (item.IsSeparator && (result.Count == 0 || result[^1].IsSeparator)) continue;
            result.Add(item);
        }
        while (result.Count > 0 && result[^1].IsSeparator) result.RemoveAt(result.Count - 1);
        return result;
    }
}
