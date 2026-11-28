using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Platform;

/// <summary>
/// Mosaic's own update check, against the GitHub releases of its (public) repository. Port of
/// <c>Sources/MosaicCore/Platform/ReleaseCheck.swift</c>; the macOS app and this one read the
/// same releases, each picking its own assets.
/// </summary>
public static class ReleaseCheck
{
    public const string Repository = "SkillzNoLogic/Mosaic-browser";

    /// <summary>Environment variable naming a test feed (same rule as macOS).</summary>
    public const string FeedVariable = "MOSAIC_UPDATE_API";

    public static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPageUrl = new($"https://github.com/{Repository}/releases");

    /// <summary>
    /// Where the latest release is looked up: GitHub, unless a test sets <c>MOSAIC_UPDATE_API</c> to
    /// another feed returning the same JSON (HTTPS, or HTTP to this PC only).
    /// </summary>
    public static Uri FeedUrl(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (environment(FeedVariable) is { Length: > 0 } text && Uri.TryCreate(text, UriKind.Absolute, out var url)
            && IsAcceptableFeedOverride(url))
        {
            return url;
        }
        return LatestReleaseUrl;
    }

    /// <summary>An update feed other than GitHub, for testing: only HTTPS, or HTTP to this PC.</summary>
    public static bool IsAcceptableFeedOverride(Uri url) => url.Scheme.ToLowerInvariant() switch
    {
        "https" => true,
        "http" => url.Host.ToLowerInvariant() is "127.0.0.1" or "localhost" or "[::1]" or "::1",
        _ => false,
    };

    /// <summary>Interprets GitHub's answer to <see cref="LatestReleaseUrl"/>.</summary>
    public static ReleaseOutcome Outcome(int status, string body, SoftwareVersion? current)
    {
        switch (status)
        {
            case 200:
                if (ParseRelease(body) is not { } release) return new ReleaseOutcome.Failed("GitHub’s reply could not be read.");
                if (current is not null && release.Version <= current) return new ReleaseOutcome.UpToDate(release.Version);
                return new ReleaseOutcome.Available(release);
            case 404:
                return new ReleaseOutcome.NotFound();
            case 403 or 429:
                return new ReleaseOutcome.RateLimited();
            default:
                return new ReleaseOutcome.Failed($"GitHub returned HTTP {status}.");
        }
    }

    public static Release? ParseRelease(string body)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is null || Text(root["tag_name"]) is not { } tag || SoftwareVersion.Parse(tag) is not { } version) return null;
        DateTimeOffset? published = DateTimeOffset.TryParse(Text(root["published_at"]), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null;
        var assets = new List<ReleaseAsset>();
        if (root["assets"] is JsonArray list)
        {
            foreach (var asset in list.OfType<JsonObject>())
            {
                if (Text(asset["name"]) is not { } name) continue;
                var size = asset["size"] is JsonValue sizeValue && sizeValue.TryGetValue(out long bytes) ? bytes : 0;
                assets.Add(new ReleaseAsset(name, size, UrlOrNull(Text(asset["browser_download_url"])), UrlOrNull(Text(asset["url"]))));
            }
        }
        return new Release(
            version,
            Text(root["name"]) is { Length: > 0 } name2 ? name2 : tag,
            UrlOrNull(Text(root["html_url"])),
            published,
            Text(root["body"]) ?? "",
            assets);
    }

    /// <summary>
    /// The Windows zip for <paramref name="architecture"/> and its signature, or null if the
    /// release lacks either (a macOS-only release, or one made before Windows updates were signed).
    /// </summary>
    public static (ReleaseAsset Zip, ReleaseAsset Signature)? UpdateAssets(
        IReadOnlyList<ReleaseAsset> assets, SoftwareVersion version, MachineArchitecture architecture)
    {
        var zipName = UpdatePackage.ZipName(version, architecture);
        var signatureName = UpdatePackage.SignatureName(version, architecture);
        var zip = assets.FirstOrDefault(asset => asset.Name == zipName);
        var signature = assets.FirstOrDefault(asset => asset.Name == signatureName);
        return zip is null || signature is null ? null : (zip, signature);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static Uri? UrlOrNull(string? text) =>
        text is not null && Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;
}

/// <summary>A GitHub release.</summary>
public sealed record Release(
    SoftwareVersion Version, string Name, Uri? PageUrl, DateTimeOffset? PublishedAt, string Notes,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>A file attached to a GitHub release.</summary>
/// <param name="Name">The file name, e.g. <c>Mosaic-0.3.1-windows-x64.zip</c>.</param>
/// <param name="Size">Bytes, as GitHub reports it.</param>
/// <param name="BrowserUrl">The public download link.</param>
/// <param name="ApiUrl">The API link (unused: the repository is public, so no token is needed).</param>
public sealed record ReleaseAsset(string Name, long Size, Uri? BrowserUrl, Uri? ApiUrl);

/// <summary>What an update check found.</summary>
public abstract record ReleaseOutcome
{
    public abstract string Message { get; }

    /// <summary>Short machine-readable name, for automation.</summary>
    public abstract string Kind { get; }

    public sealed record UpToDate(SoftwareVersion Latest) : ReleaseOutcome
    {
        public override string Message => $"Mosaic is up to date (latest release: {Latest.NumberString}).";
        public override string Kind => "upToDate";
    }

    public sealed record Available(Release Release) : ReleaseOutcome
    {
        public override string Message => $"Mosaic {Release.Version.NumberString} is available.";
        public override string Kind => "available";
    }

    /// <summary>GitHub answered 404: no published release (or the repository moved).</summary>
    public sealed record NotFound : ReleaseOutcome
    {
        public override string Message => "GitHub has no published release of Mosaic yet (HTTP 404). Try again later, or open the releases page.";
        public override string Kind => "notFound";
    }

    public sealed record RateLimited : ReleaseOutcome
    {
        public override string Message => "GitHub is limiting requests from your network. Try again in an hour.";
        public override string Kind => "rateLimited";
    }

    public sealed record Failed(string Detail) : ReleaseOutcome
    {
        public override string Message => $"Could not check for updates: {Detail}";
        public override string Kind => "failed";
    }
}

/// <summary>
/// How often Mosaic checks for updates on its own. Checks never happen in the background unless
/// the person chose <see cref="Daily"/> (or turned on automatic updates, which implies it).
/// </summary>
public enum UpdateCheckFrequency { Manual, Daily }

public static class UpdateCheckFrequencies
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>"manual" / "daily", as macOS stores it.</summary>
    public static string Id(this UpdateCheckFrequency frequency) => frequency == UpdateCheckFrequency.Daily ? "daily" : "manual";

    public static UpdateCheckFrequency? FromId(string? id) => id switch
    {
        "manual" => UpdateCheckFrequency.Manual,
        "daily" => UpdateCheckFrequency.Daily,
        _ => null,
    };

    /// <summary>Whether an automatic check is due. A clock that went backwards must not suppress checks forever.</summary>
    public static bool IsDue(this UpdateCheckFrequency frequency, DateTimeOffset? lastCheck, DateTimeOffset now)
    {
        if (frequency != UpdateCheckFrequency.Daily) return false;
        if (lastCheck is not { } last) return true;
        return now - last >= Interval || last > now;
    }
}
