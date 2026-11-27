using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mosaic.Core.Extensions;

/// <summary>A Chrome extension that has been unpacked and converted for Gecko.</summary>
public sealed record InstalledExtension
{
    /// <summary>Chrome extension ID (32 letters a–p).</summary>
    public required string Id { get; init; }
    /// <summary>Add-on ID the converted extension is registered under in Gecko.</summary>
    public required string GeckoId { get; init; }
    /// <summary>Display name, localised from the extension's default locale.</summary>
    public required string Name { get; init; }
    /// <summary>The extension's own version string.</summary>
    public required string Version { get; init; }
    /// <summary>The extension's description, localised like <see cref="Name"/>; may be empty.</summary>
    public string Summary { get; init; } = "";
    /// <summary>The <c>manifest_version</c> the extension was written for (2 or 3).</summary>
    public int ManifestVersion { get; init; } = 2;
    /// <summary>Whether the user wants the extension loaded.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>What was adapted for Gecko and what will not work there.</summary>
    public IReadOnlyList<CompatibilityNote> Notes { get; init; } = [];
    /// <summary>When this version was installed.</summary>
    public DateTimeOffset InstalledAt { get; init; }
    /// <summary>Path of the largest icon, relative to the extension's root (forward slashes).</summary>
    public string? IconPath { get; init; }
    /// <summary>True when the toolbar action opens a popup page.</summary>
    public bool HasPopup { get; init; }
    /// <summary>True when the extension has an options page.</summary>
    public bool HasOptions { get; init; }

    /// <summary>The most serious severity among <see cref="Notes"/>, or null when there are none.</summary>
    [JsonIgnore]
    public NoteSeverity? WorstSeverity => Notes.Count == 0 ? null : Notes.Max(note => note.Severity);
}

/// <summary>Thrown when a package cannot be installed. <see cref="Exception.Message"/> is user-presentable.</summary>
public sealed class ExtensionStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// On-disk library of converted extensions.
/// <code>
/// &lt;root&gt;/index.json
/// &lt;root&gt;/&lt;chrome id&gt;/source.crx   original package
/// &lt;root&gt;/&lt;chrome id&gt;/gecko/       unpacked, converted for Gecko
/// &lt;root&gt;/&lt;chrome id&gt;/chromium/    unpacked, untouched (for the Chromium engine)
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>Sources/MosaicCore/Extensions/ExtensionStore.swift</c> (behaviour pinned down by
/// <c>ExtensionStoreTests.swift</c>), with two differences: packages are unpacked with
/// <c>System.IO.Compression</c> (rejecting entries that would escape the destination), and an
/// unconverted copy is kept in <c>chromium/</c>. Not thread-safe; callers serialise access.
/// </para>
/// <para>
/// The <c>chromium/</c> copy differs from the package in two ways only, both needed for
/// Chromium to load it unpacked: Chrome's <c>_metadata</c> folder is gone, and a manifest without
/// a <c>key</c> gains the package's public key so the extension keeps its Web Store ID.
/// </para>
/// <para>
/// A package is unpacked and converted in a private folder under the root and only then swapped
/// into place, so the library never holds a half-installed extension.
/// </para>
/// </remarks>
public sealed class ExtensionStore
{
    private const string IndexFileName = "index.json";
    private const string GeckoFolder = "gecko";
    private const string ChromiumFolder = "chromium";
    private const string SourcePackageName = "source.crx";
    private const string ManifestFileName = "manifest.json";

    /// <summary>Chrome's signature data. Meaningless to Gecko, and Chromium refuses to load an
    /// unpacked extension that contains it (names starting with "_" are reserved).</summary>
    private const string MetadataFolder = "_metadata";

    /// <summary>Folders of an installation in progress and of a replaced one awaiting deletion.</summary>
    private const string StagingPrefix = ".staging-";
    private const string RetiredPrefix = ".retired-";
    private const string IndexTemporaryPrefix = ".index-";

    /// <summary>Scripts larger than this are not scanned for Chrome-only APIs.</summary>
    private const long LargestScannedScript = 8_000_000;

    /// <summary>Length of an extension ID, which names the extension's folder.</summary>
    private const int IdLength = 32;

    private const int IndexFormatVersion = 1;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private IReadOnlyList<InstalledExtension> _extensions;

    /// <summary>Opens the library in <paramref name="root"/>, creating the folder if needed.</summary>
    /// <remarks>
    /// An unreadable index is treated as an empty library, and an entry whose files have gone
    /// missing is dropped. Leftovers of an interrupted installation are cleared away.
    /// </remarks>
    /// <exception cref="IOException">The folder cannot be created.</exception>
    public ExtensionStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        RemoveLeftovers();
        _extensions = LoadIndex();
    }

    /// <summary>The library's folder, as an absolute path.</summary>
    public string Root { get; }

    /// <summary>Installed extensions, sorted by name (case-insensitive).</summary>
    /// <remarks>A snapshot: installing or removing replaces the list rather than changing it.</remarks>
    public IReadOnlyList<InstalledExtension> Extensions => _extensions;

    /// <summary>The folder holding everything kept for an extension.</summary>
    public string DirectoryFor(string id) => Path.Combine(Root, id);

    /// <summary>The unpacked copy converted for Gecko; what <c>installExtension</c> is given.</summary>
    public string GeckoDirectoryFor(string id) => Path.Combine(Root, id, GeckoFolder);

    /// <summary>The unpacked, unconverted copy the Chromium engine loads.</summary>
    public string ChromiumDirectoryFor(string id) => Path.Combine(Root, id, ChromiumFolder);

    /// <summary>The package exactly as it was installed.</summary>
    public string SourcePackageFor(string id) => Path.Combine(Root, id, SourcePackageName);

    /// <summary>Absolute path of the extension's icon, or null.</summary>
    /// <remarks>The file is not checked for existence; a manifest may name an icon it does not ship.</remarks>
    public string? IconFileFor(InstalledExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return extension.IconPath is { } icon ? ExtensionPaths.Resolve(GeckoDirectoryFor(extension.Id), icon) : null;
    }

    /// <summary>
    /// Unpacks a <c>.crx</c> (or zipped extension), converts it for Gecko and records it.
    /// Installing an ID that already exists replaces it and keeps its enabled state. A package
    /// that fails to install leaves an existing installation untouched.
    /// </summary>
    /// <param name="package">The package file's contents.</param>
    /// <param name="knownId">The extension's ID when the caller knows it (it asked the Web Store
    /// for that ID). Otherwise the ID comes from the package, from the manifest's <c>key</c>, or
    /// failing both is derived from the package's contents.</param>
    /// <exception cref="CrxParseException">The file is not an extension package.</exception>
    /// <exception cref="ExtensionStoreException">The package cannot be unpacked, has no usable
    /// manifest, or could not be written to the library.</exception>
    public InstalledExtension Install(byte[] package, string? knownId = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (knownId is not null && !CrxArchive.IsValidId(knownId))
        {
            // The ID becomes a folder name; nothing but a real ID may.
            throw new ExtensionStoreException($"“{knownId}” is not a Chrome extension ID.");
        }

        var archive = CrxArchive.Parse(package);

        // Build the whole installation somewhere private first so a bad package never disturbs an installed one.
        var staging = Path.Combine(Root, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            var record = Prepare(archive, package, knownId, staging);
            Commit(staging, record.Id);

            var extensions = _extensions.Where(extension => extension.Id != record.Id).Append(record)
                .OrderBy(extension => extension.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(extension => extension.Id, StringComparer.Ordinal)
                .ToArray();
            SaveIndex(extensions);
            _extensions = extensions;
            return record;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ExtensionStoreException($"The extension could not be saved: {error.Message}", error);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Forgets an extension and deletes its files. Unknown IDs are ignored.</summary>
    /// <remarks>
    /// Files an engine still has open cannot be deleted on Windows; the extension is forgotten
    /// regardless and what could not be deleted is left behind.
    /// </remarks>
    /// <exception cref="IOException">The index could not be written.</exception>
    public void Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var extensions = _extensions.Where(extension => extension.Id != id).ToArray();
        if (CrxArchive.IsValidId(id) && Directory.Exists(DirectoryFor(id)))
        {
            // Out of the way in one step if possible, so a partly deleted folder is never mistaken for an extension.
            var retired = Path.Combine(Root, RetiredPrefix + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.Move(DirectoryFor(id), retired);
                TryDeleteDirectory(retired);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                TryDeleteDirectory(DirectoryFor(id));
            }
        }
        SaveIndex(extensions);
        _extensions = extensions;
    }

    /// <summary>Records whether the user wants an extension loaded. Unknown IDs are ignored.</summary>
    /// <exception cref="IOException">The index could not be written.</exception>
    public void SetEnabled(bool enabled, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_extensions.Any(extension => extension.Id == id)) return;

        var extensions = _extensions
            .Select(extension => extension.Id == id ? extension with { Enabled = enabled } : extension)
            .ToArray();
        SaveIndex(extensions);
        _extensions = extensions;
    }

    // ---- Installing ----

    /// <summary>
    /// Fills <paramref name="staging"/> with a complete installation (both unpacked copies and
    /// the source package) and describes it.
    /// </summary>
    private InstalledExtension Prepare(CrxArchive archive, byte[] package, string? knownId, string staging)
    {
        if (MaximumRelativePathLength < ManifestFileName.Length)
        {
            // Otherwise every file would be skipped as too long and the package blamed for it.
            throw new ExtensionStoreException("The folder Mosaic keeps extensions in has a path too long for Windows to store an extension there.");
        }

        var chromium = Path.Combine(staging, ChromiumFolder);
        var gecko = Path.Combine(staging, GeckoFolder);
        Directory.CreateDirectory(chromium);

        IReadOnlyList<string> skipped;
        try
        {
            skipped = PackageUnpacker.Unpack(archive.ZipData, chromium, MaximumRelativePathLength);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException)
        {
            throw new ExtensionStoreException($"The extension package could not be unpacked: {error.Message}", error);
        }

        var manifestFile = Path.Combine(chromium, ManifestFileName);
        if (!File.Exists(manifestFile))
        {
            throw new ExtensionStoreException("The package has no manifest.json, so it is not an extension.");
        }
        var manifest = ReadManifest(manifestFile);

        var id = knownId
            ?? archive.ExtensionId
            ?? (ManifestConverter.StringValue(manifest["key"]) is { } key ? CrxArchive.ExtensionIdFromManifestKey(key) : null)
            ?? FallbackId(package);

        TryDeleteDirectory(Path.Combine(chromium, MetadataFolder));
        CopyDirectory(chromium, gecko);

        // Chromium derives an unpacked extension's ID from its "key", or from its folder's path
        // when there is none. Give it the key the package was signed with, so the extension has
        // the ID (and with it the storage and the origin) it has in Chrome.
        if (!manifest.ContainsKey("key") && archive.PublicKey is { } publicKey)
        {
            var withKey = (JsonObject)manifest.DeepClone();
            withKey["key"] = Convert.ToBase64String(publicKey);
            File.WriteAllText(manifestFile, ChromeJson.Serialize(withKey));
        }

        var conversion = ManifestConverter.Convert(manifest, id, path => ReadText(gecko, path));
        List<CompatibilityNote> notes = [.. conversion.Notes, .. ScanScripts(gecko)];
        if (skipped.Count > 0) notes.Add(SkippedFilesNote(skipped));

        File.WriteAllText(Path.Combine(gecko, ManifestFileName), ChromeJson.Serialize(conversion.Manifest));
        File.WriteAllText(Path.Combine(gecko, ManifestConverter.ShimFileName), ManifestConverter.ShimSource);
        File.WriteAllBytes(Path.Combine(staging, SourcePackageName), package);

        var converted = conversion.Manifest;
        var messages = LocaleMessages(gecko, manifest);
        var geckoId = ManifestConverter.StringValue(((converted["browser_specific_settings"] as JsonObject)?["gecko"] as JsonObject)?["id"])
            ?? ManifestConverter.GeckoIdFor(id);
        var action = (converted.ContainsKey("action") ? converted["action"] : converted["browser_action"]) as JsonObject;

        return new InstalledExtension
        {
            Id = id,
            GeckoId = geckoId,
            Name = Localized(ManifestConverter.StringValue(manifest["name"]), messages) ?? id,
            Version = ManifestConverter.StringValue(manifest["version"]) ?? "0",
            Summary = Localized(ManifestConverter.StringValue(manifest["description"]), messages) ?? "",
            ManifestVersion = (int)Math.Clamp(ManifestConverter.IntegerValue(manifest["manifest_version"]) ?? 2, int.MinValue, int.MaxValue),
            Enabled = _extensions.FirstOrDefault(extension => extension.Id == id)?.Enabled ?? true,
            Notes = ManifestConverter.WithoutDuplicates(notes),
            InstalledAt = DateTimeOffset.UtcNow,
            IconPath = LargestIcon(manifest),
            HasPopup = ManifestConverter.StringValue(action?["default_popup"]) is { Length: > 0 },
            HasOptions = converted.ContainsKey("options_ui"),
        };
    }

    /// <summary>
    /// Makes the staged installation the extension's folder, replacing a previous installation.
    /// The previous one is renamed aside first and put back if the new one cannot take its place.
    /// </summary>
    private void Commit(string staging, string id)
    {
        var home = DirectoryFor(id);
        string? retired = null;
        if (Directory.Exists(home))
        {
            retired = Path.Combine(Root, RetiredPrefix + Guid.NewGuid().ToString("N"));
            try
            {
                MoveDirectory(home, retired);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ExtensionStoreException(
                    $"The installed copy of this extension is in use and could not be replaced: {error.Message}", error);
            }
        }

        try
        {
            MoveDirectory(staging, home);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException && retired is not null)
        {
            try
            {
                Directory.Move(retired, home);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                // The failure being reported below is the one that matters.
            }
            throw;
        }

        if (retired is not null) TryDeleteDirectory(retired);
    }

    /// <summary>
    /// Longest path inside an extension that still fits Windows' path limit once the extension
    /// sits in the deeper of its two folders.
    /// </summary>
    private int MaximumRelativePathLength =>
        ExtensionPaths.MaximumPathLength - (Root.Length + 1 + IdLength + 1 + ChromiumFolder.Length + 1);

    /// <summary>A stable, valid-looking ID for packages that carry none (a hand-made ZIP).</summary>
    private static string FallbackId(byte[] package) => CrxArchive.ExtensionIdFromPublicKey(package);

    private static JsonObject ReadManifest(string file)
    {
        try
        {
            return ChromeJson.Parse(File.ReadAllBytes(file)) as JsonObject
                ?? throw new ExtensionStoreException(InvalidManifest("top level is not an object"));
        }
        catch (JsonException error)
        {
            throw new ExtensionStoreException(InvalidManifest(error.Message), error);
        }

        static string InvalidManifest(string why) => $"The extension's manifest.json could not be read: {why}";
    }

    /// <summary>Notes for the Chrome-only APIs used by any script of the extension.</summary>
    private static IEnumerable<CompatibilityNote> ScanScripts(string directory)
    {
        var scripts = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file) == ".js" && new FileInfo(file).Length < LargestScannedScript)
            .Order(StringComparer.Ordinal);
        return scripts.SelectMany(file => ReadText(file) is { } source ? ManifestConverter.ApiNotes(source) : []);
    }

    private static CompatibilityNote SkippedFilesNote(IReadOnlyList<string> skipped)
    {
        const int LongestExample = 80;
        var example = skipped[0].ReplaceLineEndings(" ");
        if (example.Length > LongestExample) example = example[..LongestExample] + "…";

        return new CompatibilityNote(NoteSeverity.Warning, skipped.Count == 1
            ? $"One file was left out because its name is unsafe, too long or not allowed on Windows: “{example}”."
            : $"{skipped.Count} files were left out because their names are unsafe, too long or not allowed on Windows, for example “{example}”.");
    }

    // ---- Manifest helpers ----

    /// <summary>Resolves a <c>__MSG_name__</c> placeholder against the extension's default locale.</summary>
    /// <returns>The message; the value itself when it is not a placeholder or has no message;
    /// null for a missing or empty value.</returns>
    internal static string? Localized(string? value, IReadOnlyDictionary<string, string> messages)
    {
        const string Prefix = "__MSG_", Suffix = "__";

        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length <= Prefix.Length + Suffix.Length
            || !value.StartsWith(Prefix, StringComparison.Ordinal) || !value.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return value;
        }
        var name = value[Prefix.Length..^Suffix.Length].ToLowerInvariant();
        return messages.GetValueOrDefault(name, value);
    }

    /// <summary>The default locale's messages by lower-cased name (Chrome matches names without regard to case).</summary>
    private static Dictionary<string, string> LocaleMessages(string directory, JsonObject manifest)
    {
        var messages = new Dictionary<string, string>();
        if (ManifestConverter.StringValue(manifest["default_locale"]) is not { } locale
            || ExtensionPaths.Resolve(directory, $"_locales/{locale}/messages.json") is not { } file)
        {
            return messages;
        }

        try
        {
            if (ChromeJson.Parse(File.ReadAllBytes(file)) is not JsonObject entries) return messages;
            foreach (var (name, entry) in entries)
            {
                if (ManifestConverter.StringValue((entry as JsonObject)?["message"]) is { } message)
                {
                    messages[name.ToLowerInvariant()] = message;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // No usable messages: the placeholders are shown as they are.
        }
        return messages;
    }

    /// <summary>The path of the largest entry in the manifest's <c>icons</c>, without a leading slash.</summary>
    private static string? LargestIcon(JsonObject manifest)
    {
        if (manifest["icons"] is not JsonObject icons) return null;

        string? best = null;
        long bestSize = 0;
        foreach (var (key, value) in icons)
        {
            if (!long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var size)
                || ManifestConverter.StringValue(value) is not { } path)
            {
                continue;
            }
            if (best is null || size > bestSize) (best, bestSize) = (path, size);
        }
        return best is ['/', .. var rest] ? rest : best;
    }

    // ---- Index ----

    private string IndexFile => Path.Combine(Root, IndexFileName);

    private InstalledExtension[] LoadIndex()
    {
        var extensions = new List<InstalledExtension>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(IndexFile));
            foreach (var element in document.RootElement.GetProperty("extensions").EnumerateArray())
            {
                try
                {
                    // The ID names a folder, so only a real ID is accepted from the file.
                    if (element.Deserialize(IndexJsonContext.Default.InstalledExtension) is { } extension
                        && CrxArchive.IsValidId(extension.Id) && !extension.Notes.Contains(null!)
                        && Directory.Exists(GeckoDirectoryFor(extension.Id)))
                    {
                        extensions.Add(extension);
                    }
                }
                catch (JsonException)
                {
                    // One damaged entry should not cost the user the rest of the library.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            // No index, or not one this version can read: start with whatever entries were readable.
        }
        return [.. extensions];
    }

    /// <summary>Writes the index beside the old one and renames it into place, so a crash leaves one or the other.</summary>
    private void SaveIndex(IReadOnlyList<InstalledExtension> extensions)
    {
        var index = new JsonObject
        {
            ["version"] = IndexFormatVersion,
            ["extensions"] = new JsonArray([.. extensions.Select(extension =>
                JsonSerializer.SerializeToNode(extension, IndexJsonContext.Default.InstalledExtension))]),
        };

        var temporary = Path.Combine(Root, $"{IndexTemporaryPrefix}{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(ChromeJson.Serialize(index)));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, IndexFile, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    // ---- Files ----

    /// <summary>The text of a file inside an extension, or null if it is missing, outside the
    /// extension, or not UTF-8.</summary>
    private static string? ReadText(string root, string relativePath) =>
        ExtensionPaths.Resolve(root, relativePath) is { } file ? ReadText(file) : null;

    private static string? ReadText(string file)
    {
        try
        {
            return StrictUtf8.GetString(File.ReadAllBytes(file));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>
    /// Renames a directory, allowing for Windows refusing while a virus scanner or the search
    /// indexer still has one of the freshly written files open.
    /// </summary>
    private static void MoveDirectory(string source, string destination)
    {
        const int Attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    /// <summary>Clears away what an installation or removal interrupted by a crash left behind.</summary>
    private void RemoveLeftovers()
    {
        foreach (var directory in Directory.EnumerateDirectories(Root, ".*"))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(StagingPrefix, StringComparison.Ordinal) || name.StartsWith(RetiredPrefix, StringComparison.Ordinal))
            {
                TryDeleteDirectory(directory);
            }
        }
        foreach (var file in Directory.EnumerateFiles(Root, $"{IndexTemporaryPrefix}*.tmp"))
        {
            TryDeleteFile(file);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort; the callers have nothing better to do about a folder that will not go.
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }
}

/// <summary>Serialisation of <see cref="InstalledExtension"/> for <c>index.json</c>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(InstalledExtension))]
internal sealed partial class IndexJsonContext : JsonSerializerContext;
