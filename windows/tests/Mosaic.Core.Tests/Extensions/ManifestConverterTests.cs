using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Extensions;

namespace Mosaic.Core.Tests.Extensions;

/// <summary>Port of <c>Tests/MosaicCoreTests/ManifestConverterTests.swift</c>, plus cases for the JSON model used on Windows.</summary>
public class ManifestConverterTests
{
    private const string Id = "eimadpbcbfnmbkopoojfekhnkhdbieeh";

    private static ManifestConversion Convert(string manifest, Dictionary<string, string>? files = null) =>
        ManifestConverter.Convert(Parse(manifest), Id, path => files?.GetValueOrDefault(path));

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static string? GeckoId(JsonObject manifest) => (string?)manifest["browser_specific_settings"]?["gecko"]?["id"];

    private static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(element => (string)element!)];

    [Fact]
    public void AssignsAStableGeckoIdAndDropsChromeBookkeeping()
    {
        var result = Convert("""
            {
              "manifest_version": 3, "name": "X", "version": "1.0",
              "update_url": "https://clients2.google.com/service/update2/crx",
              "key": "abc", "minimum_chrome_version": "120"
            }
            """);
        Assert.Equal($"{Id}@crx.mosaic", GeckoId(result.Manifest));
        Assert.False(result.Manifest.ContainsKey("update_url"));
        Assert.False(result.Manifest.ContainsKey("key"));
        Assert.False(result.Manifest.ContainsKey("minimum_chrome_version"));
        Assert.Equal("X", (string?)result.Manifest["name"]);
    }

    [Fact]
    public void KeepsAnExistingGeckoId()
    {
        var result = Convert("""
            {
              "manifest_version": 2,
              "browser_specific_settings": { "gecko": { "id": "real@example.org", "strict_min_version": "109.0" } }
            }
            """);
        Assert.Equal("real@example.org", GeckoId(result.Manifest));
    }

    [Fact]
    public void ServiceWorkerBecomesEventPageWithShimFirst()
    {
        var result = Convert("""{ "manifest_version": 3, "background": { "service_worker": "bg/sw.js" } }""");
        var background = result.Manifest["background"]!.AsObject();
        Assert.Equal([ManifestConverter.ShimFileName, "bg/sw.js"], Strings(background["scripts"]));
        Assert.False(background.ContainsKey("service_worker"));
        Assert.Contains(result.Notes, note => note.Severity == NoteSeverity.Info && note.Message.Contains("event page"));
    }

    [Fact]
    public void ImportScriptsAreHoistedAheadOfTheWorker()
    {
        const string Worker = """
            importScripts("lib/a.js", 'lib/b.js');
            try { importScripts(`../shared/c.js`) } catch (e) {}
            importScripts("/root.js", "https://cdn.example/x.js", "lib/a.js");
            self.addEventListener("install", () => {});
            """;
        var result = Convert(
            """{ "manifest_version": 3, "background": { "service_worker": "bg/sw.js" } }""",
            new() { ["bg/sw.js"] = Worker });
        Assert.Equal(
            [ManifestConverter.ShimFileName, "bg/lib/a.js", "bg/lib/b.js", "shared/c.js", "root.js", "bg/sw.js"],
            Strings(result.Manifest["background"]!["scripts"]));
    }

    [Fact]
    public void ImportScriptsCannotEscapeTheExtension()
    {
        var found = ManifestConverter.ImportedScripts(
            """importScripts("../../outside.js", "a/../../../outside.js", "ok.js", "./x/../y.js")""",
            "bg/sw.js");
        Assert.Equal(["bg/ok.js", "bg/y.js"], found);
    }

    [Fact]
    public void NormalizesPaths()
    {
        Assert.Equal("a/c.js", ManifestConverter.Normalize("a/./b/../c.js"));
        Assert.Equal("root.js", ManifestConverter.Normalize("/root.js"));
        Assert.Null(ManifestConverter.Normalize("a/../.."));
        Assert.Null(ManifestConverter.Normalize("../x.js"));
        Assert.Null(ManifestConverter.Normalize(""));
    }

    [Fact]
    public void ModuleWorkersAreNotScannedForImportScripts()
    {
        var result = Convert(
            """{ "manifest_version": 3, "background": { "service_worker": "sw.js", "type": "module" } }""",
            new() { ["sw.js"] = """importScripts("a.js")""" });
        var background = result.Manifest["background"]!.AsObject();
        Assert.Equal([ManifestConverter.ShimFileName, "sw.js"], Strings(background["scripts"]));
        Assert.Equal("module", (string?)background["type"]);
    }

    [Fact]
    public void ExistingBackgroundScriptsGetTheShimOnce()
    {
        var first = Convert("""{ "manifest_version": 2, "background": { "scripts": ["a.js"], "persistent": true } }""");
        var background = first.Manifest["background"]!.AsObject();
        Assert.Equal([ManifestConverter.ShimFileName, "a.js"], Strings(background["scripts"]));
        Assert.True((bool?)background["persistent"]);

        // Converting an already converted manifest must not stack shims.
        var second = ManifestConverter.Convert(first.Manifest, Id);
        Assert.Equal([ManifestConverter.ShimFileName, "a.js"], Strings(second.Manifest["background"]!["scripts"]));
    }

    [Fact]
    public void RewritesKeysGeckoSpellsDifferently()
    {
        var result = Convert("""
            {
              "manifest_version": 3,
              "options_page": "options.html",
              "incognito": "split",
              "author": { "email": "dev@example.org" },
              "side_panel": { "default_path": "panel.html" }
            }
            """);
        var options = result.Manifest["options_ui"]!.AsObject();
        Assert.Equal("options.html", (string?)options["page"]);
        Assert.True((bool?)options["open_in_tab"]);
        Assert.False(result.Manifest.ContainsKey("options_page"));
        Assert.Equal("spanning", (string?)result.Manifest["incognito"]);
        Assert.Equal("dev@example.org", (string?)result.Manifest["author"]);
        Assert.False(result.Manifest.ContainsKey("side_panel"));
        Assert.Contains(result.Notes, note => note.Severity == NoteSeverity.Warning && note.Message.Contains("side panel"));
    }

    [Fact]
    public void ChromeOnlyPermissionsAreRemovedAndReported()
    {
        var result = Convert("""
            {
              "manifest_version": 3,
              "permissions": ["storage", "debugger", "sidePanel", "tabs", "system.cpu", "identity"],
              "optional_permissions": ["offscreen", "bookmarks"],
              "host_permissions": ["<all_urls>"]
            }
            """);
        Assert.Equal(["storage", "tabs", "identity"], Strings(result.Manifest["permissions"]));
        Assert.Equal(["bookmarks"], Strings(result.Manifest["optional_permissions"]));
        Assert.Equal(["<all_urls>"], Strings(result.Manifest["host_permissions"]));
        Assert.Contains(result.Notes, note => note.Severity == NoteSeverity.Blocker && note.Message.Contains("debugger"));
        Assert.Contains(result.Notes, note => note.Severity == NoteSeverity.Blocker && note.Message.Contains("offscreen"));
        Assert.Contains(result.Notes, note => note.Severity == NoteSeverity.Warning && note.Message.Contains("system.cpu"));
        Assert.Contains(result.Notes, note => note.Severity == NoteSeverity.Warning && note.Message.Contains("identity"));
    }

    [Fact]
    public void FlagsThingsThatAreNotExtensions()
    {
        Assert.Contains(Convert("""{ "app": { "launch": {} } }""").Notes, note => note.Severity == NoteSeverity.Blocker);
        Assert.Contains(Convert("""{ "theme": { "colors": {} } }""").Notes, note => note.Severity == NoteSeverity.Blocker);
    }

    [Fact]
    public void APlainCompatibleManifestProducesNoNotes()
    {
        var result = Convert("""
            {
              "manifest_version": 2, "name": "Plain", "version": "1.0",
              "permissions": ["storage", "tabs", "https://example.com/*"],
              "content_scripts": [{ "matches": ["<all_urls>"], "js": ["cs.js"] }],
              "browser_action": { "default_popup": "popup.html" }
            }
            """);
        Assert.Empty(result.Notes);
        Assert.True(result.Manifest.ContainsKey("browser_action"));
    }

    [Fact]
    public void ScriptScanFindsChromeOnlyApis()
    {
        var notes = ManifestConverter.ApiNotes("chrome.sidePanel.open({}); chrome.storage.local.get()");
        Assert.Single(notes);
        Assert.Contains("sidePanel", notes[0].Message);
        Assert.Empty(ManifestConverter.ApiNotes("chrome.tabs.query({})"));
    }

    [Fact]
    public void ShimIsSelfContainedScript()
    {
        var shim = ManifestConverter.ShimSource;
        Assert.Contains("importScripts", shim);
        Assert.Contains("sidePanel", shim);
        // A stray Swift interpolation would show up as a literal backslash-paren.
        Assert.DoesNotContain("\\(", shim);
    }

    // ---- Beyond the Swift tests ----

    [Fact]
    public void ShimIsTheMacOSScriptCharacterForCharacter()
    {
        // Length and digest of ManifestConverter.shimSource in the Swift implementation.
        var shim = ManifestConverter.ShimSource;
        Assert.Equal(2433, shim.Length);
        Assert.Equal(
            "e9737dc24beed866d4e77fb0141c5a15b0b6942e2250dcce7df8cecfe410270e",
            System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shim))));
        Assert.DoesNotContain('\r', shim);
        Assert.StartsWith("// Generated by Mosaic. Chrome → Gecko compatibility shim.\n(() => {\n  \"use strict\";", shim);
        Assert.EndsWith("  }\n})();", shim);
    }

    [Fact]
    public void DoesNotModifyItsInput()
    {
        var manifest = Parse("""
            {
              "manifest_version": 3, "key": "abc", "update_url": "u",
              "applications": { "gecko": { "update_url": "x" } },
              "background": { "service_worker": "sw.js", "persistent": false },
              "options_page": "o.html", "incognito": "split", "author": { "email": "e" },
              "browser_action": {}, "permissions": ["debugger", "storage"], "side_panel": {}
            }
            """);
        var before = manifest.ToJsonString();

        var result = ManifestConverter.Convert(manifest, Id);

        Assert.Equal(before, manifest.ToJsonString());
        Assert.NotSame(manifest, result.Manifest);
        // The two trees share no nodes: changing the result leaves the input alone.
        result.Manifest["background"]!["scripts"]!.AsArray().Add("later.js");
        result.Manifest["permissions"]!.AsArray().Clear();
        Assert.Equal(before, manifest.ToJsonString());
    }

    [Fact]
    public void KeepsTheOrderOfKeys()
    {
        var result = Convert("""
            {
              "name": "X", "manifest_version": 3, "version": "1", "key": "k",
              "background": { "type": "module", "service_worker": "sw.js" },
              "permissions": ["storage"], "action": {}, "description": "d"
            }
            """);
        Assert.Equal(
            ["name", "manifest_version", "version", "background", "permissions", "action", "description", "browser_specific_settings"],
            result.Manifest.Select(property => property.Key));
        Assert.Equal(["type", "scripts"], result.Manifest["background"]!.AsObject().Select(property => property.Key));
    }

    [Fact]
    public void WorksOnManifestsBuiltInCodeAsWellAsParsedOnes()
    {
        var manifest = new JsonObject
        {
            ["manifest_version"] = 3,
            ["browser_action"] = new JsonObject { ["default_popup"] = "popup.html" },
            ["background"] = new JsonObject { ["scripts"] = new JsonArray("a.js"), ["persistent"] = false },
        };
        var result = ManifestConverter.Convert(manifest, Id);
        Assert.True(result.Manifest.ContainsKey("action"));           // MV3: browser_action is spelled action
        Assert.False(result.Manifest.ContainsKey("browser_action"));
        Assert.False(result.Manifest["background"]!.AsObject().ContainsKey("persistent"));
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("3.0", true)]
    [InlineData("2", false)]
    [InlineData("\"3\"", false)]
    [InlineData("2.5", false)]
    [InlineData("null", false)]
    public void ManifestVersionDecidesWhetherBrowserActionIsRenamed(string version, bool renamed)
    {
        var result = Convert($$"""{ "manifest_version": {{version}}, "browser_action": {} }""");
        Assert.Equal(renamed, result.Manifest.ContainsKey("action"));
        Assert.Equal(!renamed, result.Manifest.ContainsKey("browser_action"));
    }

    [Fact]
    public void LegacyApplicationsKeyBecomesBrowserSpecificSettings()
    {
        var result = Convert("""{ "applications": { "gecko": { "id": "old@example.org", "update_url": "https://u" } } }""");
        Assert.False(result.Manifest.ContainsKey("applications"));
        Assert.Equal("old@example.org", GeckoId(result.Manifest));
        Assert.False(result.Manifest["browser_specific_settings"]!["gecko"]!.AsObject().ContainsKey("update_url"));
    }

    [Theory]
    [InlineData("""{ "browser_specific_settings": "nonsense" }""")]
    [InlineData("""{ "browser_specific_settings": { "gecko": 7 } }""")]
    [InlineData("""{ "browser_specific_settings": { "gecko": { "id": "" } } }""")]
    [InlineData("""{ "browser_specific_settings": { "gecko": { "id": 12 } } }""")]
    [InlineData("""{ "browser_specific_settings": null, "applications": [] }""")]
    public void AMalformedGeckoSectionIsReplacedWithTheDerivedId(string manifest) =>
        Assert.Equal($"{Id}@crx.mosaic", GeckoId(Convert(manifest).Manifest));

    [Fact]
    public void ServiceWorkerNextToExistingScriptsKeepsThoseScripts()
    {
        var result = Convert(
            """{ "manifest_version": 3, "background": { "service_worker": "sw.js", "scripts": ["__mosaic_chrome_shim.js", "bg.js"], "persistent": false } }""",
            new() { ["sw.js"] = """importScripts("ignored.js")""" });
        var background = result.Manifest["background"]!.AsObject();
        Assert.Equal([ManifestConverter.ShimFileName, "bg.js"], Strings(background["scripts"]));
        Assert.False(background.ContainsKey("service_worker"));
        Assert.False(background.ContainsKey("persistent"));
    }

    [Fact]
    public void BackgroundPagesAndOddScriptListsAreLeftAlone()
    {
        const string Page = """{"manifest_version":2,"background":{"page":"bg.html","persistent":true}}""";
        Assert.Equal(Parse(Page)["background"]!.ToJsonString(), Convert(Page).Manifest["background"]!.ToJsonString());

        const string Mixed = """{"manifest_version":2,"background":{"scripts":["a.js",1]}}""";
        Assert.Equal(Parse(Mixed)["background"]!.ToJsonString(), Convert(Mixed).Manifest["background"]!.ToJsonString());
    }

    [Fact]
    public void OtherKeyRewritesFollowTheSameRulesAsOnMacOS()
    {
        var result = Convert("""
            {
              "options_page": "legacy.html", "options_ui": { "page": "real.html" },
              "author": "Jo", "incognito": "not_allowed",
              "permissions": ["nativeMessaging", 7, "enterprise.deviceAttributes", "background", "debugger", "debugger"],
              "externally_connectable": {}, "sandbox": {}, "oauth2": {}, "chrome_settings_overrides": {},
              "tts_engine": {}, "input_components": []
            }
            """);
        var manifest = result.Manifest;
        Assert.Equal("real.html", (string?)manifest["options_ui"]!["page"]);
        Assert.False(manifest.ContainsKey("options_page"));
        Assert.Equal("Jo", (string?)manifest["author"]);
        Assert.Equal("not_allowed", (string?)manifest["incognito"]);
        Assert.Equal("""["nativeMessaging",7]""", manifest["permissions"]!.ToJsonString());
        Assert.True(manifest.ContainsKey("externally_connectable"));
        Assert.True(manifest.ContainsKey("tts_engine"));

        Assert.Equal(
        [
            new(NoteSeverity.Warning, "Permission “nativeMessaging”: needs the companion desktop app to be registered for Firefox as well as Chrome."),
            new(NoteSeverity.Warning, "Permission “enterprise.deviceAttributes”: this Chrome-only API does not exist on Gecko."),
            new(NoteSeverity.Info, "Permission “background”: Chrome's run-in-background mode does not apply."),
            new(NoteSeverity.Blocker, "Permission “debugger”: the Chrome DevTools debugger API does not exist on Gecko."),
            new(NoteSeverity.Warning, "Web pages cannot message this extension on Gecko (externally_connectable is not supported)."),
            new(NoteSeverity.Warning, "Sandboxed extension pages are not supported on Gecko."),
            new(NoteSeverity.Warning, "Google sign-in through chrome.identity.getAuthToken is Chrome-only; sign-in may not work."),
            new(NoteSeverity.Info, "Search/homepage overrides are ignored inside Mosaic."),
            new(NoteSeverity.Warning, "“input_components” is a ChromeOS/Chrome-only feature and will be ignored."),
            new(NoteSeverity.Warning, "“tts_engine” is a ChromeOS/Chrome-only feature and will be ignored."),
        ],
        result.Notes);
    }

    [Fact]
    public void AnAuthorThatIsNeitherTextNorAnEmailIsDropped()
    {
        Assert.False(Convert("""{ "author": { "name": "Jo" } }""").Manifest.ContainsKey("author"));
        Assert.False(Convert("""{ "author": null }""").Manifest.ContainsKey("author"));
    }

    [Fact]
    public void ImportScriptsUnderstandsWhatJavaScriptAllowsAroundTheCall()
    {
        Assert.Equal(["a.js", "b.js"], ManifestConverter.ImportedScripts("importScripts\n\t (\n  'a.js',\n  \"b.js\"\n);", "sw.js"));
        Assert.Equal(["a.js"], ManifestConverter.ImportedScripts("self.importScripts('a.js'); importScripts(`${base}/x.js`, 'chrome-extension://id/y.js')", "/sw.js"));
        Assert.Empty(ManifestConverter.ImportedScripts("// no imports here", "sw.js"));
        Assert.Equal(["deep/lib.js"], ManifestConverter.ImportedScripts("importScripts('lib.js')", "deep/nested/../sw.js"));
    }

    [Fact]
    public void ApiNotesComeInAFixedOrder()
    {
        var notes = ManifestConverter.ApiNotes("chrome.identity.getAuthToken(); chrome.debugger.attach(); chrome.offscreen.createDocument()");
        Assert.Equal(
            [NoteSeverity.Blocker, NoteSeverity.Blocker, NoteSeverity.Warning],
            notes.Select(note => note.Severity));
        Assert.Contains("chrome.offscreen", notes[0].Message);
    }
}
