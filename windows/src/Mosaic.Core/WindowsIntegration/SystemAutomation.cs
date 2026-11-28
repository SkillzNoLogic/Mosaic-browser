using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.WindowsIntegration;

/// <summary>Default-browser operations on this system (the app's implementation uses HKCU and the shell).</summary>
public interface IDefaultBrowserService
{
    /// <param name="appName">Registration name; null for Mosaic's own ("Mosaic"). CI uses a throwaway name.</param>
    DefaultBrowserStatus Status(string? appName = null);
    DefaultBrowserStatus Register(string? appName = null);
    DefaultBrowserStatus Unregister(string? appName = null);

    /// <summary>Opens Settings › Default apps at Mosaic's page; false when it could not be opened.</summary>
    bool OpenSettings(string? appName = null);

    /// <summary>The effective default handler's ProgID for a scheme or extension, as the shell reports it (null when unknown).</summary>
    string? ShellDefault(string association);
}

/// <summary>
/// The system area's automation commands (names are Windows-only; macOS has no equivalents):
/// <list type="table">
/// <item><term><c>defaultBrowserStatus</c> [name]</term><description>reply <c>{"ok":true,"status":{…}}</c></description></item>
/// <item><term><c>registerDefault</c> [name]</term><description>writes the HKCU registration (name defaults to "Mosaic"); replies the status</description></item>
/// <item><term><c>unregisterDefault</c> [name]</term><description>removes it; replies the status</description></item>
/// <item><term><c>openDefaultAppsSettings</c></term><description>opens Settings › Default apps (interactive use only)</description></item>
/// <item><term><c>notify</c> "title|body" or JSON</term><description>posts a notification; reply <c>{"ok":true,"result":{shown,status,id}}</c></description></item>
/// <item><term><c>notifyActivate</c> "k=v;k=v"</term><description>delivers a simulated click (tests the routing)</description></item>
/// <item><term><c>taskbarProgress</c> "normal:0.4" | "indeterminate" | "none"</term><description>sets the "automation" progress source</description></item>
/// <item><term><c>activate</c> command line</term><description>parses it like a launch and applies it to the window</description></item>
/// <item><term><c>activationPayload</c> command line</term><description>replies <c>{"ok":true,"request":{…},"payload":"…"}</c> without applying</description></item>
/// </list>
/// All replies may also be written to <c>out</c>.
/// </summary>
public static class SystemAutomation
{
    public static async Task<AutomationReply?> HandleAsync(
        IDefaultBrowserService? defaultBrowser, BrowserModel model, string command, string value, string? output)
    {
        JsonObject? reply = command switch
        {
            "defaultBrowserStatus" => WithStatus(defaultBrowser, s => s.Status(Name(value))),
            "registerDefault" => WithStatus(defaultBrowser, s => s.Register(Name(value))),
            "unregisterDefault" => WithStatus(defaultBrowser, s => s.Unregister(Name(value))),
            "openDefaultAppsSettings" => defaultBrowser is null ? Fail("Default-browser integration is not available.")
                : defaultBrowser.OpenSettings(Name(value)) ? Ok() : Fail("Settings could not be opened."),
            "notify" => Notify(value),
            "notifyActivate" => NotifyActivate(value),
            "taskbarProgress" => Progress(value),
            "activate" => Activate(model, value, apply: true),
            "activationPayload" => Activate(model, value, apply: false),
            _ => null,
        };
        if (reply is null) return null;
        if (output is not null) await File.WriteAllTextAsync(output, reply.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new AutomationReply(reply.ToJsonString());
    }

    /// <summary>The Core-side part of the <c>system</c> dump key.</summary>
    public static JsonObject CoreState()
    {
        var last = ActivationRequest.LastApplied;
        return new JsonObject
        {
            ["notifications"] = UserNotifications.State(),
            ["taskbar"] = TaskbarProgress.State(),
            ["appearance"] = SystemAppearanceMonitor.Current.ToJson(),
            ["appearanceChanges"] = SystemAppearanceMonitor.ChangeCount,
            ["lastActivation"] = last is null ? null : Describe(last),
        };
    }

    private static string? Name(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonObject Ok() => new() { ["ok"] = true };

    private static JsonObject Fail(string message) => new() { ["ok"] = false, ["error"] = message };

    private static JsonObject WithStatus(IDefaultBrowserService? service, Func<IDefaultBrowserService, DefaultBrowserStatus> action)
    {
        if (service is null) return Fail("Default-browser integration is not available.");
        var status = action(service);
        var json = status.ToJson();
        var shell = new JsonObject();
        foreach (var scheme in DefaultBrowserRegistration.UrlSchemes) shell[scheme] = service.ShellDefault(scheme);
        shell[".html"] = service.ShellDefault(".html");
        json["shellDefaults"] = shell;
        return new JsonObject { ["ok"] = true, ["status"] = json };
    }

    private static JsonObject Notify(string value)
    {
        NotificationRequest request;
        var text = value.Trim();
        if (text.StartsWith('{'))
        {
            if (JsonNode.Parse(text) is not JsonObject json) return Fail("Expected a JSON object.");
            var arguments = new Dictionary<string, string>();
            if (json["args"] is JsonObject args)
                foreach (var (k, v) in args) if (v is not null) arguments[k] = v.ToString();
            request = new NotificationRequest(
                json["area"]?.GetValue<string>() ?? "automation",
                json["title"]?.GetValue<string>() ?? "Mosaic",
                json["body"]?.GetValue<string>() ?? "")
            {
                Arguments = arguments,
                Tag = json["tag"]?.GetValue<string>(),
                Silent = json["silent"]?.GetValue<bool>() ?? false,
            };
        }
        else
        {
            var parts = text.Split('|', 2);
            request = new NotificationRequest("automation", parts[0].Length > 0 ? parts[0] : "Mosaic", parts.Length > 1 ? parts[1] : "");
        }
        var result = UserNotifications.Post(request);
        return new JsonObject
        {
            ["ok"] = true,
            ["result"] = new JsonObject { ["shown"] = result.Shown, ["status"] = result.Status, ["id"] = result.Id },
        };
    }

    private static JsonObject NotifyActivate(string value)
    {
        var arguments = new Dictionary<string, string>();
        foreach (var pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2);
            arguments[parts[0]] = parts.Length > 1 ? parts[1] : "";
        }
        UserNotifications.Deliver(new NotificationActivation(arguments, new Dictionary<string, string>()));
        return Ok();
    }

    private static JsonObject Progress(string value)
    {
        var parts = value.Split(':', 2);
        if (TaskbarProgress.ParseState(parts[0]) is not { } state) return Fail($"Unknown progress state \"{parts[0]}\".");
        var fraction = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0;
        TaskbarProgress.Set("automation", state, fraction);
        return new JsonObject { ["ok"] = true, ["taskbar"] = TaskbarProgress.State() };
    }

    private static JsonObject Activate(BrowserModel model, string commandLine, bool apply)
    {
        var args = CommandLineSplitter.Split(commandLine);
        var request = ActivationRequest.Parse(args, "Mosaic.exe " + commandLine, Environment.CurrentDirectory, File.Exists);
        if (apply) request.ApplyTo(model);
        return new JsonObject { ["ok"] = true, ["request"] = Describe(request), ["payload"] = request.ToPayload() };
    }

    private static JsonObject Describe(ActivationRequest request) => new()
    {
        ["targets"] = new JsonArray([.. request.Targets.Select(t => (JsonNode)JsonValue.Create(t))]),
        ["rejected"] = new JsonArray([.. request.Rejected.Select(t => (JsonNode)JsonValue.Create(t))]),
        ["newTab"] = request.NewTab,
        ["private"] = request.Private,
        ["newWindow"] = request.NewWindow,
        ["notification"] = request.FromNotification,
    };
}
