using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Platform;

/// <summary>
/// The platform area's automation commands and <c>dump</c> field (registered with
/// <see cref="AutomationRegistry"/>), with the macOS names (<c>PlatformAutomation.swift</c>):
/// <code>
/// runtimeInstall [C:\path\Firefox Setup.exe]  install/update the private Gecko runtime
///                                             (default: download Firefox ESR from Mozilla)
/// runtimeCancel                               cancel an install in progress
/// runtimeCheck                                ask Mozilla for the current ESR version
/// runtimeRemove                               delete the private runtime
/// ruleAdd    example.com=chromium[,exact]     add or replace a per-site engine rule
/// ruleRemove example.com
/// ruleCurrent chromium                        "always open this site in …" for the current tab
/// defaultEngine gecko|chromium
/// languages en-GB,fr                          set web-content languages (or "system")
/// updateCheck / updateFrequency manual|daily
/// updateDownload (the release the last check found) / updateCancel / updateRestart
/// updateAuto on|off
/// extensionToolbar &lt;index&gt;                    perform toolbar button &lt;index&gt;
/// safeSwitch on|off                           (Windows) safe engine switching, on in the app
/// </code>
/// Long work (installs, checks, downloads) replies as soon as it has started, as on macOS; scripts
/// watch <c>dump</c>'s <c>platform</c> object for the outcome. The macOS app puts these fields at
/// the top level of <c>dump</c>; here they are grouped under <c>platform</c> so their generic names
/// ("rules", "languages", "updates") cannot collide with another area's.
/// </summary>
public static class PlatformAutomation
{
    private static int registered;

    /// <summary>Registers the commands and the dump field once per process.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("platform", Dump);
    }

    public static Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output) =>
        Task.FromResult(Handle(model, command, value));

    private static AutomationReply? Handle(BrowserModel model, string command, string value)
    {
        var platform = model.Platform;
        switch (command)
        {
            case "runtimeInstall":
                if (value.Length > 0 && !File.Exists(value)) return AutomationRegistry.Error($"No Firefox installer at {value}.");
                if (platform.Runtime.IsActive) return AutomationRegistry.Error("An install is already in progress.");
                platform.Runtime.Install(value.Length > 0 ? value : null);
                return AutomationRegistry.Ok();
            case "runtimeCancel":
                platform.Runtime.Cancel();
                return AutomationRegistry.Ok();
            case "runtimeCheck":
                _ = platform.Runtime.CheckForUpdateAsync();
                return AutomationRegistry.Ok();
            case "runtimeRemove":
                _ = platform.Runtime.RemoveAsync();
                return AutomationRegistry.Ok();
            case "ruleAdd":
            {
                var parts = value.Split('=', 2);
                if (parts.Length != 2) return AutomationRegistry.Error("Expected \"example.com=chromium[,exact]\".");
                var options = parts[1].Split(',');
                if (EngineKindExtensions.FromId(options[0]) is not { } kind) return AutomationRegistry.Error($"Unknown engine \"{options[0]}\".");
                return platform.Rules.Set(parts[0], kind, includesSubdomains: !options.Contains("exact"))
                    ? AutomationRegistry.Ok()
                    : AutomationRegistry.Error($"\"{parts[0]}\" is not a site address.");
            }
            case "ruleRemove":
            {
                var domain = EngineRuleSet.NormalizedDomain(value);
                if (platform.Rules.Rules.FirstOrDefault(rule => rule.Domain == domain) is not { } rule) return AutomationRegistry.Error($"No rule for {value}.");
                platform.Rules.Remove(rule);
                return AutomationRegistry.Ok();
            }
            case "ruleCurrent":
                if (EngineKindExtensions.FromId(value) is not { } current) return AutomationRegistry.Error($"Unknown engine \"{value}\".");
                return model.AddEngineRuleForCurrentSite(current)
                    ? AutomationRegistry.Ok()
                    : AutomationRegistry.Error("The page in front is not a web page.");
            case "defaultEngine":
                if (EngineKindExtensions.FromId(value) is not { } preferred) return AutomationRegistry.Error($"Unknown engine \"{value}\".");
                model.SetDefaultEngine(preferred);
                return model.DefaultEngine == preferred ? AutomationRegistry.Ok() : AutomationRegistry.Error($"{preferred.DisplayName()} is not available.");
            case "languages":
                if (value == "system")
                {
                    platform.Languages.UseSystemLanguages();
                    return AutomationRegistry.Ok();
                }
                var list = new LanguagePreferences(value.Split(','));
                if (list.Tags.Count == 0) return AutomationRegistry.Error("Expected language tags such as \"en-GB,fr\", or \"system\".");
                platform.Languages.Replace(list);
                return AutomationRegistry.Ok();
            case "updateCheck":
                _ = platform.Updates.CheckNowAsync();
                return AutomationRegistry.Ok();
            case "updateFrequency":
                if (UpdateCheckFrequencies.FromId(value) is not { } frequency) return AutomationRegistry.Error("Expected \"manual\" or \"daily\".");
                platform.Updates.Frequency = frequency;
                return AutomationRegistry.Ok();
            case "updateDownload":
                if (platform.Updates.Outcome is not ReleaseOutcome.Available available) return AutomationRegistry.Error("The last check found no newer release.");
                platform.Updater.Download(available.Release);
                return AutomationRegistry.Ok();
            case "updateCancel":
                platform.Updater.Cancel();
                return AutomationRegistry.Ok();
            case "updateRestart":
                if (platform.Updater.Phase is not UpdatePhase.Ready) return AutomationRegistry.Error("No update is ready to install.");
                platform.Updater.RestartToUpdate();
                return AutomationRegistry.Ok();
            case "updateAuto":
                platform.Updater.Automatic = value == "on";
                return AutomationRegistry.Ok();
            case "extensionToolbar":
                var items = model.ExtensionToolbarItems;
                if (!int.TryParse(value, out var index) || index < 0 || index >= items.Count) return AutomationRegistry.Error($"There is no extension button {value}.");
                model.PerformExtensionToolbarItem(items[index]);
                return AutomationRegistry.Ok();
            case "safeSwitch":
                platform.SafeEngineSwitching = value != "off";
                return AutomationRegistry.Ok();
            default:
                return null;
        }
    }

    /// <summary>The <c>platform</c> object of <c>dump</c>: the macOS field names.</summary>
    public static JsonNode Dump(BrowserModel model)
    {
        var platform = model.Platform;
        var runtime = platform.Runtime;
        var updater = platform.Updater;
        var updates = platform.Updates;
        var languages = platform.Languages;

        var switching = new JsonObject();
        foreach (var (id, pending) in platform.Switches.All) switching[id.ToString()] = pending.Target.Id();

        var permissions = new JsonObject();
        foreach (var extension in model.Extensions.Extensions)
        {
            var granted = platform.PermissionsOf(extension);
            permissions[extension.Id] = new JsonObject
            {
                ["apis"] = Strings(granted.Apis),
                ["sites"] = Strings(granted.Sites),
                ["allSites"] = granted.ReadsAllSites,
            };
        }

        return new JsonObject
        {
            ["runtime"] = new JsonObject
            {
                ["phase"] = runtime.Phase.Name,
                ["summary"] = runtime.Phase.Summary,
                ["fraction"] = runtime.Phase.Fraction is { } fraction ? fraction : -1,
                ["managedPath"] = runtime.EngineDirectory,
                ["installedVersion"] = runtime.InstalledRuntime?.Version ?? "",
                ["inUse"] = model.Gecko.Runtime?.ExecutablePath ?? "",
                ["inUseVersion"] = model.Gecko.Runtime?.Version ?? "",
                ["latest"] = runtime.LatestVersion?.Original ?? "",
                ["updateAvailable"] = runtime.IsUpdateAvailable,
                ["checking"] = runtime.IsChecking,
                ["message"] = runtime.Message ?? "",
                ["missing"] = model.GeckoRuntimeMissing,
            },
            ["rules"] = new JsonArray([.. platform.Rules.Rules.Select(rule => (JsonNode)new JsonObject
            {
                ["domain"] = rule.Domain,
                ["engine"] = rule.Engine,
                ["subdomains"] = rule.IncludesSubdomains,
            })]),
            ["defaultEngine"] = model.DefaultEngine.Id(),
            ["safeSwitching"] = platform.SafeEngineSwitching,
            ["switching"] = switching,
            ["lastSwitch"] = platform.Switches.LastOutcome ?? "",
            ["languages"] = Strings(languages.Preferences.Tags),
            ["languagesCustom"] = languages.IsCustom,
            ["acceptLanguage"] = languages.Preferences.AcceptLanguageHeader,
            ["chromiumAcceptLanguages"] = languages.ChromiumApplied ?? "",
            ["chromiumNeedsRestart"] = languages.ChromiumNeedsRestart,
            ["languageErrors"] = Strings(new[] { languages.GeckoError }.OfType<string>()),
            ["updates"] = new JsonObject
            {
                ["frequency"] = updates.Frequency.Id(),
                ["outcome"] = updates.Outcome?.Message ?? "",
                ["outcomeKind"] = updates.Outcome?.Kind ?? "",
                ["checking"] = updates.IsChecking,
                ["feed"] = updates.FeedUrl.ToString(),
                ["lastCheck"] = updates.LastCheck?.ToString("o") ?? "",
            },
            ["updater"] = new JsonObject
            {
                ["phase"] = updater.Phase.Name,
                ["detail"] = updater.Phase.Detail,
                ["automatic"] = updater.Automatic,
                ["locationProblem"] = updater.LocationProblem?.Message ?? "",
                ["appPath"] = updater.InstallDirectory,
                ["version"] = updater.CurrentVersion,
                ["architecture"] = updater.Architecture.Id(),
                ["staged"] = updater.StagedFolder ?? "",
                ["log"] = updater.HelperLogPath,
            },
            ["extensionPermissions"] = permissions,
            ["extensionToolbar"] = new JsonArray([.. model.ExtensionToolbarItems.Select(item => (JsonNode)new JsonObject
            {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["running"] = item.IsRunning,
            })]),
        };
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)JsonValue.Create(value))]);
}
