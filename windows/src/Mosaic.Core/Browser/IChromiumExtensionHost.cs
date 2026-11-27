namespace Mosaic.Core.Browser;

/// <summary>
/// Loads unpacked extensions into the Chromium engine's profile. The app implements this on top
/// of the WebView2 profile; tests use a fake.
/// </summary>
/// <remarks>
/// <para>This is experimental and goes beyond the macOS app, where extensions run on Gecko only.
/// Every member may fail; <see cref="ExtensionManager"/> treats a failure as "not loaded in
/// Chromium" and carries on, so nothing here can affect the Gecko path.</para>
/// <para>The profile only exists while a Chromium browser does, which is why readiness comes and
/// goes. The profile remembers extensions between runs and identifies each by an ID of its own,
/// which for an unpacked extension is not its Chrome Web Store ID.</para>
/// </remarks>
public interface IChromiumExtensionHost
{
    /// <summary>True while extensions can be listed, loaded and unloaded.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Awaited each time the profile becomes ready, before the page that made it ready starts
    /// loading; the place to bring the profile's extensions in line with the library.
    /// </summary>
    Func<Task>? OnReady { get; set; }

    /// <summary>Chromium's IDs of the extensions currently installed in the profile.</summary>
    Task<IReadOnlyCollection<string>> ListAsync();

    /// <summary>
    /// Installs the unpacked extension in <paramref name="folder"/> (re-installing it if it is
    /// already there) and returns Chromium's ID for it.
    /// </summary>
    Task<string> LoadAsync(string folder);

    /// <summary>Removes an extension by the ID <see cref="LoadAsync"/> or <see cref="ListAsync"/> gave.</summary>
    Task UnloadAsync(string chromiumId);
}
