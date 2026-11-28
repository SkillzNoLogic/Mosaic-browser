using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Permissions;

namespace Mosaic.Core.Engine;

/// <summary>
/// The engine area's automation commands (the <c>page.*</c> commands of macOS's
/// <c>PageFeatures+Commands.swift</c>) and its <c>dump</c> key, <c>"page"</c>.
/// </summary>
/// <remarks>
/// The Windows pipe carries only <c>value</c> and <c>out</c>, so arguments macOS sends as extra
/// keys travel inside <c>value</c>, separated by <c>|</c>:
/// <list type="bullet">
/// <item><c>page.screenshot</c>: <c>&lt;path&gt;</c>, <c>full|&lt;path&gt;</c>, <c>clipboard</c>, <c>full|clipboard</c>, or empty for Pictures\Screenshots.</item>
/// <item><c>page.dialog</c>: <c>accept</c>, <c>cancel</c>, or <c>accept|&lt;prompt answer&gt;</c>.</item>
/// <item><c>page.gecko</c>: <c>&lt;agent method&gt;</c> or <c>&lt;method&gt;|&lt;JSON array of arguments&gt;</c>; the selected tab's Gecko id goes first.</item>
/// </list>
/// Commands that produce something (<c>page.pdf</c>, <c>page.screenshot</c>, <c>page.eval</c>,
/// <c>page.gecko</c>, <c>page.devtoolsOpen</c>, <c>page.closePanels</c>) wait for it, write it to
/// <c>out</c> when given, and put it in the reply as <c>"result"</c>.
/// </remarks>
public static class EngineAutomation
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static int registered;

    /// <summary>
    /// Plugs the commands and the <c>"page"</c> dump key into <see cref="AutomationRegistry"/>.
    /// Called once by the app's engine mounts (not from this assembly, so the model's own tests
    /// see the dump as the build contract describes it).
    /// </summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("page", model => model.Pages.AutomationState());
    }

    public static async Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        if (!command.StartsWith("page.", StringComparison.Ordinal)) return null;
        var pages = model.Pages;
        var tab = model.SelectedTab;

        async Task<AutomationReply> Result(JsonNode? result)
        {
            if (output is not null) await File.WriteAllTextAsync(output, result?.ToJsonString(Indented) ?? "null");
            return new AutomationReply(new JsonObject { ["ok"] = true, ["result"] = result?.DeepClone() }.ToJsonString());
        }

        switch (command)
        {
            case "page.find":
                if (!pages.IsFindBarVisible) pages.ShowFindBar();
                if (!pages.IsFindBarVisible) return AutomationRegistry.Error("The page in front cannot be searched.");
                pages.FindText = value;
                return AutomationRegistry.Ok();
            case "page.findNext":
                pages.FindNext();
                return AutomationRegistry.Ok();
            case "page.findPrevious":
                pages.FindNext(backwards: true);
                return AutomationRegistry.Ok();
            case "page.findCase":
                pages.FindMatchCase = value == "true";
                return AutomationRegistry.Ok();
            case "page.findClose":
                pages.CloseFindBar();
                return AutomationRegistry.Ok();
            case "page.zoomIn":
                pages.ZoomIn();
                return AutomationRegistry.Ok();
            case "page.zoomOut":
                pages.ZoomOut();
                return AutomationRegistry.Ok();
            case "page.zoomReset":
                pages.ResetZoom();
                return AutomationRegistry.Ok();
            case "page.zoom":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)) return AutomationRegistry.Error("page.zoom needs a factor such as 1.25.");
                pages.SetZoom(factor);
                return AutomationRegistry.Ok();
            case "page.print":
                return pages.Print() ? AutomationRegistry.Ok() : AutomationRegistry.Error("The page in front cannot be printed.");
            case "page.pdf":
                if (value.Length == 0) return AutomationRegistry.Error("page.pdf needs the path to write to.");
                var pdf = await pages.ExportPdfAsync(value);
                return await Result(new JsonObject { ["ok"] = pdf is not null, ["size"] = pdf is not null ? new FileInfo(pdf).Length : 0 });
            case "page.devtools":
                return pages.ToggleDevTools() ? AutomationRegistry.Ok() : AutomationRegistry.Error("The page in front has no developer tools.");
            case "page.devtoolsOpen":
                return await Result(new JsonObject { ["open"] = pages.Host is IDevToolsProbe probe && tab is not null && probe.HasDevTools(tab) });
            case "page.mute":
                if (tab is null) return AutomationRegistry.Error("There is no tab.");
                pages.ApplyMute(tab, value != "false");
                return AutomationRegistry.Ok();
            case "page.screenshot":
                return await Screenshot(pages, value, Result);
            case "page.permission":
                if (pages.PermissionPrompts.FirstOrDefault(prompt => prompt.TabId == tab?.Id) is not { } prompt)
                {
                    return AutomationRegistry.Error("The tab in front has no permission prompt.");
                }
                pages.Answer(prompt, value switch
                {
                    "allow" => SitePermissionDecision.Allow,
                    "block" => SitePermissionDecision.Block,
                    _ => null,
                });
                return AutomationRegistry.Ok();
            case "page.setPermission":
                // "<site>|<permission>|<allow|block|forget>"
                var parts = value.Split('|');
                if (parts.Length != 3 || SitePermissionStore.ParsePermission(parts[1]) is not { } permission)
                {
                    return AutomationRegistry.Error("Expected \"<site>|<permission>|<allow|block|forget>\".");
                }
                pages.Permissions.Set(SitePermissionStore.ParseDecision(parts[2]), permission, parts[0]);
                return AutomationRegistry.Ok();
            case "page.popup":
                if (pages.PopupNotices.FirstOrDefault(notice => notice.TabId == tab?.Id) is not { } notice)
                {
                    return AutomationRegistry.Error("The tab in front has no blocked pop-ups.");
                }
                if (value is "open" or "allow") pages.OpenPopups(notice, alwaysAllow: value == "allow");
                else pages.DismissPopups(notice);
                return AutomationRegistry.Ok();
            case "page.dialog":
                if (pages.Dialogs.FirstOrDefault(dialog => dialog.TabId == tab?.Id) is not { } dialog)
                {
                    return AutomationRegistry.Error("The tab in front has no dialog.");
                }
                var answer = value.Split('|', 2);
                pages.Finish(dialog, answer[0] == "accept", answer.Length > 1 ? answer[1] : null);
                return AutomationRegistry.Ok();
            case "page.pip":
                await pages.TogglePictureInPictureAsync();
                return AutomationRegistry.Ok();
            case "page.fullscreenExit":
                pages.EndFullScreen();
                return AutomationRegistry.Ok();
            case "page.eval":
                // Chromium only: runs the script with a user gesture (test pages use it to call
                // APIs that need one).
                if (tab is null || pages.ChromiumPage(tab) is not { } page) return AutomationRegistry.Error("The tab in front is not a Chromium page.");
                return await Result(new JsonObject { ["result"] = (await page.EvaluateAsync(value, userGesture: true))?.DeepClone() });
            case "page.click":
                // "x,y[,button]" in page CSS pixels; button 0 left, 2 right. A real click.
                var numbers = value.Split(',').Select(part => double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN).ToList();
                if (tab is null || numbers.Count < 2 || numbers.Any(double.IsNaN)) return AutomationRegistry.Error("Expected \"x,y[,button]\".");
                var button = numbers.Count > 2 ? (int)numbers[2] : 0;
                if (tab.Engine == EngineKind.Chromium) pages.ChromiumPage(tab)?.Click(numbers[0], numbers[1], button);
                else if (tab.GeckoId is { } clickId) model.Gecko.Send("synthesizeClick", clickId, numbers[0], numbers[1], button);
                return AutomationRegistry.Ok();
            case "page.gecko":
                return await Result(await GeckoCall(model, value));
            case "page.contextMenuRun":
                // value: the label of an item of the last menu shown (Mosaic's or the engine's).
                return pages.RunLastMenuItem(value) ? AutomationRegistry.Ok() : AutomationRegistry.Error($"The last context menu has no item \"{value}\".");
            case "page.closePanels":
                var closed = new JsonArray();
                foreach (var name in pages.Host is IPanelCloser closer ? closer.ClosePanels() : []) closed.Add(name);
                if (model.Gecko.Status == GeckoStatus.Running)
                {
                    try
                    {
                        if (await model.Gecko.CallAsync("openPopups", true) is JsonArray popups) foreach (var popup in popups) closed.Add(popup?.DeepClone());
                        if (tab?.GeckoId is { } dialogsOf) await model.Gecko.CallAsync("closeDialogs", dialogsOf);
                    }
                    catch (Exception error) when (error is RdpException or InvalidOperationException)
                    {
                        // Nothing open in Gecko to close.
                    }
                }
                return await Result(new JsonObject { ["closed"] = closed });
            default:
                return AutomationRegistry.Error($"Unknown command \"{command}\".");
        }
    }

    private static async Task<AutomationReply> Screenshot(PageFeatures pages, string value, Func<JsonNode?, Task<AutomationReply>> result)
    {
        var full = value.StartsWith("full|", StringComparison.Ordinal) || value == "full";
        var where = full ? (value.Length > 5 ? value[5..] : "") : value;
        var target = where switch
        {
            "" => ScreenshotTarget.Screenshots,
            "clipboard" => ScreenshotTarget.Clipboard,
            _ => ScreenshotTarget.File,
        };
        var shot = await pages.TakeScreenshotAsync(full, target, target == ScreenshotTarget.File ? where : null);
        return await result(new JsonObject
        {
            ["ok"] = shot is not null,
            ["path"] = shot?.SavedTo ?? "",
            ["size"] = shot is { } taken ? new JsonObject { ["width"] = taken.Capture.Width, ["height"] = taken.Capture.Height } : new JsonObject(),
            ["cropped"] = shot?.Capture.Cropped ?? false,
        });
    }

    private static async Task<JsonNode?> GeckoCall(BrowserModel model, string value)
    {
        var split = value.Split('|', 2);
        var arguments = new List<object?>();
        if (model.SelectedTab?.GeckoId is { } id) arguments.Add(id);
        else arguments.Add(-1);
        if (split.Length > 1 && JsonNode.Parse(split[1]) is JsonArray extra) arguments.AddRange(extra.Select(node => (object?)node?.DeepClone()));
        try
        {
            return new JsonObject { ["result"] = (await model.Gecko.CallAsync(split[0], [.. arguments]))?.DeepClone() };
        }
        catch (Exception error) when (error is RdpException or InvalidOperationException or ArgumentException)
        {
            return new JsonObject { ["error"] = error.Message };
        }
    }
}

/// <summary>Optional host ability: whether a Chromium tab's developer tools are open.</summary>
public interface IDevToolsProbe
{
    bool HasDevTools(BrowserTab tab);
}

/// <summary>Optional host ability: closes Mosaic's own pickers and dialogs so scripted checks never leave one open.</summary>
public interface IPanelCloser
{
    IReadOnlyList<string> ClosePanels();
}

public sealed partial class PageFeatures
{
    private IReadOnlyList<PageMenuEntry> lastMenu = [];
    private Action<PageMenuEntry>? lastMenuRunner;

    /// <summary>The last context menu shown and how to run its items (for scripted checks); set by the app.</summary>
    public void RecordContextMenu(IReadOnlyList<PageMenuEntry> menu, Action<PageMenuEntry> run)
    {
        lastMenu = menu;
        lastMenuRunner = run;
    }

    internal bool RunLastMenuItem(string label)
    {
        static PageMenuEntry? Find(IReadOnlyList<PageMenuEntry> items, string label)
        {
            foreach (var item in items)
            {
                if (item.Label == label && !item.IsSeparator) return item;
                if (item.Children is { } children && Find(children, label) is { } found) return found;
            }
            return null;
        }
        if (Find(lastMenu, label) is not { } entry || lastMenuRunner is not { } run) return false;
        lastMenu = [];
        lastMenuRunner = null;
        run(entry);
        return true;
    }

    /// <summary>The <c>dump</c> fields: what the page overlays show and each tab's page state.</summary>
    public JsonObject AutomationState()
    {
        int IndexOf(Guid id) => model.Tabs.Select((tab, index) => (tab, index)).FirstOrDefault(pair => pair.tab.Id == id, (null!, -1)).index;
        static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(item => (JsonNode)JsonValue.Create(item)!)]);

        var prompts = new JsonArray();
        foreach (var prompt in this.prompts)
        {
            prompts.Add(new JsonObject
            {
                ["tab"] = IndexOf(prompt.TabId),
                ["engine"] = prompt.Engine.Id(),
                ["sentence"] = prompt.Sentence,
                ["kinds"] = Array(prompt.Request.Kinds.Select(kind => kind.Name())),
                ["notes"] = Array(prompt.Notes),
                ["blockedByWindows"] = Array(prompt.BlockedByWindows.Select(WindowsPrivacy.Noun)),
            });
        }
        var notices = new JsonArray();
        foreach (var notice in popupNotices)
        {
            notices.Add(new JsonObject { ["tab"] = IndexOf(notice.TabId), ["count"] = notice.Count, ["urls"] = Array(notice.Urls) });
        }
        var dialogItems = new JsonArray();
        foreach (var dialog in dialogs)
        {
            dialogItems.Add(new JsonObject
            {
                ["tab"] = IndexOf(dialog.TabId),
                ["kind"] = PageDialog.KindName(dialog.Kind),
                ["message"] = dialog.Message,
                ["origin"] = dialog.Origin,
            });
        }
        var tabs = new JsonArray();
        foreach (var tab in model.Tabs)
        {
            tabs.Add(new JsonObject { ["zoom"] = ZoomOf(tab), ["muted"] = IsMuted(tab), ["playingAudio"] = IsPlayingAudio(tab) });
        }
        var windows = new JsonObject();
        foreach (var capability in Enum.GetValues<WindowsCapability>())
        {
            windows[WindowsPrivacy.Noun(capability)] = options.WindowsAccess(capability).ToString();
        }
        return new JsonObject
        {
            ["findBarVisible"] = isFindBarVisible,
            ["findText"] = findText,
            ["findMatchCase"] = findMatchCase,
            ["findStatus"] = findStatus is { } status
                ? new JsonObject { ["current"] = status.Current, ["total"] = status.Total, ["label"] = status.Label }
                : new JsonObject(),
            ["permissionPrompts"] = prompts,
            ["popupNotices"] = notices,
            ["dialogs"] = dialogItems,
            ["fullScreenTab"] = fullScreenTabId is { } full ? IndexOf(full) : -1,
            ["hasOverlay"] = HasOverlay,
            ["sitePermissions"] = Array(Permissions.Entries().Select(entry =>
                $"{entry.Site} {SitePermissionStore.Name(entry.Permission)} {SitePermissionStore.Name(entry.Decision)}")),
            ["siteZoom"] = Array(ZoomStore.Sites.Select(entry => $"{entry.Site} {entry.Factor.ToString(CultureInfo.InvariantCulture)}")),
            ["contextMenu"] = Array(lastMenu.Select(item => item.IsSeparator ? "-" : item.Label)),
            ["windowsPrivacy"] = windows,
            ["tabs"] = tabs,
        };
    }
}
