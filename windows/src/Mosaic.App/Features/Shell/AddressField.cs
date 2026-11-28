using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;
using Windows.System;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// The address field (port of macOS's <c>AddressField</c>): wide (Chrome/Edge omnibox), compact
/// (sidebar) or centred (unified); the security icon, the bookmark star inside it when the
/// layout puts the star right after it, and suggestions from <see cref="AddressSuggestions"/>
/// in a popup under it (which hides the Gecko page while open, as any surface over the page must).
/// </summary>
internal sealed class AddressField : Grid
{
    private readonly ShellRoot shell;
    private readonly TextBox box;
    private readonly FontIcon icon;
    private readonly Border frame;
    private readonly Popup popup;
    private readonly ListView list;
    private readonly AddressBarStyle style;
    private CancellationTokenSource? query;
    private bool settingText;

    private BrowserModel Model => shell.Model;

    public AddressField(ShellRoot shell, AddressBarStyle style, bool embedsStar)
    {
        this.shell = shell;
        this.style = style;
        var theme = shell.Theme;
        var m = theme.Metrics;
        var height = style == AddressBarStyle.Compact ? m.FieldHeight - 2 : m.FieldHeight;
        var radius = style == AddressBarStyle.Omnibox ? Math.Min(height / 2, theme.CornerRadius * 2) : theme.CornerRadius;
        VerticalAlignment = VerticalAlignment.Center;
        MinWidth = style == AddressBarStyle.Compact ? 120 : 200;
        if (style == AddressBarStyle.Unified)
        {
            MaxWidth = 680 * m.Scale;
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        icon = ShellUi.Icon("magnifyingglass", m.Icon - 2, theme.SecondaryText);
        icon.Margin = new Thickness(12, 0, 4, 0);
        box = new TextBox
        {
            FontSize = m.Font,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 5, 8, 5),
            MinHeight = 0,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
        };
        // Lightweight styling: the field draws its own background and focus ring.
        foreach (var key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
        })
        {
            box.Resources[key] = theme.Transparent;
        }
        box.Resources["TextControlForeground"] = theme.PrimaryText;
        box.Resources["TextControlForegroundPointerOver"] = theme.PrimaryText;
        box.Resources["TextControlForegroundFocused"] = theme.PrimaryText;
        box.Resources["TextControlPlaceholderForeground"] = theme.SecondaryText;
        box.Resources["TextControlPlaceholderForegroundPointerOver"] = theme.SecondaryText;
        box.Resources["TextControlPlaceholderForegroundFocused"] = theme.SecondaryText;
        box.Resources["TextControlButtonForeground"] = theme.SecondaryText;
        AutomationProperties.SetName(box, "Address and search bar");
        AutomationProperties.SetAutomationId(box, "AddressField");

        var row = new Grid { Height = height };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        SetColumn(box, 1);
        row.Children.Add(box);
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        if (style == AddressBarStyle.Unified)
        {
            var reload = ShellUi.IconButton(theme, "arrow.clockwise", "Reload", "F5", Model.ReloadOrStop);
            reload.Width = reload.Height = height - 6;
            trailing.Children.Add(reload);
        }
        if (embedsStar)
        {
            var star = new ShellComponents(shell).Make(new RegionItem(-1, ShellComponent.BookmarkStar), ShellRegion.Toolbar);
            if (star is Control control)
            {
                control.Width = control.Height = height - 6;
            }
            trailing.Children.Add(star);
        }
        SetColumn(trailing, 2);
        row.Children.Add(trailing);

        frame = new Border
        {
            Child = row,
            Background = theme.ControlFill,
            BorderBrush = theme.ControlStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
        };
        Children.Add(frame);

        list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 420,
            Padding = new Thickness(4),
        };
        AutomationProperties.SetName(list, "Suggestions");
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: AddressSuggestion s }) Open(s, inNewTab: false);
        };
        var panelBorder = new Border
        {
            Child = list,
            Background = theme.Translucent
                ? new AcrylicBrush { TintColor = theme.OverlayPanel.Color, TintOpacity = 0.75, FallbackColor = theme.OverlayPanel.Color }
                : theme.OverlayPanel,
            BorderBrush = theme.CardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Math.Max(8, theme.CornerRadius)),
            Shadow = new ThemeShadow(),
            Translation = new global::System.Numerics.Vector3(0, 0, 24),
        };
        popup = new Popup { Child = panelBorder, IsLightDismissEnabled = false, ShouldConstrainToRootBounds = true };
        popup.Opened += (_, _) => FeatureMounts.CoverPage("shell.suggestions", true);
        popup.Closed += (_, _) => FeatureMounts.CoverPage("shell.suggestions", false);
        Children.Add(popup);

        box.GotFocus += (_, _) =>
        {
            Model.BeginAddressEditing();
            frame.BorderBrush = theme.Accent;
            frame.BorderThickness = new Thickness(theme.ProminentFocusRings ? 3 : 2);
            box.SelectAll();
        };
        box.LostFocus += (_, _) =>
        {
            frame.BorderBrush = theme.ControlStroke;
            frame.BorderThickness = new Thickness(1);
            Close();
            Model.EndAddressEditing();
            Refresh();
        };
        box.TextChanged += (_, _) =>
        {
            if (!settingText && box.FocusState != FocusState.Unfocused) _ = QueryAsync(box.Text);
        };
        box.PreviewKeyDown += OnKeyDown;
        SizeChanged += (_, _) => PlacePopup();

        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName is null or nameof(BrowserModel.AddressText) or nameof(BrowserModel.AddressKind) or nameof(BrowserModel.SearchEngine))
            {
                Refresh();
            }
        };
        Loaded += (_, _) =>
        {
            Model.PropertyChanged += handler;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Model.PropertyChanged -= handler;
            Close();
        };
    }

    /// <summary>Gives the field keyboard focus (Ctrl+L, Alt+D, F6, a new tab).</summary>
    public void FocusField()
    {
        box.Focus(FocusState.Keyboard);
        box.SelectAll();
    }

    public bool IsShown => IsLoaded && Visibility == Visibility.Visible && XamlRoot is not null;

    private void Refresh()
    {
        icon.Glyph = System.Icons.Glyph(Model.AddressKind switch
        {
            AddressKind.Secure => "lock",
            AddressKind.Web => "globe",
            _ => "magnifyingglass",
        });
        AutomationProperties.SetHelpText(box, Model.AddressKind == AddressKind.Secure ? "Connection is secure" : "");
        box.PlaceholderText = Model.AddressPlaceholder;
        if (!Model.IsEditingAddress && box.Text != Model.AddressText)
        {
            settingText = true;
            box.Text = Model.AddressText;
            settingText = false;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                if (popup.IsOpen && list.SelectedItem is FrameworkElement { Tag: AddressSuggestion s }) Open(s, alt);
                else if (box.Text.Trim().Length > 0)
                {
                    var text = box.Text;
                    Close();
                    if (alt) Model.NewTab(text);
                    else Model.Navigate(text);
                }
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (popup.IsOpen)
                {
                    Close();
                }
                else
                {
                    settingText = true;
                    box.Text = Model.AddressText;
                    settingText = false;
                    Model.FocusContent();
                }
                break;
            case VirtualKey.Down when popup.IsOpen:
                e.Handled = true;
                list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1);
                break;
            case VirtualKey.Up when popup.IsOpen:
                e.Handled = true;
                list.SelectedIndex = Math.Max(-1, list.SelectedIndex - 1);
                break;
        }
    }

    private void Open(AddressSuggestion suggestion, bool inNewTab)
    {
        Close();
        AddressSuggestions.OpenSuggestion(Model, suggestion, inNewTab);
    }

    private async Task QueryAsync(string text)
    {
        query?.Cancel();
        if (AddressSuggestions.Provider is not { } provider || text.Trim().Length == 0)
        {
            Close();
            return;
        }
        var cancel = query = new CancellationTokenSource();
        try
        {
            await foreach (var rows in provider(Model, text, cancel.Token).WithCancellation(cancel.Token))
            {
                if (cancel.IsCancellationRequested) return;
                Show(rows);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Suggestions are optional: typing and Enter still work.
        }
    }

    private void Show(IReadOnlyList<AddressSuggestion> rows)
    {
        list.Items.Clear();
        var theme = shell.Theme;
        foreach (var row in rows.Take(10))
        {
            var symbol = row.Kind switch
            {
                "history" => "clock",
                "bookmark" => "star",
                "tab" => "square.on.square",
                "search" => "magnifyingglass",
                _ => "globe",
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(4, 6, 4, 6), Tag = row };
            line.Children.Add(ShellUi.Icon(symbol, 14, theme.SecondaryText));
            line.Children.Add(ShellUi.Text(row.Title, theme.Metrics.Font));
            if (row.Detail is { Length: > 0 } detail) line.Children.Add(ShellUi.Text(detail, theme.Metrics.SmallFont, theme.SecondaryText));
            AutomationProperties.SetName(line, row.Detail is { Length: > 0 } d ? $"{row.Title}, {d}" : row.Title);
            list.Items.Add(line);
        }
        if (list.Items.Count == 0)
        {
            Close();
            return;
        }
        PlacePopup();
        popup.IsOpen = true;
    }

    private void PlacePopup()
    {
        if (popup.Child is FrameworkElement panel)
        {
            panel.Width = Math.Max(ActualWidth, 360);
        }
        popup.VerticalOffset = ActualHeight + 6;
        popup.HorizontalOffset = 0;
    }

    private void Close()
    {
        query?.Cancel();
        popup.IsOpen = false;
    }

    public AddressBarStyle Style => style;
}
