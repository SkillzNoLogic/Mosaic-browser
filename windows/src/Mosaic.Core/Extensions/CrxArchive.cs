using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mosaic.Core.Search;

namespace Mosaic.Core.Extensions;

/// <summary>The container a package arrived in.</summary>
public enum CrxFormat { Crx2, Crx3, Zip }

/// <summary>Why a file was rejected by <see cref="CrxArchive.Parse"/>.</summary>
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
/// <remarks>
/// <para>Port of <c>Sources/MosaicCore/Extensions/CRXArchive.swift</c>. Layouts handled:</para>
/// <list type="bullet">
/// <item>CRX3: <c>Cr24</c> | version=3 | headerLength | protobuf header | zip</item>
/// <item>CRX2: <c>Cr24</c> | version=2 | keyLength | sigLength | key | signature | zip</item>
/// <item>a bare ZIP (an unpacked extension that was zipped by hand)</item>
/// </list>
/// <para>Signatures are not verified: the package is trusted as far as its transport was.</para>
/// </remarks>
/// <param name="Format">The container the package arrived in.</param>
/// <param name="ZipData">The ZIP payload. For a bare ZIP this is the array given to <see cref="Parse"/>, not a copy.</param>
/// <param name="ExtensionId">32 characters in a–p, or null when the package does not carry one (bare ZIP).</param>
public sealed record CrxArchive(CrxFormat Format, byte[] ZipData, string? ExtensionId)
{
    private static ReadOnlySpan<byte> ZipMagic => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> CrxMagic => "Cr24"u8;

    /// <summary>Bytes of an extension ID: the leading half of a SHA-256 digest.</summary>
    private const int IdLength = 16;

    // Field numbers from Chromium's components/crx_file/crx3.proto.
    private const ulong Sha256WithRsaField = 2;            // CrxFileHeader.sha256_with_rsa (AsymmetricKeyProof)
    private const ulong Sha256WithEcdsaField = 3;          // CrxFileHeader.sha256_with_ecdsa (AsymmetricKeyProof)
    private const ulong SignedHeaderDataField = 10000;     // CrxFileHeader.signed_header_data (SignedData)
    private const ulong PublicKeyField = 1;                // AsymmetricKeyProof.public_key
    private const ulong CrxIdField = 1;                    // SignedData.crx_id

    /// <summary>
    /// The package's public key (DER SubjectPublicKeyInfo) when it carries one whose hash matches
    /// <see cref="ExtensionId"/>: the CRX2 key, or the matching proof's key in a CRX3 header.
    /// Written as the <c>key</c> of the unconverted <c>chromium/</c> copy so the Chromium engine
    /// gives the extension the same ID Chrome would.
    /// </summary>
    public byte[]? PublicKey { get; init; }

    /// <summary>Recognises the container and splits it into payload, ID and public key.</summary>
    /// <param name="data">The whole file.</param>
    /// <exception cref="CrxParseException">The file is not a package, is cut short, or is a CRX version other than 2 or 3.</exception>
    public static CrxArchive Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var bytes = data.AsSpan();

        if (bytes.Length < 4) throw NotAnExtensionPackage();
        if (bytes.StartsWith(ZipMagic)) return new CrxArchive(CrxFormat.Zip, data, null);
        if (!bytes.StartsWith(CrxMagic)) throw NotAnExtensionPackage();
        if (bytes.Length < 12) throw Truncated();

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        switch (version)
        {
            case 3:
            {
                var zipStart = 12 + (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
                if (zipStart > bytes.Length) throw Truncated();
                var header = bytes[12..(int)zipStart];
                var id = Crx3Id(header);
                return new CrxArchive(CrxFormat.Crx3, bytes[(int)zipStart..].ToArray(), id is null ? null : EncodeId(id))
                {
                    PublicKey = id is null ? null : Crx3PublicKey(header, id),
                };
            }
            case 2:
            {
                if (bytes.Length < 16) throw Truncated();
                var keyLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
                var signatureLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
                var zipStart = 16 + keyLength + signatureLength;
                if (zipStart > bytes.Length) throw Truncated();
                var key = bytes.Slice(16, (int)keyLength).ToArray();
                return new CrxArchive(CrxFormat.Crx2, bytes[(int)zipStart..].ToArray(), ExtensionIdFromPublicKey(key))
                {
                    PublicKey = key,
                };
            }
            default:
                throw new CrxParseException(CrxParseError.UnsupportedVersion, $"Unsupported CRX version {version}.", version);
        }

        static CrxParseException NotAnExtensionPackage() =>
            new(CrxParseError.NotAnExtensionPackage, "This file is not a Chrome extension package (.crx or .zip).");
        static CrxParseException Truncated() =>
            new(CrxParseError.Truncated, "The extension package is incomplete or corrupt.");
    }

    /// <summary>First 16 bytes of SHA-256(key), in base 16 written with the letters a–p.</summary>
    /// <remarks>This is how Chrome derives an extension's ID from its public key.</remarks>
    public static string ExtensionIdFromPublicKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return EncodeId(SHA256.HashData(key).AsSpan(0, IdLength));
    }

    /// <summary>The same derivation from the base64 <c>key</c> field of a manifest; null if not base64.</summary>
    public static string? ExtensionIdFromManifestKey(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);

        // Manifest keys are often wrapped over several lines.
        var cleaned = string.Concat(SwiftText.Characters(base64).Where(character => !SwiftText.IsWhitespace(character)));
        return DecodeBase64(cleaned) is { } key ? ExtensionIdFromPublicKey(key) : null;
    }

    /// <summary>
    /// Decodes base64 as leniently as Foundation's <c>Data(base64Encoded:)</c>, so that a
    /// sloppily written manifest key yields the same ID on both platforms: surplus <c>=</c> at
    /// the end and stray bits in the last character are tolerated (which
    /// <see cref="Convert.TryFromBase64String(string, Span{byte}, out int)"/> rejects), missing
    /// padding and characters outside the alphabet are not.
    /// </summary>
    /// <remarks>
    /// Two accidents of Foundation's decoder are not reproduced, and are rejected here: it reads
    /// four or more <c>=</c> on their own as one zero byte, and accepts a <c>=</c> in the third
    /// place of the final group when a digit follows it.
    /// </remarks>
    private static byte[]? DecodeBase64(string text)
    {
        var digits = text.AsSpan().TrimEnd('=');
        var padding = text.Length - digits.Length;
        if (digits.IsEmpty && padding > 0) return null; // Nothing but padding.

        var missing = (4 - digits.Length % 4) % 4;
        if (missing == 3 || padding < missing) return null;

        var bytes = new byte[digits.Length * 6 / 8];
        var written = 0;
        var buffer = 0;
        var bits = 0;
        foreach (var digit in digits)
        {
            var value = digit switch
            {
                >= 'A' and <= 'Z' => digit - 'A',
                >= 'a' and <= 'z' => digit - 'a' + 26,
                >= '0' and <= '9' => digit - '0' + 52,
                '+' => 62,
                '/' => 63,
                _ => -1,
            };
            if (value < 0) return null;

            buffer = ((buffer << 6) | value) & 0xFFFF;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[written++] = (byte)(buffer >> bits);
            }
        }
        return bytes;
    }

    /// <summary>True for exactly 32 characters in a–p.</summary>
    public static bool IsValidId(string id) =>
        id is { Length: 2 * IdLength } && id.AsSpan().IndexOfAnyExceptInRange('a', 'p') < 0;

    /// <summary>Writes each byte as two letters: 0x00 → "aa", 0x01 → "ab", … 0xff → "pp".</summary>
    internal static string EncodeId(ReadOnlySpan<byte> bytes)
    {
        var id = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            id.Append((char)('a' + (value >> 4))).Append((char)('a' + (value & 0x0F)));
        }
        return id.ToString();
    }

    // ---- CRX3 header (protobuf) ----

    /// <summary>
    /// The 16-byte binary extension ID a CRX3 header was signed for:
    /// <c>CrxFileHeader.signed_header_data</c> wraps a <c>SignedData</c> message holding it.
    /// </summary>
    private static byte[]? Crx3Id(ReadOnlySpan<byte> header) =>
        FirstBytesField(SignedHeaderDataField, header, out var signedData)
        && FirstBytesField(CrxIdField, signedData, out var id) && id.Length == IdLength
            ? id.ToArray()
            : null;

    /// <summary>
    /// The developer's key among a CRX3 header's proofs. A header carries one proof per signer
    /// (the developer, and usually the Web Store too); the developer's is the one whose key
    /// hashes to the extension ID.
    /// </summary>
    private static byte[]? Crx3PublicKey(ReadOnlySpan<byte> header, ReadOnlySpan<byte> id)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        var reader = new ProtobufReader(header);
        while (reader.NextBytesField(out var field, out var proof))
        {
            if (field is not (Sha256WithRsaField or Sha256WithEcdsaField)) continue;
            if (!FirstBytesField(PublicKeyField, proof, out var key)) continue;

            SHA256.HashData(key, digest);
            if (digest[..IdLength].SequenceEqual(id)) return key.ToArray();
        }
        return null;
    }

    /// <summary>Finds the first length-delimited value of <paramref name="field"/> in a protobuf message.</summary>
    private static bool FirstBytesField(ulong field, ReadOnlySpan<byte> message, out ReadOnlySpan<byte> value)
    {
        var reader = new ProtobufReader(message);
        while (reader.NextBytesField(out var number, out value))
        {
            if (number == field) return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Walks the fields of a protobuf message, yielding the length-delimited ones (the only wire
    /// type the CRX3 header uses) and stepping over the rest. Stops at the first malformed field.
    /// </summary>
    private ref struct ProtobufReader(ReadOnlySpan<byte> message)
    {
        private readonly ReadOnlySpan<byte> _message = message;
        private int _offset;

        public bool NextBytesField(out ulong field, out ReadOnlySpan<byte> value)
        {
            while (_offset < _message.Length && ReadVarint(out var tag))
            {
                field = tag >> 3;
                switch (tag & 0x7)
                {
                    case 0: // varint
                        if (!ReadVarint(out _)) goto malformed;
                        break;
                    case 1: // 64-bit
                        _offset += 8;
                        break;
                    case 2: // length-delimited
                        if (!ReadVarint(out var length) || length > (ulong)(_message.Length - _offset)) goto malformed;
                        value = _message.Slice(_offset, (int)length);
                        _offset += (int)length;
                        return true;
                    case 5: // 32-bit
                        _offset += 4;
                        break;
                    default: // groups, or not protobuf at all
                        goto malformed;
                }
            }

        malformed:
            _offset = _message.Length;
            field = 0;
            value = default;
            return false;
        }

        private bool ReadVarint(out ulong value)
        {
            value = 0;
            for (var shift = 0; _offset < _message.Length && shift < 64; shift += 7)
            {
                var b = _message[_offset++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }
    }
}

/// <summary>Chrome Web Store addressing.</summary>
public static class ChromeWebStore
{
    private const string DownloadEndpoint = "https://clients2.google.com/service/update2/crx";

    /// <summary>Extracts an extension ID from a bare ID or a Web Store listing URL; null otherwise.</summary>
    /// <remarks>
    /// Understands <c>chromewebstore.google.com/detail/&lt;slug&gt;/&lt;id&gt;</c> and the older
    /// <c>chrome.google.com/webstore/detail/...</c> form, with or without a trailing path or query.
    /// </remarks>
    public static string? ExtensionIdFrom(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = SwiftText.Trim(input);
        if (CrxArchive.IsValidId(text)) return text;

        if (FoundationUrl.ParseStrict(text) is not { EncodedHost: { } encodedHost } url) return null;
        var host = FoundationUrl.PercentDecode(encodedHost);
        if (!Ascii.EqualsIgnoreCase(host, "chromewebstore.google.com") && !Ascii.EqualsIgnoreCase(host, "chrome.google.com"))
        {
            return null;
        }

        var parts = (FoundationUrl.PercentDecode(url.EncodedPath) ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var detail = Array.IndexOf(parts, "detail");
        return detail < 0 ? null : parts.Skip(detail + 1).FirstOrDefault(CrxArchive.IsValidId);
    }

    /// <summary>The endpoint Chrome itself uses to download an extension's CRX.</summary>
    /// <param name="id">The extension's ID.</param>
    /// <param name="chromiumVersion">The Chrome version to claim; the store withholds packages that require a newer one.</param>
    /// <remarks>
    /// The request describes Chrome for Windows on this machine's processor architecture, where
    /// the macOS version describes Chrome for Apple silicon.
    /// </remarks>
    public static Uri DownloadUrl(string id, string chromiumVersion = "140.0.0.0") =>
        DownloadUrl(id, chromiumVersion, RuntimeInformation.OSArchitecture);

    /// <summary><see cref="DownloadUrl(string, string)"/> for a given processor architecture.</summary>
    internal static Uri DownloadUrl(string id, string chromiumVersion, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(chromiumVersion);

        // The values Chrome's update client sends: "arch" names the browser build, "os_arch" the system.
        var (arch, osArch) = architecture switch
        {
            Architecture.Arm64 => ("arm64", "arm64"),
            Architecture.X86 => ("x86", "x86"),
            _ => ("x64", "x86_64"),
        };
        (string Name, string Value)[] query =
        [
            ("response", "redirect"),
            ("os", "win"),
            ("arch", arch),
            ("os_arch", osArch),
            ("prod", "chromecrx"),
            ("prodchannel", "stable"),
            ("prodversion", chromiumVersion),
            ("acceptformat", "crx2,crx3"),
            // One parameter holding a nested query string; its "&" and "=" must stay escaped.
            ("x", $"id={id}&installsource=ondemand&uc"),
        ];
        return new Uri($"{DownloadEndpoint}?{string.Join('&', query.Select(item => $"{item.Name}={FoundationUrl.EscapeQueryItem(item.Value)}"))}");
    }

    /// <summary>The extension's page on the Chrome Web Store.</summary>
    public static Uri ListingUrl(string id) => new($"https://chromewebstore.google.com/detail/{id}");
}
