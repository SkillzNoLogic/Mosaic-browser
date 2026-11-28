using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Downloads;
using Windows.Storage.Pickers;

namespace Mosaic.App.Features.Downloads;

/// <summary>
/// Shows the download manager's questions (Free Download Manager, duplicate, safety) as a card
/// inside Mosaic's window, never a window of its own, and "Save As" as the system's save dialog
/// (port of <c>DownloadPrompter.swift</c>'s sheets). In scripted runs the save question is shown
/// as a card too, for the <c>downloadPromptSave</c> / <c>downloadPromptAnswer</c> commands, so no
/// dialog ever opens on the desktop of whoever is at the PC.
/// </summary>
internal sealed class DownloadPromptOverlay : UserControl
{
    private readonly DownloadManager downloads;
    private readonly bool isScripted;
    private readonly TextBlock title = new() { Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly FontIcon warningIcon = Icons.Make("exclamationmark.triangle.fill", 20);
    private DownloadQuestion? shown;

    public DownloadPromptOverlay(DownloadManager downloads, bool isScripted)
    {
        this.downloads = downloads;
        this.isScripted = isScripted;
        warningIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(warningIcon);
        heading.Children.Add(title);
        var card = new StackPanel { Spacing = 12 };
        card.Children.Add(heading);
        card.Children.Add(message);
        card.Children.Add(buttons);
        Content = new Border
        {
            Child = card,
            Width = 420,
            Margin = new Thickness(12),
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(this, "Download question");
        Visibility = Visibility.Collapsed;
        downloads.Prompter.PendingChanged += Show;
        Show();
    }

    private void Show()
    {
        var question = downloads.Prompter.Pending;
        if (ReferenceEquals(question, shown)) return;
        shown = question;
        if (question is { Kind: "save" } && !isScripted)
        {
            Hide();
            _ = SaveAsync(question);
            return;
        }
        if (question is null)
        {
            Hide();
            return;
        }
        title.Text = question.Kind == "save" ? $"Save “{question.SuggestedName}”" : question.Title;
        message.Text = question.Kind == "save" ? $"In {question.Message}" : question.Message;
        warningIcon.Visibility = question.IsWarning ? Visibility.Visible : Visibility.Collapsed;
        buttons.Children.Clear();
        for (var index = 0; index < question.Buttons.Count; index++)
        {
            var answer = index;
            var button = new Button { Content = question.Buttons[index] };
            // The cautious choice is the default for a warning; otherwise the first one is.
            var isDefault = question.IsWarning ? index == question.Buttons.Count - 1 : index == 0 && question.Buttons.Count > 1;
            if (isDefault) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Click += (_, _) => downloads.Prompter.Answer(answer);
            buttons.Children.Add(button);
        }
        Visibility = Visibility.Visible;
        FeatureMounts.CoverPage("downloads.prompt", true);
    }

    private void Hide()
    {
        Visibility = Visibility.Collapsed;
        FeatureMounts.CoverPage("downloads.prompt", false);
    }

    private async Task SaveAsync(DownloadQuestion question)
    {
        var name = question.SuggestedName ?? DownloadFileName.Fallback;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads, SuggestedFileName = Path.GetFileNameWithoutExtension(name) };
        var extension = Path.GetExtension(name);
        if (extension.Length > 1) picker.FileTypeChoices.Add($"{extension.TrimStart('.').ToUpperInvariant()} file", [extension]);
        picker.FileTypeChoices.Add("All files", ["."]);
        FeatureMounts.CoverPage("downloads.save", true);
        try
        {
            if (DownloadsMounts.WindowHandle == 0) throw new InvalidOperationException("no window");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, DownloadsMounts.WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (!ReferenceEquals(downloads.Prompter.Pending, question)) return;
            if (file is null) downloads.Prompter.Answer(1);
            else downloads.Prompter.Save(file.Path);
        }
        catch (Exception)
        {
            // No dialog could be shown: save to the folder that was offered rather than lose the download.
            if (ReferenceEquals(downloads.Prompter.Pending, question)) downloads.Prompter.Answer(0);
        }
        finally
        {
            FeatureMounts.CoverPage("downloads.save", false);
        }
    }
}
