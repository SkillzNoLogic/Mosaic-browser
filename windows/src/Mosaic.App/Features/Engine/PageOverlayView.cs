using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Engine;
using Mosaic.Core.Permissions;
using Windows.System;

namespace Mosaic.App.Features.Engine;

/// <summary>
/// The page overlays of the selected tab, over the top of the content area (mount:
/// <c>FeatureMounts.Overlays</c>, id "engine.page"): the find bar (top trailing), a permission
/// prompt (top leading), a blocked-pop-up notice (top trailing, under the find bar) and a
/// Chromium page's JavaScript dialog (top centre). Gecko draws its own dialogs in the page.
/// </summary>
/// <remarks>
/// Each card lives in a windowed popup (<see cref="Popup.ShouldConstrainToRootBounds"/> false):
/// its own small window above Mosaic's, so it shows over Gecko's page (a Win32 child window
/// that covers WinUI content) without hiding it, which matters for the find bar. The popups are
/// only as big as their cards, so the rest of the page stays clickable. The page is covered
/// (<c>FeatureMounts.CoverPage</c>) only while a permission prompt asks something.
/// The view also carries the page shortcuts (Ctrl+F, F3, Ctrl+Plus/Minus/0, Ctrl+P, F12…).
/// </remarks>
internal sealed class PageOverlayView : Grid
{
    private const double Gap = 10;
    private const string CoverId = "engine.prompt";

    private readonly BrowserModel model;
    private readonly PageFeatures pages;
    private readonly Popup leading = NewPopup();
    private readonly Popup trailing = NewPopup();
    private readonly Popup centre = NewPopup();
    private readonly StackPanel trailingStack = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private Border? findCard;
    private TextBox? findBox;
    private TextBlock? findStatus;
    private ToggleButton? matchCase;
    private bool loaded;

    public PageOverlayView(BrowserModel model)
    {
        this.model = model;
        pages = model.Pages;
        // Empty: clicks go to whatever is underneath; the cards are in the popups.
        Background = null;
        IsTabStop = false;
        Children.Add(leading);
        Children.Add(trailing);
        Children.Add(centre);
        trailing.Child = trailingStack;
        EngineMenus.AddShortcuts(this, model);

        pages.PropertyChanged += PagesChanged;
        pages.FindFocusRequested += FocusFindField;
        model.PropertyChanged += ModelChanged;
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            loaded = true;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            loaded = false;
            leading.IsOpen = trailing.IsOpen = centre.IsOpen = false;
            FeatureMounts.CoverPage(CoverId, false);
        };
    }

    private static Popup NewPopup() => new()
    {
        ShouldConstrainToRootBounds = false,
        IsLightDismissEnabled = false,
    };

    private void PagesChanged(object? sender, PropertyChangedEventArgs change)
    {
        switch (change.PropertyName)
        {
            case nameof(PageFeatures.CurrentPrompt) or nameof(PageFeatures.CurrentPopupNotice)
                or nameof(PageFeatures.CurrentDialog) or nameof(PageFeatures.ShowsFindBar):
                Rebuild();
                break;
            case nameof(PageFeatures.FindLabel):
                UpdateFindStatus();
                break;
            case nameof(PageFeatures.FindText) when findBox is not null && findBox.Text != pages.FindText:
                findBox.Text = pages.FindText;
                break;
            case nameof(PageFeatures.FindMatchCase) when matchCase is not null:
                matchCase.IsChecked = pages.FindMatchCase;
                break;
        }
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName == nameof(BrowserModel.ShowsGeckoPage)) UpdateCover();
    }

    private void UpdateCover() =>
        FeatureMounts.CoverPage(CoverId, loaded && pages.CurrentPrompt is not null && model.ShowsGeckoPage);

    // ---- Layout ----

    private void Rebuild()
    {
        if (!loaded) return;
        leading.Child = pages.CurrentPrompt is { } prompt ? PromptCard(prompt) : null;
        centre.Child = pages.CurrentDialog is { } dialog ? DialogCard(dialog) : null;

        // The find card is kept while the bar is open, so typing in it is never interrupted.
        var wanted = new List<UIElement>();
        if (pages.ShowsFindBar)
        {
            wanted.Add(findCard ??= FindCard());
        }
        else
        {
            findCard = null;
            findBox = null;
        }
        if (pages.CurrentPopupNotice is { } notice) wanted.Add(PopupCard(notice));
        var children = trailingStack.Children;
        for (var index = children.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(children[index])) children.RemoveAt(index);
        }
        for (var index = 0; index < wanted.Count; index++)
        {
            if (index < children.Count && ReferenceEquals(children[index], wanted[index])) continue;
            children.Insert(index, wanted[index]);
        }

        leading.IsOpen = leading.Child is not null;
        centre.IsOpen = centre.Child is not null;
        trailing.IsOpen = trailingStack.Children.Count > 0;
        UpdateCover();
        Place();
    }

    private void Place()
    {
        foreach (var popup in new[] { leading, trailing, centre })
        {
            if (popup.Child is not FrameworkElement child) continue;
            child.RequestedTheme = ActualTheme;
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = child.DesiredSize.Width;
            popup.VerticalOffset = Gap;
            popup.HorizontalOffset = popup == leading ? Gap
                : popup == trailing ? Math.Max(Gap, ActualWidth - width - Gap)
                : Math.Max(Gap, (ActualWidth - width) / 2);
        }
    }

    private static Border Card(UIElement content, double width, string name)
    {
        var card = new Border
        {
            Child = content,
            Width = width,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = Brush("SolidBackgroundFillColorTertiaryBrush"),
            BorderBrush = Brush("SurfaceStrokeColorFlyoutBrush"),
            Translation = new Vector3(0, 0, 32),
            Shadow = new ThemeShadow(),
            Margin = new Thickness(0, 0, 0, 24), // room for the shadow inside the popup
        };
        AutomationProperties.SetName(card, name);
        return card;
    }

    private static Brush? Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private static Button IconButton(string icon, string label, Action action)
    {
        var button = new Button
        {
            Content = Icons.Make(icon, 12),
            Padding = new Thickness(6),
            MinWidth = 0,
            Background = null,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button TextButton(string text, Action action, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style)) button.Style = (Style)style;
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = Application.Current.Resources.TryGetValue("CaptionTextBlockStyle", out var style) ? (Style)style : null,
        Foreground = Brush("TextFillColorSecondaryBrush"),
    };

    /// <summary>Esc on a card runs <paramref name="cancel"/>.</summary>
    private static void OnEscape(UIElement element, Action cancel)
    {
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape, ScopeOwner = element };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            cancel();
        };
        element.KeyboardAccelerators.Add(escape);
    }

    // ---- Find ----

    private Border FindCard()
    {
        findBox = new TextBox { Width = 200, PlaceholderText = "Find in page", Text = pages.FindText, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(findBox, "Find in page");
        findBox.TextChanged += (_, _) => pages.FindText = findBox.Text;
        findBox.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                pages.FindNext(backwards: shift);
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                pages.CloseFindBar();
            }
        };
        findStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        matchCase = new ToggleButton { Content = "Aa", IsChecked = pages.FindMatchCase, Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(matchCase, "Match case");
        AutomationProperties.SetName(matchCase, "Match case");
        matchCase.Click += (_, _) => pages.FindMatchCase = matchCase.IsChecked == true;
        UpdateFindStatus();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new FontIcon { Glyph = Icons.Glyph("magnifyingglass"), FontFamily = Icons.Font, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(findBox);
        row.Children.Add(findStatus);
        row.Children.Add(matchCase);
        row.Children.Add(IconButton("chevron.up", "Previous match (Shift+F3)", () => pages.FindNext(backwards: true)));
        row.Children.Add(IconButton("chevron.down", "Next match (F3)", () => pages.FindNext()));
        row.Children.Add(IconButton("xmark", "Close find bar (Esc)", pages.CloseFindBar));
        var card = Card(row, double.NaN, "Find in page");
        card.Padding = new Thickness(8, 6, 8, 6);
        return card;
    }

    private void UpdateFindStatus()
    {
        if (findStatus is null) return;
        findStatus.Text = pages.FindLabel;
        findStatus.Foreground = pages.FindStatus is { HasMatches: false } && pages.FindText.Length > 0
            ? Brush("SystemFillColorCriticalBrush")
            : Brush("TextFillColorSecondaryBrush");
        AutomationProperties.SetName(findStatus, pages.FindStatus?.AccessibleLabel ?? "");
        Place();
    }

    private void FocusFindField()
    {
        // A scripted run never takes the keyboard from the person at the PC.
        if (!model.AllowsFocusChanges) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (findBox is null) return;
            findBox.Focus(FocusState.Keyboard);
            findBox.SelectAll();
        });
    }

    // ---- Permission prompt ----

    private Border PromptCard(PermissionPrompt prompt)
    {
        var body = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Icons.Make(PromptIcon(prompt), 18);
        icon.Foreground = Brush("AccentTextFillColorPrimaryBrush");
        header.Children.Add(icon);
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = prompt.Sentence, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var note in prompt.Notes) text.Children.Add(Caption(note));
        if (prompt.BlockedByWindows.Count > 0)
        {
            var settings = new HyperlinkButton { Content = "Open Windows privacy settings", Padding = new Thickness(0) };
            settings.Click += (_, _) => pages.OpenWindowsSettings(prompt);
            text.Children.Add(settings);
        }
        SetColumn(text, 1);
        header.Children.Add(text);
        var later = IconButton("xmark", "Decide later (Esc)", () => pages.Answer(prompt, null));
        SetColumn(later, 2);
        header.Children.Add(later);
        body.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var remember = prompt.Request.IsRememberable ? $"Mosaic remembers this for {SitePermissionStore.DisplayName(prompt.Request.Site!)}" : "";
        var block = TextButton("Block", () => pages.Answer(prompt, SitePermissionDecision.Block));
        var allow = TextButton("Allow", () => pages.Answer(prompt, SitePermissionDecision.Allow), accent: true);
        AutomationProperties.SetHelpText(block, remember);
        AutomationProperties.SetHelpText(allow, remember);
        buttons.Children.Add(block);
        buttons.Children.Add(allow);
        body.Children.Add(buttons);

        var card = Card(body, 360, $"Permission request: {prompt.Sentence}");
        OnEscape(card, () => pages.Answer(prompt, null));
        return card;
    }

    private static string PromptIcon(PermissionPrompt prompt)
    {
        var kinds = prompt.Request.Kinds;
        if (kinds.Contains(PermissionKind.Camera)) return "video.fill";
        if (kinds.Contains(PermissionKind.Microphone)) return "mic.fill";
        if (kinds.Contains(PermissionKind.Screen)) return "rectangle.inset.filled.on.rectangle";
        if (kinds.Contains(PermissionKind.Location)) return "location.fill";
        if (kinds.Contains(PermissionKind.Notifications)) return "bell.fill";
        if (kinds.Contains(PermissionKind.Clipboard)) return "doc.on.clipboard";
        return "hand.raised.fill";
    }

    // ---- Blocked pop-ups ----

    private Border PopupCard(PopupNotice notice)
    {
        var body = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Icons.Make("macwindow.badge.plus", 16));
        var title = new TextBlock { Text = notice.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(title, 1);
        header.Children.Add(title);
        var dismiss = IconButton("xmark", "Dismiss (Esc)", () => pages.DismissPopups(notice));
        SetColumn(dismiss, 2);
        header.Children.Add(dismiss);
        body.Children.Add(header);
        if (notice.Urls.FirstOrDefault() is { } first)
        {
            var address = Caption(first);
            address.TextWrapping = TextWrapping.NoWrap;
            address.TextTrimming = TextTrimming.CharacterEllipsis;
            address.IsTextSelectionEnabled = true;
            body.Children.Add(address);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var open = TextButton(notice.Urls.Count > 1 ? "Open All" : "Open", () => pages.OpenPopups(notice, alwaysAllow: false));
        open.IsEnabled = notice.Urls.Count > 0 || notice.Engine == EngineKind.Gecko;
        buttons.Children.Add(open);
        if (notice.Site is { } site)
        {
            buttons.Children.Add(TextButton($"Always allow on {SitePermissionStore.DisplayName(site)}", () => pages.OpenPopups(notice, alwaysAllow: true)));
        }
        body.Children.Add(buttons);
        var card = Card(body, 320, notice.Title);
        OnEscape(card, () => pages.DismissPopups(notice));
        return card;
    }

    // ---- JavaScript dialogs ----

    private Border DialogCard(PageDialog dialog)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = dialog.Heading, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (dialog.Message.Length > 0)
        {
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 240,
                Content = new TextBlock { Text = dialog.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            });
        }
        TextBox? answer = null;
        if (dialog.HasTextField)
        {
            answer = new TextBox { Text = dialog.DefaultText };
            AutomationProperties.SetName(answer, "Answer");
            answer.KeyDown += (_, args) =>
            {
                if (args.Key != VirtualKey.Enter) return;
                args.Handled = true;
                pages.Finish(dialog, accepted: true, answer.Text);
            };
            body.Children.Add(answer);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (dialog.HasCancel) buttons.Children.Add(TextButton(dialog.CancelLabel, () => pages.Finish(dialog, accepted: false)));
        var ok = TextButton(dialog.AcceptLabel, () => pages.Finish(dialog, accepted: true, answer?.Text), accent: true);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);
        var card = Card(body, 440, dialog.Origin.Length == 0 ? "Dialog from this page" : $"Dialog from {dialog.Origin}");
        OnEscape(card, () => pages.Finish(dialog, accepted: false));
        if (model.AllowsFocusChanges)
        {
            card.Loaded += (_, _) => ((Control?)answer ?? ok).Focus(FocusState.Programmatic);
        }
        return card;
    }
}
