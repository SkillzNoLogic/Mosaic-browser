namespace Mosaic.Core.Extensions;

/// <summary>A Chrome extension that has been unpacked and converted for Gecko.</summary>
public sealed record InstalledExtension
{
    /// <summary>Chrome extension ID (32 letters a–p).</summary>
    public required string Id { get; init; }
    /// <summary>Add-on ID the converted extension is registered under in Gecko.</summary>
    public required string GeckoId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Summary { get; init; } = "";
    public int ManifestVersion { get; init; } = 2;
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<CompatibilityNote> Notes { get; init; } = [];
    public DateTimeOffset InstalledAt { get; init; }
    /// <summary>Path of the largest icon, relative to the extension's root (forward slashes).</summary>
    public string? IconPath { get; init; }
    public bool HasPopup { get; init; }
    public bool HasOptions { get; init; }

    public NoteSeverity? WorstSeverity => throw NotPorted.Yet();
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
/// Port of <c>Sources/MosaicCore/Extensions/ExtensionStore.swift</c> (behaviour pinned down by
/// <c>ExtensionStoreTests.swift</c>), with two differences: packages are unpacked with
/// <c>System.IO.Compression</c> (rejecting entries that would escape the destination), and an
/// unconverted copy is kept in <c>chromium/</c>. Not thread-safe; callers serialise access.
/// </remarks>
public sealed class ExtensionStore
{
    public ExtensionStore(string root) => throw NotPorted.Yet();

    public string Root => throw NotPorted.Yet();

    /// <summary>Installed extensions, sorted by name (case-insensitive).</summary>
    public IReadOnlyList<InstalledExtension> Extensions => throw NotPorted.Yet();

    public string DirectoryFor(string id) => throw NotPorted.Yet();
    public string GeckoDirectoryFor(string id) => throw NotPorted.Yet();
    public string ChromiumDirectoryFor(string id) => throw NotPorted.Yet();
    public string SourcePackageFor(string id) => throw NotPorted.Yet();

    /// <summary>Absolute path of the extension's icon, or null.</summary>
    public string? IconFileFor(InstalledExtension extension) => throw NotPorted.Yet();

    /// <summary>
    /// Unpacks a <c>.crx</c> (or zipped extension), converts it for Gecko and records it.
    /// Installing an ID that already exists replaces it and keeps its enabled state. A package
    /// that fails to install leaves an existing installation untouched.
    /// </summary>
    /// <exception cref="CrxParseException"/>
    /// <exception cref="ExtensionStoreException"/>
    public InstalledExtension Install(byte[] package, string? knownId = null) => throw NotPorted.Yet();

    public void Remove(string id) => throw NotPorted.Yet();

    public void SetEnabled(bool enabled, string id) => throw NotPorted.Yet();
}
