using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Platform;
using static Mosaic.App.Features.Platform.PlatformUi;

namespace Mosaic.App.Features.Platform;

/// <summary>
/// Settings › Languages: the languages websites are asked for, in order, applied to both engines
/// (Gecko's <c>intl.accept_languages</c> at once; WebView2's from the next start). Port of
/// <c>LanguagesSettingsPane.swift</c>.
/// </summary>
internal sealed class LanguagesSettingsPage : PlatformPage
{
    private readonly LanguageSettings languages;

    public LanguagesSettingsPage(BrowserModel model) : base("Languages")
    {
        languages = model.Platform.Languages;
        Body.Children.Add(Note(
            "Websites that offer several languages use this list, top first, to pick one. Both engines send it (the Accept-Language " +
            "header and navigator.languages). Mosaic’s own interface is in English for now."));
        Body.Children.Add(Header("Preferred languages"));
        Body.Children.Add(Section(List, languages));
        Body.Children.Add(Card(AddLanguage()));
        Body.Children.Add(Section(Status, languages));
    }

    private void List(StackPanel panel)
    {
        var tags = languages.Preferences.Tags;
        for (var index = 0; index < tags.Count; index++)
        {
            var tag = tags[index];
            var name = LanguagePreferences.DisplayName(tag);
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = $"{index + 1}.", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
            var label = Column(1, Text(name), Note(tag));
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var up = IconButton("chevron.up", $"Move {name} up", () => languages.Edit(list => list.Move(tag, -1)));
            up.IsEnabled = index > 0;
            var down = IconButton("chevron.down", $"Move {name} down", () => languages.Edit(list => list.Move(tag, 1)));
            down.IsEnabled = index < tags.Count - 1;
            var remove = IconButton("minus.circle", $"Remove {name}", () => languages.Edit(list => list.Remove(tag)));
            remove.IsEnabled = tags.Count > 1;
            var buttons = Row(4, up, down, remove);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            AutomationProperties.SetName(grid, $"{index + 1}. {name}");
            panel.Children.Add(Card(grid));
        }
    }

    private static Button IconButton(string icon, string name, Action click)
    {
        var button = new Button { Content = Icons.Make(icon, 14), Padding = new Thickness(8, 6, 8, 6) };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Not re-rendered, so a half-typed tag survives list changes.</summary>
    private StackPanel AddLanguage()
    {
        var common = new ComboBox { PlaceholderText = "Add a language", MinWidth = 260 };
        AutomationProperties.SetName(common, "Add a language");
        foreach (var tag in LanguagePreferences.Common) common.Items.Add(new ComboBoxItem { Content = $"{LanguagePreferences.DisplayName(tag)} ({tag})", Tag = tag });
        common.SelectionChanged += (_, _) =>
        {
            if (common.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                languages.Edit(list => list.Add(tag));
                common.SelectedIndex = -1;
            }
        };

        var custom = new TextBox { PlaceholderText = "Other tag, e.g. gsw-CH", MinWidth = 200 };
        AutomationProperties.SetName(custom, "Language tag");
        var error = new TextBlock { Foreground = Critical, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void AddCustom()
        {
            var text = custom.Text.Trim();
            if (text.Length == 0) return;
            if (LanguagePreferences.Normalize(text) is not { } tag)
            {
                error.Text = $"“{text}” isn’t a language tag. Use the form en, en-GB or zh-Hant-TW.";
                error.Visibility = Visibility.Visible;
                return;
            }
            languages.Edit(list => list.Add(tag));
            custom.Text = "";
            error.Visibility = Visibility.Collapsed;
        }
        custom.KeyDown += (_, args) =>
        {
            if (args.Key == global::Windows.System.VirtualKey.Enter) AddCustom();
        };
        return Column(8, common, Row(8, custom, Button("Add", AddCustom)), error);
    }

    private void Status(StackPanel panel)
    {
        panel.Children.Add(Header("What pages receive"));
        panel.Children.Add(Card(Column(6,
            Text(languages.Preferences.AcceptLanguageHeader),
            Note(languages.IsCustom ? "Your own list." : "Follows Windows’ preferred languages (Settings › Time & language)."))));
        if (languages.IsCustom)
        {
            panel.Children.Add(Row(8, Button("Use Windows’ languages", languages.UseSystemLanguages)));
        }
        if (languages.ChromiumNeedsRestart)
        {
            panel.Children.Add(Message("Chromium tabs use the new list after Mosaic restarts; Mozilla tabs use it now.", Tone.Neutral));
        }
        if (languages.GeckoError is { } error)
        {
            panel.Children.Add(Message($"The Mozilla engine did not take the list: {error}", Tone.Warning));
        }
    }
}
