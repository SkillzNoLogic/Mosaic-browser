namespace Mosaic.Core.Gecko;

/// <summary>A Gecko build Mosaic can run as its Mozilla engine.</summary>
/// <param name="ExecutablePath">Full path of <c>firefox.exe</c> (or a fork's equivalent).</param>
/// <param name="Name">Product name, e.g. "Firefox".</param>
/// <param name="Version">Product version, e.g. "153.8.0"; "?" if unknown.</param>
/// <param name="IsManaged">True for the private copy under <see cref="AppPaths.ManagedGecko"/>.</param>
public sealed record GeckoRuntime(string ExecutablePath, string Name, string Version, bool IsManaged)
{
    /// <summary>Environment variable naming the executable to use ahead of every other candidate.</summary>
    public const string OverrideVariable = "MOSAIC_GECKO_EXE";

    /// <summary>The executable of Mosaic's private runtime, whether or not it is installed.</summary>
    public static string ManagedExecutablePath => Path.Combine(AppPaths.ManagedGecko, "firefox.exe");

    /// <summary>
    /// Install folder and executable of each Gecko browser Mosaic knows how to drive, in order
    /// of preference. Release and ESR Firefox both default to "Mozilla Firefox"; "Mozilla Firefox
    /// ESR" is where people put an ESR they keep beside a release build.
    /// </summary>
    private static readonly (string Folder, string Executable)[] KnownBrowsers =
    [
        ("Mozilla Firefox", "firefox.exe"),
        ("Mozilla Firefox ESR", "firefox.exe"),
        ("Firefox Developer Edition", "firefox.exe"),
        ("Firefox Nightly", "firefox.exe"),
        ("LibreWolf", "librewolf.exe"),
        ("Zen Browser", "zen.exe"),
    ];

    /// <summary>
    /// Candidate executables in preference order: the <c>MOSAIC_GECKO_EXE</c> environment variable
    /// (an explicit override, also what tests use), Mosaic's private copy, then Gecko browsers
    /// installed on the machine (Firefox, Firefox ESR, Developer Edition, Nightly, LibreWolf, Zen)
    /// under Program Files / Program Files (x86) / the per-user Programs folder.
    /// </summary>
    public static IReadOnlyList<string> Candidates() =>
        CandidatesFor(Environment.GetEnvironmentVariable(OverrideVariable), InstallRoots());

    /// <summary>The first candidate that is a real Gecko runtime, or null.</summary>
    public static GeckoRuntime? Locate() => LocateAmong(Candidates());

    /// <summary>
    /// Describes the runtime at <paramref name="executablePath"/>, or null if it is not one
    /// (missing, or no Gecko <c>application.ini</c>/<c>xul</c> library beside it).
    /// </summary>
    /// <remarks>
    /// Name and version come from <c>application.ini</c> rather than from the executable's version
    /// resource, so the same code describes a Windows install, a Linux one, and the executable
    /// inside a macOS <c>.app</c> bundle (which is how the engine is integration-tested off Windows).
    /// </remarks>
    public static GeckoRuntime? FromExecutable(string executablePath, bool isManaged = false)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        string executable;
        try
        {
            executable = Path.GetFullPath(executablePath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!File.Exists(executable) || Path.GetDirectoryName(executable) is not { } folder) return null;

        // Every Gecko desktop build ships its engine as one library beside the executable.
        string[] engineLibraries = ["xul.dll", "XUL", "libxul.so"];
        if (!engineLibraries.Any(name => File.Exists(Path.Combine(folder, name)))) return null;

        // Beside the executable everywhere except in a macOS bundle, where it is a resource.
        string[] manifests =
        [
            Path.Combine(folder, "application.ini"),
            Path.Combine(folder, "..", "Resources", "application.ini"),
        ];
        if (manifests.FirstOrDefault(File.Exists) is not { } manifest) return null;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections;
        try
        {
            sections = ReadIni(manifest);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        // An application.ini without [Gecko] belongs to some other XULRunner-era leftover.
        if (!sections.ContainsKey("Gecko") || !sections.TryGetValue("App", out var app)) return null;

        return new GeckoRuntime(
            executable,
            app.GetValueOrDefault("Name") is { Length: > 0 } name ? name : "Gecko",
            app.GetValueOrDefault("Version") is { Length: > 0 } version ? version : "?",
            isManaged);
    }

    internal static IReadOnlyList<string> CandidatesFor(string? overrideExecutable, IEnumerable<string> installRoots)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(overrideExecutable)) candidates.Add(overrideExecutable);
        candidates.Add(ManagedExecutablePath);
        foreach (var root in installRoots)
        {
            candidates.AddRange(KnownBrowsers.Select(browser => Path.Combine(root, browser.Folder, browser.Executable)));
        }
        return candidates.Distinct(PathComparer).ToList();
    }

    internal static GeckoRuntime? LocateAmong(IEnumerable<string> candidates)
    {
        var managed = ManagedExecutablePath;
        return candidates
            .Select(candidate => FromExecutable(candidate, isManaged: PathComparer.Equals(candidate, managed)))
            .FirstOrDefault(runtime => runtime is not null);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Folders Windows installers put browsers in. Mozilla's installer uses Program Files when it
    /// can elevate and <c>%LOCALAPPDATA%\Mozilla Firefox</c> when it cannot
    /// (browser/installer/windows/nsis/stub.nsh); other per-user installers use
    /// <c>%LOCALAPPDATA%\Programs</c>. On other systems there are no such folders, which leaves
    /// the override and the managed copy.
    /// </summary>
    private static IEnumerable<string> InstallRoots()
    {
        if (!OperatingSystem.IsWindows()) return [];

        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localAppData)
        {
            roots.Add(Path.Combine(localAppData, "Programs"));
            roots.Add(localAppData);
        }
        return roots.Where(root => root.Length > 0);
    }

    /// <summary>Reads an INI file into section → key → value. Later duplicates win.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadIni(string path)
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[line[1..^1].Trim()] = current;
            }
            else if (current is not null && line.IndexOf('=') is > 0 and var equals)
            {
                current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }
        return sections;
    }
}

/// <summary>Screen rectangle in physical pixels, origin at the top-left of the virtual screen.</summary>
public readonly record struct GeckoRect(int X, int Y, int Width, int Height);
