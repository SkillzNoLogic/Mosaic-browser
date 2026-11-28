using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Platform;

/// <summary>
/// A dotted version number as Mozilla and GitHub releases write them: "153.8.0esr", "153.8.0",
/// "140.0b3", "v0.2.0". Comparison is numeric, component by component; missing components count
/// as zero ("153.8" == "153.8.0").
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Platform/SoftwareVersion.swift</c>. A pre-release marker ("b3",
/// "a1", "rc2", "-beta.1") sorts before the plain release of the same number. The "esr" suffix is
/// not a pre-release: "153.8.0esr" is the same release as "153.8.0". Build metadata after "+"
/// (what .NET puts in an informational version: "0.3.1+5f2c…") is ignored.
/// </remarks>
public sealed class SoftwareVersion : IComparable<SoftwareVersion>, IEquatable<SoftwareVersion>
{
    private SoftwareVersion(IReadOnlyList<int> components, string? preRelease, string original)
    {
        Components = components;
        PreRelease = preRelease;
        Original = original;
    }

    public IReadOnlyList<int> Components { get; }

    /// <summary>Pre-release label, lowercased ("b3", "beta.1"), or null for a release.</summary>
    public string? PreRelease { get; }

    public string Original { get; }

    /// <summary>The number alone, e.g. "153.8.0".</summary>
    public string NumberString => string.Join('.', Components);

    public static SoftwareVersion? Parse(string? text)
    {
        if (text is null) return null;
        var rest = text.Trim();
        var original = rest;
        if (rest.Length > 0 && rest[0] is 'v' or 'V') rest = rest[1..];
        if (rest.IndexOf('+') is >= 0 and var plus) rest = rest[..plus];
        if (rest.EndsWith("esr", StringComparison.OrdinalIgnoreCase)) rest = rest[..^3];

        // Leading run of digits and dots is the number; anything after is a label.
        var numberLength = 0;
        while (numberLength < rest.Length && (char.IsAsciiDigit(rest[numberLength]) || rest[numberLength] == '.')) numberLength++;
        var numberPart = rest[..numberLength];
        var label = rest[numberLength..].ToLowerInvariant().TrimStart('-', '+', '.');

        if (numberPart.Length == 0) return null;
        var parts = numberPart.Split('.');
        if (parts.Any(part => part.Length == 0)) return null;
        var numbers = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)) return null;
            numbers.Add(number);
        }
        return new SoftwareVersion(numbers, label.Length == 0 ? null : label, original);
    }

    public int CompareTo(SoftwareVersion? other)
    {
        if (other is null) return 1;
        var count = Math.Max(Components.Count, other.Components.Count);
        for (var index = 0; index < count; index++)
        {
            var left = index < Components.Count ? Components[index] : 0;
            var right = index < other.Components.Count ? other.Components[index] : 0;
            if (left != right) return left < right ? -1 : 1;
        }
        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (left, right) => left == right ? 0 : NumericCompare(left!, right!),
        };
    }

    /// <summary>Like Foundation's <c>.numeric</c> comparison: runs of digits compare as numbers ("b3" &lt; "b10").</summary>
    private static int NumericCompare(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
            {
                var startI = i;
                var startJ = j;
                while (i < left.Length && char.IsAsciiDigit(left[i])) i++;
                while (j < right.Length && char.IsAsciiDigit(right[j])) j++;
                var a = left[startI..i].TrimStart('0');
                var b = right[startJ..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
                var compared = string.CompareOrdinal(a, b);
                if (compared != 0) return Math.Sign(compared);
            }
            else
            {
                if (left[i] != right[j]) return left[i] < right[j] ? -1 : 1;
                i++;
                j++;
            }
        }
        return (left.Length - i).CompareTo(right.Length - j);
    }

    public bool Equals(SoftwareVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SoftwareVersion other && Equals(other);

    public override int GetHashCode()
    {
        var trimmed = Components.ToList();
        while (trimmed.Count > 0 && trimmed[^1] == 0) trimmed.RemoveAt(trimmed.Count - 1);
        var hash = new HashCode();
        foreach (var component in trimmed) hash.Add(component);
        hash.Add(PreRelease);
        return hash.ToHashCode();
    }

    public override string ToString() => Original;

    public static bool operator ==(SoftwareVersion? left, SoftwareVersion? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(SoftwareVersion? left, SoftwareVersion? right) => !(left == right);
    public static bool operator <(SoftwareVersion left, SoftwareVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SoftwareVersion left, SoftwareVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SoftwareVersion left, SoftwareVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SoftwareVersion left, SoftwareVersion right) => left.CompareTo(right) >= 0;
}

/// <summary>The machine architectures Mosaic ships for.</summary>
public enum MachineArchitecture { X64, Arm64 }

public static class MachineArchitectures
{
    /// <summary>
    /// This machine's native architecture. On Windows on Arm an x64 Mosaic runs under emulation,
    /// and .NET still reports the operating system's own architecture here.
    /// </summary>
    public static MachineArchitecture Current => RuntimeInformation.OSArchitecture == Architecture.Arm64
        ? MachineArchitecture.Arm64 : MachineArchitecture.X64;

    /// <summary>The architecture this process was built for (what an update must match).</summary>
    public static MachineArchitecture Process => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? MachineArchitecture.Arm64 : MachineArchitecture.X64;

    /// <summary>"x64" / "arm64", as release assets and automation name them.</summary>
    public static string Id(this MachineArchitecture architecture) =>
        architecture == MachineArchitecture.Arm64 ? "arm64" : "x64";
}

/// <summary>Mozilla's public release metadata (<c>product-details.mozilla.org</c>) and download addresses.</summary>
public static class FirefoxProductDetails
{
    /// <summary>Current Firefox versions, one JSON object of channel → version.</summary>
    public static readonly Uri VersionsUrl = new("https://product-details.mozilla.org/1.0/firefox_versions.json");

    /// <summary>
    /// Latest Firefox ESR's full installer for Windows on <paramref name="architecture"/>, English
    /// (US), as <c>windows/scripts/bootstrap.ps1</c> downloads it. Mozilla redirects this to its
    /// CDN, over HTTPS.
    /// </summary>
    public static Uri EsrDownloadUrl(MachineArchitecture architecture) =>
        new($"https://download.mozilla.org/?product=firefox-esr-latest-ssl&os={(architecture == MachineArchitecture.Arm64 ? "win64-aarch64" : "win64")}&lang=en-US");

    /// <summary>The current ESR version from <c>firefox_versions.json</c>, e.g. "153.8.0esr".</summary>
    public static SoftwareVersion? EsrVersion(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject root
                && root["FIREFOX_ESR"] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0
                ? SoftwareVersion.Parse(text)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hosts a runtime download may come from (the redirector and its CDN). A redirect anywhere
    /// else, or to plain HTTP, is refused.
    /// </summary>
    public static bool IsTrustedDownloadUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || !string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
        var host = url.Host.ToLowerInvariant();
        return host is "mozilla.org" or "mozilla.net" || host.EndsWith(".mozilla.org", StringComparison.Ordinal)
            || host.EndsWith(".mozilla.net", StringComparison.Ordinal);
    }
}
