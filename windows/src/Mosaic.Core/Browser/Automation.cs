using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;
using Mosaic.Core.Search;

namespace Mosaic.Core.Browser;

/// <summary>The one-line JSON answer to an automation request.</summary>
/// <param name="Json">The reply, without a trailing newline.</param>
/// <param name="QuitRequested">The app should exit once the reply has been delivered.</param>
public sealed record AutomationReply(string Json, bool QuitRequested = false);

/// <summary>
/// Scripted control for end-to-end checks, off unless Mosaic is launched with
/// <see cref="Flag"/>. The app listens on the named pipe <see cref="PipeName"/>; each client
/// writes one line of JSON and reads one line back:
/// <code>
/// {"do": "navigate", "value": "example.com"}      →  {"ok": true}
/// {"do": "dump"}                                  →  {"ok": true, "state": {…}}
/// </code>
/// This class is everything except the pipe itself and the screenshot, so it can be tested
/// without Windows. The commands are those of <c>Sources/Mosaic/App/Automation.swift</c>.
/// </summary>
/// <remarks>
/// Commands that start long work (installing an extension, opening an extension page) reply as
/// soon as the work has started, as on macOS; scripts watch <c>dump</c> for the outcome
/// (<c>extensionsBusy</c>, <c>extensionError</c>, <c>tabs</c>).
/// </remarks>
public sealed class Automation
{
    public const string Flag = "--mosaic-automation";

    /// <summary>Name of the pipe, i.e. <c>\\.\pipe\mosaic-automation</c>.</summary>
    public const string PipeName = "mosaic-automation";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly BrowserModel model;
    private readonly Func<string, Task> captureWindow;

    /// <param name="model">The browser to drive; its focus changes are switched off.</param>
    /// <param name="captureWindow">Writes a PNG of Mosaic's window to the given path.</param>
    public Automation(BrowserModel model, Func<string, Task> captureWindow)
    {
        this.model = model;
        this.captureWindow = captureWindow;
        // A scripted run must never take keyboard focus from the person at the PC.
        model.AllowsFocusChanges = false;
    }

    /// <summary>Runs one request line and produces its reply. Never throws.</summary>
    public async Task<AutomationReply> HandleAsync(string line)
    {
        try
        {
            if (ParseRequest(line) is not { } request) return Failure("The request is not a JSON object with a \"do\" command.");
            return await RunAsync(request.Command, request.Value, request.Out);
        }
        catch (Exception error)
        {
            // Whatever a command trips over (a path that cannot be written, Gecko not running)
            // belongs in the reply; the script on the other end is the only one who can act on it.
            return Failure(error.Message);
        }
    }

    private static (string Command, string Value, string? Out)? ParseRequest(string line)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
        if (parsed is not JsonObject request || Text(request["do"]) is not { } command) return null;
        return (command, Text(request["value"]) ?? "", Text(request["out"]));
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private async Task<AutomationReply> RunAsync(string command, string value, string? output)
    {
        switch (command)
        {
            case "navigate":
                model.Navigate(value);
                return Success();
            case "newTab":
                model.NewTab(value);
                return Success();
            case "closeTab":
                model.CloseSelectedTab();
                return Success();
            case "selectTab":
                if (!int.TryParse(value, out var index) || index < 0 || index >= model.Tabs.Count)
                {
                    return Failure($"There is no tab {value}.");
                }
                model.Select(model.Tabs[index]);
                return Success();
            case "engine":
                if (EngineKindExtensions.FromId(value) is not { } kind) return Failure($"Unknown engine \"{value}\".");
                model.SetEngine(kind);
                return model.Engine == kind ? Success() : Failure(model.Notice ?? "The engine could not be switched.");
            case "toggleEngine":
                model.ToggleEngine();
                return Success();
            case "searchEngine":
                if (SearchEngine.Named(value) is not { } searchEngine) return Failure($"Unknown search engine \"{value}\".");
                model.SearchEngine = searchEngine;
                return Success();
            case "back":
                model.GoBack();
                return Success();
            case "forward":
                model.GoForward();
                return Success();
            case "reload":
                model.Reload();
                return Success();
            case "installExtension":
                _ = model.Extensions.InstallFromWebStoreAsync(value);
                return Success();
            case "installFile":
                _ = model.Extensions.InstallFileAsync(value);
                return Success();
            case "removeExtension":
                if (model.Extensions.Extensions.FirstOrDefault(candidate => candidate.Id == value) is not { } removed)
                {
                    return Failure($"No extension {value} is installed.");
                }
                _ = model.Extensions.RemoveAsync(removed);
                return Success();
            case "extensionPage":
                // "<chrome id>:popup" or "<chrome id>:options"
                var parts = value.Split(':');
                if (parts is not [var chromeId, "popup" or "options"]) return Failure("Expected \"<extension id>:popup\" or \"<extension id>:options\".");
                if (model.Extensions.Extensions.FirstOrDefault(candidate => candidate.Id == chromeId) is not { } extension)
                {
                    return Failure($"No extension {chromeId} is installed.");
                }
                _ = model.OpenExtensionPageAsync(extension, parts[1]);
                return Success();
            case "showExtensions":
                model.ShowExtensions = value != "false";
                return Success();
            case "blocker":
                model.SetBlockerEnabled(value != "off");
                return Success();
            case "blockerSite":
                if (model.SelectedTab is not { } page || ContentBlocker.SiteOfPage(page.Url) is null)
                {
                    return Failure("The page in front is not a web page, so the content blocker does not apply to it.");
                }
                model.SetBlocking(value != "off", page);
                return Success();
            case "showBlocker":
                model.ShowBlocker = value != "false";
                return Success();
            case "updateLists":
                // Like installing an extension: scripts watch dump's blocker.updating for the outcome.
                _ = model.Blocker.UpdateListsAsync();
                return Success();
            case "geckoEval":
                // {"do": "geckoEval", "value": "<agent method>", "out": "<path>"}
                var result = await model.Gecko.CallAsync(value);
                if (output is not null) await File.WriteAllTextAsync(output, result?.ToJsonString() ?? "null");
                return Success();
            case "dump":
                var state = DumpState();
                if (value.Length > 0) await File.WriteAllTextAsync(value, state.ToJsonString(Indented));
                return new AutomationReply(new JsonObject { ["ok"] = true, ["state"] = state }.ToJsonString());
            case "screenshot":
                if (value.Length == 0) return Failure("screenshot needs the path to write to.");
                await captureWindow(value);
                return Success();
            case "quit":
                return Success(quit: true);
            default:
                // Feature areas register their own commands (AutomationRegistry).
                foreach (var handler in AutomationRegistry.Handlers)
                {
                    if (await handler(model, command, value, output) is { } reply) return reply;
                }
                return Failure($"Unknown command \"{command}\".");
        }
    }

    /// <summary>The browser's state as scripts see it; the keys are part of the build contract.</summary>
    public JsonObject DumpState()
    {
        var tabs = new JsonArray();
        foreach (var tab in model.Tabs)
        {
            tabs.Add(new JsonObject
            {
                ["engine"] = tab.Engine.Id(),
                ["url"] = tab.Url,
                ["title"] = tab.Title,
                ["loading"] = tab.IsLoading,
                ["canGoBack"] = tab.CanGoBack,
                ["canGoForward"] = tab.CanGoForward,
                ["geckoId"] = tab.GeckoId ?? -1,
                ["hasChromiumView"] = tab.Chromium is not null,
                ["blocked"] = tab.BlockedCount,
                ["failure"] = tab.Failure ?? "",
                ["selected"] = ReferenceEquals(tab, model.SelectedTab),
            });
        }

        var extensions = new JsonArray();
        foreach (var extension in model.Extensions.Extensions)
        {
            var notes = new JsonArray();
            foreach (var note in extension.Notes)
            {
                notes.Add($"{note.Severity.ToString().ToLowerInvariant()}: {note.Message}");
            }
            extensions.Add(new JsonObject
            {
                ["id"] = extension.Id,
                ["geckoId"] = extension.GeckoId,
                ["name"] = extension.Name,
                ["version"] = extension.Version,
                ["enabled"] = extension.Enabled,
                ["loaded"] = model.Extensions.IsLoaded(extension),
                ["notes"] = notes,
            });
        }

        return new JsonObject
        {
            ["tabs"] = tabs,
            ["engine"] = model.Engine.Id(),
            ["searchEngine"] = model.SearchEngine.Id,
            ["addressText"] = model.AddressText,
            ["chromiumAvailable"] = model.ChromiumAvailable,
            ["geckoStatus"] = StatusName(model.Gecko.Status),
            ["geckoProcessId"] = model.Gecko.ProcessId ?? -1,
            ["geckoAttached"] = model.IsGeckoAttached,
            ["notice"] = model.Notice ?? "",
            ["extensions"] = extensions,
            ["blocker"] = BlockerState(model.Blocker),
            ["extensionError"] = model.Extensions.ErrorMessage ?? "",
            ["extensionsBusy"] = model.Extensions.IsBusy,
        }.WithFeatureState(model);
    }

    private static JsonObject BlockerState(ContentBlocker blocker) => new()
    {
        ["status"] = blocker.Status.ToString().ToLowerInvariant(),
        ["enabled"] = blocker.IsEnabled,
        ["allowlist"] = new JsonArray([.. blocker.Settings.Allowlist.Order(StringComparer.Ordinal).Select(site => JsonValue.Create(site))]),
        ["engineVersion"] = blocker.EngineVersion ?? "",
        ["updating"] = blocker.IsUpdating,
        ["filters"] = blocker.FilterCount,
        ["error"] = blocker.FailureMessage ?? "",
    };

    private static string StatusName(GeckoStatus status) => status switch
    {
        GeckoStatus.Stopped => "stopped",
        GeckoStatus.Starting => "starting",
        GeckoStatus.Running => "running",
        _ => "failed",
    };

    private static AutomationReply Success(bool quit = false) => new("""{"ok":true}""", quit);

    private static AutomationReply Failure(string error) =>
        new(new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString());
}
