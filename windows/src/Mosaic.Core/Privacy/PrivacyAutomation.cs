using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Privacy;

/// <summary>
/// The privacy area's automation commands, with macOS's <c>privacy.*</c> names
/// (<c>Sources/Mosaic/Features/Privacy/BrowserModel+Privacy.swift</c>), and the <c>privacy</c> key of <c>dump</c>.
/// </summary>
/// <remarks>
/// <code>
/// privacy.set            value "httpsOnly=on", "strip=off", "thirdPartyCookies=on", "clearOnExit=on",
///                        "cosmetic=off", "doh=strict:custom:https://…", "doh=automatic:quad9", "doh=off",
///                        "list=&lt;id&gt;:on", "httpException=example.com" ("-example.com" removes it)
/// privacy.filters        value: My filters text (\n for new lines); replies once the engine is rebuilt
/// privacy.validate       value: filters, out: JSON file (also in the reply)
/// privacy.newPrivateTab  value: URL (optional)
/// privacy.blocked        value: JSON file (blocked requests of the selected tab, with filter and list)
/// privacy.siteData       value: JSON file
/// privacy.clearSite      value: site, out: file written when done
/// privacy.clearAllSiteData  out: file written when done
/// privacy.clear          value "history,cookiesAndSiteData,cache:allTime", out: file
/// privacy.cookies        value: JSON file (Chromium cookies, default and private profile)
/// privacy.status         value: JSON file (the privacy dump plus engine details)
/// privacy.settings       opens Settings › Privacy &amp; Security; value "clear" adds the dialog
/// privacy.cosmeticScript value: page URL, out: file — the style script a Chromium main frame gets
/// privacy.rewrite        value: URL — what HTTPS-only and tracking stripping make of it
/// </code>
/// Unlike macOS, commands that do work reply when it is done (the result is also in the reply's
/// <c>result</c>), so scripts need not poll for their output files.
/// </remarks>
public static class PrivacyAutomation
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

#pragma warning disable CA2255 // A module initializer in a library: registration only stores two delegates.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => AutomationRegistry.Register(HandleAsync);

    /// <summary>
    /// Adds the <c>privacy</c> key to <c>dump</c>. Called by the app's privacy mounts rather than
    /// at module load, because the v0.1 contract tests pin the exact set of <c>dump</c> keys.
    /// </summary>
    public static void RegisterState() => AutomationRegistry.RegisterState("privacy", Dump);

    public static async Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        if (!command.StartsWith("privacy.", StringComparison.Ordinal)) return null;
        var privacy = model.Privacy;
        var blocker = model.Blocker;
        switch (command)
        {
            case "privacy.set":
                return Apply(model, value) is { } problem ? AutomationRegistry.Error(problem) : AutomationRegistry.Ok();

            case "privacy.filters":
                blocker.SaveUserFilters(value.Replace("\\n", "\n", StringComparison.Ordinal));
                await blocker.CurrentBuild;
                return Reply(new JsonObject { ["filters"] = blocker.Status == BlockerStatus.Ready ? blocker.FilterCount : null }, null);

            case "privacy.validate":
            {
                var checks = blocker.Validate(value.Replace("\\n", "\n", StringComparison.Ordinal));
                return Reply(new JsonArray([.. checks.Select(check => (JsonNode)new JsonObject
                {
                    ["line"] = check.Line, ["text"] = check.Text, ["kind"] = check.KindId, ["message"] = check.Message,
                })]), output);
            }

            case "privacy.newPrivateTab":
                return model.NewPrivateTab(value) is null
                    ? AutomationRegistry.Error(model.Notice ?? "No private tab was opened.")
                    : AutomationRegistry.Ok();

            case "privacy.blocked":
            {
                if (model.SelectedTab is not { } tab) return AutomationRegistry.Error("No tab is selected.");
                var requests = await blocker.BlockedRequestsAsync(tab);
                return Reply(new JsonArray([.. requests.Select(request => (JsonNode)new JsonObject
                {
                    ["url"] = request.Url, ["type"] = request.Type, ["count"] = request.Count,
                    ["filter"] = request.Filter, ["list"] = request.List ?? "",
                })]), NullIfEmpty(value) ?? output);
            }

            case "privacy.siteData":
                await privacy.LoadSiteDataAsync();
                return Reply(SiteDataJson(privacy), NullIfEmpty(value) ?? output);

            case "privacy.clearSite":
            {
                await privacy.LoadSiteDataAsync();
                var entry = privacy.SiteData.FirstOrDefault(item => item.Site == value) ?? new SiteDataEntry(value);
                await privacy.ClearSiteDataAsync(entry);
                return Reply(new JsonObject { ["cleared"] = value }, output);
            }

            case "privacy.clearAllSiteData":
                await privacy.ClearAllSiteDataAsync();
                return Reply(new JsonObject { ["cleared"] = "all" }, output);

            case "privacy.clear":
            {
                var parts = value.Split(':', 2);
                var kinds = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(BrowsingDataKinds.FromId).OfType<BrowsingDataKind>().ToHashSet();
                if (kinds.Count == 0) return AutomationRegistry.Error($"No known kinds of data in \"{value}\".");
                var range = parts.Length > 1 ? BrowsingDataTimeRanges.FromId(parts[1]) ?? BrowsingDataTimeRange.AllTime : BrowsingDataTimeRange.AllTime;
                var summary = await privacy.ClearBrowsingDataAsync(kinds, range);
                return Reply(new JsonObject { ["summary"] = summary }, output);
            }

            case "privacy.cookies":
            {
                if (privacy.Chromium is not { IsAvailable: true } chromium) return AutomationRegistry.Error("Chromium is not available.");
                static JsonArray Cookies(IEnumerable<ChromiumCookie> cookies) => new([.. cookies.Select(cookie => (JsonNode)new JsonObject
                {
                    ["domain"] = cookie.Domain, ["name"] = cookie.Name, ["path"] = cookie.Path,
                })]);
                var result = new JsonObject
                {
                    ["default"] = Cookies(await chromium.CookiesAsync()),
                    ["private"] = Cookies(await chromium.CookiesAsync(privateProfile: true)),
                    ["privateBrowsers"] = chromium.PrivateBrowserCount,
                    ["hasPrivateProfile"] = chromium.HasPrivateProfile,
                };
                return Reply(result, NullIfEmpty(value) ?? output);
            }

            case "privacy.status":
            {
                var status = Dump(model);
                status["chromium"] = privacy.Chromium?.Status();
                if (model.Gecko.Status == Gecko.GeckoStatus.Running)
                {
                    try
                    {
                        status["gecko"] = await model.Gecko.CallAsync("privacyStatus");
                    }
                    catch (Exception error)
                    {
                        status["gecko"] = new JsonObject { ["error"] = error.Message };
                    }
                }
                return Reply(status, NullIfEmpty(value) ?? output);
            }

            case "privacy.settings":
                if (value == "clear") model.ShowClearBrowsingDataDialog();
                else model.ShowPrivacySettings();
                return AutomationRegistry.Ok();

            case "privacy.cosmeticScript":
            {
                await blocker.CurrentBuild;
                var script = blocker.Filter.CosmeticScript(value, value, isMainFrame: true);
                return Reply(new JsonObject { ["script"] = script, ["length"] = script?.Length ?? 0 }, output);
            }

            case "privacy.rewrite":
                return Reply(new JsonObject { ["url"] = privacy.Rewrite(value) ?? value }, output);

            default:
                return AutomationRegistry.Error($"Unknown privacy command \"{command}\".");
        }
    }

    /// <summary>Applies one <c>privacy.set</c> assignment; returns a problem, or null.</summary>
    private static string? Apply(BrowserModel model, string assignment)
    {
        var parts = assignment.Split('=', 2);
        if (parts.Length != 2) return $"Expected key=value, got \"{assignment}\".";
        var (key, value) = (parts[0], parts[1]);
        var on = value is "on" or "true";
        var privacy = model.Privacy;
        switch (key)
        {
            case "httpsOnly": privacy.Update(settings => settings with { HttpsOnly = on }); break;
            case "strip": privacy.Update(settings => settings with { StripTrackingParameters = on }); break;
            case "thirdPartyCookies": privacy.Update(settings => settings with { BlockThirdPartyCookies = on }); break;
            case "clearOnExit": privacy.Update(settings => settings with { ClearCookiesOnExit = on }); break;
            case "httpException":
                privacy.Update(settings => settings.WithHttpException(!value.StartsWith('-'), value.TrimStart('-')));
                break;
            case "cosmetic": model.Blocker.SetCosmeticEnabled(on); break;
            case "doh":
            {
                var pieces = value.Split(':', 3);
                if (DnsModes.FromId(pieces[0]) is not { } mode) return $"Unknown DNS mode \"{pieces[0]}\".";
                privacy.Update(settings => settings with
                {
                    DnsOverHttps = settings.DnsOverHttps with
                    {
                        Mode = mode,
                        ProviderId = pieces.Length > 1 ? pieces[1] : settings.DnsOverHttps.ProviderId,
                        CustomTemplate = pieces.Length > 2 ? pieces[2] : settings.DnsOverHttps.CustomTemplate,
                    },
                });
                break;
            }
            case "list":
            {
                var pieces = value.Split(':');
                if (FilterListCatalog.All.FirstOrDefault(list => list.Id == pieces[0]) is not { } list) return $"Unknown filter list \"{pieces[0]}\".";
                model.Blocker.SetList(list, pieces[^1] == "on");
                break;
            }
            default:
                return $"Unknown privacy setting \"{key}\".";
        }
        return null;
    }

    /// <summary>The <c>privacy</c> field of <c>dump</c> (macOS's <c>privacyDump</c>).</summary>
    public static JsonObject Dump(BrowserModel model)
    {
        var privacy = model.Privacy;
        var settings = privacy.Settings;
        var blocker = model.Blocker;
        var stats = blocker.CosmeticStats;
        return new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["httpsOnly"] = settings.HttpsOnly,
                ["httpsExceptions"] = Strings(settings.HttpsExceptions),
                ["sessionHTTPExceptions"] = Strings(privacy.SessionHttpExceptions.Order(StringComparer.Ordinal)),
                ["stripTrackingParameters"] = settings.StripTrackingParameters,
                ["blockThirdPartyCookies"] = settings.BlockThirdPartyCookies,
                ["clearCookiesOnExit"] = settings.ClearCookiesOnExit,
                ["dohMode"] = settings.DnsOverHttps.Mode.Id(),
                ["dohTemplate"] = settings.DnsOverHttps.Template ?? "",
            },
            ["tabs"] = new JsonArray([.. model.Tabs.Select(tab => (JsonNode)new JsonObject
            {
                ["isPrivate"] = tab.IsPrivate, ["security"] = ConnectionSecurities.Of(tab.Url).Id(), ["url"] = tab.Url,
            })]),
            ["privateBrowsers"] = privacy.Chromium?.PrivateBrowserCount ?? 0,
            ["hasPrivateProfile"] = privacy.Chromium?.HasPrivateProfile ?? false,
            ["chromiumNeedsRestart"] = privacy.ChromiumNeedsRestart,
            ["blocker"] = new JsonObject
            {
                ["cosmeticEnabled"] = blocker.CosmeticEnabled,
                ["cosmeticGeneric"] = stats.Generic,
                ["cosmeticSpecific"] = stats.Specific,
                ["cosmeticUnsupported"] = stats.Unsupported,
                ["lists"] = Strings(blocker.EnabledListIds.Order(StringComparer.Ordinal)),
                ["missingLists"] = Strings(blocker.Store.MissingLists().Select(list => list.Id)),
                ["userFilterLines"] = blocker.UserFilters.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length,
                ["lastUpdate"] = blocker.LastUpdateMessage ?? "",
            },
            ["lastClear"] = privacy.LastClearSummary ?? "",
            ["pendingGeckoClear"] = privacy.HasPendingGeckoClear,
            ["showClearBrowsingData"] = model.ShowClearBrowsingData,
        };
    }

    private static JsonArray SiteDataJson(PrivacyController privacy) => new([.. privacy.SiteData.Select(entry => (JsonNode)new JsonObject
    {
        ["site"] = entry.Site,
        ["hosts"] = Strings(entry.Hosts),
        ["chromiumCookies"] = entry.ChromiumCookies,
        ["geckoCookies"] = entry.GeckoCookies,
        ["geckoStorageBytes"] = entry.GeckoStorageBytes,
    })]);

    private static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(item => (JsonNode)JsonValue.Create(item))]);

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>Writes <paramref name="result"/> to <paramref name="path"/> (when given) and replies with it.</summary>
    private static AutomationReply Reply(JsonNode result, string? path)
    {
        if (path is not null) File.WriteAllText(path, result.ToJsonString(Indented));
        return new AutomationReply(new JsonObject { ["ok"] = true, ["result"] = result.DeepClone() }.ToJsonString());
    }
}
