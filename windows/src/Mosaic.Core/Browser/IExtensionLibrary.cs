using Mosaic.Core.Extensions;

namespace Mosaic.Core.Browser;

/// <summary>
/// The part of the on-disk extension library that <see cref="ExtensionManager"/> uses. The app
/// supplies <see cref="ExtensionStoreLibrary"/>; tests supply an in-memory one.
/// </summary>
/// <remarks>Like <see cref="ExtensionStore"/>, not thread-safe: the manager serialises access.</remarks>
public interface IExtensionLibrary
{
    /// <summary>Installed extensions, sorted by name.</summary>
    IReadOnlyList<InstalledExtension> Extensions { get; }

    /// <summary>Folder of the copy converted for Gecko.</summary>
    string GeckoDirectoryFor(string id);

    /// <summary>Folder of the unconverted copy, for the Chromium engine.</summary>
    string ChromiumDirectoryFor(string id);

    /// <summary>Absolute path of the extension's icon, or null.</summary>
    string? IconFileFor(InstalledExtension extension);

    /// <summary>Unpacks, converts and records a package. Throws with a user-presentable message on failure.</summary>
    InstalledExtension Install(byte[] package, string? knownId);

    void Remove(string id);

    void SetEnabled(bool enabled, string id);
}

/// <summary>The real library: <see cref="ExtensionStore"/>.</summary>
public sealed class ExtensionStoreLibrary(ExtensionStore store) : IExtensionLibrary
{
    public IReadOnlyList<InstalledExtension> Extensions => store.Extensions;
    public string GeckoDirectoryFor(string id) => store.GeckoDirectoryFor(id);
    public string ChromiumDirectoryFor(string id) => store.ChromiumDirectoryFor(id);
    public string? IconFileFor(InstalledExtension extension) => store.IconFileFor(extension);
    public InstalledExtension Install(byte[] package, string? knownId) => store.Install(package, knownId);
    public void Remove(string id) => store.Remove(id);
    public void SetEnabled(bool enabled, string id) => store.SetEnabled(enabled, id);
}
