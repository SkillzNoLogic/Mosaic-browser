namespace Mosaic.Core;

/// <summary>Where Mosaic keeps its data: <c>%LOCALAPPDATA%\Mosaic</c>.</summary>
public static class AppPaths
{
    /// <summary>
    /// Root data folder. Overridable (tests, portable installs) with the
    /// <c>MOSAIC_DATA_DIR</c> environment variable.
    /// </summary>
    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("MOSAIC_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaic");

    /// <summary>Profile of the private Gecko runtime.</summary>
    public static string GeckoProfile => Path.Combine(DataRoot, "Gecko", "Profile");

    /// <summary>WebView2 user-data folder (the Chromium engine's profile).</summary>
    public static string ChromiumData => Path.Combine(DataRoot, "Chromium");

    /// <summary>Library of converted extensions; see <see cref="Extensions.ExtensionStore"/>.</summary>
    public static string Extensions => Path.Combine(DataRoot, "Extensions");

    /// <summary>Folder of the privately installed Gecko runtime (contains <c>firefox.exe</c>).</summary>
    public static string ManagedGecko => Path.Combine(DataRoot, "Engines", "Firefox");

    /// <summary>Folder holding <c>agent.sys.mjs</c>, shipped next to the executable.</summary>
    public static string GeckoAgent => Path.Combine(AppContext.BaseDirectory, "gecko");

    /// <summary>The content blocker's filter lists as last refreshed; see <see cref="Blocker.FilterListStore"/>.</summary>
    public static string BlockerLists => Path.Combine(DataRoot, "Blocker", "Lists");

    /// <summary>
    /// The content blocker as shipped next to the executable: <c>ublock-engine.js</c>, uBlock Origin's
    /// licence and <c>lists\</c>, the snapshot of the filter lists taken when the app was built.
    /// </summary>
    public static string BlockerResources => Path.Combine(AppContext.BaseDirectory, "blocker");
}
