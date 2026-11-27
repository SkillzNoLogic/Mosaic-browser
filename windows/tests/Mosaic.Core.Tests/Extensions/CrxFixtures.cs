using System.IO.Compression;
using System.Text;

namespace Mosaic.Core.Tests.Extensions;

/// <summary>
/// Builders for synthetic packages, shared by the CRX and store tests. Port of <c>Fixtures</c> in
/// <c>Tests/MosaicCoreTests/CRXTests.swift</c>, with real ZIP archives built in memory.
/// </summary>
internal static class CrxFixtures
{
    public static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] CrxMagic = "Cr24"u8.ToArray();

    public static byte[] Le32(long value) =>
        [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF)];

    public static byte[] Varint(long value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            bytes.Add(b);
        }
        while (value != 0);
        return [.. bytes];
    }

    /// <summary>A length-delimited protobuf field.</summary>
    public static byte[] Field(int number, byte[] bytes) => [.. Varint((number << 3) | 2), .. Varint(bytes.Length), .. bytes];

    /// <summary>A CRX3 file: a header whose signed data names <paramref name="id"/>, followed by <paramref name="zip"/>.</summary>
    public static byte[] Crx3(byte[] id, byte[] zip)
    {
        // A proof entry first, to make sure unrelated fields are skipped.
        byte[] header = [.. Field(2, [.. Field(1, [1, 2, 3]), .. Field(2, [4, 5])]), .. Field(10000, Field(1, id))];
        return Crx3WithHeader(header, zip);
    }

    public static byte[] Crx3WithHeader(byte[] header, byte[] zip) => [.. CrxMagic, .. Le32(3), .. Le32(header.Length), .. header, .. zip];

    /// <summary>One <c>AsymmetricKeyProof</c> message.</summary>
    public static byte[] Proof(byte[] publicKey, byte[] signature) => [.. Field(1, publicKey), .. Field(2, signature)];

    public static byte[] Crx2(byte[] key, byte[] signature, byte[] zip) =>
        [.. CrxMagic, .. Le32(2), .. Le32(key.Length), .. Le32(signature.Length), .. key, .. signature, .. zip];

    /// <summary>Zips <paramref name="files"/> (entry name → contents), in the order given.</summary>
    public static byte[] Zip(params (string Name, string Contents)[] files) =>
        Zip([.. files.Select(file => (file.Name, Encoding.UTF8.GetBytes(file.Contents)))]);

    public static byte[] Zip((string Name, byte[] Contents)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in files)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(contents);
            }
        }
        return stream.ToArray();
    }
}

/// <summary>A scratch directory that is deleted with the test.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mosaic-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Left for the operating system's temporary-file cleanup.
        }
    }
}
