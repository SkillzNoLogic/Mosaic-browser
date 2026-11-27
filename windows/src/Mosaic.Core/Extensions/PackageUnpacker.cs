using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Mosaic.Core.Extensions;

/// <summary>
/// Unpacks an extension's ZIP payload into a directory, with <c>System.IO.Compression</c>.
/// </summary>
/// <remarks>
/// <para>
/// The macOS version shells out to <c>unzip</c> and accepts its "completed with warnings" exit
/// status. The equivalent here: the archive as a whole must be readable and every file's data
/// intact, but an entry whose <em>name</em> is the problem is skipped and reported instead of
/// failing the installation. That covers names that would escape the destination ("zip slip"),
/// names Windows cannot store (see <see cref="ExtensionPaths"/>), paths too long for Windows, and
/// entries that collide with one another as file and directory.
/// </para>
/// <para>
/// Nothing but file contents is taken from the archive: no timestamps, no permissions, and no
/// symbolic links (a link entry becomes an ordinary file holding its target path).
/// </para>
/// </remarks>
internal static class PackageUnpacker
{
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint CentralDirectoryHeaderSignature = 0x02014B50;
    private const int EndOfCentralDirectoryLength = 22;

    /// <summary>Unpacks every storable file of the archive.</summary>
    /// <param name="zipData">The ZIP archive.</param>
    /// <param name="destination">An existing directory to unpack into.</param>
    /// <param name="maximumRelativePathLength">Longest path, relative to the extension's root, that
    /// will still fit Windows' path limit once the extension is in its final place.</param>
    /// <returns>The names of the entries that were skipped, as written in the archive.</returns>
    /// <exception cref="InvalidDataException">The archive is corrupt, or stores a file in a way that cannot be read.</exception>
    public static IReadOnlyList<string> Unpack(byte[] zipData, string destination, int maximumRelativePathLength)
    {
        var skipped = new List<string>();
        var leadingJunk = LeadingJunkLength(zipData);
        using var stream = new MemoryStream(zipData, leadingJunk, zipData.Length - leadingJunk, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/') || name.EndsWith('\\')) continue; // A directory; made when a file needs it.

            if (ExtensionPaths.Split(name, collapseParentReferences: false) is not { } components
                || components.Sum(component => component.Length + 1) - 1 > maximumRelativePathLength)
            {
                skipped.Add(name);
                continue;
            }

            var target = Path.Combine([destination, .. components]);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                Extract(entry, output);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && CollidesWithAnotherEntry(destination, components))
            {
                skipped.Add(name);
            }
        }
        return skipped;
    }

    /// <summary>
    /// Writes an entry's contents, checking them against the length and CRC-32 the archive
    /// records. (Not every .NET version's <see cref="ZipArchive"/> verifies the checksum itself.)
    /// </summary>
    private static void Extract(ZipArchiveEntry entry, Stream output)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var input = entry.Open();
            var checksum = uint.MaxValue;
            long length = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                foreach (var value in buffer.AsSpan(0, read))
                {
                    checksum = Crc32Table[(byte)(checksum ^ value)] ^ (checksum >> 8);
                }
                output.Write(buffer, 0, read);
                length += read;
            }
            if (~checksum != entry.Crc32 || length != entry.Length)
            {
                throw new InvalidDataException($"The file “{entry.FullName}” inside the package is damaged.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Lookup table for the CRC-32 of ZIP (the reflected IEEE 802.3 polynomial).</summary>
    private static readonly uint[] Crc32Table = [.. Enumerable.Range(0, 256).Select(index =>
    {
        var remainder = (uint)index;
        for (var bit = 0; bit < 8; bit++)
        {
            remainder = (remainder & 1) != 0 ? 0xEDB88320 ^ (remainder >> 1) : remainder >> 1;
        }
        return remainder;
    })];

    /// <summary>
    /// True when the entry could not be written because an earlier entry occupies its place: a
    /// file where it needs a directory, or a directory where it wants to be a file. Archives made
    /// on a case-sensitive system produce this on Windows ("Icon" next to "icon/16.png").
    /// </summary>
    private static bool CollidesWithAnotherEntry(string destination, string[] components)
    {
        var path = destination;
        foreach (var directory in components.AsSpan(..^1))
        {
            path = Path.Combine(path, directory);
            if (File.Exists(path)) return true;
        }
        return Directory.Exists(Path.Combine(path, components[^1]));
    }

    /// <summary>
    /// The number of bytes in front of the archive that its own bookkeeping does not account for.
    /// </summary>
    /// <remarks>
    /// A ZIP is located from its end: the last record says where the central directory starts,
    /// counted from the first byte of the archive. When stray bytes precede the archive (padding
    /// after a CRX header, typically) every such offset is short by their length. <c>unzip</c>
    /// notices that the directory is not where the record claims, warns ("extra bytes at beginning
    /// or within zipfile") and compensates; <see cref="ZipArchive"/> gives up. Skipping the stray
    /// bytes lets it read such a package too.
    /// </remarks>
    private static int LeadingJunkLength(ReadOnlySpan<byte> zip)
    {
        // The record is followed only by a comment of at most 65,535 bytes.
        var earliest = Math.Max(0, zip.Length - EndOfCentralDirectoryLength - ushort.MaxValue);
        for (var position = zip.Length - EndOfCentralDirectoryLength; position >= earliest; position--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip[position..]) != EndOfCentralDirectorySignature) continue;

            long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(zip[(position + 12)..]);
            long declaredStart = BinaryPrimitives.ReadUInt32LittleEndian(zip[(position + 16)..]);
            var actualStart = position - directorySize;

            // Only compensate when the directory is provably elsewhere: not at the declared
            // offset, but exactly where its size says it ends. (ZIP64 archives, whose fields
            // here are placeholders, fail both tests and are left to ZipArchive.)
            var shifted = actualStart > declaredStart
                && !HasSignature(zip, declaredStart, CentralDirectoryHeaderSignature)
                && HasSignature(zip, actualStart, CentralDirectoryHeaderSignature);
            return shifted ? (int)(actualStart - declaredStart) : 0;
        }
        return 0;
    }

    private static bool HasSignature(ReadOnlySpan<byte> data, long offset, uint signature) =>
        offset >= 0 && offset <= data.Length - sizeof(uint)
        && BinaryPrimitives.ReadUInt32LittleEndian(data[(int)offset..]) == signature;
}
