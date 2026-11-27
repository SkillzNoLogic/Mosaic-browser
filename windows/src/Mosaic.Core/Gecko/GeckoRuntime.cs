namespace Mosaic.Core.Gecko;

/// <summary>A Gecko build Mosaic can run as its Mozilla engine.</summary>
/// <param name="ExecutablePath">Full path of <c>firefox.exe</c> (or a fork's equivalent).</param>
/// <param name="Name">Product name, e.g. "Firefox".</param>
/// <param name="Version">Product version, e.g. "153.8.0"; "?" if unknown.</param>
/// <param name="IsManaged">True for the private copy under <see cref="AppPaths.ManagedGecko"/>.</param>
public sealed record GeckoRuntime(string ExecutablePath, string Name, string Version, bool IsManaged)
{
    /// <summary>
    /// Candidate executables in preference order: the <c>MOSAIC_GECKO_EXE</c> environment variable
    /// (an explicit override, also what tests use), Mosaic's private copy, then Gecko browsers
    /// installed on the machine (Firefox, Firefox ESR, Developer Edition, Nightly, LibreWolf, Zen)
    /// under Program Files / Program Files (x86) / the per-user Programs folder.
    /// </summary>
    public static IReadOnlyList<string> Candidates() => throw NotPorted.Yet();

    /// <summary>The first candidate that is a real Gecko runtime, or null.</summary>
    public static GeckoRuntime? Locate() => throw NotPorted.Yet();

    /// <summary>
    /// Describes the runtime at <paramref name="executablePath"/>, or null if it is not one
    /// (missing, or no Gecko <c>application.ini</c>/<c>xul</c> library beside it).
    /// </summary>
    public static GeckoRuntime? FromExecutable(string executablePath, bool isManaged = false) => throw NotPorted.Yet();
}

/// <summary>Screen rectangle in physical pixels, origin at the top-left of the virtual screen.</summary>
public readonly record struct GeckoRect(int X, int Y, int Width, int Height);

/// <summary>
/// The dedicated Gecko profile Mosaic runs its engine with, so the user's own Firefox profile
/// is never read or changed.
/// </summary>
/// <remarks>Port of <c>GeckoProfile</c> in <c>Sources/MosaicCore/Gecko/GeckoRuntime.swift</c>.</remarks>
public static class GeckoProfile
{
    /// <summary>Writes the files that must be in place before Gecko starts (user.js, chrome/userChrome.css,
    /// and xulstore.json when an initial rectangle is given).</summary>
    public static void Prepare(string profileDirectory, GeckoRect? initialRect = null) => throw NotPorted.Yet();

    public static string UserJs => throw NotPorted.Yet();
    public static string UserChromeCss => throw NotPorted.Yet();
}
