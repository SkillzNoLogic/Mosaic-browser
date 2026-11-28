using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.WindowsIntegration;
using Windows.Foundation;

namespace Mosaic.App.Features.System;

/// <summary>
/// Native (Fluent) context menus for every area, built from <see cref="MenuItemSpec"/>:
/// <code>
/// ContextMenus.Show(tabView, e.GetPosition(tabView),
/// [
///     new MenuItemSpec("&amp;Duplicate", () =&gt; model.Duplicate(tab)) { Icon = "plus.square.on.square" },
///     new MenuItemSpec("Pin", () =&gt; model.Pin(tab)) { Icon = "pin", Checked = tab.IsPinned },
///     MenuItemSpec.Separator,
///     new MenuItemSpec("Close", () =&gt; model.Close(tab)) { Icon = "xmark", Shortcut = "Ctrl+W" },
/// ]);
/// </code>
/// Menus are windowed popups (<c>ShouldConstrainToRootBounds = false</c>), so they draw above
/// Gecko's child window and may extend past Mosaic's edges, like Windows' own menus. While a menu
/// is open the page is also covered (<see cref="FeatureMounts.CoverPage"/>) unless the caller
/// passes <c>coverPage: false</c> — do that for a menu about the page itself, so the page stays
/// visible behind its own context menu.
/// </summary>
public static class ContextMenus
{
    private const string CoverId = "system.contextMenu";
    private static MenuFlyout? open;

    /// <summary>Whether a menu shown through here is open (for <c>dump</c>).</summary>
    public static bool IsOpen => open is not null;

    public static int ShownCount { get; private set; }

    /// <summary>Builds the flyout (exposed so areas can attach it as a <c>ContextFlyout</c>).</summary>
    public static MenuFlyout Build(IEnumerable<MenuItemSpec> items, bool coverPage = true)
    {
        var flyout = new MenuFlyout { ShouldConstrainToRootBounds = false };
        foreach (var item in MenuItemSpec.Tidy(items)) flyout.Items.Add(Element(item));
        flyout.Opening += (_, _) =>
        {
            open = flyout;
            ShownCount++;
            if (coverPage) FeatureMounts.CoverPage(CoverId, true);
        };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(open, flyout)) open = null;
            if (coverPage) FeatureMounts.CoverPage(CoverId, false);
        };
        return flyout;
    }

    /// <summary>Shows a menu at <paramref name="position"/> (relative to <paramref name="target"/>; null: under the pointer's last spot / the target's corner).</summary>
    public static MenuFlyout Show(FrameworkElement target, Point? position, IEnumerable<MenuItemSpec> items, bool coverPage = true)
    {
        var flyout = Build(items, coverPage);
        var options = new FlyoutShowOptions { ShowMode = FlyoutShowMode.Standard };
        if (position is { } point) options.Position = point;
        else options.Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(target, options);
        return flyout;
    }

    /// <summary>Closes the menu shown through here, if any.</summary>
    public static void CloseOpen() => open?.Hide();

    private static MenuFlyoutItemBase Element(MenuItemSpec item)
    {
        if (item.IsSeparator) return new MenuFlyoutSeparator();
        var (text, accessKey) = item.SplitAccessKey();

        if (item.Children.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = text, IsEnabled = item.Enabled };
            if (item.Icon is { } subIcon) sub.Icon = Icons.Make(subIcon);
            if (accessKey is { } subKey) sub.AccessKey = subKey.ToString();
            foreach (var child in MenuItemSpec.Tidy(item.Children)) sub.Items.Add(Element(child));
            return sub;
        }

        MenuFlyoutItem element = item.Checked is { } isChecked
            ? new ToggleMenuFlyoutItem { IsChecked = isChecked }
            : new MenuFlyoutItem();
        element.Text = text;
        element.IsEnabled = item.Enabled && item.Invoke is not null;
        if (item.Icon is { } icon) element.Icon = Icons.Make(icon);
        if (item.Shortcut is { } shortcut) element.KeyboardAcceleratorTextOverride = shortcut;
        if (accessKey is { } key) element.AccessKey = key.ToString();
        if (item.Destructive && Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush) && brush is Brush critical)
            element.Foreground = critical;
        if (item.Invoke is { } invoke) element.Click += (_, _) => invoke();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
        return element;
    }
}
