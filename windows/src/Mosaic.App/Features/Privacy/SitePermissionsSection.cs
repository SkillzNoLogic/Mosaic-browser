using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Browser;
using static Mosaic.App.Features.Privacy.PrivacyUi;

namespace Mosaic.App.Features.Privacy;

/// <summary>
/// Settings › Privacy &amp; Security › Site Permissions: the decisions sites were given (camera,
/// microphone, location, notifications, pop-ups, autoplay, clipboard), changed or forgotten
/// here. The engine area owns the store (its prompts consult it); this page reads and writes it
/// through <see cref="Source"/>, which <see cref="PrivacyMounts"/> connects to the engine area's
/// <c>SitePermissionStore</c>.
/// </summary>
internal static class SitePermissionsSection
{
    /// <summary>One remembered decision.</summary>
    internal sealed record Entry(string Site, string Permission, string Title, string IconName, bool Allowed);

    /// <summary>The engine area's store, seen through what this page needs.</summary>
    internal interface IStore
    {
        IReadOnlyList<Entry> Entries(BrowserModel model);
        void Set(BrowserModel model, Entry entry, bool? allow);
        event Action? Changed;
    }

    public static IStore? Source { get; set; }

    public static void Build(StackPanel page, BrowserModel model)
    {
        var (_, rows) = Section(page, "Site Permissions");
        var list = new StackPanel { Spacing = 6 };
        rows.Children.Add(list);
        void Fill()
        {
            list.Children.Clear();
            if (Source is not { } store)
            {
                list.Children.Add(Caption("Site permissions are not available in this build."));
                return;
            }
            var entries = store.Entries(model);
            if (entries.Count == 0)
            {
                list.Children.Add(Caption("No decisions remembered yet. When a site asks for the camera, microphone, location, notifications or pop-ups, your answer is listed here."));
            }
            foreach (var entry in entries)
            {
                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(Text(entry.Site));
                text.Children.Add(Caption(entry.Title));
                var decision = new ComboBox { ItemsSource = new[] { "Allow", "Block" }, SelectedIndex = entry.Allowed ? 0 : 1 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(decision, $"{entry.Title} for {entry.Site}");
                decision.SelectionChanged += (_, _) => store.Set(model, entry, decision.SelectedIndex == 0);
                var forget = Button("Forget", () => store.Set(model, entry, null), $"Forget {entry.Title} decision for {entry.Site}");
                ToolTipService.SetToolTip(forget, "Forget (the site will ask again)");
                var icon = Icon(entry.IconName);
                var leading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                leading.Children.Add(icon);
                leading.Children.Add(text);
                list.Children.Add(Row(leading, decision, forget));
            }
        }
        Fill();
        if (Source is { } source)
        {
            void Changed() => list.DispatcherQueue?.TryEnqueue(Fill);
            source.Changed += Changed;
            list.Unloaded += (_, _) => source.Changed -= Changed;
        }
    }
}
