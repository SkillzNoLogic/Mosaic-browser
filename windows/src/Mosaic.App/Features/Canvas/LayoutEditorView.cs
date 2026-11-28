using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Canvas;
using Mosaic.Core.Canvas.Interim;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Key = Mosaic.Core.Canvas.CanvasController.KeyDirection;
using DragItem = Mosaic.Core.Canvas.CanvasController.DragItem;

namespace Mosaic.App.Features.Canvas;

/// <summary>
/// Customise Layout (macOS LayoutCanvasView): a live miniature of the window built from the
/// active layout, whose buttons are dragged between rows (WinUI drag and drop) and reordered;
/// Available Items; refused drops explained; context menus; and the keyboard for everything a
/// drag does (arrows select, Alt+arrows move, Enter picks up and drops, Esc cancels, Delete
/// removes, Ctrl+Z / Ctrl+Y undo and redo). Changes apply to the real window as they are made.
/// </summary>
internal sealed class LayoutEditorView : UserControl, ICanvasView
{
    private readonly BrowserModel model;
    private readonly CanvasController canvas;
    private readonly ContentControl miniatureHost;
    private readonly ContentControl optionsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = CanvasUi.Text("", 13);
    private readonly FontIcon statusIcon = Icons.Make("info.circle", 14);
    private readonly ContentControl inspectorHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl paletteHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button undo, redo, reset;
    private string shownKey = "";

    public LayoutEditorView(BrowserModel model, CanvasController canvas)
    {
        this.model = model;
        this.canvas = canvas;
        var column = new StackPanel { Spacing = 14, Padding = new Thickness(20), MaxWidth = 1100 };

        undo = CanvasUi.Button("Undo", "arrow.uturn.backward", canvas.Undo, iconOnly: true, tooltip: "Undo (Ctrl+Z while the layout is focused)");
        redo = CanvasUi.Button("Redo", "arrow.uturn.forward", canvas.Redo, iconOnly: true, tooltip: "Redo (Ctrl+Y while the layout is focused)");
        reset = CanvasUi.Button("Reset", "arrow.counterclockwise", canvas.ResetToPreset, tooltip: "Return this preset to its factory layout and look (undoable)");
        var save = CanvasUi.Button("Save…", "square.and.arrow.down", () => { }, tooltip: "Save this layout and its appearance as a named configuration");
        save.Flyout = SaveFlyout(canvas);
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(CanvasUi.Column(3, CanvasUi.Title("Customise Layout"),
            CanvasUi.Caption("Drag buttons between rows and into Available Items. Changes apply to this window as you make them.")));
        var actions = CanvasUi.Row(8, CanvasUi.Row(2, undo, redo), reset, save);
        actions.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        column.Children.Add(header);
        column.Children.Add(optionsHost);

        miniatureHost = new ContentControl
        {
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            MinHeight = 430,
            CornerRadius = new CornerRadius(16),
        };
        AutomationProperties.SetName(miniatureHost, "Window layout");
        AutomationProperties.SetHelpText(miniatureHost, "Select an item with the arrow keys; Alt and the arrow keys move it.");
        AutomationProperties.SetAutomationId(miniatureHost, "canvas-miniature");
        miniatureHost.PreviewKeyDown += OnKey;
        column.Children.Add(miniatureHost);

        statusIcon.VerticalAlignment = VerticalAlignment.Center;
        column.Children.Add(CanvasUi.Row(8, statusIcon, status));
        column.Children.Add(inspectorHost);
        column.Children.Add(paletteHost);
        Content = new ScrollViewer { Content = column, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Unloaded += (_, _) => canvas.EndGrab(commit: true);
    }

    // MARK: Refresh

    public void Refresh()
    {
        var layout = canvas.Layout;
        undo.IsEnabled = canvas.CanUndo;
        redo.IsEnabled = canvas.CanRedo;
        reset.IsEnabled = !layout.ContentEquals(MosaicLayout.Factory(canvas.ActivePreset));
        reset.Content = CanvasUi.Row(6, Icons.Make("arrow.counterclockwise", 14), new TextBlock { Text = $"Reset to {canvas.ActivePreset.Title()}" });

        if (canvas.Message is { } message && message.Text.Length > 0)
        {
            status.Text = message.Text;
            status.Foreground = message.IsError ? CanvasUi.Brush("TextFillColorPrimaryBrush", Microsoft.UI.Colors.Black) : CanvasUi.Secondary;
            statusIcon.Glyph = Icons.Glyph(message.IsError ? "exclamationmark.triangle.fill" : "info.circle");
            statusIcon.Foreground = message.IsError ? CanvasUi.Caution : CanvasUi.Secondary;
        }
        else
        {
            status.Text = "Arrow keys select · Alt+arrow keys move · Enter picks up and drops · Delete removes · Ctrl+Z undoes";
            status.Foreground = CanvasUi.Secondary;
            statusIcon.Glyph = Icons.Glyph("info.circle");
            statusIcon.Foreground = CanvasUi.Secondary;
        }

        // Rebuilding while a drag is in flight would remove the element being dragged.
        var key = $"{layout.JsonText(pretty: false)}|{canvas.Selection}|{canvas.Grab?.Component}|{canvas.WorkspaceID}|{ActualTheme}";
        if (canvas.Dragging is not null || key == shownKey) return;
        shownKey = key;
        optionsHost.Content = OptionsBar(layout);
        miniatureHost.Content = new Miniature(this, layout).Root;
        inspectorHost.Content = Inspector(layout);
        paletteHost.Content = Palette(layout);
    }

    // MARK: Options

    private UIElement OptionsBar(MosaicLayout layout)
    {
        var bar = new WrapPanel { HorizontalSpacing = 18, VerticalSpacing = 10 };
        bar.Children.Add(CanvasUi.Labelled("Tabs", CanvasUi.Segmented("Tabs",
            [(TabPlacement.Top, "Top"), (TabPlacement.SidebarLeading, "Left"), (TabPlacement.SidebarTrailing, "Right")],
            layout.TabPlacement, canvas.SetTabPlacement)));
        if (layout.TabPlacement.IsVertical())
        {
            bar.Children.Add(CanvasUi.Labelled("Sidebar", CanvasUi.Choice(Enum.GetValues<SidebarBehavior>(), layout.Sidebar, s => s.Title(),
                s => canvas.Set(l => l.Sidebar = s))));
            bar.Children.Add(CanvasUi.Labelled("Width", CanvasUi.Slider("Sidebar width", 180, 420, layout.SidebarWidth,
                v => canvas.Set(l => l.SidebarWidth = v, coalesce: "sidebarWidth"), 120, 1)));
        }
        else
        {
            bar.Children.Add(CanvasUi.Labelled("Tab style", CanvasUi.Choice(Enum.GetValues<TabStyle>(), layout.TabStyle, s => s.Title(),
                s => canvas.Set(l => l.TabStyle = s))));
        }
        bar.Children.Add(CanvasUi.Labelled("Address bar", CanvasUi.Choice(Enum.GetValues<AddressBarStyle>(), layout.AddressBar, s => s.Title(),
            s => canvas.Set(l => l.AddressBar = s))));
        var bookmarks = new CheckBox { Content = "Bookmarks bar", IsChecked = layout.BookmarksBar };
        bookmarks.Click += (_, _) => canvas.Set(l => l.BookmarksBar = bookmarks.IsChecked == true);
        bar.Children.Add(bookmarks);
        return bar;
    }

    private static Flyout SaveFlyout(CanvasController canvas)
    {
        var name = new TextBox { Header = "Name", Width = 260 };
        AutomationProperties.SetName(name, "Configuration name");
        var flyout = new Flyout();
        void Save()
        {
            var chosen = string.IsNullOrWhiteSpace(name.Text) ? canvas.Layout.Name : name.Text;
            canvas.SaveCurrent(chosen);
            flyout.Hide();
        }
        name.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Save(); e.Handled = true; } };
        var save = CanvasUi.Accented(CanvasUi.Button("Save", null, Save));
        var cancel = CanvasUi.Button("Cancel", null, () => flyout.Hide());
        var buttons = CanvasUi.Row(8, cancel, save);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        flyout.Content = CanvasUi.Column(10, CanvasUi.Heading("Save Configuration"),
            CanvasUi.Caption("Saves this layout and its appearance (colours, background) under a name."), name, buttons);
        flyout.Opening += (_, _) =>
        {
            name.Text = "";
            name.PlaceholderText = canvas.Layout.Name;
        };
        return flyout;
    }

    // MARK: Keyboard

    private static bool Down(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var alt = Down(VirtualKey.Menu);
        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var moving = alt || canvas.Grab is not null;
        void Arrow(Key direction)
        {
            if (moving) canvas.MoveSelected(direction);
            else canvas.MoveSelection(direction);
        }
        switch (e.Key)
        {
            case VirtualKey.Left: Arrow(Key.Left); break;
            case VirtualKey.Right: Arrow(Key.Right); break;
            case VirtualKey.Up: Arrow(Key.Up); break;
            case VirtualKey.Down: Arrow(Key.Down); break;
            case VirtualKey.Enter or VirtualKey.Space: canvas.ToggleGrab(); break;
            case VirtualKey.Escape when canvas.Grab is not null: canvas.EndGrab(commit: false); break;
            case VirtualKey.Delete or VirtualKey.Back: canvas.RemoveSelected(); break;
            case VirtualKey.Z when ctrl:
                if (shift) canvas.Redo();
                else canvas.Undo();
                break;
            case VirtualKey.Y when ctrl: canvas.Redo(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Select(CanvasSlot slot)
    {
        canvas.Message = null;
        canvas.Selection = slot;
        miniatureHost.Focus(FocusState.Programmatic);
    }

    // MARK: Context menus

    private MenuFlyout ChipMenu(CanvasSlot slot, ShellComponent component, int count)
    {
        var menu = new MenuFlyout();
        void Add(string text, Action action, bool enabled = true, string? tooltip = null)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            if (tooltip is not null) ToolTipService.SetToolTip(item, tooltip);
            menu.Items.Add(item);
        }
        Add("Move Left", () => { canvas.Selection = slot; canvas.MoveSelected(Key.Left); }, slot.Index > 0);
        Add("Move Right", () => { canvas.Selection = slot; canvas.MoveSelected(Key.Right); }, slot.Index < count - 1);
        menu.Items.Add(MoveToMenu(slot, component));
        menu.Items.Add(new MenuFlyoutSeparator());
        var removing = LayoutCanvasRules.VerdictRemoving(slot, canvas.Layout);
        Add("Remove", () => canvas.Remove(slot), removing.IsAllowed, removing.Message);
        return menu;
    }

    /// <summary>"Move To ▸" every region; regions that refuse the component are disabled, with the reason as a tooltip.</summary>
    private MenuFlyoutSubItem MoveToMenu(CanvasSlot slot, ShellComponent component)
    {
        var sub = new MenuFlyoutSubItem { Text = "Move To" };
        foreach (var region in LayoutCanvasRules.RegionOrder)
        {
            var verdict = LayoutCanvasRules.VerdictPlacing(component, region, canvas.Layout, slot);
            var item = new MenuFlyoutItem { Text = region.Title(), IsEnabled = verdict.IsAllowed && region != slot.Region };
            if (verdict.Message is { } reason) ToolTipService.SetToolTip(item, reason);
            item.Click += (_, _) => canvas.Move(slot, region, canvas.Layout.Components(region).Count);
            sub.Items.Add(item);
        }
        return sub;
    }

    // MARK: Inspector and palette

    private UIElement Inspector(MosaicLayout layout)
    {
        if (canvas.Selection is not { } slot || canvas.ComponentAt(slot) is not { } component) return new Grid();
        var count = layout.Components(slot.Region).Count;
        var left = CanvasUi.Button("Move left", "chevron.left", () => canvas.MoveSelected(Key.Left), iconOnly: true, tooltip: "Move left (Alt+←)");
        left.IsEnabled = slot.Index > 0;
        var right = CanvasUi.Button("Move right", "chevron.right", () => canvas.MoveSelected(Key.Right), iconOnly: true, tooltip: "Move right (Alt+→)");
        right.IsEnabled = slot.Index < count - 1;
        var moveTo = new DropDownButton { Content = "Move To" };
        var moveMenu = new MenuFlyout();
        foreach (var item in MoveToMenu(slot, component).Items.ToList()) moveMenu.Items.Add(item);
        moveTo.Flyout = moveMenu;
        var removing = LayoutCanvasRules.VerdictRemoving(slot, layout);
        var remove = CanvasUi.Button("Remove", "trash", () => canvas.Remove(slot), tooltip: removing.Message ?? "Back to Available Items (Delete)");
        remove.IsEnabled = removing.IsAllowed;
        var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = CanvasUi.Subtle, Child = Icons.Make(component.Symbol(), 15) };
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        var text = CanvasUi.Column(1, CanvasUi.Heading(component.Title()), CanvasUi.Caption($"{slot.Region.Title()} · {slot.Index + 1} of {count}"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = CanvasUi.Row(6, left, right, moveTo, remove);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        var card = CanvasUi.Card(grid, 10);
        AutomationProperties.SetName(card, "Selected item");
        return card;
    }

    private UIElement Palette(MosaicLayout layout)
    {
        var chips = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var component in LayoutCanvasRules.Palette(layout))
        {
            var chip = Chip(component, ChipStyle.Palette, selected: false, drawn: true);
            chip.CanDrag = true;
            chip.DragStarting += (_, e) =>
            {
                canvas.Dragging = DragItem.FromPalette(component);
                e.Data.SetText("mosaic-canvas:" + component.Raw());
                e.Data.RequestedOperation = DataPackageOperation.Move;
            };
            chip.DropCompleted += (_, _) => EndDrag();
            chip.DoubleTapped += (_, _) => canvas.Add(component);
            var menu = new MenuFlyout();
            var addTo = new MenuFlyoutSubItem { Text = "Add To" };
            foreach (var region in LayoutCanvasRules.RegionOrder)
            {
                var verdict = LayoutCanvasRules.VerdictPlacing(component, region, layout);
                var item = new MenuFlyoutItem { Text = region.Title(), IsEnabled = verdict.IsAllowed };
                if (verdict.Message is { } reason) ToolTipService.SetToolTip(item, reason);
                item.Click += (_, _) => canvas.Add(component, region);
                addTo.Items.Add(item);
            }
            var addSuggested = new MenuFlyoutItem { Text = "Add" };
            addSuggested.Click += (_, _) => canvas.Add(component);
            menu.Items.Add(addSuggested);
            menu.Items.Add(addTo);
            chip.ContextFlyout = menu;
            chip.IsTabStop = true;
            chip.KeyDown += (_, e) => { if (e.Key is VirtualKey.Enter or VirtualKey.Space) { canvas.Add(component); e.Handled = true; } };
            ToolTipService.SetToolTip(chip, $"{component.Title()}: drag into the window, double-click or press Enter to add");
            AutomationProperties.SetHelpText(chip, "Available. Double-click, press Enter or use the context menu to place it.");
            chips.Children.Add(chip);
        }
        var header = CanvasUi.Row(10, CanvasUi.Heading("Available Items"),
            CanvasUi.Caption("Drag into the window, or double-click to add. Drop items here to remove them."));
        var border = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = CanvasUi.Subtle,
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            AllowDrop = true,
            Child = CanvasUi.Column(10, header, chips),
        };
        AutomationProperties.SetName(border, "Available items");
        border.DragOver += (_, e) =>
        {
            if (canvas.Dragging is not { From: not null } item) { e.AcceptedOperation = DataPackageOperation.None; return; }
            var verdict = canvas.PaletteVerdict(item);
            e.AcceptedOperation = verdict.IsAllowed ? DataPackageOperation.Move : DataPackageOperation.None;
            e.DragUIOverride.Caption = verdict.Message ?? $"Remove {item.Component.Title()}";
            e.DragUIOverride.IsCaptionVisible = true;
            border.BorderBrush = CanvasUi.Critical;
            border.Background = CanvasUi.CriticalBackground;
        };
        border.DragLeave += (_, _) =>
        {
            border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            border.Background = CanvasUi.Subtle;
        };
        border.Drop += (_, _) =>
        {
            if (canvas.Dragging is { } item)
            {
                var verdict = canvas.PaletteVerdict(item);
                if (verdict.Message is { } message) canvas.Say(message, true);
                else canvas.DropOnPalette(item);
            }
            EndDrag();
        };
        return border;
    }

    private void EndDrag()
    {
        canvas.Dragging = null;
        shownKey = "";  // redraw now that nothing is being dragged
        Refresh();
    }

    // MARK: Chips

    internal enum ChipStyle { Row, Column, Palette }

    internal Border Chip(ShellComponent component, ChipStyle style, bool selected, bool drawn, bool lifted = false)
    {
        UIElement content;
        if (component == ShellComponent.AddressField && style != ChipStyle.Column)
            content = CanvasUi.Row(6, Icons.Make("magnifyingglass", 13), CanvasUi.Text("Address Field", 13, CanvasUi.Secondary, wrap: false));
        else if (component == ShellComponent.TabStrip && style == ChipStyle.Row)
        {
            var tabs = CanvasUi.Row(4);
            for (var i = 0; i < 3; i++) tabs.Children.Add(new Rectangle { Width = 56, Height = 18, RadiusX = 4, RadiusY = 4, Fill = CanvasUi.Subtle, Opacity = i == 0 ? 1 : 0.5 });
            tabs.Children.Add(CanvasUi.Text(drawn ? "Tabs" : "Tab strip (tabs are in the sidebar)", 12, CanvasUi.Secondary, wrap: false));
            content = tabs;
        }
        else if (style == ChipStyle.Palette)
            content = CanvasUi.Row(6, Icons.Make(component.Symbol(), 14), CanvasUi.Text(component.Title(), 13, wrap: false));
        else if (component == ShellComponent.FlexibleSpace)
            content = Icons.Make("arrow.left.and.right", 12);
        else
            content = Icons.Make(component.Symbol(), 15);
        if (content is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            element.HorizontalAlignment = component.IsFlexible() && style != ChipStyle.Palette ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        }
        var chip = new Border
        {
            Child = content,
            Height = style == ChipStyle.Palette ? 32 : 30,
            MinWidth = style == ChipStyle.Column ? 40 : 32,
            Padding = new Thickness(style == ChipStyle.Column ? 0 : 9, 0, style == ChipStyle.Column ? 0 : 9, 0),
            CornerRadius = new CornerRadius(8),
            Opacity = drawn ? 1 : 0.5,
            Background = component == ShellComponent.FlexibleSpace && style != ChipStyle.Palette ? null : CanvasUi.CardBackground,
            BorderBrush = selected ? CanvasUi.Accent : CanvasUi.CardStroke,
            BorderThickness = new Thickness(selected ? 2 : 1),
        };
        if (lifted) chip.Translation = new global::System.Numerics.Vector3(0, -2, 16);
        AutomationProperties.SetName(chip, component.Title());
        return chip;
    }

    // MARK: The miniature

    /// <summary>The window drawn from the layout: each region a drop zone, each component a chip.</summary>
    private sealed class Miniature
    {
        private readonly LayoutEditorView editor;
        private readonly MosaicLayout layout;
        public Grid Root { get; } = new();

        public Miniature(LayoutEditorView editor, MosaicLayout layout)
        {
            this.editor = editor;
            this.layout = layout;
            var vertical = layout.TabPlacement.IsVertical();
            var sidebarShown = vertical || layout.Contains(ShellComponent.SidebarToggle);
            Root.CornerRadius = new CornerRadius(16);
            Root.BorderBrush = CanvasUi.CardStroke;
            Root.BorderThickness = new Thickness(1);
            Root.MinHeight = 430;
            Root.RequestedTheme = layout.Appearance.ColorScheme switch
            {
                ColorSchemePreference.Light => ElementTheme.Light,
                ColorSchemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            Root.Children.Add(new GradientBackground(layout.Appearance.BackgroundForWorkspace(editor.canvas.WorkspaceID)));

            var frame = new Grid();
            frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var title = Zone(ShellRegion.TitleBar, horizontal: true);
            title.MinHeight = 46;
            title.Margin = new Thickness(0, 0, 138, 0);  // the caption buttons (– □ ×) sit at the trailing end on Windows
            frame.Children.Add(title);
            frame.Children.Add(CaptionButtons());

            var body = new Grid();
            Grid.SetRow(body, 1);
            void Column(UIElement element, GridLength width)
            {
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
                Grid.SetColumn((FrameworkElement)element, body.ColumnDefinitions.Count - 1);
                body.Children.Add(element);
            }
            var rail = Zone(ShellRegion.Rail, horizontal: false);
            Column(rail, new GridLength(58));
            if (layout.TabPlacement != TabPlacement.SidebarTrailing) Column(Sidebar(sidebarShown), GridLength.Auto);
            var main = new Grid();
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var toolbar = Zone(ShellRegion.Toolbar, horizontal: true);
            toolbar.MinHeight = 46;
            if (layout.Appearance.ToolbarSurface) toolbar.Background = CanvasUi.CardBackground;
            main.Children.Add(toolbar);
            var tabBar = Zone(ShellRegion.TabBar, horizontal: true);
            tabBar.MinHeight = 40;
            Grid.SetRow(tabBar, 1);
            main.Children.Add(tabBar);
            if (layout.BookmarksBar)
            {
                var strip = Bookmarks();
                Grid.SetRow(strip, 2);
                main.Children.Add(strip);
            }
            var page = Page();
            Grid.SetRow(page, 3);
            main.Children.Add(page);
            Column(main, new GridLength(1, GridUnitType.Star));
            if (layout.TabPlacement == TabPlacement.SidebarTrailing) Column(Sidebar(true), GridLength.Auto);
            Column(Zone(ShellRegion.TrailingRail, horizontal: false), new GridLength(58));
            frame.Children.Add(body);
            Root.Children.Add(frame);
        }

        private static UIElement CaptionButtons()
        {
            var row = CanvasUi.Row(0);
            foreach (var glyph in new[] { "", "", "" })
                row.Children.Add(new Border { Width = 46, Height = 32, Child = new FontIcon { Glyph = glyph, FontFamily = Icons.Font, FontSize = 10, Opacity = 0.6 } });
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetAccessibilityView(row, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return row;
        }

        private FrameworkElement Sidebar(bool shown)
        {
            var vertical = layout.TabPlacement.IsVertical();
            var grid = new Grid { Width = vertical ? 236 : 190, Padding = new Thickness(6), RowSpacing = 6, Opacity = vertical || shown ? 1 : 0.8 };
            grid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(vertical ? 8 : 16), 128, 128, 128));
            for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var top = Zone(ShellRegion.SidebarTop, horizontal: true);
            var middle = Zone(ShellRegion.SidebarMiddle, horizontal: true);
            Grid.SetRow(middle, 1);
            var bottom = Zone(ShellRegion.SidebarBottom, horizontal: true);
            Grid.SetRow(bottom, 3);
            var filler = new StackPanel { Spacing = 5 };
            if (vertical)
            {
                for (var i = 0; i < 6; i++)
                {
                    var row = CanvasUi.Row(6, new Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, Fill = CanvasUi.Secondary, Opacity = i == 0 ? 0.6 : 0.3 },
                        new Rectangle { Width = 90 - i * 9, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = CanvasUi.Secondary, Opacity = 0.3 });
                    row.Padding = new Thickness(8, 6, 8, 6);
                    filler.Children.Add(row);
                }
                filler.Children.Add(CanvasUi.Caption("Tabs"));
            }
            else
            {
                filler.Children.Add(CanvasUi.Caption(shown ? "Sidebar — hidden until the Sidebar button opens it" : "Sidebar — add the Sidebar button to use it"));
            }
            AutomationProperties.SetAccessibilityView(filler, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetRow(filler, 2);
            grid.Children.Add(top);
            grid.Children.Add(middle);
            grid.Children.Add(filler);
            grid.Children.Add(bottom);
            return grid;
        }

        private static FrameworkElement Bookmarks()
        {
            var row = CanvasUi.Row(10, Icons.Make("book", 10));
            for (var i = 0; i < 6; i++) row.Children.Add(new Rectangle { Width = 46, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = CanvasUi.Secondary, Opacity = 0.3, VerticalAlignment = VerticalAlignment.Center });
            row.Padding = new Thickness(12, 0, 12, 0);
            row.Height = 22;
            return row;
        }

        private FrameworkElement Page()
        {
            var a = layout.Appearance;
            var inset = Math.Max(a.ContentInset, 6);
            var radius = Math.Max(a.ContentCornerRadius, 8);
            var lines = new StackPanel { Spacing = 9, Padding = new Thickness(20) };
            lines.Children.Add(new Rectangle { Width = 150, Height = 10, RadiusX = 5, RadiusY = 5, HorizontalAlignment = HorizontalAlignment.Left,
                Fill = a.Accent == HexColor.System ? CanvasUi.Accent : CanvasUi.ColorBrush(a.Accent), Opacity = 0.5 });
            for (var i = 0; i < 4; i++)
                lines.Children.Add(new Rectangle { Width = 320 - i * 46, Height = 6, RadiusX = 3, RadiusY = 3, HorizontalAlignment = HorizontalAlignment.Left, Fill = CanvasUi.Secondary, Opacity = 0.2 });
            lines.Children.Add(CanvasUi.Caption("Your pages keep running while you edit: nothing is reloaded."));
            var page = new Border
            {
                Margin = new Thickness(inset),
                CornerRadius = new CornerRadius(radius),
                Background = CanvasUi.Brush("SolidBackgroundFillColorBaseBrush", Windows.UI.Color.FromArgb(255, 250, 250, 250)),
                BorderBrush = a.Glow ? CanvasUi.ColorBrush(a.Accent) : null,
                BorderThickness = new Thickness(a.Glow ? 1.5 : 0),
                Child = lines,
            };
            AutomationProperties.SetAccessibilityView(page, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return page;
        }

        /// <summary>One region: its chips, a drop target with an insertion indicator, and a refusal shown when the dragged item can't go here.</summary>
        private Border Zone(ShellRegion region, bool horizontal)
        {
            var canvas = editor.canvas;
            var items = layout.Components(region);
            var strip = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
            var chips = new List<Border>();
            for (var i = 0; i < items.Count; i++)
            {
                var component = items[i];
                var slot = new CanvasSlot(region, i);
                var selected = canvas.Selection == slot;
                var chip = editor.Chip(component, horizontal ? ChipStyle.Row : ChipStyle.Column, selected,
                    LayoutCanvasRules.IsDrawn(component, layout), lifted: selected && canvas.Grab is not null);
                if (horizontal)
                {
                    strip.ColumnDefinitions.Add(new ColumnDefinition { Width = component.IsFlexible() ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                    Grid.SetColumn(chip, i);
                }
                else
                {
                    strip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(chip, i);
                }
                Wire(chip, slot, component, items.Count);
                chips.Add(chip);
                strip.Children.Add(chip);
            }
            var indicator = new Rectangle { Fill = CanvasUi.Accent, RadiusX = 1.5, RadiusY = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            var overlay = new Microsoft.UI.Xaml.Controls.Canvas { IsHitTestVisible = false };
            overlay.Children.Add(indicator);
            var refusal = Icons.Make("nosign", 14);
            refusal.Foreground = CanvasUi.Critical;
            refusal.Visibility = Visibility.Collapsed;
            refusal.HorizontalAlignment = HorizontalAlignment.Right;
            refusal.VerticalAlignment = VerticalAlignment.Top;
            refusal.Margin = new Thickness(0, 4, 6, 0);
            var inner = new Grid();
            inner.Children.Add(strip);
            if (items.Count == 0)
            {
                var empty = CanvasUi.Caption(horizontal ? $"{region.Title()} — drop here" : (region == ShellRegion.Rail ? "Rail" : "Edge column"));
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.VerticalAlignment = VerticalAlignment.Center;
                empty.IsHitTestVisible = false;
                if (!horizontal) empty.RenderTransform = new RotateTransform { Angle = -90 };
                empty.RenderTransformOrigin = new Point(0.5, 0.5);
                inner.Children.Add(empty);
                inner.MinHeight = 30;
            }
            inner.Children.Add(overlay);
            inner.Children.Add(refusal);
            var zone = new Border
            {
                Child = inner,
                Padding = new Thickness(5),
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(6, 128, 128, 128)),
                BorderBrush = CanvasUi.CardStroke,
                BorderThickness = new Thickness(1),
                AllowDrop = true,
                VerticalAlignment = horizontal ? VerticalAlignment.Top : VerticalAlignment.Stretch,
            };
            AutomationProperties.SetName(zone, region.Title());
            AutomationProperties.SetAutomationId(zone, "canvas-region-" + region.Raw());
            var normalBackground = zone.Background;

            int IndexAt(Point position)
            {
                var index = 0;
                foreach (var chip in chips)
                {
                    var origin = chip.TransformToVisual(strip).TransformPoint(new Point(0, 0));
                    var middle = horizontal ? origin.X + chip.ActualWidth / 2 : origin.Y + chip.ActualHeight / 2;
                    if (middle < (horizontal ? position.X : position.Y)) index++;
                }
                return index;
            }

            void ShowIndicator(int index)
            {
                double at;
                if (chips.Count == 0) at = 4;
                else if (index < chips.Count)
                {
                    var origin = chips[index].TransformToVisual(strip).TransformPoint(new Point(0, 0));
                    at = (horizontal ? origin.X : origin.Y) - 3;
                }
                else
                {
                    var last = chips[^1];
                    var origin = last.TransformToVisual(strip).TransformPoint(new Point(0, 0));
                    at = (horizontal ? origin.X + last.ActualWidth : origin.Y + last.ActualHeight) + 1;
                }
                if (horizontal)
                {
                    indicator.Width = 3;
                    indicator.Height = Math.Max(inner.ActualHeight - 4, 10);
                    Microsoft.UI.Xaml.Controls.Canvas.SetLeft(indicator, Math.Max(at, 0));
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(indicator, 2);
                }
                else
                {
                    indicator.Height = 3;
                    indicator.Width = Math.Max(inner.ActualWidth - 4, 10);
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(indicator, Math.Max(at, 0));
                    Microsoft.UI.Xaml.Controls.Canvas.SetLeft(indicator, 2);
                }
                indicator.Visibility = Visibility.Visible;
            }

            void Clear()
            {
                indicator.Visibility = Visibility.Collapsed;
                refusal.Visibility = Visibility.Collapsed;
                zone.Background = normalBackground;
                zone.BorderBrush = CanvasUi.CardStroke;
                zone.BorderThickness = new Thickness(1);
            }

            zone.DragOver += (_, e) =>
            {
                if (canvas.Dragging is not { } item) { e.AcceptedOperation = DataPackageOperation.None; return; }
                var verdict = canvas.Verdict(item, region);
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
                zone.BorderThickness = new Thickness(2);
                if (verdict.Message is { } message)
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    e.DragUIOverride.Caption = message;
                    zone.Background = CanvasUi.CriticalBackground;
                    zone.BorderBrush = CanvasUi.Critical;
                    refusal.Visibility = Visibility.Visible;
                    ToolTipService.SetToolTip(refusal, message);
                    indicator.Visibility = Visibility.Collapsed;
                    if (canvas.Message?.Text != message) canvas.Say(message, true);  // the page keeps the drag source while dragging
                }
                else
                {
                    e.AcceptedOperation = DataPackageOperation.Move;
                    e.DragUIOverride.Caption = $"Move to {region.Title()}";
                    zone.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 0, 103, 192));
                    zone.BorderBrush = CanvasUi.Accent;
                    refusal.Visibility = Visibility.Collapsed;
                    ShowIndicator(IndexAt(e.GetPosition(strip)));
                }
            };
            zone.DragLeave += (_, _) => Clear();
            zone.Drop += (_, e) =>
            {
                var index = IndexAt(e.GetPosition(strip));
                Clear();
                if (canvas.Dragging is not { } item) return;
                var verdict = canvas.Verdict(item, region);
                if (verdict.Message is { } message)
                {
                    canvas.Say(message, true);
                    canvas.Dragging = null;
                }
                else
                {
                    canvas.Drop(item, region, index);
                }
                editor.EndDrag();
            };
            return zone;
        }

        private void Wire(Border chip, CanvasSlot slot, ShellComponent component, int count)
        {
            var canvas = editor.canvas;
            chip.CanDrag = true;
            chip.Tapped += (_, e) => { editor.Select(slot); e.Handled = true; };
            chip.DragStarting += (_, e) =>
            {
                // Not selected here: a redraw now would remove the element being dragged.
                canvas.Dragging = DragItem.Placed(slot, component);
                e.Data.SetText("mosaic-canvas:" + component.Raw());
                e.Data.RequestedOperation = DataPackageOperation.Move;
            };
            chip.DropCompleted += (_, _) => editor.EndDrag();
            chip.ContextFlyout = editor.ChipMenu(slot, component, count);
            ToolTipService.SetToolTip(chip, component.Title());
            AutomationProperties.SetItemStatus(chip, $"{slot.Region.Title()}, {slot.Index + 1} of {count}{(canvas.Selection == slot ? ", selected" : "")}");
            AutomationProperties.SetAutomationId(chip, $"canvas-chip-{slot.Region.Raw()}-{slot.Index}");
        }
    }
}

/// <summary>Lays children out in rows, wrapping when a row is full (macOS FlowLayout).</summary>
internal sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                x = 0;
                y += line + VerticalSpacing;
                line = 0;
            }
            x += size.Width + HorizontalSpacing;
            widest = Math.Max(widest, x - HorizontalSpacing);
            line = Math.Max(line, size.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? widest : Math.Min(widest, availableSize.Width), y + line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += line + VerticalSpacing;
                line = 0;
            }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            line = Math.Max(line, size.Height);
        }
        return finalSize;
    }
}
