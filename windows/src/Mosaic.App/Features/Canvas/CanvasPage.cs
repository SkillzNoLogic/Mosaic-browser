using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;
using Mosaic.Core.Canvas;
using Mode = Mosaic.Core.Canvas.CanvasController.EditorMode;

namespace Mosaic.App.Features.Canvas;

/// <summary>A canvas view that redraws from the controller.</summary>
internal interface ICanvasView
{
    void Refresh();
}

/// <summary>
/// <c>mosaic://customize</c>: Customise Layout, Gradient Studio and Configurations (macOS:
/// Settings › Mosaics' modes; Presets stay on the shell's Mosaics page). Changes apply to the
/// window as they are made; pages keep running. Redraws are coalesced to one per UI-thread turn
/// and stop while the page is unloaded.
/// </summary>
internal sealed class CanvasPage : Grid
{
    private readonly BrowserModel model;
    private readonly CanvasController canvas;
    private readonly ContentControl content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly SelectorBar modes = new();
    private ICanvasView? view;
    private Mode shownMode = (Mode)(-1);
    private bool refreshQueued;

    public CanvasPage(BrowserModel model)
    {
        this.model = model;
        canvas = model.Canvas;
        if (canvas.Mode == Mode.Presets) canvas.Mode = Mode.Layout;
        AutomationProperties.SetName(this, "Customise Mosaic");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        foreach (var (mode, title) in new[] { (Mode.Layout, "Customise Layout"), (Mode.Gradient, "Gradient Studio"), (Mode.Configurations, "Configurations") })
        {
            var item = new SelectorBarItem { Text = title, Tag = mode };
            AutomationProperties.SetAutomationId(item, "canvas-mode-" + mode.ToString().ToLowerInvariant());
            modes.Items.Add(item);
        }
        modes.SelectionChanged += (_, _) =>
        {
            if (modes.SelectedItem?.Tag is Mode mode && mode != canvas.Mode) canvas.Mode = mode;
        };
        var header = new Grid { Padding = new Thickness(20, 12, 20, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(modes);
        if (CanvasServices.ShowMosaicsSettings is { } settings)
        {
            var presets = CanvasUi.Button("Mosaic Presets…", "square.grid.2x2", () => settings(model), tooltip: "Settings › Mosaics: the presets");
            SetColumn(presets, 1);
            header.Children.Add(presets);
        }
        Children.Add(header);
        SetRow(content, 1);
        Children.Add(content);

        Loaded += (_, _) =>
        {
            canvas.Changed += QueueRefresh;
            canvas.Announce += Speak;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            canvas.Changed -= QueueRefresh;
            canvas.Announce -= Speak;
            canvas.EndGrab(commit: true);
        };
    }

    private void Speak(string text) => CanvasUi.Announce(this, text);

    private void QueueRefresh()
    {
        if (refreshQueued) return;
        refreshQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        var mode = canvas.Mode == Mode.Presets ? Mode.Layout : canvas.Mode;
        if (mode != shownMode)
        {
            shownMode = mode;
            view = mode switch
            {
                Mode.Gradient => new GradientStudioView(canvas),
                Mode.Configurations => new ConfigurationsView(model, canvas),
                _ => new LayoutEditorView(model, canvas),
            };
            content.Content = view;
            foreach (var item in modes.Items)
            {
                if (item.Tag is Mode m && m == mode && modes.SelectedItem != item) modes.SelectedItem = item;
            }
        }
        view?.Refresh();
    }
}
