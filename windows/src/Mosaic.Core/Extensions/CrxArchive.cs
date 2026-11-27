namespace Mosaic.Core.Extensions;

public enum CrxFormat { Crx2, Crx3, Zip }

public enum CrxParseError { NotAnExtensionPackage, UnsupportedVersion, Truncated }

/// <summary>Thrown by <see cref="CrxArchive.Parse"/>. <see cref="Exception.Message"/> is user-presentable.</summary>
public sealed class CrxParseException(CrxParseError error, string message, uint? version = null) : Exception(message)
{
    public CrxParseError Error { get; } = error;
    /// <summary>The offending version for <see cref="CrxParseError.UnsupportedVersion"/>.</summary>
    public uint? Version { get; } = version;
}

/// <summary>
/// A Chrome extension package (<c>.crx</c>) reduced to its ZIP payload and extension ID.
/// Handles CRX3, CRX2 and bare ZIPs.
/// </summary>
/// <remarks>Port of <c>Sources/MosaicCore/Extensions/CRXArchive.swift</c>.</remarks>
public sealed record CrxArchive(CrxFormat Format, byte[] ZipData, string? ExtensionId)
{
    /// <summary>
    /// The package's public key (DER SubjectPublicKeyInfo) when it carries one whose hash matches
    /// <see cref="ExtensionId"/>: the CRX2 key, or the matching proof's key in a CRX3 header.
    /// Written as the <c>key</c> of the unconverted <c>chromium/</c> copy so the Chromium engine
    /// gives the extension the same ID Chrome would.
    /// </summary>
    public byte[]? PublicKey { get; init; }

    /// <exception cref="CrxParseException"/>
    public static CrxArchive Parse(byte[] data) => throw NotPorted.Yet();

    /// <summary>First 16 bytes of SHA-256(key), in base 16 written with the letters a–p.</summary>
    public static string ExtensionIdFromPublicKey(byte[] key) => throw NotPorted.Yet();

    /// <summary>The same derivation from the base64 <c>key</c> field of a manifest; null if not base64.</summary>
    public static string? ExtensionIdFromManifestKey(string base64) => throw NotPorted.Yet();

    /// <summary>True for exactly 32 characters in a–p.</summary>
    public static bool IsValidId(string id) => throw NotPorted.Yet();
}

/// <summary>Chrome Web Store addressing.</summary>
public static class ChromeWebStore
{
    /// <summary>Extracts an extension ID from a bare ID or a Web Store listing URL; null otherwise.</summary>
    public static string? ExtensionIdFrom(string input) => throw NotPorted.Yet();

    /// <summary>The endpoint Chrome itself uses to download an extension's CRX.</summary>
    public static Uri DownloadUrl(string id, string chromiumVersion = "140.0.0.0") => throw NotPorted.Yet();

    public static Uri ListingUrl(string id) => new($"https://chromewebstore.google.com/detail/{id}");
}
