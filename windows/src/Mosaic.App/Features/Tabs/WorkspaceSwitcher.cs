using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Mosaic.Core.Tabs;

namespace Mosaic.App.Features.Tabs;

/// <summary>
/// The "workspaces" layout component: one button per workspace (its icon; the current one
/// highlighted), and a + button. Click switches, right-click offers rename/icon/move/delete, and
/// a tab dragged onto a workspace moves there. Horizontal by default (Zen/Arc sidebar bottom,
/// Edge's workspace button row); the shell can set <see cref="Orientation"/> for a rail.
/// </summary>
public sealed class WorkspaceSwitcher : UserControl
{
    private readonly BrowserModel model;
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<(Workspace Workspace, PropertyChangedEventHandler Handler)> handlers = [];
    private bool attached;

    public WorkspaceSwitcher(BrowserModel model)
    {
        this.model = model;
        Content = buttons;
        AutomationProperties.SetName(this, "Workspaces");
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    public Orientation Orientation
    {
        get => buttons.Orientation;
        set => buttons.Orientation = value;
    }

    private void Attach()
    {
        if (attached) return;
        attached = true;
        model.Workspaces.CollectionChanged += WorkspacesChanged;
        model.PropertyChanged += ModelChanged;
        Rebuild();
    }

    private void Detach()
    {
        if (!attached) return;
        attached = false;
        model.Workspaces.CollectionChanged -= WorkspacesChanged;
        model.PropertyChanged -= ModelChanged;
        Release();
    }

    private void WorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserModel.SelectedWorkspaceId)) Rebuild();
    }

    private void Release()
    {
        foreach (var (workspace, handler) in handlers) workspace.PropertyChanged -= handler;
        handlers.Clear();
    }

    private void Rebuild()
    {
        Release();
        buttons.Children.Clear();
        foreach (var workspace in model.Workspaces) buttons.Children.Add(WorkspaceButton(workspace));

        var add = Plain(TabsStyle.Icon("plus", "+", 14));
        AutomationProperties.SetName(add, "New workspace");
        ToolTipService.SetToolTip(add, "New workspace (Ctrl+Alt+N)");
        add.Click += (_, _) => model.CreateWorkspace();
        buttons.Children.Add(add);
    }

    private Button WorkspaceButton(Workspace workspace)
    {
        var selected = workspace.Id == model.SelectedWorkspaceId;
        var button = Plain(Glyph(workspace));
        if (selected)
        {
            button.Background = TabsStyle.Subtle;
            button.BorderBrush = TabsStyle.Accent;
            button.BorderThickness = new Thickness(0, 0, 0, 2);
        }
        void Describe()
        {
            AutomationProperties.SetName(button, selected ? $"{workspace.Name}, current workspace" : $"Switch to {workspace.Name}");
            ToolTipService.SetToolTip(button, $"{workspace.Name} · {TabsStyle.Plural(model.TabsIn(workspace.Id).Count, "tab", "tabs")}");
        }
        Describe();
        PropertyChangedEventHandler changed = (_, _) =>
        {
            button.Content = Glyph(workspace);
            Describe();
        };
        workspace.PropertyChanged += changed;
        handlers.Add((workspace, changed));
        button.PointerEntered += (_, _) => Describe();
        button.Click += (_, _) => model.SelectWorkspace(workspace.Id);
        button.ContextFlyout = TabsMenus.ForWorkspace(model, workspace);

        button.AllowDrop = true;
        button.DragOver += (_, e) => TabDragDrop.AcceptMove(e);
        button.Drop += async (_, e) =>
        {
            if (await TabDragDrop.DraggedTabAsync(model, e) is { } tab) model.MoveTabToWorkspace(tab, workspace.Id);
        };
        return button;
    }

    private static FrameworkElement Glyph(Workspace workspace) =>
        TabsStyle.Icon(workspace.Symbol, workspace.Name.Length > 0 ? workspace.Name[..1].ToUpperInvariant() : "•", 15);

    private static Button Plain(object content) => new()
    {
        Content = content,
        Width = 34,
        Height = 30,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(TabsStyle.Transparent),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(6),
    };
}
