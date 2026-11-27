using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Extensions;

namespace Mosaic.Core.Tests.Extensions;

/// <summary>
/// Port of <c>ExtensionStoreTests</c> in <c>Tests/MosaicCoreTests/ExtensionStoreTests.swift</c>,
/// followed by tests for what is specific to Windows: the unconverted Chromium copy, unpacking
/// without an external tool, and the store's own index.
/// </summary>
public sealed class ExtensionStoreTests : IDisposable
{
    private const string ManifestV3 = """
        {
          // Chrome allows comments in manifests.
          "manifest_version": 3,
          "name": "__MSG_appName__",
          "description": "__MSG_appDesc__",
          "version": "2.5.1",
          "default_locale": "en",
          "icons": { "16": "icons/16.png", "128": "/icons/128.png", "48": "icons/48.png" },
          "action": { "default_popup": "popup.html" },
          "options_page": "options.html",
          "background": { "service_worker": "sw.js" },
          "permissions": ["storage", "offscreen"]
        }
        """;

    private static readonly (string Name, string Contents)[] Files =
    [
        ("manifest.json", ManifestV3),
        ("_locales/en/messages.json", """{"appName": {"message": "Test Extension"}, "appDesc": {"message": "Does things"}}"""),
        ("sw.js", """importScripts("lib.js"); chrome.sidePanel.setPanelBehavior({});"""),
        ("lib.js", "var lib = 1;"),
        ("popup.html", "<p>popup</p>"),
        ("options.html", "<p>options</p>"),
        ("icons/128.png", "png"),
        ("_metadata/verified_contents.json", "{}"),
    ];

    private const string ChromeId = "abcdefghijklmnopabcdefghijklmnop";

    private readonly TemporaryDirectory _root = new();
    private readonly ExtensionStore _store;

    public ExtensionStoreTests() => _store = new ExtensionStore(_root.Path);

    public void Dispose() => _root.Dispose();

    private static byte[] Crx(params (string Name, string Contents)[] files)
    {
        byte[] id = [.. Enumerable.Range(0, 16).Select(i => (byte)((i << 4) | ((i + 1) % 16)))];
        return CrxFixtures.Crx3(id, CrxFixtures.Zip(files.Length == 0 ? Files : files));
    }

    /// <summary>The standard files with some replaced or added.</summary>
    private static (string Name, string Contents)[] FilesWith(params (string Name, string Contents)[] changes) =>
        [.. Files.Where(file => changes.All(change => change.Name != file.Name)), .. changes];

    private static JsonObject ReadJson(string file) => JsonNode.Parse(File.ReadAllText(file))!.AsObject();

    private static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(element => (string)element!)];

    private string[] StagingLeftovers() =>
        [.. Directory.EnumerateFileSystemEntries(_root.Path).Select(Path.GetFileName).Where(name => name!.StartsWith('.'))!];

    [Fact]
    public void InstallsConvertsAndRecordsAnExtension()
    {
        var record = _store.Install(Crx(), ChromeId);

        Assert.Equal(ChromeId, record.Id);
        Assert.Equal($"{ChromeId}@crx.mosaic", record.GeckoId);
        Assert.Equal("Test Extension", record.Name);
        Assert.Equal("Does things", record.Summary);
        Assert.Equal("2.5.1", record.Version);
        Assert.Equal(3, record.ManifestVersion);
        Assert.True(record.Enabled);
        Assert.True(record.HasPopup);
        Assert.True(record.HasOptions);
        Assert.Equal("icons/128.png", record.IconPath);
        Assert.Equal(NoteSeverity.Blocker, record.WorstSeverity);
        Assert.Contains(record.Notes, note => note.Message.Contains("offscreen"));
        Assert.Contains(record.Notes, note => note.Message.Contains("chrome.sidePanel"));

        var gecko = _store.GeckoDirectoryFor(ChromeId);
        var manifest = ReadJson(Path.Combine(gecko, "manifest.json"));
        Assert.Equal([ManifestConverter.ShimFileName, "lib.js", "sw.js"], Strings(manifest["background"]!["scripts"]));
        Assert.Equal(["storage"], Strings(manifest["permissions"]));

        Assert.True(File.Exists(Path.Combine(gecko, ManifestConverter.ShimFileName)));
        Assert.True(File.Exists(Path.Combine(gecko, "popup.html")));
        Assert.False(Directory.Exists(Path.Combine(gecko, "_metadata")));
        Assert.True(File.Exists(_store.SourcePackageFor(ChromeId)));
        // Nothing left behind from unpacking.
        Assert.Empty(StagingLeftovers());
    }

    [Fact]
    public void IdComesFromThePackageWhenNotSupplied()
    {
        var record = _store.Install(Crx());
        Assert.True(CrxArchive.IsValidId(record.Id));
        Assert.Equal(CrxArchive.Parse(Crx()).ExtensionId, record.Id);
    }

    [Fact]
    public void LibraryPersistsAcrossInstances()
    {
        _store.Install(Crx(), ChromeId);
        _store.SetEnabled(false, ChromeId);

        var reopened = new ExtensionStore(_root.Path);
        var extension = Assert.Single(reopened.Extensions);
        Assert.Equal("Test Extension", extension.Name);
        Assert.False(extension.Enabled);
    }

    [Fact]
    public void ReinstallReplacesAndKeepsEnabledState()
    {
        _store.Install(Crx(), ChromeId);
        _store.SetEnabled(false, ChromeId);
        var again = _store.Install(Crx(), ChromeId);
        Assert.Single(_store.Extensions);
        Assert.False(again.Enabled);
    }

    [Fact]
    public void RemoveDeletesFilesAndRecord()
    {
        _store.Install(Crx(), ChromeId);
        _store.Remove(ChromeId);
        Assert.Empty(_store.Extensions);
        Assert.False(Directory.Exists(_store.DirectoryFor(ChromeId)));
        Assert.Empty(new ExtensionStore(_root.Path).Extensions);
    }

    [Fact]
    public void RejectsPackagesWithoutAManifest()
    {
        var error = Assert.Throws<ExtensionStoreException>(() => _store.Install(CrxFixtures.Zip(("readme.txt", "hello"))));
        Assert.Equal("The package has no manifest.json, so it is not an extension.", error.Message);
        Assert.Empty(_store.Extensions);
    }

    [Fact]
    public void RejectsFilesThatAreNotPackages()
    {
        var error = Assert.Throws<CrxParseException>(() => _store.Install(Encoding.UTF8.GetBytes("<!doctype html>")));
        Assert.Equal(CrxParseError.NotAnExtensionPackage, error.Error);
    }

    [Fact]
    public void ABrokenPackageDoesNotDisturbAnInstalledOne()
    {
        _store.Install(Crx(), ChromeId);
        var broken = CrxFixtures.Zip(("manifest.json", "{ this is not json"));
        Assert.Throws<ExtensionStoreException>(() => _store.Install(broken, ChromeId));
        Assert.Equal("Test Extension", _store.Extensions[0].Name);
        Assert.True(File.Exists(Path.Combine(_store.GeckoDirectoryFor(ChromeId), "manifest.json")));
    }

    [Fact]
    public void LocalisationFallsBackToTheRawValue()
    {
        var none = new Dictionary<string, string>();
        Assert.Equal("__MSG_missing__", ExtensionStore.Localized("__MSG_missing__", none));
        Assert.Equal("Hi", ExtensionStore.Localized("__MSG_AppName__", new Dictionary<string, string> { ["appname"] = "Hi" }));
        Assert.Equal("Plain", ExtensionStore.Localized("Plain", none));
        Assert.Null(ExtensionStore.Localized("", none));
    }

    // ---- The Chromium copy ----

    [Fact]
    public void KeepsAnUnconvertedCopyForChromium()
    {
        _store.Install(Crx(), ChromeId);

        var chromium = _store.ChromiumDirectoryFor(ChromeId);
        Assert.Equal(Path.Combine(_root.Path, ChromeId, "chromium"), chromium);
        // No public key in this package, so the manifest is the package's, byte for byte.
        Assert.Equal(ManifestV3, File.ReadAllText(Path.Combine(chromium, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(chromium, ManifestConverter.ShimFileName)));
        Assert.False(Directory.Exists(Path.Combine(chromium, "_metadata")));
        foreach (var (name, contents) in Files.Where(file => !file.Name.StartsWith("_metadata/")))
        {
            Assert.Equal(contents, File.ReadAllText(Path.Combine(chromium, name)));
        }
        // Converting the Gecko copy left the other files there alone, too.
        Assert.Equal("var lib = 1;", File.ReadAllText(Path.Combine(_store.GeckoDirectoryFor(ChromeId), "lib.js")));
        Assert.Equal(Crx(), File.ReadAllBytes(_store.SourcePackageFor(ChromeId)));
    }

    [Fact]
    public void ChromiumCopyGainsThePackageKeySoItKeepsItsId()
    {
        using var developer = RSA.Create(2048);
        var publicKey = developer.ExportSubjectPublicKeyInfo();
        byte[] header =
        [
            .. CrxFixtures.Field(2, CrxFixtures.Proof(publicKey, [1, 2, 3])),
            .. CrxFixtures.Field(10000, CrxFixtures.Field(1, SHA256.HashData(publicKey)[..16])),
        ];

        var record = _store.Install(CrxFixtures.Crx3WithHeader(header, CrxFixtures.Zip(Files)));

        var manifest = ReadJson(Path.Combine(_store.ChromiumDirectoryFor(record.Id), "manifest.json"));
        var key = (string?)manifest["key"];
        Assert.Equal(Convert.ToBase64String(publicKey), key);
        // The ID Chromium will derive from that key is the one the extension is filed under.
        Assert.Equal(record.Id, CrxArchive.ExtensionIdFromManifestKey(key!));
        // Otherwise the manifest is unconverted, in its original order with the key at the end.
        Assert.Equal("sw.js", (string?)manifest["background"]!["service_worker"]);
        Assert.Equal(["storage", "offscreen"], Strings(manifest["permissions"]));
        Assert.Equal("options.html", (string?)manifest["options_page"]);
        Assert.Equal(
            ["manifest_version", "name", "description", "version", "default_locale", "icons", "action", "options_page", "background", "permissions", "key"],
            manifest.Select(property => property.Key));
        // Gecko has no use for the key.
        Assert.False(ReadJson(Path.Combine(_store.GeckoDirectoryFor(record.Id), "manifest.json")).ContainsKey("key"));
    }

    [Fact]
    public void ChromiumCopyOfACrx2GetsItsKeyAsWell()
    {
        var key = Encoding.UTF8.GetBytes("not really a key");
        var record = _store.Install(CrxFixtures.Crx2(key, [1, 2, 3, 4], CrxFixtures.Zip(("manifest.json", """{"name":"Old","version":"1"}"""))));

        Assert.Equal(CrxArchive.ExtensionIdFromPublicKey(key), record.Id);
        var manifest = ReadJson(Path.Combine(_store.ChromiumDirectoryFor(record.Id), "manifest.json"));
        Assert.Equal(Convert.ToBase64String(key), (string?)manifest["key"]);
    }

    [Fact]
    public void AManifestThatAlreadyHasAKeyIsNotTouched()
    {
        using var developer = RSA.Create(2048);
        var publicKey = developer.ExportSubjectPublicKeyInfo();
        var ownKey = Convert.ToBase64String(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var manifestText = $$"""
            {
              "name": "Keyed", // with a comment that must survive
              "version": "1", "key": "{{ownKey}}",
            }
            """;
        var package = CrxFixtures.Crx2(publicKey, [0], CrxFixtures.Zip(("manifest.json", manifestText)));

        var record = _store.Install(package);

        Assert.Equal(manifestText, File.ReadAllText(Path.Combine(_store.ChromiumDirectoryFor(record.Id), "manifest.json")));
    }

    [Fact]
    public void AZippedExtensionTakesItsIdFromItsManifestKeyOrItsContents()
    {
        var key = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var keyed = CrxFixtures.Zip(("manifest.json", $$"""{"name":"Keyed","version":"1","key":"{{Convert.ToBase64String(key)}}"}"""));
        Assert.Equal(CrxArchive.ExtensionIdFromPublicKey(key), _store.Install(keyed).Id);

        var plain = CrxFixtures.Zip(("manifest.json", """{"name":"Plain","version":"1"}"""));
        var record = _store.Install(plain);
        Assert.Equal(CrxArchive.ExtensionIdFromPublicKey(plain), record.Id);
        Assert.True(CrxArchive.IsValidId(record.Id));
        // No key to give Chromium, so the manifest stays as it was.
        Assert.Equal("""{"name":"Plain","version":"1"}""", File.ReadAllText(Path.Combine(_store.ChromiumDirectoryFor(record.Id), "manifest.json")));
        Assert.Equal(2, _store.Extensions.Count);
    }

    // ---- Unpacking ----

    [Fact]
    public void EntriesThatWouldEscapeAreSkippedAndTheRestInstalls()
    {
        using var outer = new TemporaryDirectory();
        var store = new ExtensionStore(Path.Combine(outer.Path, "library"));
        var package = Crx(FilesWith(
            ("../escaped.txt", "x"),
            ("../../escaped.txt", "x"),
            ("../../../escaped.txt", "x"),
            ("..\\..\\escaped.txt", "x"),
            ("gecko/../../../escaped.txt", "x")));

        var record = store.Install(package, ChromeId);

        Assert.Equal("Test Extension", record.Name);
        Assert.Empty(Directory.EnumerateFiles(outer.Path, "escaped.txt", SearchOption.AllDirectories));
        Assert.Equal(["library"], Directory.EnumerateFileSystemEntries(outer.Path).Select(Path.GetFileName));
        Assert.Contains(record.Notes, note =>
            note.Severity == NoteSeverity.Warning && note.Message.StartsWith("5 files were left out") && note.Message.Contains("“../escaped.txt”"));
        Assert.True(File.Exists(Path.Combine(store.ChromiumDirectoryFor(ChromeId), "popup.html")));
    }

    [Fact]
    public void EntriesWindowsCannotStoreAreSkippedAndTheRestInstalls()
    {
        var package = Crx(FilesWith(
            ("NUL", "device"),
            ("lib/con.js", "device with an extension"),
            ("COM1/x.js", "device as a folder"),
            ("manifest.json.", "{ \"name\": \"Impostor\" }"),
            ("manifest.json ", "{ \"name\": \"Impostor\" }"),
            ("manifest.json::$DATA", "{ \"name\": \"Impostor\" }"),
            ("sw.js:hidden", "stream"),
            ("what?.js", "x"),
            ("a*b|c<d>e\".js", "x"),
            ("deep/" + string.Join('/', Enumerable.Repeat("0123456789abcdef", 16)) + "/far.js", "x"),
            (new string('n', 300) + ".js", "x"),
            ("fine/ordinary name (1).js", "kept")));

        var record = _store.Install(package, ChromeId);

        Assert.Equal("Test Extension", record.Name);
        Assert.Equal("2.5.1", record.Version);
        foreach (var directory in (string[])[_store.GeckoDirectoryFor(ChromeId), _store.ChromiumDirectoryFor(ChromeId)])
        {
            var names = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .Select(entry => Path.GetRelativePath(directory, entry).Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();
            Assert.Contains("fine/ordinary name (1).js", names);
            Assert.Contains("lib.js", names);
            Assert.DoesNotContain(names, name => name.Split('/').Any(part => !ExtensionPaths.IsStorableName(part)));
            Assert.DoesNotContain(names, name => name.Contains("NUL") || name.Contains("con.js") || name.Contains("COM1") || name.Contains("far.js"));
            Assert.All(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories), file => Assert.True(
                (_store.ChromiumDirectoryFor(ChromeId).Length + 1 + Path.GetRelativePath(directory, file).Length) <= ExtensionPaths.MaximumPathLength));
        }
        Assert.Contains(record.Notes, note =>
            note.Severity == NoteSeverity.Warning && note.Message.StartsWith("11 files were left out") && note.Message.Contains("“NUL”"));
    }

    [Fact]
    public void ASingleSkippedFileIsReportedInTheSingular()
    {
        var record = _store.Install(Crx(FilesWith(("aux.js", "x"))), ChromeId);
        Assert.Contains(
            new CompatibilityNote(NoteSeverity.Warning, "One file was left out because its name is unsafe, too long or not allowed on Windows: “aux.js”."),
            record.Notes);
    }

    [Fact]
    public void ALongOrMultiLineNameIsAbbreviatedInTheNote()
    {
        var record = _store.Install(Crx(FilesWith((new string('n', 300) + "\n.js", "x"))), ChromeId);
        var note = Assert.Single(record.Notes, note => note.Message.StartsWith("One file was left out"));
        Assert.EndsWith($"“{new string('n', 80)}…”.", note.Message);
    }

    [Fact]
    public void ALibraryTooDeepForWindowsSaysSoInsteadOfBlamingThePackage()
    {
        var deep = Path.Combine([_root.Path, .. Enumerable.Repeat("0123456789abcdefghij", 10)]);
        var store = new ExtensionStore(deep);

        var error = Assert.Throws<ExtensionStoreException>(() => store.Install(Crx(), ChromeId));

        Assert.Contains("path too long for Windows", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(deep));
    }

    [Fact]
    public void PaddingBeforeTheArchiveIsTolerated()
    {
        // unzip reports "extra bytes at beginning or within zipfile" for this and carries on.
        byte[] id = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        var package = CrxFixtures.Crx3(id, [.. new byte[6], .. CrxFixtures.Zip(Files), .. new byte[4]]);

        var record = _store.Install(package);

        Assert.Equal("aaabacadaeafagahaiajakalamanaoap", record.Id);
        Assert.Equal("Test Extension", record.Name);
    }

    [Fact]
    public void ACorruptPackageFailsCleanly()
    {
        var zip = CrxFixtures.Zip(Files);
        byte[] id = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

        var truncated = Assert.Throws<ExtensionStoreException>(() => _store.Install(CrxFixtures.Crx3(id, zip[..(zip.Length - 40)])));
        Assert.StartsWith("The extension package could not be unpacked: ", truncated.Message);
        Assert.IsType<InvalidDataException>(truncated.InnerException);

        Assert.Throws<ExtensionStoreException>(() => _store.Install(CrxFixtures.Crx3(id, [])));
        Assert.Throws<ExtensionStoreException>(() => _store.Install([.. CrxFixtures.ZipMagic, 9, 9, 9]));

        Assert.Empty(_store.Extensions);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root.Path));
    }

    [Fact]
    public void AZipWithWindowsStyleSeparatorsInstalls()
    {
        var package = CrxFixtures.Zip([.. Files.Select(file => (file.Name.Replace('/', '\\'), file.Contents))]);
        var record = _store.Install(package);

        Assert.Equal("Test Extension", record.Name); // found _locales\en\messages.json
        Assert.True(File.Exists(Path.Combine(_store.GeckoDirectoryFor(record.Id), "icons", "128.png")));
    }

    // ---- Reading manifests ----

    [Fact]
    public void ManifestsMayUseEverythingChromeTolerates()
    {
        const string Manifest = """
            /* block comment */
            {
              "manifest_version": 2, // line comment
              "name": "Tolerated",
              "version": "1.0",
              "permissions": ["storage", "tabs",],
              "browser_action": { "default_popup": "popup.html", },
            }
            """;
        byte[] withMark = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Manifest)];
        var record = _store.Install(CrxFixtures.Zip([("manifest.json", withMark)]));

        Assert.Equal("Tolerated", record.Name);
        Assert.Equal(2, record.ManifestVersion);
        Assert.True(record.HasPopup);
        Assert.False(record.HasOptions);
        Assert.Null(record.IconPath);
        Assert.Null(record.WorstSeverity);
        // What Gecko is given is plain JSON.
        Assert.Equal(["storage", "tabs"], Strings(ReadJson(Path.Combine(_store.GeckoDirectoryFor(record.Id), "manifest.json"))["permissions"]));
    }

    [Fact]
    public void LocaleFilesMayUseEverythingChromeTolerates()
    {
        const string Messages = """
            {
              // Translators: the product name.
              "appName": { "message": "First" },
              "APPNAME": { "message": "Wörterbuch 日本", "description": "repeated; the last one wins", },
              "appDesc": { "message": "Tut Dinge" },
            }
            """;
        byte[] withMark = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Messages)];
        var package = CrxFixtures.Zip([("manifest.json", Encoding.UTF8.GetBytes(ManifestV3)), ("_locales/en/messages.json", withMark)]);

        var record = _store.Install(package);

        Assert.Equal("Wörterbuch 日本", record.Name);
        Assert.Equal("Tut Dinge", record.Summary);
    }

    [Theory]
    [InlineData("""{"name":"__MSG_appName__","version":"1"}""")]                                 // no default_locale
    [InlineData("""{"name":"__MSG_appName__","version":"1","default_locale":"fr"}""")]           // no such locale
    [InlineData("""{"name":"__MSG_appName__","version":"1","default_locale":"broken"}""")]       // unreadable messages
    [InlineData("""{"name":"__MSG_appName__","version":"1","default_locale":"../../.."}""")]     // not a locale at all
    [InlineData("""{"name":"__MSG_other__","version":"1","default_locale":"en"}""")]             // no such message
    public void UnresolvablePlaceholdersAreShownAsTheyAre(string manifest)
    {
        var record = _store.Install(CrxFixtures.Zip(
            ("manifest.json", manifest),
            ("_locales/en/messages.json", """{"appName": {"message": "Test Extension"}}"""),
            ("_locales/broken/messages.json", "not json")));
        Assert.StartsWith("__MSG_", record.Name);
    }

    [Fact]
    public void MissingManifestFieldsGetTheSameDefaultsAsOnMacOS()
    {
        var package = CrxFixtures.Zip(("manifest.json", "{}"));
        var record = _store.Install(package);

        Assert.Equal(record.Id, record.Name);
        Assert.Equal("0", record.Version);
        Assert.Equal("", record.Summary);
        Assert.Equal(2, record.ManifestVersion);
        Assert.Equal($"{record.Id}@crx.mosaic", record.GeckoId);
        Assert.False(record.HasPopup);
        Assert.False(record.HasOptions);
        Assert.Empty(record.Notes);
    }

    [Theory]
    [InlineData("[1, 2, 3]", "top level is not an object")]
    [InlineData("\"text\"", "top level is not an object")]
    [InlineData("null", "top level is not an object")]
    [InlineData("{ 'single': 1 }", "")]
    [InlineData("", "")]
    public void RejectsManifestsThatAreNotJsonObjects(string manifest, string reason)
    {
        var error = Assert.Throws<ExtensionStoreException>(() => _store.Install(CrxFixtures.Zip(("manifest.json", manifest))));
        Assert.StartsWith("The extension's manifest.json could not be read: " + reason, error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root.Path));
    }

    [Theory]
    [InlineData("""{ "browser_action": { "default_popup": "p.html" } }""", true, false)]
    [InlineData("""{ "manifest_version": 3, "browser_action": { "default_popup": "p.html" } }""", true, false)]
    [InlineData("""{ "action": { "default_popup": "" } }""", false, false)]
    [InlineData("""{ "action": {}, "browser_action": { "default_popup": "p.html" } }""", false, false)]
    [InlineData("""{ "page_action": { "default_popup": "p.html" } }""", false, false)]
    [InlineData("""{ "options_ui": { "page": "o.html" } }""", false, true)]
    [InlineData("""{ "options_page": "o.html" }""", false, true)]
    [InlineData("""{ "options_page": 5 }""", false, false)]
    public void PopupAndOptionsFollowTheConvertedManifest(string manifest, bool hasPopup, bool hasOptions)
    {
        var record = _store.Install(CrxFixtures.Zip(("manifest.json", manifest)));
        Assert.Equal(hasPopup, record.HasPopup);
        Assert.Equal(hasOptions, record.HasOptions);
    }

    [Theory]
    [InlineData("""{ "16": "a.png", "128": "/big.png", "48": "c.png" }""", "big.png")]
    [InlineData("""{ "128": "small.png", "1024": "dir/huge.png" }""", "dir/huge.png")]
    [InlineData("""{ "big": "x.png", "32": 7, "16": "only.png" }""", "only.png")]
    [InlineData("""{ "big": "x.png" }""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""[]""", null)]
    public void IconPathIsTheLargestIcon(string icons, string? expected)
    {
        var record = _store.Install(CrxFixtures.Zip(("manifest.json", $$"""{ "icons": {{icons}} }""")));
        Assert.Equal(expected, record.IconPath);
        Assert.Equal(
            expected is null ? null : Path.Combine([_store.GeckoDirectoryFor(record.Id), .. expected.Split('/')]),
            _store.IconFileFor(record));
    }

    [Fact]
    public void AnIconPathCannotPointOutsideTheExtension()
    {
        var record = _store.Install(CrxFixtures.Zip(("manifest.json", """{ "icons": { "64": "../../../secret.png" } }""")));
        Assert.Equal("../../../secret.png", record.IconPath);
        Assert.Null(_store.IconFileFor(record));
    }

    [Fact]
    public void ScriptsAreScannedWhereverTheyAre()
    {
        var record = _store.Install(CrxFixtures.Zip(
            ("manifest.json", """{"name":"Scan","version":"1"}"""),
            ("a/deep/one.js", "chrome.debugger.attach()"),
            ("two.js", "chrome.tabCapture.capture(); chrome.debugger.detach()"),
            ("not-a-script.txt", "chrome.offscreen.createDocument()"),
            ("three.JS", "chrome.desktopCapture.chooseDesktopMedia()")));

        Assert.Equal(
            ["Uses chrome.debugger, which does not exist on Gecko.", "Uses chrome.tabCapture, which does not exist on Gecko."],
            record.Notes.Select(note => note.Message));
        Assert.Equal(NoteSeverity.Blocker, record.WorstSeverity);
    }

    [Fact]
    public void AScriptThatIsNotUtf8IsNotScannedOrHoisted()
    {
        byte[] latin1 = [.. "importScripts('lib.js'); chrome.debugger; // caf"u8, 0xE9];
        var record = _store.Install(CrxFixtures.Zip(
        [
            ("manifest.json", """{"manifest_version":3,"name":"L","version":"1","background":{"service_worker":"sw.js"}}"""u8.ToArray()),
            ("sw.js", latin1),
        ]));

        var manifest = ReadJson(Path.Combine(_store.GeckoDirectoryFor(record.Id), "manifest.json"));
        Assert.Equal([ManifestConverter.ShimFileName, "sw.js"], Strings(manifest["background"]!["scripts"]));
        Assert.DoesNotContain(record.Notes, note => note.Message.Contains("debugger"));
    }

    [Fact]
    public void TheShimIsWrittenAsUtf8WithoutAByteOrderMark()
    {
        var record = _store.Install(Crx(), ChromeId);
        var shim = File.ReadAllBytes(Path.Combine(_store.GeckoDirectoryFor(record.Id), ManifestConverter.ShimFileName));
        Assert.Equal(Encoding.UTF8.GetBytes(ManifestConverter.ShimSource), shim);
        Assert.Equal((byte)'{', File.ReadAllBytes(Path.Combine(_store.GeckoDirectoryFor(record.Id), "manifest.json"))[0]);
    }

    // ---- The library ----

    [Fact]
    public void WorstSeverityRanksBlockerAboveWarningAboveInfo()
    {
        static InstalledExtension With(params NoteSeverity[] severities) => new()
        {
            Id = ChromeId, GeckoId = "g", Name = "n", Version = "1",
            Notes = [.. severities.Select(severity => new CompatibilityNote(severity, severity.ToString()))],
        };
        Assert.Null(With().WorstSeverity);
        Assert.Equal(NoteSeverity.Info, With(NoteSeverity.Info).WorstSeverity);
        Assert.Equal(NoteSeverity.Warning, With(NoteSeverity.Info, NoteSeverity.Warning, NoteSeverity.Info).WorstSeverity);
        Assert.Equal(NoteSeverity.Blocker, With(NoteSeverity.Warning, NoteSeverity.Blocker, NoteSeverity.Info).WorstSeverity);
    }

    [Fact]
    public void PathsAreLaidOutUnderTheRoot()
    {
        Assert.Equal(Path.GetFullPath(_root.Path), _store.Root);
        Assert.Equal(Path.Combine(_store.Root, ChromeId), _store.DirectoryFor(ChromeId));
        Assert.Equal(Path.Combine(_store.Root, ChromeId, "gecko"), _store.GeckoDirectoryFor(ChromeId));
        Assert.Equal(Path.Combine(_store.Root, ChromeId, "chromium"), _store.ChromiumDirectoryFor(ChromeId));
        Assert.Equal(Path.Combine(_store.Root, ChromeId, "source.crx"), _store.SourcePackageFor(ChromeId));
    }

    [Fact]
    public void CreatesItsRootIncludingMissingParents()
    {
        var nested = Path.Combine(_root.Path, "a", "b", "Extensions");
        var store = new ExtensionStore(nested);
        Assert.True(Directory.Exists(nested));
        Assert.Empty(store.Extensions);
    }

    [Fact]
    public void ExtensionsAreSortedByNameIgnoringCase()
    {
        foreach (var name in (string[])["delta", "Alpha", "charlie", "Bravo"])
        {
            _store.Install(CrxFixtures.Zip(("manifest.json", $$"""{"name":"{{name}}","version":"1"}""")));
        }
        Assert.Equal(["Alpha", "Bravo", "charlie", "delta"], _store.Extensions.Select(extension => extension.Name));
        Assert.Equal(["Alpha", "Bravo", "charlie", "delta"], new ExtensionStore(_root.Path).Extensions.Select(extension => extension.Name));
    }

    [Fact]
    public void TheExtensionListIsASnapshot()
    {
        var before = _store.Extensions;
        _store.Install(Crx(), ChromeId);
        var installed = _store.Extensions;
        _store.SetEnabled(false, ChromeId);
        _store.Remove(ChromeId);

        Assert.Empty(before);
        Assert.True(Assert.Single(installed).Enabled);
        Assert.Empty(_store.Extensions);
    }

    [Fact]
    public void ReinstallingReplacesEveryFile()
    {
        _store.Install(Crx(FilesWith(("old-only.js", "old"))), ChromeId);
        var firstInstall = _store.Extensions[0].InstalledAt;

        var record = _store.Install(Crx(FilesWith(("new-only.js", "new"), ("lib.js", "var lib = 2;"))), ChromeId);

        foreach (var directory in (string[])[_store.GeckoDirectoryFor(ChromeId), _store.ChromiumDirectoryFor(ChromeId)])
        {
            Assert.False(File.Exists(Path.Combine(directory, "old-only.js")));
            Assert.True(File.Exists(Path.Combine(directory, "new-only.js")));
            Assert.Equal("var lib = 2;", File.ReadAllText(Path.Combine(directory, "lib.js")));
        }
        Assert.True(record.InstalledAt >= firstInstall);
        Assert.Empty(StagingLeftovers());
        Assert.Equal([ChromeId, "index.json"], Directory.EnumerateFileSystemEntries(_root.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void SetEnabledAndRemoveIgnoreUnknownIds()
    {
        _store.Install(Crx(), ChromeId);
        _store.SetEnabled(false, "ponmlkjihgfedcbaponmlkjihgfedcba");
        _store.Remove("ponmlkjihgfedcbaponmlkjihgfedcba");

        var extension = Assert.Single(_store.Extensions);
        Assert.True(extension.Enabled);
        _store.SetEnabled(false, ChromeId);
        _store.SetEnabled(true, ChromeId);
        Assert.True(Assert.Single(new ExtensionStore(_root.Path).Extensions).Enabled);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../sibling")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")]
    public void OnlyRealIdsNameFolders(string notAnId)
    {
        using var outer = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(outer.Path, "sibling"));
        File.WriteAllText(Path.Combine(outer.Path, "sibling", "precious.txt"), "keep me");
        var store = new ExtensionStore(Path.Combine(outer.Path, "library"));
        store.Install(Crx(), ChromeId);

        var error = Assert.Throws<ExtensionStoreException>(() => store.Install(Crx(), notAnId));
        Assert.Contains("is not a Chrome extension ID", error.Message);
        store.Remove(notAnId);

        Assert.True(File.Exists(Path.Combine(outer.Path, "sibling", "precious.txt")));
        Assert.True(Directory.Exists(store.GeckoDirectoryFor(ChromeId)));
        Assert.Single(store.Extensions);
        Assert.Equal(["library", "sibling"], Directory.EnumerateFileSystemEntries(outer.Path).Select(Path.GetFileName).Order());
    }

    // ---- index.json ----

    [Fact]
    public void TheIndexRoundTripsEveryField()
    {
        var installed = _store.Install(Crx(), ChromeId);
        _store.SetEnabled(false, ChromeId);

        var reloaded = Assert.Single(new ExtensionStore(_root.Path).Extensions);

        Assert.Equal(installed with { Enabled = false, Notes = reloaded.Notes }, reloaded);
        Assert.Equal(installed.Notes, reloaded.Notes);
        Assert.Equal(installed.InstalledAt, reloaded.InstalledAt);
        Assert.NotEmpty(reloaded.Notes);
    }

    [Fact]
    public void TheIndexIsReadableJsonWrittenInOnePiece()
    {
        _store.Install(CrxFixtures.Zip(("manifest.json", """{"name":"Wörterbuch 日本","version":"1","permissions":["debugger"]}""")));

        var text = File.ReadAllText(Path.Combine(_root.Path, "index.json"));
        var index = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(1, (int?)index["version"]);
        var entry = Assert.Single(index["extensions"]!.AsArray())!.AsObject();
        Assert.Equal("Wörterbuch 日本", (string?)entry["name"]);
        Assert.Equal("Blocker", (string?)entry["notes"]![0]!["severity"]);
        Assert.False(entry.ContainsKey("worstSeverity"));
        Assert.Contains("Wörterbuch 日本", text); // not \u-escaped
        // No temporary file is left next to it.
        Assert.Empty(StagingLeftovers());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"version":1,"extensions":"none"}""")]
    [InlineData("""{"version":1,"extensions":[1,"two",null,{},{"id":"abcdefghijklmnopabcdefghijklmnop"}]}""")]
    public void AnUnreadableIndexIsAnEmptyLibraryThatWorks(string index)
    {
        _store.Install(Crx(), ChromeId);
        File.WriteAllText(Path.Combine(_root.Path, "index.json"), index);

        var store = new ExtensionStore(_root.Path);
        Assert.Empty(store.Extensions);

        // The orphaned files do not get in the way of installing again.
        store.Install(Crx(), ChromeId);
        Assert.Single(new ExtensionStore(_root.Path).Extensions);
    }

    [Fact]
    public void DamagedOrDanglingIndexEntriesAreDroppedAndTheRestKept()
    {
        _store.Install(Crx(), ChromeId);
        var indexFile = Path.Combine(_root.Path, "index.json");
        var index = JsonNode.Parse(File.ReadAllText(indexFile))!.AsObject();
        var good = index["extensions"]![0]!;
        JsonObject Variant(Action<JsonObject> change)
        {
            var copy = good.DeepClone().AsObject();
            change(copy);
            return copy;
        }
        index["extensions"] = new JsonArray(
            Variant(entry => entry["id"] = "ponmlkjihgfedcbaponmlkjihgfedcba"),   // files are gone
            Variant(entry => entry["id"] = ".."),                                // would name a folder outside the library
            Variant(entry => entry.Remove("name")),                              // a required field is missing
            Variant(entry => entry["name"] = null),
            Variant(entry => entry["notes"] = new JsonArray((JsonNode?)null)),
            Variant(entry => entry["notes"]![0]!["severity"] = "Catastrophic"),
            Variant(entry => entry["enabled"] = "yes"),
            "nonsense",
            good.DeepClone());
        File.WriteAllText(indexFile, index.ToJsonString());

        var extension = Assert.Single(new ExtensionStore(_root.Path).Extensions);
        Assert.Equal(ChromeId, extension.Id);
        Assert.Equal("Test Extension", extension.Name);
    }

    [Fact]
    public void LeftoversOfAnInterruptedInstallationAreClearedOnOpening()
    {
        _store.Install(Crx(), ChromeId);
        Directory.CreateDirectory(Path.Combine(_root.Path, ".staging-0123", "gecko"));
        File.WriteAllText(Path.Combine(_root.Path, ".staging-0123", "gecko", "manifest.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root.Path, ".retired-4567"));
        File.WriteAllText(Path.Combine(_root.Path, ".index-89ab.tmp"), "{");
        Directory.CreateDirectory(Path.Combine(_root.Path, ".something-else"));

        var store = new ExtensionStore(_root.Path);

        Assert.Equal(
            [".something-else", ChromeId, "index.json"],
            Directory.EnumerateFileSystemEntries(_root.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Single(store.Extensions);
    }
}
