using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;
using Mosaic.Core.Tabs;

namespace Mosaic.App.Features.Tabs;

/// <summary>Settings › Performance: sleeping tabs. Port of <c>PerformanceSettingsPane.swift</c>.</summary>
internal sealed class PerformanceSettingsPage : UserControl
{
    private readonly BrowserModel model;
    private readonly ToggleSwitch unload = new() { OnContent = "", OffContent = "" };
    private readonly ComboBox minutes = new() { MinWidth = 160 };
    private readonly ToggleSwitch neverPinned = new() { OnContent = "", OffContent = "" };
    private readonly ToggleSwitch pressure = new() { OnContent = "", OffContent = "" };
    private readonly TextBlock asleep = new();
    private readonly TextBlock memory = new();
    private readonly TextBlock lastChecked = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool updating;

    public PerformanceSettingsPage(BrowserModel model)
    {
        this.model = model;
        AutomationProperties.SetName(this, "Performance settings");

        unload.Toggled += (_, _) => Change(s => s with { UnloadInactiveTabs = unload.IsOn });
        neverPinned.Toggled += (_, _) => Change(s => s with { NeverUnloadPinned = neverPinned.IsOn });
        pressure.Toggled += (_, _) => Change(s => s with { RespondToMemoryPressure = pressure.IsOn });
        minutes.SelectionChanged += (_, _) =>
        {
            if (minutes.SelectedItem is ComboBoxItem { Tag: int value }) Change(s => s with { InactiveMinutes = value });
        };

        var unloadNow = new Button { Content = "Unload Background Tabs Now" };
        ToolTipService.SetToolTip(unloadNow, "Unloads every tab except the one you are on and the exceptions above");
        unloadNow.Click += async (_, _) => await model.UnloadBackgroundTabsAsync();

        var content = new StackPanel();
        content.Children.Add(TabsStyle.SectionHeader("Sleeping tabs"));
        content.Children.Add(TabsStyle.Row("Unload inactive tabs", "Frees the memory of tabs you have not used for a while", unload));
        content.Children.Add(TabsStyle.Row("Unload a tab after", null, minutes));
        content.Children.Add(TabsStyle.Row("Never unload pinned tabs", null, neverPinned));
        content.Children.Add(TabsStyle.Row("Unload more tabs when memory is low",
            "When Windows reports low memory, tabs are unloaded sooner; when it is critical, every background tab that may be is unloaded", pressure));
        content.Children.Add(TabsStyle.Footnote(
            "An unloaded tab keeps its place, title and address and is shown dimmed. Selecting it loads the page again. Never unloaded: the tab you are on, tabs playing audio, tabs with a download in progress, and pages that ask to stay open, such as a form with unsaved changes (Mozilla tabs)."));

        content.Children.Add(TabsStyle.SectionHeader("Now"));
        content.Children.Add(TabsStyle.Row("Unloaded tabs", null, asleep));
        content.Children.Add(TabsStyle.Row("Memory pressure", null, memory));
        content.Children.Add(TabsStyle.Row("Last checked", null, lastChecked));
        content.Children.Add(TabsStyle.Row("Unload background tabs", null, unloadNow));

        content.Children.Add(TabsStyle.SectionHeader("How each engine unloads"));
        content.Children.Add(TabsStyle.Row("Mozilla", "Keeps the tab’s back and forward history and scroll position.", null));
        content.Children.Add(TabsStyle.Row("Chromium", "Reloads the page from its address; back and forward history is not kept.", null));

        Content = TabsStyle.Page("Performance", content);
        clock.Tick += (_, _) => RefreshNow();
        Loaded += (_, _) =>
        {
            model.PropertyChanged += ModelChanged;
            model.TabOrganizationChanged += RefreshNow;
            clock.Start();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            model.PropertyChanged -= ModelChanged;
            model.TabOrganizationChanged -= RefreshNow;
            clock.Stop();
        };
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserModel.TabSettings)) Refresh();
        else if (e.PropertyName is nameof(BrowserModel.MemoryPressure) or nameof(BrowserModel.LastSleepCheck) or nameof(BrowserModel.AsleepTabCount)) RefreshNow();
    }

    private void Change(Func<SleepSettings, SleepSettings> change)
    {
        if (updating) return;
        model.TabSettings = model.TabSettings with { Sleep = change(model.TabSettings.Sleep) };
    }

    private void Refresh()
    {
        updating = true;
        try
        {
            var sleep = model.TabSettings.Sleep;
            unload.IsOn = sleep.UnloadInactiveTabs;
            neverPinned.IsOn = sleep.NeverUnloadPinned;
            pressure.IsOn = sleep.RespondToMemoryPressure;
            var choices = SleepSettings.MinuteChoices.Contains(sleep.InactiveMinutes)
                ? SleepSettings.MinuteChoices
                : [.. SleepSettings.MinuteChoices.Append(sleep.InactiveMinutes).Order()];
            minutes.Items.Clear();
            foreach (var choice in choices)
            {
                minutes.Items.Add(new ComboBoxItem { Content = TabsStyle.Minutes(choice), Tag = choice });
            }
            minutes.SelectedIndex = Array.IndexOf(choices, sleep.InactiveMinutes);
            minutes.IsEnabled = sleep.UnloadInactiveTabs;
        }
        finally
        {
            updating = false;
        }
        RefreshNow();
    }

    private void RefreshNow()
    {
        asleep.Text = $"{model.AsleepTabCount} of {model.Tabs.Count}";
        memory.Text = model.MemoryPressure.DisplayName();
        lastChecked.Text = model.LastSleepCheck is { } checkedAt ? TabsStyle.Ago(checkedAt) : "Not yet";
    }
}
