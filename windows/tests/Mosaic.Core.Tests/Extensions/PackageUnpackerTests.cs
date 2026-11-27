using System.IO.Compression;
using System.Text;
using Mosaic.Core.Extensions;

namespace Mosaic.Core.Tests.Extensions;

/// <summary>Unpacking with System.IO.Compression: what is written, what is skipped, what fails.</summary>
public class PackageUnpackerTests
{
    private const int NoLimit = int.MaxValue;

    /// <summary>All files below <paramref name="directory"/>, as sorted forward-slash relative paths.</summary>
    private static string[] FilesIn(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>An archive whose single entry is stored without compression.</summary>
    private static byte[] StoredZip(string name, string contents)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
            entry.Write(Encoding.UTF8.GetBytes(contents));
        }
        return stream.ToArray();
    }

    [Fact]
    public void UnpacksFilesIntoNestedDirectories()
    {
        using var directory = new TemporaryDirectory();
        var zip = CrxFixtures.Zip(("manifest.json", "{}"), ("a/b/c.js", "deep"), ("empty.txt", ""), ("ünï/côdé 日本.txt", "names"));

        var skipped = PackageUnpacker.Unpack(zip, directory.Path, NoLimit);

        Assert.Empty(skipped);
        Assert.Equal(["a/b/c.js", "empty.txt", "manifest.json", "ünï/côdé 日本.txt"], FilesIn(directory.Path));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(directory.Path, "a", "b", "c.js")));
        Assert.Equal(0, new FileInfo(Path.Combine(directory.Path, "empty.txt")).Length);
    }

    [Fact]
    public void LargeAndBinaryContentsSurviveIntact()
    {
        using var directory = new TemporaryDirectory();
        var binary = new byte[300_000];
        new Random(1).NextBytes(binary);
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible ", 50_000)));

        PackageUnpacker.Unpack(CrxFixtures.Zip([("random.bin", binary), ("text.txt", text)]), directory.Path, NoLimit);

        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(directory.Path, "random.bin")));
        Assert.Equal(text, File.ReadAllBytes(Path.Combine(directory.Path, "text.txt")));
    }

    [Fact]
    public void EntriesThatWouldEscapeTheDestinationAreSkipped()
    {
        using var parent = new TemporaryDirectory();
        var destination = Directory.CreateDirectory(Path.Combine(parent.Path, "inner", "destination")).FullName;
        var zip = CrxFixtures.Zip(
            ("ok.txt", "ok"),
            ("../escaped.txt", "x"),
            ("../../escaped.txt", "x"),
            ("a/../../escaped.txt", "x"),
            ("..\\escaped.txt", "x"),
            ("a/../b.txt", "x"),
            (parent.Path.Replace('\\', '/') + "/absolute.txt", "x"),
            ("C:/absolute.txt", "x"),
            ("C:\\absolute.txt", "x"));

        var skipped = PackageUnpacker.Unpack(zip, destination, NoLimit);

        // An absolute Unix path is merely made relative, as unzip does; everything else is refused.
        var relocated = (parent.Path.Replace('\\', '/') + "/absolute.txt").TrimStart('/');
        var relocatedIsStorable = !relocated.Contains(':');
        Assert.Equal(relocatedIsStorable ? 7 : 8, skipped.Count);
        Assert.Contains("../escaped.txt", skipped);
        Assert.Contains("a/../b.txt", skipped);
        Assert.Contains("C:\\absolute.txt", skipped);

        // Nothing was written anywhere but below the destination.
        string[] expected = relocatedIsStorable ? ["ok.txt", relocated] : ["ok.txt"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), FilesIn(destination));
        Assert.Equal(["inner"], Directory.EnumerateFileSystemEntries(parent.Path).Select(Path.GetFileName));
        Assert.Equal(["destination"], Directory.EnumerateFileSystemEntries(Path.Combine(parent.Path, "inner")).Select(Path.GetFileName));
    }

    [Fact]
    public void EntriesWithNamesWindowsCannotStoreAreSkipped()
    {
        using var directory = new TemporaryDirectory();
        string[] unstorable =
        [
            "NUL", "con.js", "lib/aux.d.ts", "COM1/inside.js", "LPT¹",
            "trailing-dot.", "trailing-space ", "folder./x.js", "folder /y.js",
            "manifest.json::$DATA", "a:b.js", "what?.js", "star*.js", "pipe|.js", "quote\".js", "less<.js", "more>.js",
            "tab\t.js", "new\nline.js",
        ];
        var zip = CrxFixtures.Zip([("manifest.json", "real"), .. unstorable.Select(name => (name, "x")), ("lib/ok.js", "ok")]);

        var skipped = PackageUnpacker.Unpack(zip, directory.Path, NoLimit);

        Assert.Equal(unstorable, skipped);
        Assert.Equal(["lib/ok.js", "manifest.json"], FilesIn(directory.Path));
        // In particular nothing aliased the manifest.
        Assert.Equal("real", File.ReadAllText(Path.Combine(directory.Path, "manifest.json")));
    }

    [Fact]
    public void PathsTooLongForWindowsAreSkipped()
    {
        using var directory = new TemporaryDirectory();
        var fits = "d/" + new string('a', 38);          // 40 characters
        var tooLong = "d/" + new string('b', 39);       // 41 characters
        var nameTooLong = new string('c', 256);
        var zip = CrxFixtures.Zip((fits, "x"), (tooLong, "x"), (nameTooLong, "x"));

        Assert.Equal([tooLong, nameTooLong], PackageUnpacker.Unpack(zip, directory.Path, maximumRelativePathLength: 40));
        Assert.Equal([fits], FilesIn(directory.Path));
    }

    [Fact]
    public void BackslashesSeparateDirectoriesAsTheyDoOnWindows()
    {
        using var directory = new TemporaryDirectory();
        var skipped = PackageUnpacker.Unpack(CrxFixtures.Zip(("dir\\sub\\file.js", "x"), ("dir\\", "")), directory.Path, NoLimit);

        Assert.Empty(skipped);
        Assert.Equal(["dir/sub/file.js"], FilesIn(directory.Path));
    }

    [Fact]
    public void DirectoryEntriesNeedNoFileAndRepeatedEntriesOverwrite()
    {
        using var directory = new TemporaryDirectory();
        var zip = CrxFixtures.Zip(("dir/", ""), ("dir/file.txt", "first"), ("dir/file.txt", "second"));

        Assert.Empty(PackageUnpacker.Unpack(zip, directory.Path, NoLimit));
        Assert.Equal("second", File.ReadAllText(Path.Combine(directory.Path, "dir", "file.txt")));
    }

    [Fact]
    public void AFileAndADirectoryOfTheSameNameDoNotFailTheWholePackage()
    {
        using var directory = new TemporaryDirectory();
        var zip = CrxFixtures.Zip(("icon", "a file"), ("icon/16.png", "needs a directory"), ("lib/a.js", "x"), ("lib", "wants to be a file"), ("ok.txt", "ok"));

        var skipped = PackageUnpacker.Unpack(zip, directory.Path, NoLimit);

        Assert.Equal(["icon/16.png", "lib"], skipped);
        Assert.Equal(["icon", "lib/a.js", "ok.txt"], FilesIn(directory.Path));
    }

    [Fact]
    public void BytesAroundTheArchiveAreTolerated()
    {
        var zip = CrxFixtures.Zip(("manifest.json", "{}"), ("a/b.js", "content"));
        byte[][] variants =
        [
            [.. new byte[7], .. zip],                                     // padding in front (unzip: "extra bytes at beginning")
            [.. Encoding.ASCII.GetBytes("junk PK not a header"), .. zip],
            [.. zip, .. new byte[9]],                                     // padding behind
            [.. new byte[3], .. zip, 1, 2, 3, 4, 5],
        ];
        foreach (var variant in variants)
        {
            using var directory = new TemporaryDirectory();
            Assert.Empty(PackageUnpacker.Unpack(variant, directory.Path, NoLimit));
            Assert.Equal(["a/b.js", "manifest.json"], FilesIn(directory.Path));
            Assert.Equal("content", File.ReadAllText(Path.Combine(directory.Path, "a", "b.js")));
        }
    }

    [Fact]
    public void AnEmptyArchiveUnpacksToNothing()
    {
        using var directory = new TemporaryDirectory();
        Assert.Empty(PackageUnpacker.Unpack(CrxFixtures.Zip(), directory.Path, NoLimit));
        Assert.Empty(FilesIn(directory.Path));
    }

    [Fact]
    public void ACorruptArchiveIsAnError()
    {
        using var directory = new TemporaryDirectory();
        var zip = CrxFixtures.Zip(("manifest.json", string.Concat(Enumerable.Repeat("some compressible text ", 200))));

        // Not an archive at all.
        Assert.Throws<InvalidDataException>(() => PackageUnpacker.Unpack([.. CrxFixtures.ZipMagic, 9, 9, 9], directory.Path, NoLimit));
        Assert.Throws<InvalidDataException>(() => PackageUnpacker.Unpack([], directory.Path, NoLimit));
        // Cut short: the directory at the end is gone.
        Assert.Throws<InvalidDataException>(() => PackageUnpacker.Unpack(zip[..(zip.Length / 2)], directory.Path, NoLimit));
        // Compressed data damaged (the local header is 30 bytes plus the 13 of the name).
        var damaged = (byte[])zip.Clone();
        Array.Fill(damaged, (byte)0xFF, 43, 6);
        Assert.Throws<InvalidDataException>(() => PackageUnpacker.Unpack(damaged, directory.Path, NoLimit));
        // One changed byte in an uncompressed entry: only the checksum can tell.
        var stored = StoredZip("manifest.json", "UNIQUE-CONTENT");
        stored[stored.AsSpan().IndexOf("UNIQUE-CONTENT"u8)] = (byte)'V';
        Assert.Throws<InvalidDataException>(() => PackageUnpacker.Unpack(stored, directory.Path, NoLimit));
    }
}
