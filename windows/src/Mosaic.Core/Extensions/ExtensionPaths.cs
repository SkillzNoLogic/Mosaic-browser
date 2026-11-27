namespace Mosaic.Core.Extensions;

/// <summary>
/// Decides which paths from inside an extension package may touch the disk.
/// </summary>
/// <remarks>
/// <para>
/// Paths in a package (ZIP entry names, and file references in its manifest) are written by a
/// stranger. Before one is combined with a directory it is split into components here, and every
/// component must be a name Windows can store faithfully. That keeps a package from writing or
/// reading outside its own folder, and from aliasing another file: Windows silently drops
/// trailing dots and spaces, treats <c>manifest.json::$DATA</c> as <c>manifest.json</c>, and
/// routes <c>NUL.js</c> to a device.
/// </para>
/// <para>
/// The same rules apply on every operating system, so a package unpacks identically wherever the
/// code runs (and is tested). Rules follow Microsoft's "Naming Files, Paths, and Namespaces".
/// </para>
/// </remarks>
internal static class ExtensionPaths
{
    /// <summary>
    /// Longest full path the engines are sure to open: Windows' <c>MAX_PATH</c> (260) less its
    /// terminating null. .NET itself can go beyond it, but programs that are not long-path aware
    /// cannot read what it would create.
    /// </summary>
    public const int MaximumPathLength = 259;

    /// <summary>Longest single file or directory name NTFS stores.</summary>
    public const int MaximumNameLength = 255;

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Splits a path found inside a package into components that are safe to append to a directory.
    /// Both slashes separate; empty and <c>.</c> components (and so a leading slash) are dropped.
    /// </summary>
    /// <param name="path">The path as written in the package.</param>
    /// <param name="collapseParentReferences">True to resolve <c>..</c> against the components
    /// before it, as a browser does for a path in a manifest; false to refuse any <c>..</c>, as
    /// befits an archive entry, which has no business containing one.</param>
    /// <returns>The components, or null when the path is empty, climbs out of its root, or
    /// contains a name Windows cannot store.</returns>
    public static string[]? Split(string path, bool collapseParentReferences)
    {
        var components = new List<string>();
        foreach (var component in path.Split('/', '\\'))
        {
            switch (component)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (!collapseParentReferences || components.Count == 0) return null;
                    components.RemoveAt(components.Count - 1);
                    break;
                default:
                    if (!IsStorableName(component)) return null;
                    components.Add(component);
                    break;
            }
        }
        return components.Count == 0 ? null : [.. components];
    }

    /// <summary>
    /// The file a manifest-relative <paramref name="path"/> refers to inside <paramref name="root"/>,
    /// or null when it does not stay inside it.
    /// </summary>
    public static string? Resolve(string root, string path) =>
        Split(path, collapseParentReferences: true) is { } components ? Path.Combine([root, .. components]) : null;

    /// <summary>True when Windows stores a file or directory under exactly this name.</summary>
    public static bool IsStorableName(string name)
    {
        if (name.Length is 0 or > MaximumNameLength) return false;
        if (name[^1] is '.' or ' ') return false;
        if (name.AsSpan().IndexOfAny("<>:\"/\\|?*") >= 0 || name.AsSpan().IndexOfAnyInRange('\0', '\u001F') >= 0) return false;

        // A device name stays a device name whatever extension follows it: "NUL.tar.gz" is NUL.
        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return !DeviceNames.Contains(stem);
    }
}
