using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mosaic.Core.Extensions;

/// <summary>How much a <see cref="CompatibilityNote"/> matters.</summary>
public enum NoteSeverity
{
    /// <summary>Adapted automatically; nothing to do.</summary>
    Info,
    /// <summary>A feature of the extension will be missing or behave differently.</summary>
    Warning,
    /// <summary>The extension depends on something Gecko does not have; expect breakage.</summary>
    Blocker,
}

/// <summary>Something the user should know about how a Chrome extension will behave on Gecko.</summary>
public sealed record CompatibilityNote(NoteSeverity Severity, string Message);

/// <summary>The outcome of <see cref="ManifestConverter.Convert"/>.</summary>
/// <param name="Manifest">The manifest Gecko should load.</param>
/// <param name="Notes">What was adapted or will not work, without duplicates, in the order found.</param>
public sealed record ManifestConversion(JsonObject Manifest, IReadOnlyList<CompatibilityNote> Notes);

/// <summary>Rewrites a Chrome <c>manifest.json</c> into one Gecko will load.</summary>
/// <remarks>
/// <para>
/// Gecko and Chromium share the WebExtensions API surface, so most of a manifest passes through
/// untouched. This converter handles the places where they differ: the add-on ID, MV3 service
/// workers (Gecko runs them as event pages), a few keys with different spellings, and
/// Chrome-only capabilities, which are reported as <see cref="CompatibilityNote"/>s rather than
/// silently dropped.
/// </para>
/// <para>
/// Port of <c>Sources/MosaicCore/Extensions/ManifestConverter.swift</c>, including the shim
/// script, verbatim. Behaviour is pinned down by <c>ManifestConverterTests.swift</c>.
/// </para>
/// </remarks>
public static partial class ManifestConverter
{
    /// <summary>File name of the compatibility shim injected ahead of background scripts.</summary>
    public const string ShimFileName = "__mosaic_chrome_shim.js";

    /// <summary>Gecko add-on ID used for a converted Chrome extension: <c>&lt;id&gt;@crx.mosaic</c>.</summary>
    public static string GeckoIdFor(string chromeId) => $"{chromeId}@crx.mosaic";

    /// <summary>Converts a Chrome manifest for Gecko.</summary>
    /// <param name="manifest">The parsed Chrome manifest. Not modified.</param>
    /// <param name="chromeId">The extension's Chrome ID.</param>
    /// <param name="readFile">Returns the text of a file inside the extension (path relative to its
    /// root, forward slashes), or null; used to resolve <c>importScripts()</c> in service workers.</param>
    /// <returns>A converted copy, keeping the manifest's key order, and the notes for the user.</returns>
    public static ManifestConversion Convert(JsonObject manifest, string chromeId, Func<string, string?>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(chromeId);

        var output = (JsonObject)manifest.DeepClone();
        var notes = new List<CompatibilityNote>();
        var version = IntegerValue(manifest["manifest_version"]) ?? 2;

        // --- Things that are not extensions Gecko can host at all ----------------
        if (manifest.ContainsKey("app"))
        {
            notes.Add(new(NoteSeverity.Blocker, "This is a Chrome App, not an extension. Chrome Apps cannot run on Gecko."));
        }
        if (manifest.ContainsKey("theme"))
        {
            notes.Add(new(NoteSeverity.Blocker, "This is a Chrome theme. Chrome and Gecko themes use different formats."));
        }
        if (manifest.ContainsKey("nacl_modules") || manifest.ContainsKey("plugins"))
        {
            notes.Add(new(NoteSeverity.Blocker, "Uses native plug-in code (NaCl/NPAPI), which Gecko does not support."));
        }

        // --- Identity ---------------------------------------------------------
        var settings = output["browser_specific_settings"] as JsonObject ?? output["applications"] as JsonObject ?? new JsonObject();
        var gecko = settings["gecko"] as JsonObject ?? new JsonObject();
        if (StringValue(gecko["id"]) is not { Length: > 0 })
        {
            gecko["id"] = GeckoIdFor(chromeId);
        }
        // Chrome extensions never declare a Gecko version floor that means anything here.
        gecko.Remove("update_url");
        Attach(settings, "gecko", gecko);
        output.Remove("applications");
        Attach(output, "browser_specific_settings", settings);

        // --- Chrome-only bookkeeping keys ---------------------------------------
        foreach (var key in ChromeBookkeepingKeys)
        {
            output.Remove(key);
        }

        // --- Background ---------------------------------------------------------
        if (output["background"] is JsonObject background)
        {
            if (StringValue(background["service_worker"]) is { } worker)
            {
                var scripts = StringArray(background["scripts"]) ?? [];
                if (scripts.Count == 0)
                {
                    var isModule = StringValue(background["type"]) == "module";
                    if (!isModule && readFile?.Invoke(worker) is { } source)
                    {
                        scripts.AddRange(ImportedScripts(source, worker));
                    }
                    scripts.Add(worker);
                }
                background["scripts"] = WithShimFirst(scripts);
                background.Remove("service_worker");
                background.Remove("persistent");
                notes.Add(new(NoteSeverity.Info, "Background service worker runs as a Gecko event page."));
            }
            else if (StringArray(background["scripts"]) is { } scripts)
            {
                background["scripts"] = WithShimFirst(scripts);
                if (version >= 3) background.Remove("persistent");
            }
        }

        // --- Keys with a different spelling -------------------------------------
        if (StringValue(output["options_page"]) is { } optionsPage)
        {
            if (!output.ContainsKey("options_ui"))
            {
                output["options_ui"] = new JsonObject { ["page"] = optionsPage, ["open_in_tab"] = true };
            }
            output.Remove("options_page");
        }
        if (StringValue(output["incognito"]) == "split")
        {
            output["incognito"] = "spanning";
            notes.Add(new(NoteSeverity.Warning, "Split incognito mode is not available on Gecko; private windows share the extension's normal context."));
        }
        if (output.TryGetPropertyValue("author", out var author) && StringValue(author) is null)
        {
            if (StringValue((author as JsonObject)?["email"]) is { } email)
            {
                output["author"] = email;
            }
            else
            {
                output.Remove("author");
            }
        }
        if (version >= 3 && output.TryGetPropertyValue("browser_action", out var browserAction) && !output.ContainsKey("action"))
        {
            output.Remove("browser_action");
            output["action"] = browserAction;
        }

        // --- Capabilities Gecko lacks -------------------------------------------
        foreach (var key in PermissionListKeys)
        {
            if (output[key] is not JsonArray listed) continue;
            for (var index = 0; index < listed.Count; index++)
            {
                if (StringValue(listed[index]) is not { } name) continue;
                if (UnsupportedPermission(name) is { } reason)
                {
                    notes.Add(new(reason.Severity, $"Permission “{name}”: {reason.Message}"));
                    listed.RemoveAt(index--);
                }
                else if (CaveatPermissions.TryGetValue(name, out var caveat))
                {
                    notes.Add(new(NoteSeverity.Warning, $"Permission “{name}”: {caveat}"));
                }
            }
        }

        if (output.Remove("side_panel"))
        {
            notes.Add(new(NoteSeverity.Warning, "Chrome side panel is not available on Gecko; the panel UI will not appear."));
        }
        if (output.ContainsKey("externally_connectable"))
        {
            notes.Add(new(NoteSeverity.Warning, "Web pages cannot message this extension on Gecko (externally_connectable is not supported)."));
        }
        if (output.ContainsKey("sandbox"))
        {
            notes.Add(new(NoteSeverity.Warning, "Sandboxed extension pages are not supported on Gecko."));
        }
        if (output.ContainsKey("oauth2"))
        {
            notes.Add(new(NoteSeverity.Warning, "Google sign-in through chrome.identity.getAuthToken is Chrome-only; sign-in may not work."));
        }
        if (output.ContainsKey("chrome_settings_overrides"))
        {
            notes.Add(new(NoteSeverity.Info, "Search/homepage overrides are ignored inside Mosaic."));
        }
        foreach (var key in ChromeOsOnlyKeys)
        {
            if (output.ContainsKey(key))
            {
                notes.Add(new(NoteSeverity.Warning, $"“{key}” is a ChromeOS/Chrome-only feature and will be ignored."));
            }
        }

        return new ManifestConversion(output, WithoutDuplicates(notes));
    }

    /// <summary>Keys that only mean something to Chrome and its Web Store.</summary>
    private static readonly string[] ChromeBookkeepingKeys = ["update_url", "key", "minimum_chrome_version", "differential_fingerprint"];

    /// <summary>The manifest keys that list API permissions.</summary>
    private static readonly string[] PermissionListKeys = ["permissions", "optional_permissions"];

    /// <summary>Features of ChromeOS that no other browser reads.</summary>
    private static readonly string[] ChromeOsOnlyKeys = ["file_browser_handlers", "file_system_provider_capabilities", "input_components", "tts_engine"];

    /// <summary>Chrome APIs that have no Gecko counterpart, mapped to what the user should expect.</summary>
    private static readonly FrozenDictionary<string, (NoteSeverity Severity, string Message)> UnsupportedPermissions =
        new Dictionary<string, (NoteSeverity, string)>
        {
            ["debugger"] = (NoteSeverity.Blocker, "the Chrome DevTools debugger API does not exist on Gecko."),
            ["offscreen"] = (NoteSeverity.Blocker, "offscreen documents do not exist on Gecko."),
            ["sidePanel"] = (NoteSeverity.Warning, "the side panel API does not exist on Gecko."),
            ["tabCapture"] = (NoteSeverity.Blocker, "tab capture does not exist on Gecko."),
            ["desktopCapture"] = (NoteSeverity.Blocker, "desktop capture does not exist on Gecko."),
            ["pageCapture"] = (NoteSeverity.Warning, "saving pages as MHTML does not exist on Gecko."),
            ["declarativeContent"] = (NoteSeverity.Warning, "declarativeContent rules do not exist on Gecko."),
            ["background"] = (NoteSeverity.Info, "Chrome's run-in-background mode does not apply."),
            ["contentSettings"] = (NoteSeverity.Warning, "per-site content settings cannot be changed by extensions on Gecko."),
            ["fontSettings"] = (NoteSeverity.Warning, "font settings cannot be changed by extensions on Gecko."),
            ["gcm"] = (NoteSeverity.Warning, "Google Cloud Messaging is Chrome-only."),
            ["processes"] = (NoteSeverity.Warning, "the processes API is Chrome-only."),
            ["power"] = (NoteSeverity.Warning, "the power API is Chrome-only."),
            ["readingList"] = (NoteSeverity.Warning, "the reading list API is Chrome-only."),
            ["tts"] = (NoteSeverity.Warning, "text-to-speech is Chrome-only."),
            ["ttsEngine"] = (NoteSeverity.Warning, "text-to-speech engines are Chrome-only."),
            ["favicon"] = (NoteSeverity.Warning, "the favicon API is Chrome-only; site icons inside the extension may be missing."),
            ["printing"] = (NoteSeverity.Warning, "the printing API is ChromeOS-only."),
            ["printerProvider"] = (NoteSeverity.Warning, "the printer provider API is Chrome-only."),
            ["documentScan"] = (NoteSeverity.Warning, "document scanning is ChromeOS-only."),
            ["loginState"] = (NoteSeverity.Warning, "the loginState API is ChromeOS-only."),
            ["platformKeys"] = (NoteSeverity.Warning, "the platformKeys API is ChromeOS-only."),
            ["certificateProvider"] = (NoteSeverity.Warning, "the certificate provider API is ChromeOS-only."),
            ["fileSystemProvider"] = (NoteSeverity.Warning, "the file system provider API is ChromeOS-only."),
            ["vpnProvider"] = (NoteSeverity.Warning, "the VPN provider API is ChromeOS-only."),
            ["wallpaper"] = (NoteSeverity.Warning, "the wallpaper API is ChromeOS-only."),
            ["audio"] = (NoteSeverity.Warning, "the audio API is ChromeOS-only."),
            ["webAuthenticationProxy"] = (NoteSeverity.Warning, "the WebAuthn proxy API is Chrome-only."),
        }.ToFrozenDictionary();

    /// <summary>Whole API families that exist only in Chrome, recognised by their prefix.</summary>
    private static readonly string[] UnsupportedPermissionPrefixes = ["enterprise.", "system.", "accessibilityFeatures."];

    /// <summary>Permissions Gecko accepts but implements only partly.</summary>
    private static readonly FrozenDictionary<string, string> CaveatPermissions = new Dictionary<string, string>
    {
        ["identity"] = "only the launchWebAuthFlow sign-in flow works on Gecko.",
        ["nativeMessaging"] = "needs the companion desktop app to be registered for Firefox as well as Chrome.",
    }.ToFrozenDictionary();

    /// <summary>
    /// Chrome-only API namespaces to look for in an extension's scripts, since many are used
    /// without a matching manifest permission.
    /// </summary>
    private static readonly (string Needle, CompatibilityNote Note)[] ChromeOnlyApis =
    [
        ("chrome.sidePanel", new(NoteSeverity.Warning, "Uses chrome.sidePanel, which does not exist on Gecko.")),
        ("chrome.offscreen", new(NoteSeverity.Blocker, "Uses chrome.offscreen, which does not exist on Gecko.")),
        ("chrome.debugger", new(NoteSeverity.Blocker, "Uses chrome.debugger, which does not exist on Gecko.")),
        ("chrome.tabCapture", new(NoteSeverity.Blocker, "Uses chrome.tabCapture, which does not exist on Gecko.")),
        ("chrome.desktopCapture", new(NoteSeverity.Blocker, "Uses chrome.desktopCapture, which does not exist on Gecko.")),
        ("chrome.identity.getAuthToken", new(NoteSeverity.Warning, "Uses chrome.identity.getAuthToken (Google sign-in), which is Chrome-only.")),
        ("chrome.declarativeContent", new(NoteSeverity.Warning, "Uses chrome.declarativeContent, which does not exist on Gecko.")),
    ];

    /// <summary>Scans script text for Chrome-only APIs.</summary>
    /// <returns>One note per API family the script mentions.</returns>
    public static IReadOnlyList<CompatibilityNote> ApiNotes(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return [.. ChromeOnlyApis.Where(api => script.Contains(api.Needle, StringComparison.Ordinal)).Select(api => api.Note)];
    }

    /// <summary>Paths imported by <c>importScripts(...)</c> calls in a worker, relative to the extension root.</summary>
    /// <remarks>
    /// Gecko event pages have no <c>importScripts</c>, so these are loaded as ordinary background
    /// scripts ahead of the worker itself. Paths are returned once each, in call order; remote
    /// URLs, template strings with substitutions and paths that climb out of the extension are
    /// left out.
    /// </remarks>
    /// <param name="source">The worker script's text.</param>
    /// <param name="workerPath">The worker's own path, which relative imports are resolved against.</param>
    public static IReadOnlyList<string> ImportedScripts(string source, string workerPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(workerPath);

        // Everything but the worker's file name; Normalize() discards the empty pieces.
        var workerDirectory = string.Join('/', workerPath.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1));
        var result = new List<string>();
        foreach (Match call in ImportScriptsCall().Matches(source))
        {
            foreach (Match literal in StringLiteral().Matches(call.Groups[1].Value))
            {
                var path = literal.Groups[1].Value;
                if (path.Contains("://", StringComparison.Ordinal) || path.Contains("${", StringComparison.Ordinal)) continue;

                var joined = path.StartsWith('/') ? path : $"{workerDirectory}/{path}";
                if (Normalize(joined) is { } resolved && !result.Contains(resolved)) result.Add(resolved);
            }
        }
        return result;
    }

    /// <summary>Collapses <c>.</c> and <c>..</c>; null for a path that would leave the extension.</summary>
    /// <param name="path">A path relative to the extension root, with forward slashes.</param>
    public static string? Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case ".":
                    continue;
                case "..":
                    if (parts.Count == 0) return null;
                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    [GeneratedRegex(@"importScripts\s*\(([^)]*)\)")]
    private static partial Regex ImportScriptsCall();

    [GeneratedRegex("""["'`]([^"'`]+)["'`]""")]
    private static partial Regex StringLiteral();

    private static (NoteSeverity Severity, string Message)? UnsupportedPermission(string name)
    {
        if (UnsupportedPermissions.TryGetValue(name, out var reason)) return reason;
        return UnsupportedPermissionPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
            ? (NoteSeverity.Warning, "this Chrome-only API does not exist on Gecko.")
            : null;
    }

    /// <summary>The background script list with the shim in front, exactly once.</summary>
    private static JsonArray WithShimFirst(IEnumerable<string> scripts)
    {
        var array = new JsonArray(ShimFileName);
        foreach (var script in scripts.Where(script => script != ShimFileName)) array.Add(script);
        return array;
    }

    /// <summary>
    /// Puts <paramref name="child"/> under <paramref name="key"/> unless it is already there;
    /// a node cannot be assigned to the parent it already belongs to.
    /// </summary>
    private static void Attach(JsonObject parent, string key, JsonObject child)
    {
        if (!ReferenceEquals(parent[key], child)) parent[key] = child;
    }

    /// <summary>The notes in their original order, each one once.</summary>
    internal static List<CompatibilityNote> WithoutDuplicates(List<CompatibilityNote> notes)
    {
        var seen = new HashSet<CompatibilityNote>();
        return notes.FindAll(seen.Add);
    }

    // ---- Reading loosely typed manifest values ----

    /// <summary>The node's value if it is a JSON string, otherwise null.</summary>
    internal static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>The node's value if it is a JSON number with an integral value, otherwise null.</summary>
    internal static long? IntegerValue(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return null;

        // Going through the JSON text works whatever the node was created from (parsed, int, double…).
        var text = value.ToJsonString();
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) return integer;
        // Written as 3.0 or 3e0: still the integer 3.
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? (long)number
            : null;
    }

    /// <summary>The node's elements if it is an array of nothing but strings, otherwise null.</summary>
    private static List<string>? StringArray(JsonNode? node)
    {
        if (node is not JsonArray array) return null;

        var strings = new List<string>(array.Count);
        foreach (var element in array)
        {
            if (StringValue(element) is not { } text) return null;
            strings.Add(text);
        }
        return strings;
    }
}
