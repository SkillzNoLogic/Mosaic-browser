using System.Security.Cryptography;
using System.Text;
using Mosaic.Core.Extensions;

namespace Mosaic.Core.Tests.Extensions;

/// <summary>Port of <c>CRXArchiveTests</c> in <c>Tests/MosaicCoreTests/CRXTests.swift</c>, plus the public-key recovery added for Windows.</summary>
public class CrxArchiveTests
{
    private static readonly byte[] Payload = [.. CrxFixtures.ZipMagic, 9, 9, 9];

    private static byte[] Sequence(int count) => [.. Enumerable.Range(0, count).Select(i => (byte)i)];

    private static CrxParseException ParseFails(byte[] data) => Assert.Throws<CrxParseException>(() => CrxArchive.Parse(data));

    [Fact]
    public void ParsesCrx3AndReadsIdFromSignedHeader()
    {
        var archive = CrxArchive.Parse(CrxFixtures.Crx3(Sequence(16), Payload));
        Assert.Equal(CrxFormat.Crx3, archive.Format);
        Assert.Equal(Payload, archive.ZipData);
        // Each byte becomes two letters a–p: 0x00 → "aa", 0x01 → "ab", … 0x0f → "ap".
        Assert.Equal("aaabacadaeafagahaiajakalamanaoap", archive.ExtensionId);
    }

    [Fact]
    public void ParsesCrx2AndDerivesIdFromPublicKey()
    {
        var key = Encoding.UTF8.GetBytes("not really a key");
        var archive = CrxArchive.Parse(CrxFixtures.Crx2(key, [1, 2, 3, 4], Payload));
        Assert.Equal(CrxFormat.Crx2, archive.Format);
        Assert.Equal(Payload, archive.ZipData);
        Assert.Equal(CrxArchive.ExtensionIdFromPublicKey(key), archive.ExtensionId);
        Assert.True(CrxArchive.IsValidId(archive.ExtensionId ?? ""));
    }

    [Fact]
    public void AcceptsBareZip()
    {
        var archive = CrxArchive.Parse(Payload);
        Assert.Equal(CrxFormat.Zip, archive.Format);
        Assert.Null(archive.ExtensionId);
        Assert.Null(archive.PublicKey);
        Assert.Equal(Payload, archive.ZipData);
    }

    [Fact]
    public void RejectsGarbageAndTruncatedFiles()
    {
        Assert.Equal(CrxParseError.NotAnExtensionPackage, ParseFails(Encoding.UTF8.GetBytes("<html>not found</html>")).Error);
        Assert.Equal(CrxParseError.NotAnExtensionPackage, ParseFails([]).Error);
        Assert.Equal(CrxParseError.Truncated, ParseFails([.. "Cr24"u8, .. CrxFixtures.Le32(3), .. CrxFixtures.Le32(5000), 1, 2]).Error);

        var unsupported = ParseFails([.. "Cr24"u8, .. CrxFixtures.Le32(7), .. CrxFixtures.Le32(0)]);
        Assert.Equal(CrxParseError.UnsupportedVersion, unsupported.Error);
        Assert.Equal(7u, unsupported.Version);
    }

    [Fact]
    public void Crx3WithoutSignedHeaderHasNoId()
    {
        var archive = CrxArchive.Parse(CrxFixtures.Crx3WithHeader(CrxFixtures.Field(2, [1, 2, 3]), Payload));
        Assert.Null(archive.ExtensionId);
        Assert.Null(archive.PublicKey);
    }

    [Fact]
    public void ManifestKeyGivesSameIdAsRawKey()
    {
        var key = Sequence(64);
        Assert.Equal(CrxArchive.ExtensionIdFromPublicKey(key), CrxArchive.ExtensionIdFromManifestKey(Convert.ToBase64String(key)));
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("***"));
    }

    [Fact]
    public void ValidatesIds()
    {
        Assert.True(CrxArchive.IsValidId("eimadpbcbfnmbkopoojfekhnkhdbieeh"));
        Assert.False(CrxArchive.IsValidId("eimadpbcbfnmbkopoojfekhnkhdbiee"));      // 31 chars
        Assert.False(CrxArchive.IsValidId("eimadpbcbfnmbkopoojfekhnkhdbieez"));     // z is outside a–p
        Assert.False(CrxArchive.IsValidId("../../../../../../../../etc/passwd"));
    }

    // ---- Beyond the Swift tests ----

    [Fact]
    public void ErrorMessagesAreTheOnesShownOnMacOS()
    {
        Assert.Equal("This file is not a Chrome extension package (.crx or .zip).", ParseFails([1, 2, 3, 4, 5]).Message);
        Assert.Equal("The extension package is incomplete or corrupt.", ParseFails([.. "Cr24"u8, 3, 0]).Message);
        Assert.Equal("Unsupported CRX version 7.", ParseFails([.. "Cr24"u8, .. CrxFixtures.Le32(7), .. CrxFixtures.Le32(0)]).Message);
    }

    [Fact]
    public void TruncationIsDetectedAtEveryLengthField()
    {
        Assert.Equal(CrxParseError.NotAnExtensionPackage, ParseFails([.. "Cr2"u8]).Error);
        Assert.Equal(CrxParseError.Truncated, ParseFails([.. "Cr24"u8, .. CrxFixtures.Le32(2), .. CrxFixtures.Le32(1)]).Error);
        Assert.Equal(CrxParseError.Truncated, ParseFails([.. "Cr24"u8, .. CrxFixtures.Le32(2), .. CrxFixtures.Le32(4), .. CrxFixtures.Le32(4), 1, 2, 3]).Error);
        // Lengths that would overflow a 32-bit sum.
        Assert.Equal(CrxParseError.Truncated, ParseFails([.. "Cr24"u8, .. CrxFixtures.Le32(2), .. CrxFixtures.Le32(0xFFFFFFFF), .. CrxFixtures.Le32(0xFFFFFFFF)]).Error);
        Assert.Equal(CrxParseError.Truncated, ParseFails([.. "Cr24"u8, .. CrxFixtures.Le32(3), .. CrxFixtures.Le32(0xFFFFFFFF)]).Error);
    }

    [Fact]
    public void ACrxWithNothingAfterItsHeaderHasAnEmptyPayload()
    {
        Assert.Empty(CrxArchive.Parse([.. "Cr24"u8, .. CrxFixtures.Le32(3), .. CrxFixtures.Le32(0)]).ZipData);
        Assert.Empty(CrxArchive.Parse(CrxFixtures.Crx2([1], [2], [])).ZipData);
    }

    [Fact]
    public void Crx2PublicKeyIsTheKeyInTheFile()
    {
        var key = Encoding.UTF8.GetBytes("not really a key");
        var archive = CrxArchive.Parse(CrxFixtures.Crx2(key, [1, 2, 3, 4], Payload));
        Assert.Equal(key, archive.PublicKey);
    }

    [Fact]
    public void Crx3PublicKeyIsTheRsaProofKeyThatHashesToTheId()
    {
        using var developer = RSA.Create(2048);
        using var publisher = RSA.Create(2048);
        var developerKey = developer.ExportSubjectPublicKeyInfo();
        var id = SHA256.HashData(developerKey)[..16];

        // As the Web Store ships them: the store's own proof as well as the developer's.
        byte[] header =
        [
            .. CrxFixtures.Field(2, CrxFixtures.Proof(publisher.ExportSubjectPublicKeyInfo(), [1, 2, 3])),
            .. CrxFixtures.Field(2, CrxFixtures.Proof(developerKey, [4, 5, 6])),
            .. CrxFixtures.Field(10000, CrxFixtures.Field(1, id)),
        ];
        var archive = CrxArchive.Parse(CrxFixtures.Crx3WithHeader(header, Payload));

        Assert.Equal(developerKey, archive.PublicKey);
        Assert.Equal(archive.ExtensionId, CrxArchive.ExtensionIdFromPublicKey(archive.PublicKey!));
        Assert.Equal(archive.ExtensionId, CrxArchive.ExtensionIdFromManifestKey(Convert.ToBase64String(archive.PublicKey!)));
    }

    [Fact]
    public void Crx3PublicKeyMayComeFromAnEcdsaProofAfterTheSignedData()
    {
        using var developer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var publisher = RSA.Create(2048);
        var developerKey = developer.ExportSubjectPublicKeyInfo();
        byte[] header =
        [
            .. CrxFixtures.Field(10000, CrxFixtures.Field(1, SHA256.HashData(developerKey)[..16])),
            .. CrxFixtures.Field(4, [0x1F, 0x8B]), // verified_contents, to be stepped over
            .. CrxFixtures.Field(2, CrxFixtures.Proof(publisher.ExportSubjectPublicKeyInfo(), [1])),
            .. CrxFixtures.Field(3, CrxFixtures.Proof(developerKey, [2])),
        ];

        Assert.Equal(developerKey, CrxArchive.Parse(CrxFixtures.Crx3WithHeader(header, Payload)).PublicKey);
    }

    [Fact]
    public void Crx3WithoutAProofForItsIdHasNoPublicKey()
    {
        // The standard fixture's proof key is three arbitrary bytes that do not hash to the ID.
        var archive = CrxArchive.Parse(CrxFixtures.Crx3(Sequence(16), Payload));
        Assert.NotNull(archive.ExtensionId);
        Assert.Null(archive.PublicKey);
    }

    [Fact]
    public void Crx3HeaderFieldsOfOtherWireTypesAreSteppedOver()
    {
        byte[] header =
        [
            0x08, 0x96, 0x01,                         // field 1, varint 150
            0x11, 1, 2, 3, 4, 5, 6, 7, 8,             // field 2, 64-bit
            0x1D, 1, 2, 3, 4,                         // field 3, 32-bit
            .. CrxFixtures.Field(10000, CrxFixtures.Field(1, Sequence(16))),
        ];
        Assert.Equal("aaabacadaeafagahaiajakalamanaoap", CrxArchive.Parse(CrxFixtures.Crx3WithHeader(header, Payload)).ExtensionId);
    }

    [Theory]
    [InlineData(new byte[] { 0x0B })]                       // a group: not something a CRX header contains
    [InlineData(new byte[] { 0x12, 0x7F, 1, 2 })]           // a length running past the end
    [InlineData(new byte[] { 0x80, 0x80, 0x80 })]           // an unterminated varint
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void AMalformedCrx3HeaderYieldsNoIdRatherThanAnError(byte[] garbage)
    {
        byte[] header = [.. garbage, .. CrxFixtures.Field(10000, CrxFixtures.Field(1, Sequence(16)))];
        var archive = CrxArchive.Parse(CrxFixtures.Crx3WithHeader(header, Payload));
        Assert.Equal(CrxFormat.Crx3, archive.Format);
        Assert.Null(archive.ExtensionId);
        Assert.Equal(Payload, archive.ZipData);
    }

    [Fact]
    public void ASignedIdOfTheWrongLengthIsIgnored() =>
        Assert.Null(CrxArchive.Parse(CrxFixtures.Crx3(Sequence(15), Payload)).ExtensionId);

    [Fact]
    public void ManifestKeysMayBeWrappedAcrossLines()
    {
        var key = Sequence(64);
        var wrapped = string.Join("\r\n  ", Convert.ToBase64String(key).Chunk(20).Select(chunk => new string(chunk)));
        Assert.Equal(CrxArchive.ExtensionIdFromPublicKey(key), CrxArchive.ExtensionIdFromManifestKey(wrapped));
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("AAAA=A"));
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("AAA")); // padding is required
    }

    [Fact]
    public void ManifestKeysAreDecodedAsLenientlyAsOnMacOS()
    {
        // Values are what the macOS implementation returns for the same strings.
        var abc = CrxArchive.ExtensionIdFromPublicKey("ABC"u8.ToArray());
        Assert.Equal("lfneaefmdpeggpkjbpocmmgklohjcdck", abc);
        Assert.Equal(abc, CrxArchive.ExtensionIdFromManifestKey("QUJD"));
        Assert.Equal(abc, CrxArchive.ExtensionIdFromManifestKey("QUJD=="));                                   // surplus padding
        Assert.Equal("aelindeocaogaemknlaeljnlipghhimd", CrxArchive.ExtensionIdFromManifestKey("+/=="));      // stray bits in the last digit
        Assert.Equal("odlameecjipmbmbejkplpemijjgpljce", CrxArchive.ExtensionIdFromManifestKey(""));
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("+/="));
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("QQ==QUJD"));
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("A==="));
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("QUJD-_"));                                         // not the URL-safe alphabet
        // Deliberately stricter than macOS, whose decoder turns these into bytes by accident.
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("===="));
        Assert.Null(CrxArchive.ExtensionIdFromManifestKey("+/=A"));
    }

    [Fact]
    public void IdsUseTheLettersAToP()
    {
        Assert.Equal("aappkl", CrxArchive.EncodeId([0x00, 0xFF, 0xAB]));
        // Dark Reader's Web Store ID is well known; an ID is 32 such letters.
        Assert.False(CrxArchive.IsValidId("EIMADPBCBFNMBKOPOOJFEKHNKHDBIEEH"));
        Assert.False(CrxArchive.IsValidId(""));
    }
}

/// <summary>Port of <c>ChromeWebStoreTests</c> in <c>Tests/MosaicCoreTests/CRXTests.swift</c>.</summary>
public class ChromeWebStoreTests
{
    private const string Id = "eimadpbcbfnmbkopoojfekhnkhdbieeh";

    [Theory]
    [InlineData("eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("  eimadpbcbfnmbkopoojfekhnkhdbieeh\n")]
    [InlineData("https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh?hl=en&pli=1")]
    [InlineData("https://chromewebstore.google.com/detail/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh/reviews")]
    [InlineData("https://chrome.google.com/webstore/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    public void FindsId(string input) => Assert.Equal(Id, ChromeWebStore.ExtensionIdFrom(input));

    [Theory]
    [InlineData("")]
    [InlineData("dark reader")]
    [InlineData("https://chromewebstore.google.com/category/extensions")]
    [InlineData("https://evil.example/detail/x/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://chromewebstore.google.com/detail/dark-reader/short")]
    public void RejectsNonListings(string input) => Assert.Null(ChromeWebStore.ExtensionIdFrom(input));

    [Fact]
    public void DownloadUrlCarriesTheIdAndAsksForCrx()
    {
        var url = ChromeWebStore.DownloadUrl(Id);
        var query = url.Query.TrimStart('?').Split('&').Select(item => item.Split('=', 2))
            .ToDictionary(item => item[0], item => Uri.UnescapeDataString(item[1]));
        Assert.Equal("clients2.google.com", url.Host);
        Assert.Equal($"id={Id}&installsource=ondemand&uc", query["x"]);
        Assert.Equal("crx2,crx3", query["acceptformat"]);
        Assert.Equal("redirect", query["response"]);
        // The nested "&" must be escaped or the server would see separate parameters.
        Assert.Contains("%26installsource", url.AbsoluteUri);
    }

    // ---- Beyond the Swift tests ----

    [Theory]
    [InlineData("HTTPS://ChromeWebStore.Google.COM/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("//chromewebstore.google.com/detail/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://chromewebstore.google.com/detail/dark%20reader/eimadpbcbfnmbkopoojfekhnkhdbieeh#reviews")]
    [InlineData("https://chromewebstore.google.com/detail/dark reader/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://chromewebstore.google.com:443/detail//x//eimadpbcbfnmbkopoojfekhnkhdbieeh/")]
    [InlineData("​https://chromewebstore.google.com/detail/x/eimadpbcbfnmbkopoojfekhnkhdbieeh ")]
    public void FindsIdInLessTidyLinks(string input) => Assert.Equal(Id, ChromeWebStore.ExtensionIdFrom(input));

    [Theory]
    [InlineData("chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh")] // no "//": not a host
    [InlineData("https://chromewebstore.google.com.evil.example/detail/x/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://chromewebstore.google.com@evil.example/detail/x/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://evil.example/?https://chromewebstore.google.com/detail/x/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://chromewebstore.google.com/eimadpbcbfnmbkopoojfekhnkhdbieeh/detail")] // the ID follows "detail"
    [InlineData("https://chromewebstore.google.com/?detail/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("https://chromewebstore.google.com:port/detail/eimadpbcbfnmbkopoojfekhnkhdbieeh")]
    [InlineData("EIMADPBCBFNMBKOPOOJFEKHNKHDBIEEH")]
    public void RejectsLookalikes(string input) => Assert.Null(ChromeWebStore.ExtensionIdFrom(input));

    [Fact]
    public void DownloadUrlDescribesChromeForWindows()
    {
        Assert.Equal(
            "https://clients2.google.com/service/update2/crx?response=redirect&os=win&arch=x64&os_arch=x86_64&prod=chromecrx"
            + $"&prodchannel=stable&prodversion=140.0.0.0&acceptformat=crx2,crx3&x=id%3D{Id}%26installsource%3Dondemand%26uc",
            ChromeWebStore.DownloadUrl(Id, "140.0.0.0", System.Runtime.InteropServices.Architecture.X64).AbsoluteUri);

        var arm = ChromeWebStore.DownloadUrl(Id, "150.1.2.3", System.Runtime.InteropServices.Architecture.Arm64).AbsoluteUri;
        Assert.Contains("&arch=arm64&os_arch=arm64&", arm);
        Assert.Contains("&prodversion=150.1.2.3&", arm);
    }

    [Fact]
    public void ListingUrlPointsAtTheStore() =>
        Assert.Equal($"https://chromewebstore.google.com/detail/{Id}", ChromeWebStore.ListingUrl(Id).AbsoluteUri);
}
