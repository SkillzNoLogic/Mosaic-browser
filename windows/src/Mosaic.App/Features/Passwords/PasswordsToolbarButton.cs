using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Passwords;

namespace Mosaic.App.Features.Passwords;

/// <summary>
/// Mount point (area: passwords), layout component id <c>passwords</c>: the key button at the
/// trailing end of the address field. Collapsed unless the selected tab's site has saved
/// passwords; accent-coloured while the page shows a sign-in form. Click: the saved logins for
/// the site in a flyout (fill one, or Manage Passwords…); Ctrl+\ does the same from the keyboard.
/// Port of <c>PasswordsToolbarButton</c> in <c>PasswordPromptOverlay.swift</c>.
/// </summary>
internal sealed class PasswordsToolbarButton : Button
{
    private const string CoverId = "passwords.fill";

    private readonly BrowserModel model;
    private readonly PasswordManager passwords;
    private readonly Flyout flyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private BrowserTab? watchedTab;

    public PasswordsToolbarButton(BrowserModel model)
    {
        this.model = model;
        passwords = model.Passwords;
        Padding = new Thickness(6);
        MinWidth = 0;
        MinHeight = 0;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderThickness = new Thickness(0);
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, "Saved passwords for this site");
        ToolTipService.SetToolTip(this, "Fill a saved password (Ctrl+\\)");
        Flyout = flyout;
        flyout.Opening += (_, _) =>
        {
            flyout.Content = FlyoutContent();
            FeatureMounts.CoverPage(CoverId, true);
        };
        flyout.Closed += (_, _) => FeatureMounts.CoverPage(CoverId, false);
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
            Watch(null);
            FeatureMounts.CoverPage(CoverId, false);
        };
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserModel.SelectedTab)) Refresh();
    }

    private void TabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Url)) Refresh();
    }

    private void Watch(BrowserTab? tab)
    {
        if (ReferenceEquals(tab, watchedTab)) return;
        if (watchedTab is not null) watchedTab.PropertyChanged -= TabChanged;
        watchedTab = tab;
        if (tab is not null) tab.PropertyChanged += TabChanged;
    }

    private void Refresh()
    {
        var tab = model.SelectedTab;
        Watch(tab);
        // Before the vault has been read there is nothing to show, and the button must not be
        // the thing that reads it on the startup path.
        var count = passwords.IsVaultLoaded ? passwords.Fillable(tab).Count : 0;
        Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0) return;
        var active = passwords.OffersFill(tab);
        var icon = Icons.Make(active ? "key.fill" : "key", 14);
        icon.Foreground = PasswordsUi.Brush(active ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        Content = icon;
    }

    private UIElement FlyoutContent()
    {
        var tab = model.SelectedTab;
        var credentials = passwords.Fillable(tab);
        var panel = new StackPanel { Spacing = 6, MinWidth = 240 };
        if (credentials.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = $"Saved for {credentials[0].Origin.DisplayName}", Style = PasswordsUi.Style("BodyStrongTextBlockStyle") });
        }
        foreach (var credential in credentials)
        {
            panel.Children.Add(PasswordsUi.LoginButton(credential, () =>
            {
                flyout.Hide();
                _ = passwords.FillAsync(tab, credential);
            }));
        }
        panel.Children.Add(new MenuFlyoutSeparator());
        var manage = new HyperlinkButton { Content = "Manage Passwords…", Padding = new Thickness(0) };
        manage.Click += (_, _) =>
        {
            flyout.Hide();
            passwords.OpenSettings();
        };
        panel.Children.Add(manage);
        return panel;
    }
}
