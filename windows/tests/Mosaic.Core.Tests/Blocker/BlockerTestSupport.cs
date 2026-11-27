using Mosaic.Core.Blocker;

namespace Mosaic.Core.Tests.Blocker;

/// <summary>
/// Where the real engine bundle is: the folder named by <c>MOSAIC_UBLOCK_DIR</c>, or else
/// <c>vendor/ublock</c> as <c>scripts/bootstrap.ps1</c> leaves it, found above the test folder.
/// </summary>
internal static class UBlockBundle
{
    public const string Variable = "MOSAIC_UBLOCK_DIR";

    public static string? Directory { get; } = Locate();

    public static string BundlePath => Path.Combine(Directory!, "ublock-engine.js");
    public static string ListsDirectory => Path.Combine(Directory!, "lists");
    public static string SuffixListPath => Path.Combine(ListsDirectory, FilterListCatalog.SuffixListFileName);

    private static string? Locate()
    {
        static bool IsBundle(string folder) =>
            File.Exists(Path.Combine(folder, "ublock-engine.js"))
            && File.Exists(Path.Combine(folder, "lists", FilterListCatalog.SuffixListFileName));

        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named) return IsBundle(named) ? named : null;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "vendor", "ublock");
            if (IsBundle(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>A fact that runs uBlock Origin's real engine; skipped where the bundle has not been built.</summary>
public sealed class UBlockFactAttribute : FactAttribute
{
    public UBlockFactAttribute()
    {
        if (UBlockBundle.Directory is null)
        {
            Skip = $"Run scripts/bootstrap.ps1, or set {UBlockBundle.Variable} to a folder with ublock-engine.js and lists/, to run the engine tests.";
        }
    }
}

/// <summary>
/// A stand-in for <c>ublock-engine.js</c> with the same entry points, for tests of the code around
/// the engine: it blocks any URL containing "ads.", allows any containing "allowed", and counts
/// every line starting with "||" as a filter. A list whose text is "THROW" makes loading fail, and
/// a request URL containing "throw" makes <c>match</c> throw.
/// </summary>
internal static class FakeBundle
{
    public const string Source = """
        // As polyfills.js does in the real bundle: console output goes to the host's sink.
        if (typeof console === "undefined") {
          globalThis.console = { warn: (...args) => __mosaicLog("warn", args.join(" ")) };
        }
        var MosaicBlocker = (() => {
          let filters = 0;
          let suffixList = null;
          return {
            version: "0.0-test",
            init(text) { suffixList = text; return true; },
            beginLists() { if (suffixList === null) throw new Error("init() must be called first"); filters = 0; },
            addList(name, raw) {
              if (raw === "THROW") throw new Error("bad list " + name);
              filters += raw.split("\n").filter(line => line.startsWith("||")).length;
            },
            commitLists() { console.warn("committed"); return filters; },
            match(url, originURL, type) {
              if (url.includes("throw")) throw new Error("cannot match " + url);
              if (url.includes("allowed")) return 2;
              return url.includes("ads.") ? 1 : 0;
            },
          };
        })();
        """;

    /// <summary>Writes the stand-in to <c>ublock-engine.js</c> in <paramref name="folder"/> and returns its path.</summary>
    public static string WriteTo(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "ublock-engine.js");
        File.WriteAllText(path, Source);
        return path;
    }
}
