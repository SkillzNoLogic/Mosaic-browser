using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Passwords;

namespace Mosaic.App.Features.Passwords;

/// <summary>
/// Mount point (area: passwords): the compact password prompt for the selected tab, over the
/// content area at the top trailing corner: Save / Update after a sign-in, a suggested strong
/// password on a sign-up form, or the choice of login when a site has several. Draws nothing
/// when there is nothing to ask. Port of <c>PasswordPromptOverlay.swift</c>.
/// </summary>
/// <remarks>
/// Gecko's page is a native child window that would cover this card, so while it shows, the page
/// is covered (<see cref="FeatureMounts.CoverPage"/>): the card is the one thing to answer.
/// </remarks>
internal sealed class PasswordPromptView : Grid
{
    private const string CoverId = "passwords.prompt";

    private readonly BrowserModel model;
    private readonly PasswordManager passwords;
    private PasswordManager.Prompt? shown;
    private BrowserTab? shownTab;

    public PasswordPromptView(BrowserModel model)
    {
        this.model = model;
        passwords = model.Passwords;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Loaded += (_, _) =>
        {
            passwords.Changed += Refresh;
            model.PropertyChanged += ModelChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            passwords.Changed -= Refresh;
            model.PropertyChanged -= ModelChanged;
            FeatureMounts.CoverPage(CoverId, false);
        };
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserModel.SelectedTab)) Refresh();
    }

    private void Refresh()
    {
        var tab = model.SelectedTab;
        var prompt = passwords.PromptFor(tab);
        if (Equals(prompt, shown) && ReferenceEquals(tab, shownTab)) return;
        shown = prompt;
        shownTab = tab;
        Children.Clear();
        if (tab is not null && prompt is not null) Children.Add(Card(tab, prompt));
        FeatureMounts.CoverPage(CoverId, prompt is not null);
    }

    /// <summary>The card for <paramref name="prompt"/>; also used by automation snapshots.</summary>
    internal Border Card(BrowserTab tab, PasswordManager.Prompt prompt)
    {
        var content = new StackPanel { Spacing = 10 };
        switch (prompt)
        {
            case PasswordManager.Prompt.Save save: SaveContent(content, tab, save); break;
            case PasswordManager.Prompt.Generate generate: GenerateContent(content, tab, generate); break;
            case PasswordManager.Prompt.Choose choose: ChooseContent(content, tab, choose); break;
        }
        var card = new Border
        {
            Child = content,
            Width = 340,
            Margin = new Thickness(12),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = PasswordsUi.Brush("AcrylicInAppFillColorDefaultBrush", "SolidBackgroundFillColorBaseBrush"),
            BorderBrush = PasswordsUi.Brush("SurfaceStrokeColorFlyoutBrush", "CardStrokeColorDefaultBrush"),
            Shadow = new ThemeShadow(),
            Translation = new global::System.Numerics.Vector3(0, 0, 32),
        };
        AutomationProperties.SetLandmarkType(card, AutomationLandmarkType.Custom);
        AutomationProperties.SetName(card, "Password prompt");
        return card;
    }

    private UIElement Header(string title, string icon, BrowserTab tab)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = Icons.Make(icon, 16);
        glyph.Foreground = PasswordsUi.Brush("AccentTextFillColorPrimaryBrush");
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 2, 0, 0);
        row.Children.Add(glyph);
        var text = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Style = PasswordsUi.Style("BodyStrongTextBlockStyle") };
        AutomationProperties.SetHeadingLevel(text, AutomationHeadingLevel.Level2);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var close = PasswordsUi.IconButton("xmark", "Close", () => passwords.DismissPrompt(tab), 10);
        close.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(close, 2);
        row.Children.Add(close);
        return row;
    }

    private void SaveContent(StackPanel content, BrowserTab tab, PasswordManager.Prompt.Save save)
    {
        var isUpdate = save.Offer is SaveOffer.Update;
        var submission = save.Submission;
        content.Children.Add(Header(isUpdate ? $"Update password for {submission.Origin.DisplayName}?"
                                             : $"Save password for {submission.Origin.DisplayName}?", "key.fill", tab));

        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        PasswordsUi.AddLabel(grid, "Username", 0);
        TextBox? usernameBox = null;
        if (isUpdate)
        {
            PasswordsUi.Place(grid, new TextBlock
            {
                Text = submission.Username.Length == 0 ? "No username" : submission.Username,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            }, 0, 1);
        }
        else
        {
            usernameBox = new TextBox { Text = submission.Username, PlaceholderText = "No username" };
            AutomationProperties.SetName(usernameBox, "Username");
            PasswordsUi.Place(grid, usernameBox, 0, 1);
        }
        PasswordsUi.AddLabel(grid, "Password", 1);
        var reveal = false;
        var dots = new string('•', Math.Clamp(submission.Password.Length, 8, 16));
        var passwordText = new TextBlock
        {
            Text = dots,
            FontFamily = PasswordsUi.Mono,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(passwordText, "Password hidden");
        var eye = PasswordsUi.IconButton("eye", "Show password", () => { }, 12);
        eye.Click += (_, _) =>
        {
            reveal = !reveal;
            passwordText.Text = reveal ? submission.Password : dots;
            AutomationProperties.SetName(passwordText, reveal ? "Password" : "Password hidden");
            PasswordsUi.SetIcon(eye, reveal ? "eye.slash" : "eye", reveal ? "Hide password" : "Show password");
        };
        var passwordRow = new Grid();
        passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        passwordRow.Children.Add(passwordText);
        Grid.SetColumn(eye, 1);
        passwordRow.Children.Add(eye);
        PasswordsUi.Place(grid, passwordRow, 1, 1);
        content.Children.Add(grid);

        content.Children.Add(PasswordsUi.Caption("Stored encrypted for your Windows account on this PC."));

        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!isUpdate)
        {
            var never = new Button { Content = "Never for This Site" };
            ToolTipService.SetToolTip(never, $"Don’t offer to save passwords on {submission.Origin.DisplayName}");
            never.Click += (_, _) => passwords.NeverSave(tab);
            buttons.Children.Add(never);
        }
        var notNow = new Button { Content = "Not Now" };
        notNow.Click += (_, _) => passwords.DismissPrompt(tab);
        Grid.SetColumn(notNow, 2);
        buttons.Children.Add(notNow);
        var accept = new Button { Content = isUpdate ? "Update" : "Save", Style = PasswordsUi.Style("AccentButtonStyle") };
        accept.Click += (_, _) => passwords.AcceptSave(tab, usernameBox?.Text.Trim());
        Grid.SetColumn(accept, 3);
        buttons.Children.Add(accept);
        content.Children.Add(buttons);
    }

    private void GenerateContent(StackPanel content, BrowserTab tab, PasswordManager.Prompt.Generate generate)
    {
        content.Children.Add(Header("Use a strong password?", "key.viewfinder", tab));
        var box = new Grid { Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Background = PasswordsUi.Brush("SubtleFillColorSecondaryBrush") };
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = generate.Password,
            FontFamily = PasswordsUi.Mono,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(text, "Suggested password");
        box.Children.Add(text);
        var again = PasswordsUi.IconButton("arrow.clockwise", "Suggest another password", () => passwords.Regenerate(tab), 12);
        Grid.SetColumn(again, 1);
        box.Children.Add(again);
        content.Children.Add(box);
        content.Children.Add(PasswordsUi.Caption("Mosaic fills it in and offers to save it when you submit the form."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var notNow = new Button { Content = "Not Now" };
        notNow.Click += (_, _) => passwords.DismissPrompt(tab);
        var use = new Button { Content = "Use Password", Style = PasswordsUi.Style("AccentButtonStyle") };
        use.Click += (_, _) => _ = passwords.AcceptGeneratedAsync(tab);
        buttons.Children.Add(notNow);
        buttons.Children.Add(use);
        content.Children.Add(buttons);
    }

    private void ChooseContent(StackPanel content, BrowserTab tab, PasswordManager.Prompt.Choose choose)
    {
        content.Children.Add(Header($"Fill a password for {choose.Origin.DisplayName}", "key.fill", tab));
        var list = new StackPanel { Spacing = 2 };
        foreach (var credential in choose.Credentials)
        {
            list.Children.Add(PasswordsUi.LoginButton(credential, () => _ = passwords.FillAsync(tab, credential)));
        }
        content.Children.Add(list);
        var manage = new HyperlinkButton { Content = "Manage Passwords…", Padding = new Thickness(0) };
        manage.Click += (_, _) => passwords.OpenSettings();
        content.Children.Add(manage);
    }
}
