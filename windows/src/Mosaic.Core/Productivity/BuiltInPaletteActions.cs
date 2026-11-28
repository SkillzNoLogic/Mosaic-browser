using Mosaic.Core.Browser;
using Mosaic.Core.Search;
using Mosaic.Core.Shell;

namespace Mosaic.Core.Productivity;

/// <summary>
/// The palette actions for everything the browser model can do today. Port of
/// <c>Sources/Mosaic/Features/Productivity/BuiltInPaletteActions.swift</c>; actions of other areas
/// are registered by those areas or added in <see cref="IntegrationPaletteActions"/>.
/// </summary>
internal static class BuiltInPaletteActions
{
    public static IEnumerable<PaletteAction> Actions(BrowserModel model)
    {
        var shortcuts = model.Productivity.Shortcuts;
        KeyCombo? Key(string id) => shortcuts.Combo(id);
        var tab = model.SelectedTab;
        var isWebPage = tab is { IsStartPage: false } && ProductivityServices.IsWebPage(tab.Url);
        var items = new List<PaletteAction>();

        // Tabs
        items.Add(new("tab.new", "New Tab", () => model.NewTab())
        {
            Keywords = ["open", "blank"], Icon = "plus.square", Shortcut = Key(ShortcutCommands.NewTab), Boost = 3,
        });
        if (tab is not null)
        {
            items.Add(new("tab.close", "Close Tab", () => { if (model.Tabs.Contains(tab)) model.Close(tab); })
            {
                Subtitle = tab.DisplayTitle, Icon = "xmark.square", Shortcut = Key(ShortcutCommands.CloseTab),
            });
        }
        if (model.Tabs.Count > 1)
        {
            items.Add(new("tab.next", "Show Next Tab", () => model.SelectAdjacentTab(1))
            {
                Keywords = ["switch"], Icon = "arrow.right.square", Shortcut = Key(ShortcutCommands.NextTab),
            });
            items.Add(new("tab.previous", "Show Previous Tab", () => model.SelectAdjacentTab(-1))
            {
                Keywords = ["switch"], Icon = "arrow.left.square", Shortcut = Key(ShortcutCommands.PreviousTab),
            });
        }

        // Navigation
        items.Add(new("nav.location", "Open Location…", () => model.FocusAddressBar(activating: true))
        {
            Keywords = ["address", "url", "go to"], Icon = "link", Shortcut = Key(ShortcutCommands.OpenLocation),
        });
        if (tab is not null && isWebPage)
        {
            items.Add(new("nav.reload", "Reload Page", model.Reload)
            {
                Keywords = ["refresh"], Icon = "arrow.clockwise", Shortcut = Key(ShortcutCommands.Reload),
            });
            if (tab.IsLoading)
            {
                items.Add(new("nav.stop", "Stop Loading", model.StopLoading) { Icon = "xmark", Shortcut = Key(ShortcutCommands.Stop) });
            }
            if (tab.CanGoBack)
            {
                items.Add(new("nav.back", "Back", model.GoBack)
                {
                    Keywords = ["previous page"], Icon = "chevron.left", Shortcut = Key(ShortcutCommands.Back),
                });
            }
            if (tab.CanGoForward)
            {
                items.Add(new("nav.forward", "Forward", model.GoForward)
                {
                    Keywords = ["next page"], Icon = "chevron.right", Shortcut = Key(ShortcutCommands.Forward),
                });
            }
            if (ProductivityIntegration.CopyText is { } copy)
            {
                var url = tab.Url;
                items.Add(new("page.copyAddress", "Copy Page Address", () =>
                {
                    copy(url);
                    model.Notice = "Address copied";
                })
                {
                    Subtitle = url, Keywords = ["url", "link", "clipboard"], Icon = "doc.on.doc",
                });
            }
        }

        // Reader view
        if (tab is not null && isWebPage)
        {
            var reader = model.Productivity.Reader;
            var active = reader.IsActive(tab);
            var available = reader.IsAvailable(tab);
            items.Add(new("reader.toggle", active ? "Leave Reader View" : "Show Reader View", () => reader.Toggle(tab))
            {
                Subtitle = active || available ? null : "This page may not be an article",
                Keywords = ["reader mode", "article", "read", "distraction"],
                Icon = active ? "doc.plaintext.fill" : "doc.plaintext",
                Shortcut = Key(ShortcutCommands.ReaderView),
                Boost = available ? 2 : 0,
            });
            if (active) items.AddRange(ReaderSettingActions(reader));
        }

        // Engines and search
        if (tab is not null)
        {
            var other = tab.Engine.Other();
            items.Add(new("engine.switch", $"Switch Tab to {other.DisplayName()}", () => model.SetEngine(other))
            {
                Subtitle = model.ChromiumAvailable || other == EngineKind.Gecko ? null : "Chromium (WebView2) is not available",
                Keywords = ["engine", "rendering", other.EngineName(), "gecko", "blink", "firefox", "chrome", "webview2", "edge"],
                Icon = "arrow.triangle.2.circlepath",
                Shortcut = Key(ShortcutCommands.ToggleEngine),
            });
        }
        items.Add(new("search.next", "Next Search Engine", model.CycleSearchEngine)
        {
            Subtitle = $"Now: {model.SearchEngine.Name}", Keywords = ["cycle"], Icon = "magnifyingglass",
            Shortcut = Key(ShortcutCommands.NextSearchEngine),
        });
        foreach (var engine in SearchEngine.BuiltIn)
        {
            if (engine == model.SearchEngine) continue;
            var chosen = engine;
            items.Add(new($"search.use.{engine.Id}", $"Search with {engine.Name}", () =>
            {
                model.SearchEngine = chosen;
                model.Notice = $"Search engine: {chosen.Name}";
            })
            {
                Keywords = ["search engine", "default search"], Icon = "magnifyingglass",
            });
        }

        // Content blocker
        if (model.Blocker.IsAvailable)
        {
            var on = model.Blocker.IsEnabled;
            items.Add(new("blocker.toggle", on ? "Turn Off Content Blocker" : "Turn On Content Blocker", () => model.SetBlockerEnabled(!on))
            {
                Keywords = ["ads", "trackers", "adblock", "ublock", "shield"], Icon = on ? "shield.slash" : "shield",
                Shortcut = Key(ShortcutCommands.ToggleBlocker),
            });
            if (tab is not null && isWebPage && ContentBlocker.SiteOfPage(tab.Url) is { } site)
            {
                var filtering = model.Blocker.IsFiltering(tab.Url);
                items.Add(new("blocker.site", filtering ? $"Allow Ads on {site}" : $"Block Ads on {site}", () => model.SetBlocking(!filtering, tab))
                {
                    Keywords = ["blocker", "trackers", "exception", "allowlist"],
                    Icon = filtering ? "shield.lefthalf.filled.slash" : "shield.lefthalf.filled",
                });
            }
            items.Add(new("blocker.panel", "Show Blocker Panel", () => model.ShowBlocker = true)
            {
                Keywords = ["blocked", "ads", "trackers"], Icon = "shield.lefthalf.filled",
            });
        }

        // Extensions
        items.Add(new("extensions.show", "Extensions…", () => model.ShowExtensions = true)
        {
            Keywords = ["add-ons", "chrome web store"], Icon = "puzzlepiece.extension", Shortcut = Key(ShortcutCommands.Extensions),
        });

        // Speed Dial
        if (tab is not null && isWebPage)
        {
            var dial = model.Productivity.SpeedDial;
            if (!dial.Dial.Contains(tab.Url) && SpeedDial.NormalizedUrl(tab.Url) is not null)
            {
                var (title, url) = (tab.Title, tab.Url);
                items.Add(new("speedDial.add", "Add Page to Speed Dial", () =>
                {
                    model.Notice = dial.TryAdd(title, url) ? "Added to Speed Dial" : "Could not add this page to Speed Dial";
                })
                {
                    Subtitle = tab.DisplayTitle, Keywords = ["new tab", "shortcut", "pin", "favourite"], Icon = "square.grid.3x3.square",
                });
            }
        }

        // Settings beyond the category list
        if (ProductivityIntegration.OpenSettings is { } openSettings)
        {
            items.Add(new("settings.open", "Settings…", () => openSettings(model, SettingsCategory.General))
            {
                Keywords = ["preferences", "options"], Icon = "gearshape",
            });
            items.Add(new("shortcuts.customise", "Customise Keyboard Shortcuts…", () => openSettings(model, SettingsCategory.Shortcuts))
            {
                Keywords = ["keys", "hotkeys", "bindings", "settings"], Icon = "command",
            });
        }
        return items;
    }

    private static IEnumerable<PaletteAction> ReaderSettingActions(ReaderMode reader)
    {
        var s = reader.Settings;
        Action Change(Func<ReaderSettings, ReaderSettings> edit) => () => reader.Update(edit(reader.Settings));
        if (s.FontSize < 9)
        {
            yield return new("reader.larger", "Reader: Larger Text", Change(r => r with { FontSize = r.FontSize + 1 }))
            {
                Keywords = ["font size", "bigger"], Icon = "textformat.size.larger", Boost = -8,
            };
        }
        if (s.FontSize > 1)
        {
            yield return new("reader.smaller", "Reader: Smaller Text", Change(r => r with { FontSize = r.FontSize - 1 }))
            {
                Keywords = ["font size"], Icon = "textformat.size.smaller", Boost = -8,
            };
        }
        var otherFont = s.Font == ReaderFont.Serif ? ReaderFont.Sans : ReaderFont.Serif;
        yield return new("reader.font", $"Reader: {otherFont.Title()} Font", Change(r => r with { Font = otherFont }))
        {
            Keywords = ["typeface"], Icon = "textformat", Boost = -8,
        };
        foreach (var theme in Enum.GetValues<ReaderTheme>())
        {
            if (theme == s.Theme) continue;
            var chosen = theme;
            yield return new($"reader.theme.{theme.Id()}", $"Reader: {theme.Title()} Background", Change(r => r with { Theme = chosen }))
            {
                Keywords = ["colour", "color", "theme"], Icon = "circle.lefthalf.filled", Boost = -8,
            };
        }
    }
}
