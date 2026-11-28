using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Mosaic.Core.Search;

namespace Mosaic.App.Features.Library;

/// <summary>
/// Settings › Search: the default engine, remote search suggestions (off by default) and search
/// keywords with the user's own engines. Port of <c>SearchSettingsPane.swift</c>.
/// </summary>
internal sealed class SearchSettingsPage : Grid
{
    private readonly BrowserModel model;
    private readonly BrowserLibrary library;
    private readonly ComboBox engines = new() { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch remote = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBlock remoteTitle = LibraryUi.Text("");
    private readonly TextBlock remoteDetail = LibraryUi.Wrapping("");
    private readonly StackPanel customList = new() { Spacing = 0 };
    private bool updating;

    public SearchSettingsPage(BrowserModel model)
    {
        this.model = model;
        library = model.Library;
        Background = LibraryUi.PageBackground;
        var page = new StackPanel { Spacing = 16, MaxWidth = 760, Padding = new Thickness(28, 20, 28, 28), HorizontalAlignment = HorizontalAlignment.Stretch };
        Children.Add(new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = page,
        });
        var title = LibraryUi.Text("Search", "TitleTextBlockStyle");
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        page.Children.Add(title);

        // Default engine.
        AutomationProperties.SetName(engines, "Default search engine");
        engines.SelectionChanged += (_, _) =>
        {
            if (updating || engines.SelectedItem is not ComboBoxItem { Tag: SearchEngine engine }) return;
            model.SearchEngine = engine;
        };
        page.Children.Add(Card(
            Setting("Default search engine",
                "Used when what you type in the address bar is not an address. Ctrl+Shift+S cycles through the built-in engines.",
                engines)));

        // Suggestions.
        AutomationProperties.SetName(remote, "Search suggestions from the search engine");
        remote.Toggled += (_, _) =>
        {
            if (!updating) library.RemoteSuggestionsEnabled = remote.IsOn;
        };
        var suggestions = new StackPanel { Spacing = 14 };
        suggestions.Children.Add(SectionTitle("Address bar suggestions"));
        var remoteRow = new Grid { ColumnSpacing = 16 };
        remoteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        remoteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var remoteText = new StackPanel { Spacing = 2 };
        remoteText.Children.Add(remoteTitle);
        remoteText.Children.Add(remoteDetail);
        remoteRow.Children.Add(remoteText);
        SetColumn(remote, 1);
        remoteRow.Children.Add(remote);
        suggestions.Children.Add(remoteRow);
        var local = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        local.Children.Add(LibraryUi.Icon("checkmark", 12));
        local.Children.Add(LibraryUi.Text("History, bookmarks and open tabs are always suggested, matched on this PC.", secondary: true));
        suggestions.Children.Add(local);
        page.Children.Add(Card(suggestions));

        // Keywords and custom engines.
        var shortcuts = new StackPanel { Spacing = 12 };
        var shortcutsHeader = new Grid();
        shortcutsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shortcutsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcutsHeader.Children.Add(SectionTitle("Search shortcuts"));
        var add = LibraryUi.LabelButton("plus", "Add Search Engine…", (_, _) => _ = EditAsync(null));
        Grid.SetColumn(add, 1);
        shortcutsHeader.Children.Add(add);
        shortcuts.Children.Add(shortcutsHeader);
        shortcuts.Children.Add(LibraryUi.Wrapping(
            "Type a keyword, a space and your search in the address bar: “w mosaic browser” searches Wikipedia, “@ddg privacy” searches DuckDuckGo."));
        shortcuts.Children.Add(customList);
        page.Children.Add(Card(shortcuts));

        LibraryUi.Observe(this, library, [nameof(BrowserLibrary.CustomEngines), nameof(BrowserLibrary.RemoteSuggestionsEnabled), nameof(BrowserLibrary.IsLoaded)], Refresh);
        LibraryUi.Observe(this, model, [nameof(BrowserModel.SearchEngine)], Refresh);
        Loaded += (_, _) => library.EnsureLoaded();
    }

    private static Border Card(UIElement content) => new()
    {
        Background = LibraryUi.CardBackground,
        BorderBrush = LibraryUi.CardStroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(18, 16, 18, 16),
        Child = content,
    };

    private static TextBlock SectionTitle(string text)
    {
        var title = LibraryUi.Text(text, "BodyStrongTextBlockStyle");
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return title;
    }

    private static Grid Setting(string title, string detail, UIElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(SectionTitle(title));
        text.Children.Add(LibraryUi.Wrapping(detail));
        grid.Children.Add(text);
        Grid.SetColumn((FrameworkElement)control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private void Refresh()
    {
        updating = true;
        try
        {
            engines.Items.Clear();
            foreach (var engine in library.AllSearchEngines)
            {
                var item = new ComboBoxItem { Content = engine.Name, Tag = engine };
                engines.Items.Add(item);
                if (engine.Id == model.SearchEngine.Id) engines.SelectedItem = item;
            }
            var provider = model.SearchEngine.Name;
            var hasEndpoint = SearchShortcuts.SuggestTemplate(model.SearchEngine, library.CustomEngines) is not null;
            remote.IsOn = library.RemoteSuggestionsEnabled;
            remoteTitle.Text = $"Include search suggestions from {provider}";
            remoteDetail.Text = hasEndpoint
                ? $"What you type in the address bar is sent to {provider} as you type, without cookies. Never from private tabs. Off unless you turn it on."
                : $"{provider} has no suggestion service; add one to the engine below to use this.";
            RebuildCustomList();
        }
        finally
        {
            updating = false;
        }
    }

    private void RebuildCustomList()
    {
        customList.Children.Clear();
        foreach (var engine in library.CustomEngines)
        {
            customList.Children.Add(EngineRow(engine.Name, engine.Template, engine.Keyword, engine.Id == model.SearchEngine.Id, engine));
        }
        var builtInHeader = LibraryUi.Text("Built-in keywords", "CaptionTextBlockStyle", secondary: true);
        builtInHeader.Margin = new Thickness(0, 12, 0, 2);
        customList.Children.Add(builtInHeader);
        foreach (var engine in SearchEngine.BuiltIn)
        {
            customList.Children.Add(EngineRow(engine.Name, engine.SearchTemplate, SearchShortcuts.Keyword(engine) ?? "", engine.Id == model.SearchEngine.Id, null));
        }
    }

    private Grid EngineRow(string name, string template, string keyword, bool isDefault, CustomSearchEngine? custom)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        var text = new StackPanel { Spacing = 1 };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleLine.Children.Add(LibraryUi.Text(name));
        if (isDefault) titleLine.Children.Add(LibraryUi.TagChip("Default"));
        text.Children.Add(titleLine);
        text.Children.Add(LibraryUi.Text(template.Replace("%s", "…", StringComparison.Ordinal), secondary: true, size: 12));
        row.Children.Add(text);
        if (keyword.Length > 0)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                BorderBrush = LibraryUi.CardStroke,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = keyword, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 },
            };
            AutomationProperties.SetName(chip, $"Keyword {keyword}");
            Grid.SetColumn(chip, 1);
            row.Children.Add(chip);
        }
        if (custom is not null)
        {
            var actions = new DropDownButton { Content = LibraryUi.Icon("ellipsis", 12), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(actions, $"Actions for {name}");
            var menu = new MenuFlyout();
            menu.Items.Add(LibraryUi.MenuItem("Edit…", () => _ = EditAsync(custom), "edit"));
            menu.Items.Add(LibraryUi.MenuItem("Make Default", () => model.SearchEngine = custom.SearchEngine, "checkmark"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(LibraryUi.MenuItem("Delete", () => _ = library.DeleteEngineAsync(custom), "trash"));
            actions.Flyout = menu;
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
        }
        return row;
    }

    /// <summary>Adds (null) or edits a custom engine in a dialog, with validation.</summary>
    private async Task EditAsync(CustomSearchEngine? engine)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        var name = new TextBox { Header = "Name", PlaceholderText = "Wikipedia", Text = engine?.Name ?? "" };
        var keyword = new TextBox { Header = "Keyword", PlaceholderText = "w", Text = engine?.Keyword ?? "", IsSpellCheckEnabled = false };
        var template = new TextBox { Header = "Search address", PlaceholderText = "https://example.com/search?q=%s", Text = engine?.Template ?? "", IsSpellCheckEnabled = false };
        var suggest = new TextBox { Header = "Suggestions (optional)", PlaceholderText = "OpenSearch JSON address with %s", Text = engine?.SuggestTemplate ?? "", IsSpellCheckEnabled = false };
        foreach (var box in new[] { name, keyword, template, suggest }) panel.Children.Add(box);
        panel.Children.Add(LibraryUi.Wrapping("Use %s where the search terms go. Suggestions are only fetched when search suggestions are on."));
        var problem = LibraryUi.Wrapping("", secondary: false);
        problem.Foreground = LibraryUi.Brush("SystemFillColorCriticalBrush", 196, 43, 28);
        panel.Children.Add(problem);

        var dialog = new ContentDialog
        {
            Title = engine is null ? "Add Search Engine" : "Edit Search Engine",
            Content = panel,
            PrimaryButtonText = engine is null ? "Add" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        string? Problem()
        {
            if (CustomSearchEngine.Problem(name.Text, keyword.Text.Trim(), template.Text.Trim()) is { } found) return found;
            if (!library.IsKeywordAvailable(keyword.Text.Trim(), engine?.Id)) return $"The keyword “{keyword.Text.Trim()}” is already used.";
            var suggestion = suggest.Text.Trim();
            if (suggestion.Length > 0 && CustomSearchEngine.Problem(name.Text, "x", suggestion) is not null) return "The suggestions address must be a web address with %s.";
            return null;
        }
        void Validate()
        {
            var found = Problem();
            // Say what is wrong only once something has been typed.
            problem.Text = name.Text.Length + template.Text.Length > 0 ? found ?? "" : "";
            dialog.IsPrimaryButtonEnabled = found is null;
        }
        foreach (var box in new[] { name, keyword, template, suggest }) box.TextChanged += (_, _) => Validate();
        Validate();
        if (await LibraryUi.ShowDialogAsync(dialog, this) != ContentDialogResult.Primary || Problem() is not null) return;
        var saved = new CustomSearchEngine
        {
            Id = engine?.Id ?? "custom." + Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = name.Text.Trim(),
            Keyword = keyword.Text.Trim(),
            Template = template.Text.Trim(),
            SuggestTemplate = suggest.Text.Trim() is { Length: > 0 } s ? s : null,
        };
        await library.SaveEngineAsync(saved);
    }
}
