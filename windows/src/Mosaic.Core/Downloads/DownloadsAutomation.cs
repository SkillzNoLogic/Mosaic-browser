using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Downloads;

/// <summary>
/// Scripted control of downloads (see <see cref="Automation"/>), with the macOS command names
/// (<c>DownloadManager+Automation.swift</c>):
/// <code>
/// downloadsFolder &lt;path&gt;                     where downloads are saved
/// downloadsSetting &lt;key&gt;=&lt;value&gt;            askWhereToSave, openSafeFiles, notify, organize,
///                                            fdmMode (off|ask|always), fdmMinimumMB, fdmCookies, fdmContextMenu
/// download &lt;url&gt;                             start a download in Chromium (falls back to Gecko)
/// downloadGecko &lt;url&gt;                        start a download in Gecko
/// downloadPause|downloadResume|downloadCancel|downloadRetry|downloadRemove|downloadOpen|
/// downloadReveal|downloadKeep|downloadTrash|downloadWithFDM &lt;item&gt;
///                                            &lt;item&gt;: index in the list (0 = newest) or a file name
/// downloadLinkToFDM &lt;url&gt;                    as the link context-menu item does
/// downloadsClear                             clear finished entries
/// downloadPromptAnswer &lt;n&gt;                   press button n of the open download question
/// downloadPromptSave &lt;path&gt;                  save the open "Save As" question as &lt;path&gt;
/// downloadsPopover true|false                show or hide the toolbar flyout
/// downloadsPage                              open mosaic://downloads
/// downloadsSearch &lt;text&gt;                     the Downloads page's search field
/// downloadsSnapshot &lt;png&gt;                    render the flyout, page and settings to PNGs (app)
/// </code>
/// and the <c>downloads</c> key of <c>dump</c>.
/// </summary>
public static class DownloadsAutomation
{
#pragma warning disable CA2255 // registering with the automation pipe is cheap and must happen before any request
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("downloads", model => State(model.Downloads));
    }

    private static readonly HashSet<string> ItemCommands =
    [
        "downloadPause", "downloadResume", "downloadCancel", "downloadRetry", "downloadRemove", "downloadOpen",
        "downloadReveal", "downloadKeep", "downloadTrash", "downloadWithFDM",
    ];

    private static Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        if (!command.StartsWith("download", StringComparison.Ordinal) || command == "downloadsSnapshot")
        {
            return Task.FromResult<AutomationReply?>(null);
        }
        return Task.FromResult(Handle(model.Downloads, command, value));
    }

    /// <summary>Runs one downloads command; null when <paramref name="command"/> is not one.</summary>
    public static AutomationReply? Handle(DownloadManager downloads, string command, string value)
    {
        switch (command)
        {
            case "downloadsFolder":
                downloads.Settings.FolderPath = Environment.ExpandEnvironmentVariables(value);
                return AutomationRegistry.Ok();
            case "downloadsSetting":
                return ApplySetting(downloads.Settings, value)
                    ? AutomationRegistry.Ok()
                    : AutomationRegistry.Error($"Unknown downloads setting \"{value}\".");
            case "download":
                downloads.StartInChromium(value);
                return AutomationRegistry.Ok();
            case "downloadGecko":
                downloads.StartInGecko(value);
                return AutomationRegistry.Ok();
            case "downloadLinkToFDM":
                if (!downloads.Fdm.IsInstalled) return AutomationRegistry.Error("Free Download Manager is not installed.");
                downloads.SendLinkToFdm(value, null, downloads.CurrentEngine?.Invoke() ?? EngineKind.Chromium);
                return AutomationRegistry.Ok();
            case "downloadsClear":
                downloads.ClearFinished();
                return AutomationRegistry.Ok();
            case "downloadPromptAnswer":
                return downloads.Prompter.Answer(int.TryParse(value, out var index) ? index : 0)
                    ? AutomationRegistry.Ok()
                    : AutomationRegistry.Error("No download question is open.");
            case "downloadPromptSave":
                return downloads.Prompter.Save(Environment.ExpandEnvironmentVariables(value))
                    ? AutomationRegistry.Ok()
                    : AutomationRegistry.Error("No \"Save As\" question is open.");
            case "downloadsPopover":
                downloads.IsPopoverShown = value != "false";
                return AutomationRegistry.Ok();
            case "downloadsPage":
                downloads.OpenDownloadsPage();
                return AutomationRegistry.Ok();
            case "downloadsSearch":
                downloads.SearchText = value;
                return AutomationRegistry.Ok();
        }
        if (!ItemCommands.Contains(command)) return null;
        if (Item(downloads, value) is not { } id) return AutomationRegistry.Error($"No download \"{value}\".");
        switch (command)
        {
            case "downloadPause": downloads.Pause(id); break;
            case "downloadResume": downloads.Resume(id); break;
            case "downloadCancel": downloads.Cancel(id); break;
            case "downloadRetry": downloads.Retry(id); break;
            case "downloadRemove": downloads.Remove(id); break;
            case "downloadOpen": downloads.Open(id); break;
            case "downloadReveal": downloads.Reveal(id); break;
            case "downloadKeep": downloads.Keep(id); break;
            case "downloadTrash": downloads.Trash(id); break;
            case "downloadWithFDM": downloads.SendToFdm(id); break;
        }
        return AutomationRegistry.Ok();
    }

    private static Guid? Item(DownloadManager downloads, string value)
    {
        var items = downloads.Items;
        if (int.TryParse(value, out var index) && index >= 0 && index < items.Count) return items[index].Id;
        return items.FirstOrDefault(record => record.FileName == value)?.Id;
    }

    private static bool ApplySetting(DownloadSettings settings, string assignment)
    {
        var parts = assignment.Split('=', 2);
        if (parts.Length != 2) return false;
        var on = parts[1].ToLowerInvariant() is "true" or "on" or "1" or "yes";
        switch (parts[0])
        {
            case "askWhereToSave": settings.AskWhereToSave = on; break;
            case "openSafeFiles": settings.OpenSafeFiles = on; break;
            case "notify": settings.NotifyWhenFinished = on; break;
            case "organize": settings.Rules = settings.Rules with { IsEnabled = on }; break;
            case "fdmMode":
                if (FdmModes.FromId(parts[1]) is not { } mode) return false;
                settings.FdmMode = mode;
                break;
            case "fdmMinimumMB": settings.FdmMinimumMB = int.TryParse(parts[1], out var mb) ? mb : 0; break;
            case "fdmCookies": settings.FdmPassCookies = on; break;
            case "fdmContextMenu": settings.FdmContextMenu = on; break;
            default: return false;
        }
        return true;
    }

    /// <summary>The <c>downloads</c> section of <c>dump</c> (macOS keys, plus <c>markOfTheWeb</c> for Windows).</summary>
    public static JsonObject State(DownloadManager downloads)
    {
        var settings = downloads.Settings;
        var items = new JsonArray();
        foreach (var record in downloads.Items)
        {
            var path = record.DestinationPath ?? "";
            var exists = path.Length > 0 && File.Exists(path);
            var mark = exists ? MarkOfTheWeb.Read(path) ?? "" : "";
            items.Add(new JsonObject
            {
                ["fileName"] = record.FileName,
                ["url"] = record.Url,
                ["referrer"] = record.Referrer ?? "",
                ["engine"] = record.Engine.Id(),
                ["state"] = record.State.Id(),
                ["totalBytes"] = record.TotalBytes,
                ["receivedBytes"] = record.ReceivedBytes,
                ["speed"] = record.Speed,
                ["path"] = path,
                ["fileExists"] = exists,
                // macOS's "quarantine" key; on Windows the Zone.Identifier stream's text.
                ["quarantine"] = mark,
                ["markOfTheWeb"] = mark,
                ["zoneId"] = MarkOfTheWeb.ZoneOf(mark) ?? -1,
                ["error"] = record.ErrorMessage ?? "",
                ["canResume"] = record.CanResume,
                ["warning"] = record.NeedsSafetyConfirmation ? record.Assessment.Summary ?? "" : "",
                ["risk"] = record.Assessment.Risk?.ToString() ?? "",
                ["acknowledged"] = record.WarningAcknowledged,
                ["chromiumID"] = downloads.ChromiumLink(record.Id) is null ? -1 : 1,
                ["geckoID"] = downloads.GeckoId(record.Id) ?? -1,
            });
        }
        var pending = downloads.Prompter.Pending;
        return new JsonObject
        {
            ["folder"] = settings.FolderPath,
            ["historyFile"] = downloads.HistoryPath,
            ["askWhereToSave"] = settings.AskWhereToSave,
            ["openSafeFiles"] = settings.OpenSafeFiles,
            ["organize"] = settings.Rules.IsEnabled,
            ["notify"] = settings.NotifyWhenFinished,
            ["fdmInstalled"] = downloads.Fdm.IsInstalled,
            ["fdmVersion"] = downloads.Fdm.Version ?? "",
            ["fdmMode"] = settings.FdmMode.Id(),
            ["fdmMinimumMB"] = settings.FdmMinimumMB,
            ["fdmCookies"] = settings.FdmPassCookies,
            ["fdmContextMenu"] = downloads.Fdm.IsInstalled && settings.FdmContextMenu,
            ["lastFDMRoute"] = downloads.LastFdmRoute ?? "",
            ["geckoWatching"] = downloads.GeckoWatching,
            ["activeCount"] = downloads.ActiveCount,
            ["overallProgress"] = downloads.OverallProgress ?? -1,
            ["popoverShown"] = downloads.IsPopoverShown,
            ["unseenCompletion"] = downloads.HasUnseenCompletion,
            ["pendingPrompt"] = pending is null
                ? null
                : new JsonObject
                {
                    ["kind"] = pending.Kind,
                    ["title"] = pending.Title,
                    ["buttons"] = new JsonArray([.. pending.Buttons.Select(button => JsonValue.Create(button))]),
                },
            ["lastNotification"] = downloads.LastNotification ?? "",
            ["lastOpenRequest"] = downloads.LastOpenRequest ?? "",
            ["lastAutoOpen"] = downloads.LastAutoOpen ?? "",
            ["lastMarkOfTheWeb"] = downloads.LastMarkOfTheWeb ?? "",
            ["searchText"] = downloads.SearchText,
            ["filteredCount"] = downloads.FilteredItems.Count,
            ["items"] = items,
        };
    }
}
