using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Downloads;

namespace Mosaic.App.Features.Downloads;

/// <summary>
/// One download in the flyout or on the Downloads page (port of <c>DownloadRowView.swift</c>):
/// icon, name, progress, status, the safety warning with Keep / Move to Recycle Bin, and the
/// buttons beside it. Updated in place as the record changes, so rows are not rebuilt on every
/// progress report.
/// </summary>
internal sealed class DownloadRowView : Grid
{
    private readonly DownloadManager downloads;
    private readonly bool compact;
    private readonly FontIcon icon;
    private readonly FontIcon handedOffBadge;
    private readonly TextBlock name = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 2) };
    private readonly TextBlock status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detail = new() { FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border warning = new();
    private readonly TextBlock warningText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
    private DownloadRecord record;

    public DownloadRowView(DownloadManager downloads, DownloadRecord record, bool compact)
    {
        this.downloads = downloads;
        this.record = record;
        this.compact = compact;
        Padding = new Thickness(compact ? 12 : 8, compact ? 6 : 6, 8, compact ? 6 : 6);
        ColumnSpacing = 10;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // hit-testable for double-tap and the menu

        var iconSize = compact ? 22 : 28;
        icon = Icons.Make("doc", iconSize);
        handedOffBadge = Icons.Make("arrow.up.forward.app", iconSize * 0.45);
        handedOffBadge.HorizontalAlignment = HorizontalAlignment.Right;
        handedOffBadge.VerticalAlignment = VerticalAlignment.Bottom;
        var iconBox = new Grid { Width = iconSize + 6, Height = iconSize + 6, VerticalAlignment = VerticalAlignment.Top };
        iconBox.Children.Add(icon);
        iconBox.Children.Add(handedOffBadge);
        AutomationProperties.SetAccessibilityView(iconBox, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Children.Add(iconBox);

        name.FontSize = compact ? 13 : 14;
        var info = new StackPanel { Spacing = 1 };
        info.Children.Add(name);
        info.Children.Add(progress);
        info.Children.Add(status);
        if (!compact) info.Children.Add(detail);
        info.Children.Add(warning);
        SetColumn(info, 1);
        Children.Add(info);

        var warningPanel = new StackPanel { Spacing = 6 };
        var warningLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var triangle = Icons.Make("exclamationmark.triangle.fill", 14);
        triangle.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
        warningLine.Children.Add(triangle);
        warningLine.Children.Add(warningText);
        warningText.MaxWidth = compact ? 220 : 560;
        warningPanel.Children.Add(warningLine);
        var keep = new Button { Content = "Keep" };
        ToolTipService.SetToolTip(keep, "Keep the file; you will not be warned about it again");
        keep.Click += (_, _) => downloads.Keep(this.record.Id);
        var recycle = new Button { Content = "Move to Recycle Bin" };
        recycle.Click += (_, _) => downloads.Trash(this.record.Id);
        var warningButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        warningButtons.Children.Add(keep);
        warningButtons.Children.Add(recycle);
        warningPanel.Children.Add(warningButtons);
        warning.Child = warningPanel;
        warning.Padding = new Thickness(8);
        warning.Margin = new Thickness(0, 4, 0, 0);
        warning.CornerRadius = new CornerRadius(6);
        warning.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xA5, 0x00));
        AutomationProperties.SetName(warning, "Warning");

        SetColumn(actions, 2);
        Children.Add(actions);

        DoubleTapped += (_, _) => downloads.Open(this.record.Id);
        ContextFlyout = DownloadMenus.ContextMenu(downloads, () => this.record);
        Update(record);
    }

    public Guid Id => record.Id;

    public void Update(DownloadRecord changed)
    {
        record = changed;
        var exists = record.State != DownloadState.Completed || DownloadManager.FileExists(record);
        name.Text = record.FileName;
        ToolTipService.SetToolTip(name, record.FileName);
        icon.Glyph = Icons.Glyph(DownloadFileCategories.Of(record.FileName, record.MimeType)?.Symbol() ?? "doc");
        handedOffBadge.Visibility = record.State == DownloadState.HandedOff ? Visibility.Visible : Visibility.Collapsed;

        progress.Visibility = record.State.IsActive() ? Visibility.Visible : Visibility.Collapsed;
        if (record.FractionCompleted is { } fraction)
        {
            progress.IsIndeterminate = false;
            progress.Value = fraction;
        }
        else
        {
            progress.IsIndeterminate = record.State == DownloadState.InProgress;
        }
        progress.ShowPaused = record.State == DownloadState.Paused;
        AutomationProperties.SetName(progress, record.FractionCompleted is { } f ? $"{(int)(f * 100)} percent" : "Downloading, size unknown");

        var line = record.State == DownloadState.Completed && !exists ? "Moved or deleted" : DownloadFormat.Status(record);
        status.Text = line;
        status.Foreground = record.State == DownloadState.Failed
            ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        detail.Text = DetailLine();

        var showWarning = record.State == DownloadState.Completed && exists && record.NeedsSafetyConfirmation;
        warning.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
        if (showWarning) warningText.Text = record.Assessment.Summary ?? "This file may be dangerous.";

        AutomationProperties.SetName(this, $"{record.FileName}, {line}");
        UpdateActions(exists);
    }

    private string DetailLine()
    {
        var parts = new List<string>();
        if (record.Host is { } host) parts.Add(host);
        parts.Add(record.Engine.DisplayName());
        parts.Add(record.StartedAt.ToLocalTime().ToString("t"));
        if (record.State == DownloadState.Completed && record.DestinationPath is { } path && Path.GetDirectoryName(path) is { } folder)
        {
            parts.Add(folder);
        }
        return string.Join(" · ", parts);
    }

    private string? shownActions;

    private void UpdateActions(bool exists)
    {
        var key = $"{record.State}:{exists}:{record.Engine}";
        if (key == shownActions) return;
        shownActions = key;
        actions.Children.Clear();
        switch (record.State)
        {
            case DownloadState.InProgress:
                actions.Children.Add(ActionButton("pause.circle", "Pause", () => downloads.Pause(record.Id)));
                actions.Children.Add(ActionButton("xmark.circle", "Cancel", () => downloads.Cancel(record.Id)));
                break;
            case DownloadState.Paused:
                actions.Children.Add(ActionButton("play.circle", "Resume", () => downloads.Resume(record.Id)));
                actions.Children.Add(ActionButton("xmark.circle", "Cancel", () => downloads.Cancel(record.Id)));
                break;
            case DownloadState.Failed or DownloadState.Cancelled:
                actions.Children.Add(ActionButton("arrow.clockwise.circle",
                    record.Engine == DownloadEngine.Fdm ? "Download in Mosaic" : "Retry", () => downloads.Retry(record.Id)));
                break;
            case DownloadState.Completed:
                actions.Children.Add(exists
                    ? ActionButton("folder", "Show in folder", () => downloads.Reveal(record.Id))
                    : ActionButton("arrow.clockwise.circle", "Download again", () => downloads.Retry(record.Id)));
                break;
        }
    }

    private static Button ActionButton(string symbol, string label, Action action)
    {
        var button = new Button
        {
            Content = Icons.Make(symbol, 16),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
}

/// <summary>The menu shared by the flyout's rows and the Downloads page.</summary>
internal static class DownloadMenus
{
    public static MenuFlyout ContextMenu(DownloadManager downloads, Func<DownloadRecord> current)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var record = downloads.Record(current().Id) ?? current();
            var exists = DownloadManager.FileExists(record);
            void Add(string text, Action action)
            {
                var item = new MenuFlyoutItem { Text = text };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            if (record.State == DownloadState.Completed && exists)
            {
                Add("Open", () => downloads.Open(record.Id));
                Add("Show in folder", () => downloads.Reveal(record.Id));
            }
            switch (record.State)
            {
                case DownloadState.InProgress:
                    Add("Pause", () => downloads.Pause(record.Id));
                    Add("Cancel", () => downloads.Cancel(record.Id));
                    break;
                case DownloadState.Paused:
                    Add("Resume", () => downloads.Resume(record.Id));
                    Add("Cancel", () => downloads.Cancel(record.Id));
                    break;
                case DownloadState.Failed or DownloadState.Cancelled:
                    Add(record.Engine == DownloadEngine.Fdm ? "Download in Mosaic" : "Retry", () => downloads.Retry(record.Id));
                    break;
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Copy download link", () => downloads.CopyLink(record.Id));
            if (downloads.Fdm.IsInstalled && FdmPolicy.Supports(record.Url))
            {
                Add("Download with Free Download Manager", () => downloads.SendToFdm(record.Id));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Remove from list", () => downloads.Remove(record.Id));
            if (record.State == DownloadState.Completed && exists) Add("Move to Recycle Bin", () => downloads.Trash(record.Id));
        };
        // Gecko's page is a child window that would draw over the menu.
        menu.Opened += (_, _) => FeatureMounts.CoverPage("downloads.menu", true);
        menu.Closed += (_, _) => FeatureMounts.CoverPage("downloads.menu", false);
        return menu;
    }
}
